# Faza 3: codul recuperat rulat pe starea REALA a jocului, la punctele reale de apel.
#
# Fata de faza 2, care cheama metodele jocului cu argumente fabricate si compara doua hash-uri, aici se
# pune un carlig Harmony pe metoda reala, se asteapta ca JOCUL sa o cheme singur, si abia atunci se
# cheama si metoda recuperata - cu aceleasi argumente si cu starea copiata din obiectul viu. Se compara
# valoarea intoarsa, bit cu bit, cu cea a metodei adevarate.
#
# ATENTIE, doua lucruri pe care un build de solutie le ascunde:
#   1. Cpp2IL.VerifyMod, Cpp2IL.VerifyCore si Cpp2IL.VerifyCheck NU sunt in Cpp2IL.slnx. "dotnet build"
#      pe solutie raporteaza succes fara sa le atinga, deci jocul ar porni cu modul VECHI si s-ar trage
#      concluzia ca nimic din ce e aici nu functioneaza. Scriptul le compileaza pe nume.
#   2. Modul se incarca din Mods, nu din bin. Scriptul copiaza.
#
#   .\run-phase3.ps1 -Mode plan         scrie doar lista de lucru si se opreste
#   .\run-phase3.ps1 -Mode shadow       masoara: jocul ramane neatins, se compara doar raspunsurile
#   .\run-phase3.ps1 -Mode substitute   pe deasupra, INLOCUIESTE raspunsul jocului dupa N potriviri
#
# Despre -Batch: implicit 1, adica o metoda pe rand, fiindca asa jurnalul .inflight numeste exact
# vinovatul cand procesul moare. In modul shadow jocul nu este atins deloc - raspunsul lui ramane al lui -
# deci acolo o transa mai mare este alegerea rezonabila pentru prima masuratoare (-Batch 25), cu pretul ca
# o moarte de proces cere o repornire cu -Batch 1 ca sa iasa la iveala care metoda a fost. In modul
# substitute lasa-l pe 1.
#
# Despre -Max: rezultatele se aduna in subst-results.tsv si o sesiune noua sare peste ce s-a masurat deja,
# deci lista se poate parcurge in reprize scurte. Cu -Batch 1 si -Dwell 30 o lista de 1.640 de metode ar
# cere treisprezece ore intr-o singura sesiune.
param(
    [ValidateSet("plan", "shadow", "substitute")]
    [string]$Mode = "plan",
    [string]$Game = "C:\Users\Helper\Desktop\StumblePeak",
    [string]$Dlls = "C:\Users\Helper\Desktop\Cpp2IL-Fork\out_w13on",
    [string]$Filter = "Assembly-CSharp",
    [string]$Tier = "01",
    [int]$Batch = 1,
    [int]$Samples = 64,
    [int]$Dwell = 30,
    [int]$Promote = 16,
    # Cate metode se incearca intr-o sesiune. Rezultatele se aduna pe disc si sesiunea urmatoare sare peste
    # ce s-a masurat deja, deci masuratoarea se face in reprize scurte in loc de una singura interminabila.
    [int]$Max = 0,
    [int]$TimeoutMinutes = 30,
    # Jurnalul este facut ca sa supravietuiasca mortii procesului: la repornire, metoda din .inflight
    # ajunge in .skip si rularea merge mai departe de unde a ramas. De aceea scriptul reporneste singur.
    [int]$Restarts = 0,
    [switch]$Fresh
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$mods = Join-Path $Game "Mods"
$log = Join-Path $Game "MelonLoader\Latest.log"
$exe = Join-Path $Game "StumblePeak.exe"

foreach ($required in @($exe, $Dlls)) {
    if (-not (Test-Path $required)) { Write-Error "lipseste: $required"; exit 2 }
}

Write-Host "compilez modul (pe nume - nu este in solutie)..."
& dotnet build (Join-Path $root "Cpp2IL.VerifyMod") -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "compilarea modului a esuat"; exit 3 }

$out = Join-Path $root "Cpp2IL.VerifyMod\bin\Release"
foreach ($dll in @("Cpp2IL.VerifyMod.dll", "Cpp2IL.VerifyCore.dll")) {
    $from = Join-Path $out $dll
    if (-not (Test-Path $from)) { Write-Error "lipseste $from"; exit 3 }
    Copy-Item $from (Join-Path $mods $dll) -Force
}

