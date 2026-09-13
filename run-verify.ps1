# Verificarea diferentiala, in trei pasi care se ruleaza IN ORDINEA ASTA si o singura data fiecare (in
# afara de ultimul, care se reia in reprize).
#
#   .\run-verify.ps1 -Mode index
#       O sesiune de joc care nu cheama nimic: enumereaza metodele prin Il2CppInterop si scrie
#       Mods\verify-game-index.tsv si Mods\verify-game-fields.tsv. Nu poate cadea. Se face O SINGURA DATA
#       si se refoloseste; se reface doar dupa ce se schimba jocul sau normalizarea cheilor.
#
#   .\run-verify.ps1 -Mode plan
#       Fara joc. Citeste indexul de mai sus plus DLL-urile recuperate si scrie lista de lucru:
#       Mods\verify-worklist.tsv, cu argumentele fiecarei metode scrise pe fata, si Mods\verify-blocked.tsv,
#       cu motivul pentru fiecare metoda care nu ajunge in lista.
#
#       Pasul asta cere JIT-ului sa compileze fiecare corp recuperat, si unele corpuri omoara procesul. De
#       aceea se reia singur pana termina: jurnalul verify-prepare-inflight.txt tine minte la care metoda
#       era, iar repornirea urmatoare o trece drept ucigasa si merge mai departe. O moarte costa aici o
#       secunda; aceeasi moarte in joc costa patruzeci.
#
#   .\run-verify.ps1 -Mode run -Restarts 40
#       Sesiuni de joc care NU descopera nimic: citesc lista gata facuta si cheama. Rezultatele se aduna in
#       Mods\verify-results.tsv si nu se rescriu niciodata, deci universul se parcurge in reprize.
param(
    [ValidateSet("index", "plan", "run")]
    [string]$Mode = "index",
    [string]$Game = "C:\Users\Helper\Desktop\StumblePeak",
    [string]$Dlls = "C:\Users\Helper\Desktop\Cpp2IL-Fork\out_w13on",
    [int]$Max = 2000,
    [int]$PerFrame = 25,
    [int]$TimeoutMinutes = 30,
    [int]$Restarts = 0,
    [int]$Seed = 1,
    # Numai pentru -Mode plan: fragmente de nume de assembly, despartite prin virgula. Universul se masoara
    # asa pe felii, fara recompilare: -Only "quantum,PhotonDeterministic" sau -Skip "Rewired".
    [string]$Only = "",
    [string]$Skip = "",
    # Numai pentru -Mode plan: sare peste oracolul de compilare. Lista de lucru va cuprinde atunci si
    # corpuri pe care JIT-ul le refuza, iar refuzurile se vor plati in sesiuni de joc, ca inainte.
    [switch]$NoPrepare,
    # Sterge rezultatele si lista de sarituri. De folosit numai dupa o recompilare a codului recuperat.
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

if ($Fresh) {
    foreach ($stale in @("verify-results.tsv", "verify-skip.txt", "verify-inflight.txt", "verify-summary.txt")) {
        $path = Join-Path $mods $stale
        if (Test-Path $path) { Remove-Item $path -Force }
    }
    Write-Host "-Fresh: rezultatele si lista de sarituri au fost sterse."
}

# ---------------------------------------------------------------------------------------------------
# plan: nu are nevoie de joc deloc
# ---------------------------------------------------------------------------------------------------
if ($Mode -eq "plan") {
    Write-Host "compilez planificatorul..."
    & dotnet build (Join-Path $root "Cpp2IL.VerifyPlan") -c Release
    if ($LASTEXITCODE -ne 0) { Write-Error "compilarea planificatorului a esuat"; exit 3 }

    $tool = Join-Path $root "Cpp2IL.VerifyPlan\bin\Release\Cpp2IL.VerifyPlan.dll"
    if (-not (Test-Path $tool)) { Write-Error "lipseste $tool"; exit 3 }

    $extra = @()
    if ($NoPrepare) { $extra += "--no-prepare" }
    if ($Only) { $extra += "--only"; $extra += $Only }
    if ($Skip) { $extra += "--skip"; $extra += $Skip }

    # Reluat pana termina fara sa moara. Oracolul de compilare chiar omoara procesul pe unele corpuri, iar
    # asta ESTE mecanismul, nu un esec: fiecare moarte adauga o metoda la ce se stie si repornirea continua
    # de la urmatoarea. Fara bucla, un singur corp ucigas ar opri planificarea la jumatate de fiecare data.
    for ($attempt = 1; $attempt -le 200; $attempt++) {
        Write-Host ""
        Write-Host "planificare, incercarea $attempt..."
        & dotnet $tool $mods $Dlls --seed $Seed @extra
        if ($LASTEXITCODE -eq 0) {
            Write-Host ""
            Write-Host "gata. lista de lucru: $mods\verify-worklist.tsv"
            exit 0
        }

        Write-Host "planificatorul a iesit cu $LASTEXITCODE - se reia de unde a ramas."
    }

    Write-Error "planificatorul nu a terminat in 200 de incercari"
    exit 4
}

# ---------------------------------------------------------------------------------------------------
# index si run: amandoua pornesc jocul
# ---------------------------------------------------------------------------------------------------
#
# ATENTIE: Cpp2IL.VerifyMod si Cpp2IL.VerifyCore NU sunt in Cpp2IL.slnx. "dotnet build" pe solutie
# raporteaza succes fara sa le atinga, deci jocul ar porni cu modul VECHI si s-ar trage concluzia ca nimic
# din ce e aici nu functioneaza. Scriptul le compileaza pe nume. Si modul se incarca din Mods, nu din bin,
# deci scriptul copiaza.
Write-Host "compilez modul (pe nume - nu este in solutie)..."
& dotnet build (Join-Path $root "Cpp2IL.VerifyMod") -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "compilarea modului a esuat"; exit 3 }

