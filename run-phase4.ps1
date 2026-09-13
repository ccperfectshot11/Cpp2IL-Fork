# Faza 4: maturare ACTIVA. Nu se mai asteapta ca jocul sa cheme o metoda - o chemam noi, pe amandoua
# partile, in acelasi proces, cu exact aceleasi argumente.
#
# Fata de fazele de dinainte:
#   faza 2 cheama metodele in DOUA procese si compara hash-uri, deci merge numai acolo unde argumentele se
#          pot cladi identic de ambele parti - 1.232 de metode verificate din 36.546, adica 3,4%;
#   faza 3 pune carlige si asteapta ca jocul sa cheme metoda - argumente adevarate, dar numai daca jocul
#          binevoieste: intr-o sesiune, 8 verificate si 22 niciodata chemate;
#   faza 4 nu asteapta pe nimeni si nu trece nicio granita de proces.
#
# ATENTIE, doua lucruri pe care un build de solutie le ascunde:
#   1. Cpp2IL.VerifyMod, Cpp2IL.VerifyCore si Cpp2IL.VerifyCheck NU sunt in Cpp2IL.slnx. "dotnet build" pe
#      solutie raporteaza succes fara sa le atinga, deci jocul ar porni cu modul VECHI si s-ar trage
#      concluzia ca nimic din ce e aici nu functioneaza. Scriptul le compileaza pe nume.
#   2. Modul se incarca din Mods, nu din bin. Scriptul copiaza.
#
# Ordinea in care merita rulat, si de ce anume in ordinea asta:
#
#   .\run-phase4.ps1 -Mode dump
#       Numai metadate, niciun apel. Scrie Mods\active-requirements.tsv: un rand pentru fiecare metoda din
#       univers, cu ce ii trebuie ca sa poata fi chemata si - cand nu poate - cu motivul exact. Este ieftin,
#       nu poate cadea, si este artefactul care ramane chiar daca restul nu apuca sa ruleze.
#
#   .\run-phase4.ps1 -Mode static -Restarts 5
#       Numai metodele STATICE. Nu au receptor, deci nu pot cadea din cauza unui camp nul citit dintr-un
#       obiect fabricat pe zero. Aici se aduna rezultatele ieftine si tari.
#
#   .\run-phase4.ps1 -Mode full -Restarts 40
#       Si metodele de instanta, cu receptor fabricat. ASTEAPTA-TE LA MULTE MORTI DE PROCES: IL2CPP compilat
#       pentru livrare nu mai emite verificari de nul, deci o metoda care dereferentiaza un camp zero nu da
#       NullReferenceException, ci o violare de acces care omoara procesul si pe care niciun try nu o prinde.
#       De asta exista jurnalul: metoda din active-inflight.txt ajunge in active-skip.txt la repornire, este
#       inregistrata ca CRASHED - care ESTE un rezultat despre ea - si rularea merge mai departe. Un -Restarts
#       mare nu este incapatanare, este chiar mecanismul.
#
# Rezultatele se aduna in Mods\active-results.tsv si nu se rescriu niciodata: o sesiune noua sare peste ce
# s-a masurat deja, deci universul se parcurge in reprize de cate douazeci de minute.
#
# TOCMAI DE ACEEA, cand se schimba felul in care se fabrica intrarile, trebuie -Fresh: o metoda masurata
# deja cu argumente null NU va fi chemata din nou cu sir, fiindca cheia ei este in active-results.tsv.
# Fara -Fresh, o imbunatatire a intrarilor se vede numai pe metodele nemasurate inca, si pare mult mai mica
# decat este.
param(
    [ValidateSet("dump", "static", "full")]
    [string]$Mode = "dump",
    [string]$Game = "C:\Users\Helper\Desktop\StumblePeak",
    [string]$Dlls = "C:\Users\Helper\Desktop\Cpp2IL-Fork\out_w13on",
    # Gol inseamna "tot universul". Pune fragmente de nume de assembly ca sa masori pe bucati:
    # -Filter "quantum,PhotonDeterministic" sau -Filter "Assembly-CSharp".
    [string]$Filter = "",
    # Rewired_Core si Rewired_Windows sunt 14.169 de metode, adica aproape un sfert din univers, si sunt o
    # biblioteca comerciala de input luata de-a gata. Au fost lasate INAUNTRU fiindca asa s-a cerut - cand nu
    # e limpede de care parte cade un assembly, se masoara - dar se scot de aici fara recompilare.
    [string]$Skip = "",
    # Cate metode intr-o sesiune. 0 = toate, ceea ce pentru un univers de ~59.000 inseamna ore.
    [int]$Max = 2000,
    [int]$PerFrame = 25,
    [int]$TimeoutMinutes = 30,
    [int]$Restarts = 0,
    # Implicit, o cadere a procesului OPRESTE maturarea. Repornirea automata dupa crash a insemnat, la
    # prima incercare, jocul deschizandu-se si murind de zeci de ori la rand pentru ~110 metode masurate
    # de fiecare data - adica aproape tot timpul petrecut in pornit jocul, nu in masurat. Cat timp caderea
    # fatala nu e reparata, repornirea trebuie ceruta pe fata.
    [switch]$RestartOnCrash,
    [switch]$Redump,
    # Intoarce maturarea la felul de dinainte, pentru cand trebuie aflat daca o schimbare de cifre vine de
    # la intrarile mai bune sau de la altceva. -NoRefArgs pune inapoi null in parametrii de tip clasa;
    # -NoReceiverFields lasa receptorul pe zero, fara campuri scrise. Rulate una cate una, ele SEPARA
    # efectul celor doua schimbari.
    [switch]$NoRefArgs,
    [switch]$NoReceiverFields,
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

# -Fresh sterge si lista de sarituri. De folosit numai dupa o recompilare a codului recuperat: altfel
# metodele care au omorat procesul in sesiunile trecute sunt incercate din nou, una cate una, si sesiunea
# se duce pe repornit.
if ($Fresh) {
    foreach ($stale in @("active-results.tsv", "active-skip.txt", "active-inflight.txt", "active-summary.txt", "active-requirements.tsv", "active-excluded-assemblies.txt")) {
        $path = Join-Path $mods $stale
        if (Test-Path $path) { Remove-Item $path -Force }
    }
}

$env:CPP2IL_ACTIVE = "1"
$env:CPP2IL_ACTIVE_DLLS = $Dlls
$env:CPP2IL_ACTIVE_FILTER = $Filter
$env:CPP2IL_ACTIVE_SKIP = $Skip
$env:CPP2IL_ACTIVE_MAX = "$Max"
$env:CPP2IL_ACTIVE_PER_FRAME = "$PerFrame"
$env:CPP2IL_ACTIVE_DUMP_ONLY = if ($Mode -eq "dump") { "1" } else { "0" }
$env:CPP2IL_ACTIVE_RECEIVERS = if ($Mode -eq "static") { "0" } else { "1" }
$env:CPP2IL_ACTIVE_REDUMP = if ($Redump) { "1" } else { "0" }
# Intrarile de calitate: siruri si tablouri adevarate in loc de null, si campuri scrise in receptor in loc
# de zero. Pornite implicit - fara ele randurile ies etichetate "null-reference" si "uninitialised-receiver",
# adica tocmai galetile in care masuratoarea a aratat ca nu se afla nimic.
$env:CPP2IL_ACTIVE_REF_ARGS = if ($NoRefArgs) { "0" } else { "1" }
$env:CPP2IL_ACTIVE_RECEIVER_FIELDS = if ($NoReceiverFields) { "0" } else { "1" }
# Lista de siguranta ramane PORNITA. Se cheama metode, nu se doar observa, deci plati, cont, telemetrie si
# retea stau pe dinafara. Nu o opri decat cu jocul deconectat de la servere, si atunci pe fata.
$env:CPP2IL_ACTIVE_SAFETY = "1"
# Modul isi inchide jocul singur cand termina, la fel ca fazele 2 si 3.
$env:CPP2IL_VERIFY_AUTO = "1"

$summary = Join-Path $mods "active-summary.txt"
$inflight = Join-Path $mods "active-inflight.txt"

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
                Get-Content $log -Tail 4 | Where-Object { $_ -match "Faza 4|Dump|Univers|incercate|ACTIVE_DONE|ATENTIE|a murit" } | ForEach-Object { Write-Host "  $_" }
            }
        }

        Start-Sleep -Seconds 5
    }

    $still = if ($gameId) { Get-Process -Id $gameId -ErrorAction SilentlyContinue } else { $null }
    if ($still) { Write-Host "inchid jocul..."; $still.Kill(); $still.WaitForExit(10000) }

    if (Test-Path $summary) { break }

    # Fara rezumat inseamna ca procesul a murit inainte sa termine. Jurnalul numeste exact o metoda - aici,
    # spre deosebire de faza 3, se cheama cate una pe rand, deci nu exista mai multi suspecti - iar pornirea
    # urmatoare o sare si o inregistreaza ca CRASHED.
    if (Test-Path $inflight) {
        Write-Host "a murit in:" -ForegroundColor Yellow
        Get-Content $inflight | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }

    # Rezultatele adunate pana aici sunt deja pe disc si o rulare viitoare le sare, deci oprirea nu pierde
    # nimic. Metoda din inflight este inregistrata ca CRASHED la urmatoarea pornire, oricand ar fi ea.
    if (-not $RestartOnCrash) {
        Write-Host ""
        Write-Host "procesul a murit. NU repornesc jocul (adauga -RestartOnCrash daca chiar vrei asta)." -ForegroundColor Yellow
        break
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

$requirements = Join-Path $mods "active-requirements.tsv"
if (Test-Path $requirements) {
    Write-Host ""
    Write-Host "--- cele mai dese motive pentru care o metoda NU se poate chema ---"
    # Coloana 15 (de la 1) este lista de blocaje, separate prin bara verticala; separatorul de campuri este
    # 0x1F. Se numara primul blocaj al fiecarei metode, fiindca el este cel care ar trebui reparat intai.
    Get-Content $requirements | Select-Object -Skip 1 | ForEach-Object {
        $fields = $_.Split([char]0x1F)
        if ($fields.Count -gt 14 -and $fields[14].Length -gt 0) { $fields[14].Split("|")[0] }
    } | Group-Object | Sort-Object Count -Descending | Select-Object -First 15 Count, Name | Format-Table -AutoSize
}

$results = Join-Path $mods "active-results.tsv"
if (Test-Path $results) {
    Write-Host ""
    Write-Host "--- primele dezacorduri, daca exista ---"
    Get-Content $results | Where-Object { $_ -match "DISAGREES" } | Select-Object -First 15
}
