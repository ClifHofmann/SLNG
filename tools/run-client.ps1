param(
    # Passes --diag to the client. That switch turns on the whole diagnostic apparatus:
    # Logger's Debug level (AvatarRenderer's per-mesh rigging lines -- [RiggedMesh],
    # [JointOverride], [HeadSize]), the stats overlay, the main-thread watchdog and the
    # per-object asset logging. Off by default so an ordinary session stays quiet and fast.
    [switch]$Diag,

    # BUG-RENDER-16 prototype: passes --foliage-alpha=<mode>. Re-routes undeclared-alpha world-prim
    # faces that analyzeAlphaData calls high-frequency (thin grass, wispy foliage) away from the
    # hard Scissor cutout:
    #   blend   - reference-viewer parity, soft edge, but Godot's coarse per-object sort can pop it
    #   hash    - stochastic cutout, no sort/pop, gradient becomes a dither (can shimmer, no TAA)
    #   prepass - blend colour + alpha depth-prepass; soft edge, two draws (can still flicker)
    #   edge       - scissor pass + ALPHA_ANTIALIASING_EDGE; still ~4 MSAA coverage levels, hard-ish
    #   blenddepth - blended colour + depth_draw_always; soft outer edge, no flicker, overlap opaque
    #   blendcore  - blend colour + the viewer's alpha DEPTH pass (alpha >= -FoliageCoreAlpha writes
    #                depth before any transparent draw); soft edge, cores occlude in every draw
    #                order. Calms sparse grass; HAZES dense canopies (pine needles), so it is
    #                opt-in per session, not the default (see RenderConfig.HighFrequencyFoliageAlpha).
    #   scissor    - BUG-RENDER-11's earlier shipped behaviour
    # 'scissor' here means "pass nothing", i.e. the client's own default (RenderConfig).
    # Combine with -Diag to see the per-texture [FaceAlpha] verdicts in the log.
    [ValidateSet('scissor', 'blend', 'hash', 'prepass', 'edge', 'blenddepth', 'blendcore')]
    [string]$FoliageAlpha = 'scissor',

    # BUG-RENDER-16: alpha threshold of the blendcore depth pass. 0 leaves the built-in default
    # (0.33, the viewer's lldrawpoolalpha.cpp:217). Passes --foliage-core-alpha=N.
    [double]$FoliageCoreAlpha = 0,

    # BUG-RENDER-16: ALPHA_HASH_SCALE for -FoliageAlpha hash (also the shipped default). Higher =
    # finer dither grain. 0 leaves the built-in default (2.0). Passes --foliage-hash-scale=N.
    [double]$FoliageHashScale = 0,

    # BUG-RENDER-16: the reference viewer's alpha-sort hysteresis, ported per object. 0 = off
    # (default). The viewer's own value is 0.64 -- a CHORD on the unit sphere, i.e. re-sort only
    # after ~37 deg of direction change, NOT 0.64 radians (llspatialpartition.cpp:667, where `eye`
    # is normalize3fast()'d before the comparison).
    #
    # This is the lever for the flicker the surface merge cannot reach: measured on Millenium,
    # 937 of 1310 transparent objects have exactly ONE sorted surface, so there is no internal tie
    # left to merge away and what reorders them is the between-object sort, re-run every frame.
    # Expect the trade the viewer makes: no continuous shimmer, but the order snaps once per
    # ~37 deg of orbit. Passes --alpha-sort-hysteresis=N.
    [double]$AlphaSortHysteresis = 0,

    # BUG-RENDER-16: sort transparent objects by PLANAR depth along the view axis, the way the
    # reference viewer does for alpha groups (llspatialpartition.cpp:684-692, pipeline.cpp:3732),
    # instead of Godot's radial distance from the camera.
    #
    # This is the one that matches the reported symptom: radial distance is invariant under camera
    # ROTATION (which is why turning is calm) but changes by completely different amounts for an
    # object ahead and one off to the side when you WALK -- so the order churns. Planar depth
    # drops by the same amount for every object as you move along the view axis, preserving order.
    # Passes --alpha-sort-planar.
    [switch]$AlphaSortPlanar,

    # BUG-RENDER-16 (v0.22.26): 'off' keeps every sorted-transparent surface of a multi-surface
    # object on its parent instance, i.e. tied at one depth (the pre-v0.22.26 behaviour). The
    # default gives each such surface its own instance so Godot sorts it by its own bounds, the
    # way the viewer sorts per face. Passes --alpha-split=off.
    [ValidateSet('on', 'off')]
    [string]$AlphaSplit = 'on',

    # BUG-RENDER-16: FALSIFICATION TEST, not a fix. Freezes each transparent object's sort depth at
    # first sight so the draw order becomes completely camera-independent and can never change.
    # If the grass still flickers with this on, the cause is not the transparent sort order at all
    # and every fix aimed at it is wasted. Expect odd layering while walking -- that is not what is
    # being judged; the only question is whether it still shimmers. Passes --alpha-sort-freeze.
    [switch]$AlphaSortFreeze,

    # FEAT-PERF-06: turn OFF MultiMesh instancing (on by default). With it on, groups of identical
    # repeated static prims -- same shared mesh, same single material, same shadow flag -- are drawn
    # by one MultiMeshInstance3D instead of one MeshInstance3D each; a prim pops back to its own
    # node the instant it is selected, edited, animated or leaves draw distance. This switch is the
    # A/B: run the same spot with and without it and compare `draws` / `process` ms / FPS in the
    # perf overlay (and the [Instancing] line in logs/slng-perf.log). Passes --no-instancing.
    [switch]$NoInstancing
)