$out = Join-Path $root "Cpp2IL.VerifyMod\bin\Release"
foreach ($dll in @("Cpp2IL.VerifyMod.dll", "Cpp2IL.VerifyCore.dll")) {
    $from = Join-Path $out $dll
    if (-not (Test-Path $from)) { Write-Error "lipseste $from"; exit 3 }
    Copy-Item $from (Join-Path $mods $dll) -Force
}

if ($Mode -eq "run") {
    $worklist = Join-Path $mods "verify-worklist.tsv"
    if (-not (Test-Path $worklist)) {
        Write-Error "lipseste $worklist - ruleaza intai: .\run-verify.ps1 -Mode plan"
        exit 2
    }
}

$env:CPP2IL_VERIFY_MODE = $Mode
$env:CPP2IL_VERIFY_DLLS = $Dlls
$env:CPP2IL_VERIFY_MAX = "$Max"
$env:CPP2IL_VERIFY_PER_FRAME = "$PerFrame"
$env:CPP2IL_VERIFY_SEED_RECEIVERS = "1"

$summary = Join-Path $mods "verify-summary.txt"

for ($attempt = 0; $attempt -le $Restarts; $attempt++) {
    if (Test-Path $summary) { Remove-Item $summary -Force }
    if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }

    Write-Host ""
    Write-Host "pornirea $($attempt + 1) / $($Restarts + 1), mod=$Mode..."

    $before = @(Get-Process -Name "StumblePeak" -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Start-Process -FilePath $exe -WorkingDirectory $Game | Out-Null

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $finished = $false

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5

        if (Test-Path $summary) { $finished = $true; break }

        $alive = @(Get-Process -Name "StumblePeak" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
        if ($alive.Count -eq 0) {
            Write-Host "  jocul a murit - metoda vinovata este in verify-inflight.txt si va fi sarita."
            break
        }
    }

    Get-Process -Name "StumblePeak" -ErrorAction SilentlyContinue |
        Where-Object { $before -notcontains $_.Id } |
        Stop-Process -Force -ErrorAction SilentlyContinue

    if ($finished) {
        Write-Host ""
        Get-Content $summary
        if ($Mode -eq "index") { break }
    }
}

Write-Host ""
Write-Host "fisierele sunt in $mods"
