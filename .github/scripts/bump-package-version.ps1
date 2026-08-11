[CmdletBinding()]
param(
    [string]$Path = "package.json"
)

$ErrorActionPreference = "Stop"

$resolvedPath = (Resolve-Path $Path).Path
$raw = [System.IO.File]::ReadAllText($resolvedPath)

$match = [regex]::Match($raw, '"version"\s*:\s*"(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)"')
if (-not $match.Success) {
    throw "Could not find a semantic version in $resolvedPath"
}

$major = [int]$match.Groups["major"].Value
$minor = [int]$match.Groups["minor"].Value
$patch = [int]$match.Groups["patch"].Value + 1
$nextVersion = "$major.$minor.$patch"

$updated = [regex]::Replace(
    $raw,
    '"version"\s*:\s*"\d+\.\d+\.\d+"',
    '"version": "' + $nextVersion + '"',
    1
)

[System.IO.File]::WriteAllText($resolvedPath, $updated, [System.Text.UTF8Encoding]::new($false))
Write-Output $nextVersion