$ErrorActionPreference = 'Stop'

Write-Host "=== SLNG Launcher ===" -ForegroundColor Cyan

# 1. Setup Environment (loads the .NET 8 SDK into PATH)
Write-Host "[1/4] Setting up .NET environment..."
. (Join-Path $PSScriptRoot "dev-env.ps1")

# 2. Clear Godot Mono Cache (prevents Godot from loading stale buggy DLLs)
$godotMonoCache = Join-Path $PSScriptRoot "..\app\.godot\mono"
if (Test-Path $godotMonoCache) {
    Write-Host "[2/4] Clearing Godot Mono cache..."
    Remove-Item -Recurse -Force $godotMonoCache
} else {
    Write-Host "[2/4] Godot Mono cache already clean."
}

# 3. Build the solution cleanly using the .NET CLI
Write-Host "[3/4] Building SLNG solution..."
dotnet build (Join-Path $PSScriptRoot "..\SLNG.sln")
Write-Host "Building Godot App..."
dotnet build (Join-Path $PSScriptRoot "..\app\SLNG.App.csproj")

# 4. Start Godot
# Godot's windowed .exe is a GUI-subsystem process -- Console.WriteLine/GD.Print output isn't
# visible in the launching terminal and isn't reliably flushed to Godot's own log file either
# (observed: identical, unchanged log content across multiple play sessions). Explicitly
# redirecting via PowerShell gives the child process real OS-level pipe handles for
# stdout/stderr, which .NET's Console class can write to even though the exe has no console of
# its own -- and Tee-Object writes through immediately rather than buffering until exit, so the
# transcript survives even if the window is closed mid-session instead of exited cleanly.
$transcriptPath = Join-Path $PSScriptRoot "..\client-output.log"
Write-Host "[4/4] Starting Godot Client... (output also captured to $transcriptPath)" -ForegroundColor Green
$ErrorActionPreference = 'Continue'

# Godot puts everything after a bare `--` into GetCmdlineUserArgs, which is one of the two places
# Diagnostics.Initialize looks (see Diagnostics.cs) -- so the flag has to go after the separator,
# not next to --path.
$clientArgs = @('--path', (Join-Path $PSScriptRoot "..\app"))
$userArgs = @()
if ($Diag) {
    Write-Host "      diagnostics ON (--diag)" -ForegroundColor Yellow
    $userArgs += '--diag'
}
if ($FoliageAlpha -ne 'scissor') {
    Write-Host "      foliage alpha = $FoliageAlpha (--foliage-alpha=$FoliageAlpha)" -ForegroundColor Yellow
    $userArgs += "--foliage-alpha=$FoliageAlpha"
}
if ($FoliageCoreAlpha -gt 0) {
    Write-Host "      foliage core alpha = $FoliageCoreAlpha (--foliage-core-alpha)" -ForegroundColor Yellow
    $userArgs += "--foliage-core-alpha=$([string]::Format([cultureinfo]::InvariantCulture, '{0}', $FoliageCoreAlpha))"
}
if ($FoliageHashScale -gt 0) {
    Write-Host "      foliage hash scale = $FoliageHashScale" -ForegroundColor Yellow
    $userArgs += "--foliage-hash-scale=$([string]::Format([cultureinfo]::InvariantCulture, '{0}', $FoliageHashScale))"
}
if ($AlphaSortHysteresis -gt 0) {
    Write-Host "      alpha sort hysteresis = $AlphaSortHysteresis (--alpha-sort-hysteresis)" -ForegroundColor Yellow
    $userArgs += "--alpha-sort-hysteresis=$([string]::Format([cultureinfo]::InvariantCulture, '{0}', $AlphaSortHysteresis))"
}
if ($AlphaSortPlanar) {
    Write-Host "      alpha sort by planar view-axis depth (--alpha-sort-planar)" -ForegroundColor Yellow
    $userArgs += '--alpha-sort-planar'
}
if ($AlphaSplit -eq 'off') {
    Write-Host "      alpha per-surface split OFF (--alpha-split=off)" -ForegroundColor Yellow
    $userArgs += '--alpha-split=off'
}
if ($AlphaSortFreeze) {
    Write-Host "      alpha sort FROZEN -- falsification test (--alpha-sort-freeze)" -ForegroundColor Magenta
    $userArgs += '--alpha-sort-freeze'
}
if ($NoInstancing) {
    Write-Host "      MultiMesh instancing OFF (--no-instancing)" -ForegroundColor Yellow
    $userArgs += '--no-instancing'
}
if ($userArgs.Count -gt 0) {
    $clientArgs += @('--') + $userArgs
}
godot @clientArgs *>&1 | Tee-Object -FilePath $transcriptPath
