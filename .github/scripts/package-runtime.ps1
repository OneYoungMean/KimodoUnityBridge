[CmdletBinding()]
param(
    [string]$QuickServerRepo = "https://github.com/OneYoungMean/NvlabKimodoQuickServer.git",
    [string]$QuickServerRef = "main",
    [string]$OutputZip
)

$ErrorActionPreference = "Stop"

function Ensure-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name"
    }
}

Ensure-Command git
Ensure-Command robocopy

$scriptRoot = (Resolve-Path $PSScriptRoot).Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..\..")).Path

if ([string]::IsNullOrWhiteSpace($QuickServerRef)) {
    $QuickServerRef = "main"
}

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $repoRoot "dist\KimodoUnityBridge.zip"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("kimodo-package-" + [Guid]::NewGuid().ToString("N"))
$repoArchive = Join-Path $tempRoot "repo.zip"
$stagingRoot = Join-Path $tempRoot "staging"
$quickServerSource = Join-Path $tempRoot "NvlabKimodoQuickServer-source"
$runtimeRoot = Join-Path $stagingRoot "NvlabKimodoQuickServer~"

Write-Host "Repo root: $repoRoot"
Write-Host "QuickServer ref: $QuickServerRef"
Write-Host "Output zip: $OutputZip"

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

    Write-Host "Exporting tracked files from this repository..."
    & git -C $repoRoot archive --format=zip -o $repoArchive HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "git archive failed with exit code $LASTEXITCODE"
    }

    Expand-Archive -LiteralPath $repoArchive -DestinationPath $stagingRoot -Force

    $githubDir = Join-Path $stagingRoot ".github"
    if (Test-Path $githubDir) {
        Remove-Item -LiteralPath $githubDir -Recurse -Force
    }

    Write-Host "Cloning external runtime repo..."
    & git clone --depth 1 --branch $QuickServerRef $QuickServerRepo $quickServerSource
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed with exit code $LASTEXITCODE"
    }

    New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

    Write-Host "Copying runtime repo into NvlabKimodoQuickServer~..."
    & robocopy $quickServerSource $runtimeRoot /MIR /XD ".git" /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE"
    }

    $outputDir = Split-Path -Parent $OutputZip
    if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    if (Test-Path $OutputZip) {
        Remove-Item -LiteralPath $OutputZip -Force
    }

    Write-Host "Creating zip archive..."
    Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $OutputZip -Force

    Write-Host "Package ready: $OutputZip"
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
