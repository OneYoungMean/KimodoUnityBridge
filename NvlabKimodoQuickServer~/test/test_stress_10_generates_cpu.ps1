param(
    [string]$ModelsRoot = "C:\nvlab\models~",
    [string]$ModelName = "Kimodo-SOMA-RP-v1",
    [int]$Count = 10,
    [double]$Duration = 5.0,
    [int]$DiffusionSteps = 100
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$runBat = Join-Path $root "run_server.bat"
$portFile = Join-Path $root "serverport"
$logDir = Join-Path $root "log"
$recycleDir = Join-Path $root "archive\recycle"
$runLog = Join-Path $logDir "stress_run_server_cpu.log"
$outputDir = Join-Path $logDir ("stress_outputs_cpu_" + (Get-Date -Format "yyyyMMdd_HHmmss"))

if (-not (Test-Path -LiteralPath $runBat)) { throw "Missing run_server.bat: $runBat" }
if (-not (Test-Path -LiteralPath $ModelsRoot)) { throw "Models root not found: $ModelsRoot" }

if (-not $env:UV_CACHE_DIR) {
    $env:UV_CACHE_DIR = Join-Path $root ".uv-cache"
}
if (-not $env:KIMODO_VENV_PATH) {
    $env:KIMODO_VENV_PATH = Join-Path $root "kimodo\.venv"
}

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
New-Item -ItemType Directory -Path $recycleDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
if (Test-Path -LiteralPath $portFile) {
    $bak = Join-Path $recycleDir ("serverport.stress.cpu.{0}.{1}" -f (Get-Date -Format "yyyyMMdd_HHmmss"), (Get-Random))
    Move-Item -LiteralPath $portFile -Destination $bak -Force
}

$env:KIMODO_SETUP_DEVICE = "cpu"
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

    $results = @()
    $totalSw = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 1; $i -le $Count; $i++) {
        $prompt = "stress cpu " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff") + " #$i"
        $payload = @{
            cmd = "generate"
            prompt = $prompt
            duration = $Duration
            diffusion_steps = $DiffusionSteps
            seed = (100 + $i)
        } | ConvertTo-Json -Compress

        $sw = [Diagnostics.Stopwatch]::StartNew()
        Send-Json $payload
        $ret = Read-UntilDone -timeoutSec 7200
        $sw.Stop()
        $saved = ""
        if (-not [string]::IsNullOrWhiteSpace($ret.last)) {
            $responseFile = Join-Path $outputDir ("case_{0:D2}_response.json" -f $i)
            Set-Content -LiteralPath $responseFile -Value $ret.last -Encoding UTF8
            $saved = $responseFile
            try {
                $obj = $ret.last | ConvertFrom-Json -ErrorAction Stop
                if ($null -ne $obj -and $null -ne $obj.motion_json_compact) {
                    $motionFile = Join-Path $outputDir ("case_{0:D2}_motion.json" -f $i)
                    Set-Content -LiteralPath $motionFile -Value ([string]$obj.motion_json_compact) -Encoding UTF8
                    $saved = $motionFile
                }
            } catch {
                # keep response file only
            }
        }
        $results += [pscustomobject]@{
            Index = $i
            Ok = $ret.ok
            Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
            Last = $ret.last
            Saved = $saved
        }
        if (-not $ret.ok) { break }
    }
    $totalSw.Stop()

    try { Send-Json '{"cmd":"quit"}' } catch {}
    Start-Sleep -Seconds 1
    try { $writer.Dispose(); $reader.Dispose(); $stream.Dispose(); $client.Close() } catch {}

    $okCount = @($results | Where-Object { $_.Ok }).Count
    Write-Host ("[STRESS_CPU] host={0} port={1}" -f $svrHost, $svrPort)
    Write-Host ("[STRESS_CPU] total_sec={0} ok_count={1}" -f [math]::Round($totalSw.Elapsed.TotalSeconds, 2), $okCount)
    Write-Host ("[STRESS_CPU] output_dir={0}" -f $outputDir)
    foreach ($r in $results) {
        Write-Host ("[CASE] #{0} ok={1} sec={2} saved={3}" -f $r.Index, $r.Ok, $r.Seconds, $r.Saved)
    }

    if ($okCount -lt $Count) {
        throw "stress test failed before completing all cases"
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
}
