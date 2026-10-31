<#
.SYNOPSIS
    FEAT-PERF-28 baseline: cold and warm arrival runs, one after the other, with the logs collected.

.DESCRIPTION
    ADR 0005 asks for 3 cold and 3 warm runs at one fixed place before anything is changed. This script
    does the housekeeping so every run starts from the same state:

      1. Sets the real caches aside (cache\ and grids\<grid>\cache\objects\), so a cold run really is cold.
      2. COLD runs: empties the (test) caches, starts tools\run-client.ps1 -Diag, waits until you close the
         viewer, copies godot.log, then goes on to the next run.
      3. WARM runs: leaves the caches as the last cold run filled them, same procedure.
      4. Puts your own caches back (also when you press Ctrl+C), prints a summary of every run.

    Everything lands in baseline\<date-time>\ : kalt1.log ... warm3.log, runs.csv, summary.txt, info.txt.
    Nothing outside baseline\ is deleted except the TEST caches this script itself filled.

    In every run: log in, go to the SAME place (same landmark), do not move or zoom the camera, wait until
    the [Arrival] line is in the log or about 5 minutes, log out, close the viewer.

.PARAMETER ColdRuns
    Cold runs to do (default 3). With 0 the script does not touch your caches: warm runs then use whatever
    cache is there.
.PARAMETER WarmRuns
    Warm runs to do (default 3).
.PARAMETER Place
    Free text for info.txt, e.g. KinkyEvent.
.PARAMETER OutDir
    Where to put the results. Give an existing folder to add runs to it (numbering goes on).
.PARAMETER Grid
    The grid folder under grids\ (default agni).
.PARAMETER NoPrompt
    Do not wait for Enter between runs and do not ask for a note after a run.
.PARAMETER DryRun
    Print what would happen and touch nothing.