# Lista de lucru se rescrie numai in modul plan; in rest se citeste asa cum este, ca sa se poata taia
# linii din ea cu mana fara ca rularea urmatoare sa le puna la loc.
if ($Fresh) {
    foreach ($stale in @("subst-worklist.txt", "subst-results.tsv", "subst-skip.txt", "subst-inflight.txt", "subst-summary.txt", "subst-bisect.txt")) {
        $path = Join-Path $mods $stale
        if (Test-Path $path) { Remove-Item $path -Force }
    }
}

$env:CPP2IL_SUBST = "1"
$env:CPP2IL_SUBST_DLLS = $Dlls
$env:CPP2IL_SUBST_FILTER = $Filter
$env:CPP2IL_SUBST_TIER = $Tier
$env:CPP2IL_SUBST_BATCH = "$Batch"
$env:CPP2IL_SUBST_SAMPLES = "$Samples"
$env:CPP2IL_SUBST_DWELL = "$Dwell"
$env:CPP2IL_SUBST_PROMOTE = "$Promote"
$env:CPP2IL_SUBST_MAX = "$Max"
$env:CPP2IL_SUBST_MODE = if ($Mode -eq "substitute") { "substitute" } else { "shadow" }
$env:CPP2IL_SUBST_PLAN = if ($Mode -eq "plan") { "1" } else { "0" }
# Modul isi inchide jocul singur cand termina, la fel ca faza 2.
$env:CPP2IL_VERIFY_AUTO = "1"

$summary = Join-Path $mods "subst-summary.txt"

for ($attempt = 0; $attempt -le $Restarts; $attempt++) {
    if (Test-Path $summary) { Remove-Item $summary -Force }
    if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }

    Write-Host ""
    Write-Host "pornirea $($attempt + 1) / $($Restarts + 1), mod=$Mode..."

    $before = @(Get-Process -Name "StumblePeak" -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Start-Process -FilePath $exe -WorkingDirectory $Game | Out-Null

    $gameId = $null
    foreach ($i in 1..30) {
        Start-Sleep -Seconds 1
        $now = @(Get-Process -Name "StumblePeak" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
        if ($now.Count -gt 0) { $gameId = $now[0].Id; break }
    }

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $lastSize = 0

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $summary) { break }
        if ($gameId -and -not (Get-Process -Id $gameId -ErrorAction SilentlyContinue)) {
            Write-Host "jocul s-a inchis." -ForegroundColor Yellow
            break
        }

        if (Test-Path $log) {
            $size = (Get-Item $log).Length
            if ($size -ne $lastSize) {
                $lastSize = $size
                Get-Content $log -Tail 4 | Where-Object { $_ -match "Faza 3|Plan:|Lista de lucru|treapta|respinse|incercate|SUBST_DONE|ATENTIE" } | ForEach-Object { Write-Host "  $_" }
            }
        }

        Start-Sleep -Seconds 5
    }

    $still = if ($gameId) { Get-Process -Id $gameId -ErrorAction SilentlyContinue } else { $null }
    if ($still) { Write-Host "inchid jocul..."; $still.Kill(); $still.WaitForExit(10000) }

    if (Test-Path $summary) { break }

    # Fara rezumat inseamna ca procesul a murit inainte sa termine. Jurnalul .inflight numeste vinovatul,
    # iar pornirea urmatoare il sare - de aceea repornirea este utila, nu incapatanata.
    $inflight = Join-Path $mods "subst-inflight.txt"
    if (Test-Path $inflight) {
        Write-Host "a murit in:" -ForegroundColor Yellow
        Get-Content $inflight | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
}

Write-Host ""
if (Test-Path $summary) {
    Get-Content $summary
} else {
    Write-Host "nu s-a scris niciun rezumat. ultimele linii din log:" -ForegroundColor Red
    if (Test-Path $log) { Get-Content $log -Tail 40 } else { Write-Host "(nu exista log)" }
    exit 1
}

$results = Join-Path $mods "subst-results.tsv"
if (Test-Path $results) {
    Write-Host ""
    Write-Host "--- primele dezacorduri, daca exista ---"
    Get-Content $results | Where-Object { $_ -match "DISAGREES" } | Select-Object -First 15
}
