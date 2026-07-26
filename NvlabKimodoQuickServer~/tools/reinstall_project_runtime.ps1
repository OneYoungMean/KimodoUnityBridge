param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot
)

$ErrorActionPreference = "Stop"
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectRootPath = [IO.Path]::GetFullPath($ProjectRoot)
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $projectRootPath "NvlabKimodoQuickServer~"))

if (-not (Test-Path -LiteralPath (Join-Path $projectRootPath "Assets") -PathType Container) -or
    -not (Test-Path -LiteralPath (Join-Path $projectRootPath "ProjectSettings") -PathType Container)) {
    throw "Not a Unity project root: $projectRootPath"
}
if ([IO.Path]::GetFileName($runtimeRoot) -ne "NvlabKimodoQuickServer~" -or
    [IO.Path]::GetDirectoryName($runtimeRoot) -ne $projectRootPath) {
    throw "Refusing to reinstall unexpected runtime path: $runtimeRoot"
}

$serverPort = Join-Path $runtimeRoot "serverport"
if (Test-Path -LiteralPath $serverPort -PathType Leaf) {
    $endpoint = @{}
    foreach ($line in Get-Content -LiteralPath $serverPort) {
        if ($line -match '^([^=]+)=(.*)$') {
            $endpoint[$matches[1].Trim().ToLowerInvariant()] = $matches[2].Trim()
        } elseif ($line -match '^([^:]+):(\d+)$') {
            $endpoint.host = $matches[1]
            $endpoint.port = $matches[2]
        }
    }
    if ($endpoint.host -notin @("127.0.0.1", "localhost", "::1")) {
        throw "Refusing to stop non-local QuickServer: $($endpoint.host)"
    }

    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.Connect($endpoint.host, [int]$endpoint.port)
        $stream = $client.GetStream()
        $request = [Text.Encoding]::UTF8.GetBytes("{`"cmd`":`"session.close`"}`n")
        $stream.Write($request, 0, $request.Length)
        $stream.Flush()
    } finally {
        $client.Dispose()
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath $serverPort) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-Path -LiteralPath $serverPort) {
        throw "Timed out waiting for QuickServer to stop: $serverPort"
    }
}

New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
$backupRoot = $runtimeRoot + ".reinstall-backup-" + [DateTime]::Now.ToString("yyyyMMdd-HHmmss")
$oldEntries = @(Get-ChildItem -LiteralPath $runtimeRoot -Force | Where-Object Name -ne "models")
if ($oldEntries.Count -gt 0) {
    if (Test-Path -LiteralPath $backupRoot) {
        throw "Refusing to overwrite reinstall backup: $backupRoot"
    }
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    foreach ($entry in $oldEntries) {
        Move-Item -LiteralPath $entry.FullName -Destination $backupRoot
    }
}

$trackedPrefix = "NvlabKimodoQuickServer~/"
$packageRoot = [IO.Path]::GetDirectoryName($sourceRoot)
$trackedFiles = & git -C $packageRoot ls-files --cached --others --exclude-standard -- $trackedPrefix
if ($LASTEXITCODE -ne 0 -or -not $trackedFiles) {
    throw "Failed to enumerate packaged QuickServer files."
}
foreach ($tracked in $trackedFiles) {
    $relativePath = $tracked.Substring($trackedPrefix.Length).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $source = Join-Path $sourceRoot $relativePath
    $destination = Join-Path $runtimeRoot $relativePath
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

Write-Output "Reinstalled Kimodo runtime from '$sourceRoot' to '$runtimeRoot' (models preserved; old files archived at '$backupRoot')."
