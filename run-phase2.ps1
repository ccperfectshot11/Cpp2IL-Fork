# Runs Phase 2 end to end: starts the game with the sweep set to automatic, waits for the mod to say it
# is done, and reports what came back. The mod quits the game itself in this mode, so nothing is left
# running afterwards.
#
#   .\run-phase2.ps1 [-Game <dir>] [-TimeoutMinutes 30]
param(
    [string]$Game = "C:\Users\Helper\Desktop\StumblePeak",
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
$mods = Join-Path $Game "Mods"
$phase1 = Join-Path $mods "verifycheck-phase1.json"
$phase2 = Join-Path $mods "verifycheck-phase2.json"
$log = Join-Path $Game "MelonLoader\Latest.log"
$exe = Join-Path $Game "StumblePeak.exe"

foreach ($required in @($exe, $phase1, (Join-Path $mods "Cpp2IL.VerifyMod.dll"))) {
    if (-not (Test-Path $required)) { Write-Error "missing: $required"; exit 2 }
}

# A stale output would be reported as this run's result.
if (Test-Path $phase2) { Remove-Item $phase2 -Force }
if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }

Write-Host "pornesc jocul cu maturarea automata..."
$env:CPP2IL_VERIFY_AUTO = "1"
# Start-Process -PassThru comes back empty for this executable - the launcher re-execs - so the process
# is found by name afterwards instead.
$before = @(Get-Process -Name "StumblePeak" -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
Start-Process -FilePath $exe -WorkingDirectory $Game | Out-Null

$gameId = $null
foreach ($i in 1..30) {
    Start-Sleep -Seconds 1
    $now = @(Get-Process -Name "StumblePeak" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
    if ($now.Count -gt 0) { $gameId = $now[0].Id; break }
}

if (-not $gameId) { Write-Host "nu am gasit procesul jocului dupa pornire." -ForegroundColor Yellow }

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$lastSize = 0

while ((Get-Date) -lt $deadline) {
    if (Test-Path $phase2) { break }

    # The game exiting before the file appears means the sweep died with it - there is nothing to wait for.
    if ($gameId -and -not (Get-Process -Id $gameId -ErrorAction SilentlyContinue) -and -not (Test-Path $phase2)) {
        Write-Host "jocul s-a inchis fara sa scrie rezultatul." -ForegroundColor Yellow
        break
    }

    if (Test-Path $log) {
        $size = (Get-Item $log).Length
        if ($size -ne $lastSize) {
            $lastSize = $size
            Get-Content $log -Tail 3 | Where-Object { $_ -match "VerifyMod|Phase 1|Indexed|fuzzed|Resolved|VERIFY_DONE" } | ForEach-Object { Write-Host "  $_" }
        }
    }

    Start-Sleep -Seconds 5
}

$still = if ($gameId) { Get-Process -Id $gameId -ErrorAction SilentlyContinue } else { $null }
if ($still) { Write-Host "inchid jocul..."; $still.Kill(); $still.WaitForExit(10000) }

Write-Host ""
if (Test-Path $phase2) {
    Write-Host "gata: $phase2" -ForegroundColor Green
    Write-Host ""
    Write-Host "--- ultimele linii din log ---"
    Get-Content $log -Tail 25 | Where-Object { $_ -match "VerifyMod|Resolved|fuzzed|Indexed|Error|Exception" }
    Write-Host ""
    Write-Host "--- comparatia celor doua faze ---"
    & "$PSScriptRoot\Cpp2IL.VerifyCheck\bin\Release\net10.0\Cpp2IL.VerifyCheck.exe" --compare $phase1 $phase2
} else {
    Write-Host "nu s-a scris niciun rezultat. ultimele linii din log:" -ForegroundColor Red
    if (Test-Path $log) { Get-Content $log -Tail 40 } else { Write-Host "(nu exista log)" }
    exit 1
}
