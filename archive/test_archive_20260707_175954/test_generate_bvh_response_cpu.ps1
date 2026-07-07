param(
    [string]$ModelsRoot = "C:\nvlab\models~",
    [string]$ModelName = "Kimodo-SOMA-RP-v1",
    [double]$Duration = 5.0,
    [int]$DiffusionSteps = 100
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$runBat = Join-Path $root "run_server.bat"
$portFile = Join-Path $root "serverport"
$logDir = Join-Path $root "log"
$recycleDir = Join-Path $root "archive\recycle"
$runLog = Join-Path $logDir "test_generate_bvh_response_cpu.log"
$responseFile = Join-Path $logDir "test_generate_bvh_response_cpu.response.json"

if (-not (Test-Path -LiteralPath $runBat)) { throw "Missing run_server.bat: $runBat" }
if (-not (Test-Path -LiteralPath $ModelsRoot)) { throw "Models root not found: $ModelsRoot" }

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
New-Item -ItemType Directory -Path $recycleDir -Force | Out-Null
if (Test-Path -LiteralPath $portFile) {
    $bak = Join-Path $recycleDir ("serverport.test.bvh.cpu.{0}.{1}" -f (Get-Date -Format "yyyyMMdd_HHmmss"), (Get-Random))
    Move-Item -LiteralPath $portFile -Destination $bak -Force
}

$env:KIMODO_SETUP_DEVICE = "cpu"
$env:KIMODO_BRIDGE_OUTPUT_FORMAT = "bvh"
$env:KIMODO_BRIDGE_BVH_STANDARD_TPOSE = "1"

$runCmd = "call `"$runBat`" --model `"$ModelName`" --device cpu --models-root `"$ModelsRoot`" --output file --log `"$runLog`""
$proc = Start-Process -FilePath "cmd.exe" -ArgumentList @("/d", "/c", $runCmd) -WorkingDirectory $root -WindowStyle Normal -PassThru

try {
    $waitPortSeconds = 1200
    $waited = 0
    while (-not (Test-Path -LiteralPath $portFile)) {
        Start-Sleep -Seconds 1
        $waited++
        if ($waited -ge $waitPortSeconds) {
            throw "serverport not found within ${waitPortSeconds}s"
        }
    }

    $line = (Get-Content -LiteralPath $portFile -TotalCount 1).Trim()
    if ($line -notmatch "^(?<h>[^:]+):(?<p>\d+)$") {
        throw "invalid serverport content: $line"
    }
    $svrHost = $matches.h
    $svrPort = [int]$matches.p

    $client = New-Object System.Net.Sockets.TcpClient
    $iar = $client.BeginConnect($svrHost, $svrPort, $null, $null)
    if (-not $iar.AsyncWaitHandle.WaitOne(120000)) { throw "connect timeout" }
    $client.EndConnect($iar)
    $stream = $client.GetStream()
    $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)), 1024, $true)
    $writer.AutoFlush = $true
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $false, 1024, $true)

    function Send-Json([string]$json) {
        $writer.WriteLine($json)
    }

    function Read-Line-WithTimeout([int]$timeoutSec) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
            if ($stream.DataAvailable) {
                $ln = $reader.ReadLine()
                if (-not [string]::IsNullOrWhiteSpace($ln)) { return $ln }
            } else {
                Start-Sleep -Milliseconds 100
            }
        }
        return $null
    }

    function Wait-ReadyByPing([int]$timeoutSec) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
            Send-Json '{"cmd":"ping"}'
            $slice = [Diagnostics.Stopwatch]::StartNew()
            while ($slice.Elapsed.TotalSeconds -lt 3) {
                $ln = Read-Line-WithTimeout -timeoutSec 1
                if ($null -eq $ln) { continue }
                if ($ln -match '"status"\s*:\s*"pong"') { return $true }
                if ($ln -match '"status"\s*:\s*"error"') { return $false }
                if ($ln -match '"status"\s*:\s*"loading"') { break }
            }
            Start-Sleep -Seconds 2
        }
        return $false
    }

    function Read-UntilDone([int]$timeoutSec) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $last = ""
        while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
            $ln = Read-Line-WithTimeout -timeoutSec 2
            if ($null -eq $ln) { continue }
            $last = $ln
            if ($ln -match '"status"\s*:\s*"done"') { return @{ ok = $true; last = $ln } }
            if ($ln -match '"status"\s*:\s*"error"') { return @{ ok = $false; last = $ln } }
        }
        return @{ ok = $false; last = $last }
    }

    if (-not (Wait-ReadyByPing -timeoutSec 1800)) {
        throw "service not ready by ping timeout"
    }

    $payload = @{
        cmd = "generate"
        prompt = "A person walks forward."
        duration = $Duration
        diffusion_steps = $DiffusionSteps
        seed = 123
    } | ConvertTo-Json -Compress

    Send-Json $payload
    $ret = Read-UntilDone -timeoutSec 7200
    if (-not $ret.ok) {
        throw "generate did not complete successfully: $($ret.last)"
    }

    Set-Content -LiteralPath $responseFile -Value $ret.last -Encoding UTF8
    $obj = $ret.last | ConvertFrom-Json -ErrorAction Stop

    if ($null -eq $obj.motion_bvh -or [string]::IsNullOrWhiteSpace([string]$obj.motion_bvh)) {
        throw "response does not contain motion_bvh"
    }
    if ($null -ne $obj.motion_json_compact) {
        throw "response should not contain motion_json_compact when KIMODO_BRIDGE_OUTPUT_FORMAT=bvh"
    }
    if ([string]$obj.output_format -ne "bvh") {
        throw "response output_format is not 'bvh'"
    }
    if (-not ([string]$obj.motion_bvh).StartsWith("HIERARCHY")) {
        throw "motion_bvh does not look like BVH text"
    }

    Write-Host ("[TEST_BVH_CPU] host={0} port={1}" -f $svrHost, $svrPort)
    Write-Host ("[TEST_BVH_CPU] response_file={0}" -f $responseFile)
    Write-Host "[TEST_BVH_CPU] PASS"

    try { Send-Json '{"cmd":"quit"}' } catch {}
    Start-Sleep -Seconds 1
    try { $writer.Dispose(); $reader.Dispose(); $stream.Dispose(); $client.Close() } catch {}
}
finally {
    Remove-Item Env:\KIMODO_BRIDGE_OUTPUT_FORMAT -ErrorAction SilentlyContinue
    Remove-Item Env:\KIMODO_BRIDGE_BVH_STANDARD_TPOSE -ErrorAction SilentlyContinue
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
}