#>
param(
    [int]$ColdRuns = 3,
    [int]$WarmRuns = 3,
    [string]$Place = '',
    [string]$OutDir = '',
    [string]$Grid = 'agni',
    [switch]$NoPrompt,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$userData = Join-Path $env:APPDATA 'Godot\app_userdata\Puris Viewer'
$cacheDir = Join-Path $userData 'cache'
$objectsDir = Join-Path $userData "grids\$Grid\cache\objects"
$asideCache = "$cacheDir.baseline-aside"
$asideObjects = "$objectsDir.baseline-aside"
$testCache = "$cacheDir.baseline-test"
$testObjects = "$objectsDir.baseline-test"
$godotLog = Join-Path $userData 'logs\godot.log'

if (-not $OutDir) { $OutDir = Join-Path $repo ("baseline\" + (Get-Date -Format 'yyyyMMdd-HHmm')) }
$summaryScript = Join-Path $repo 'tools\arrival_summary.py'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

function Test-ViewerRunning {
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'godot*' }
}

# Samples the machine's received bytes once a second into a CSV (second,Mbit/s), as a background job. All
# adapters that are up are summed (loopback and tunnels left out), so a VPN next to the real adapter can count twice.
function Start-NetSampler($csv) {
    Start-Job -ArgumentList $csv -ScriptBlock {
        param($csv)
        function Total {
            $sum = 0L
            foreach ($n in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
                if ($n.OperationalStatus -ne 'Up') { continue }
                if ($n.NetworkInterfaceType -in 'Loopback', 'Tunnel') { continue }
                $sum += $n.GetIPv4Statistics().BytesReceived
            }
            $sum
        }
        $prev = Total
        $sec = 0
        while ($true) {
            Start-Sleep -Seconds 1
            $now = Total
            $sec++
            # InvariantCulture: on a German machine "0,8" would split the CSV column.
            Add-Content -Path $csv -Value ("{0},{1}" -f $sec, (($now - $prev) * 8 / 1e6).ToString('0.0', [cultureinfo]::InvariantCulture))
            $prev = $now
        }
    }
}

# Peak Mbit/s over one second and over a 5 s window, from the sampler's CSV.
function Get-NetPeaks($csv) {
    if (-not (Test-Path $csv)) { return @{ Peak1 = 0; Peak5 = 0 } }
    $v = @(Get-Content $csv | ForEach-Object { [double]($_ -split ',')[1] })
    if ($v.Count -eq 0) { return @{ Peak1 = 0; Peak5 = 0 } }
    $peak5 = 0
    for ($i = 0; $i -lt $v.Count; $i++) {
        $take = [Math]::Min(5, $v.Count - $i)
        $avg = ($v[$i..($i + $take - 1)] | Measure-Object -Sum).Sum / 5
        if ($avg -gt $peak5) { $peak5 = $avg }
    }
    @{ Peak1 = ($v | Measure-Object -Maximum).Maximum; Peak5 = $peak5 }
}

function Next-Index($prefix) {
    $n = 1
    while (Test-Path (Join-Path $OutDir "$prefix$n.log")) { $n++ }
    $n
}

# --- plan -------------------------------------------------------------------------------------------------
Say '=== FEAT-PERF-28 baseline ===' Cyan
Say "Ergebnisse : $OutDir"
Say "Laeufe     : $ColdRuns kalt, dann $WarmRuns warm$(if ($Place) { " ($Place)" })"
if ($ColdRuns -gt 0) {
    Say "Caches     : werden fuer die Dauer beiseitegelegt ($asideCache) und am Ende zurueckgestellt"
} else {
    Say 'Caches     : unangetastet (nur warme Laeufe) - der Cache sollte vom selben Ort warm sein' Yellow
}
if ($DryRun) {
    Say 'DryRun: nichts wird veraendert.' Yellow
    foreach ($p in @($cacheDir, $objectsDir, $godotLog, $summaryScript)) { Say ("  {0,-12} {1}" -f $(if (Test-Path $p) { 'vorhanden' } else { 'FEHLT' }), $p) }
    return
}

if (Test-ViewerRunning) {
    Say 'Ein Godot-Prozess laeuft noch (Viewer oder Editor). Bitte schliessen und neu starten:' Red
    Test-ViewerRunning | ForEach-Object { Say "  $($_.Name) (PID $($_.Id))" Red }
    exit 1
}
foreach ($p in @($asideCache, $asideObjects)) {
    if ($ColdRuns -gt 0 -and (Test-Path $p)) {
        Say "$p existiert schon - ein frueherer Lauf wurde unterbrochen. Dort liegen deine echten Caches." Red
        Say 'Bitte von Hand zurueckbenennen (cache.baseline-aside -> cache usw.) und dann neu starten.' Red
        exit 1
    }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

$branch = (git -C $repo branch --show-current) 2>$null
$commit = (git -C $repo rev-parse --short HEAD) 2>$null
$version = (Select-String -Path (Join-Path $repo 'app\scripts\Boot.cs') -Pattern 'AppVersion = "([^"]+)"' |
    Select-Object -First 1).Matches.Groups[1].Value
$info = Join-Path $OutDir 'info.txt'
if (-not (Test-Path $info)) {
    @("started   : $(Get-Date -Format 'yyyy-MM-dd HH:mm')", "place     : $Place", "version   : $version", "branch    : $branch @ $commit",
      "machine   : $env:COMPUTERNAME") | Set-Content $info
}
$runsCsv = Join-Path $OutDir 'runs.csv'
if (-not (Test-Path $runsCsv)) { 'label,kind,minutes,net_peak_1s_mbit,net_peak_5s_mbit,note,log' | Set-Content $runsCsv }

$moved = $false
$plan = @()
for ($i = 1; $i -le $ColdRuns; $i++) { $plan += 'kalt' }
for ($i = 1; $i -le $WarmRuns; $i++) { $plan += 'warm' }

try {
    if ($ColdRuns -gt 0) {
        foreach ($pair in @(@($cacheDir, $asideCache), @($objectsDir, $asideObjects))) {
            if (Test-Path $pair[0]) { Rename-Item $pair[0] (Split-Path $pair[1] -Leaf) }
        }
        $moved = $true
        Say 'Deine Caches liegen jetzt beiseite.' Green
    }

    $step = 0
    foreach ($kind in $plan) {
        $step++
        $n = Next-Index $kind
        $label = "$kind$n"

        if ($kind -eq 'kalt') {
            Remove-Item -Recurse -Force $cacheDir, $objectsDir -ErrorAction SilentlyContinue
        }

        Say ''
        Say "--- Lauf $step von $($plan.Count): $label ($(if ($kind -eq 'kalt') { 'KALT: Cache leer' } else { 'WARM: Cache vom letzten Lauf' })) ---" Cyan
        Say '  1. einloggen, zum immer gleichen Ort teleportieren (selbes Landmark)'
        Say '  2. Kamera nicht bewegen, nicht zoomen'
        Say '  3. warten bis [Arrival] im Log steht oder ca. 5 Minuten'
        Say '  4. ausloggen und den Viewer schliessen -> der naechste Schritt startet von selbst'
        Say '  Optional: Task Manager > Leistung > Netzwerk beobachten (das Skript misst die Spitze selbst mit).'
        if (-not $NoPrompt) { Read-Host 'Enter zum Starten' | Out-Null }

        $netCsv = Join-Path $OutDir "$label.net.csv"
        Remove-Item $netCsv -ErrorAction SilentlyContinue
        $sampler = Start-NetSampler $netCsv
        $t0 = Get-Date
        try {
            & (Join-Path $repo 'tools\run-client.ps1') -Diag
        } finally {
            Stop-Job $sampler -ErrorAction SilentlyContinue
            Remove-Job $sampler -Force -ErrorAction SilentlyContinue
        }
        $minutes = [Math]::Round(((Get-Date) - $t0).TotalMinutes, 1)

        Start-Sleep -Seconds 2   # the log is closed by now; a moment for the file system
        $dest = Join-Path $OutDir "$label.log"
        if (Test-Path $godotLog) { Copy-Item $godotLog $dest } else { Say "godot.log nicht gefunden: $godotLog" Red }
        $peaks = Get-NetPeaks $netCsv
        $note = ''
        if (-not $NoPrompt) { $note = Read-Host 'Notiz zu diesem Lauf (z.B. "Bronte nackt"), Enter = keine' }
        $inv = [cultureinfo]::InvariantCulture
        '{0},{1},{2},{3},{4},"{5}",{6}' -f $label, $kind, $minutes.ToString($inv), $peaks.Peak1.ToString('0', $inv), $peaks.Peak5.ToString('0', $inv),
            ($note -replace '"', "'"), (Split-Path $dest -Leaf) | Add-Content $runsCsv
        Say ("{0}: {1} min, Netz-Spitze {2:0} Mbit/s (5 s: {3:0}), Log: {4}" -f $label, $minutes, $peaks.Peak1, $peaks.Peak5, $dest) Green
    }
} finally {
    if ($moved) {
        Say ''
        Say 'Stelle deine Caches wieder her...' Yellow
        # The test caches this script filled are kept under .baseline-test (replacing an older one of its own).
        foreach ($t in @(@($cacheDir, $testCache, $asideCache), @($objectsDir, $testObjects, $asideObjects))) {
            if (Test-Path $t[1]) { Remove-Item -Recurse -Force $t[1] }
            if (Test-Path $t[0]) { Rename-Item $t[0] (Split-Path $t[1] -Leaf) }
            if (Test-Path $t[2]) { Rename-Item $t[2] (Split-Path $t[0] -Leaf) }
        }
        Say 'Fertig: deine Caches sind zurueck (die Test-Caches heissen *.baseline-test und koennen geloescht werden).' Green
    }
}

# --- summary ----------------------------------------------------------------------------------------------
$logs = @(Get-ChildItem $OutDir -Filter '*.log' | Sort-Object Name | ForEach-Object { $_.FullName })
if ($logs.Count -gt 0 -and (Test-Path $summaryScript)) {
    $summary = Join-Path $OutDir 'summary.txt'
    # Set-Content -Encoding utf8: Tee-Object wrote UTF-16 on some hosts, which other tools could not read.
    $text = & python -I $summaryScript @logs
    $text | Set-Content -Path $summary -Encoding utf8
    $text
    Say ''
    Say "Zusammenfassung: $summary"
}
Say "Laufliste (Netz-Spitzen, Notizen): $runsCsv"
