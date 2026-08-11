[CmdletBinding()]
param(
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
Ensure-Command tar

function Remove-PathIfExists {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer) {
        [System.IO.Directory]::Delete($item.FullName, $true)
        return
    }

    [System.IO.File]::Delete($item.FullName)
}

$scriptRoot = (Resolve-Path $PSScriptRoot).Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..\..")).Path

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $repoRoot "dist\KimodoUnityBridge.zip"
}

$tempRoot = Join-Path ([System.IO.Path]::GetPathRoot($repoRoot)) ("kpack-" + [Guid]::NewGuid().ToString("N"))
$repoArchive = Join-Path $tempRoot "repo.zip"
$stagingRoot = Join-Path $tempRoot "staging"

Write-Host "Repo root: $repoRoot"
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
        Remove-PathIfExists -Path $githubDir
    }

    $outputDir = Split-Path -Parent $OutputZip
    if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    if (Test-Path $OutputZip) {
        Remove-PathIfExists -Path $OutputZip
    }

    Write-Host "Creating zip archive..."
    & tar -a -cf $OutputZip -C $stagingRoot .
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed with exit code $LASTEXITCODE"
    }

    Write-Host "Package ready: $OutputZip"
}
finally {
    try {
        Remove-PathIfExists -Path $tempRoot
    }
    catch {
        Write-Warning ("Failed to remove temporary path: " + $tempRoot + " - " + $_.Exception.Message)
    }
}
