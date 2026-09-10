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
    #   scissor (default) - BUG-RENDER-11's shipped behaviour
    # Combine with -Diag to see the per-texture [FaceAlpha] verdicts in the log.
    [ValidateSet('scissor', 'blend', 'hash', 'prepass', 'edge', 'blenddepth')]
    [string]$FoliageAlpha = 'scissor',

    # BUG-RENDER-16: ALPHA_HASH_SCALE for -FoliageAlpha hash (also the shipped default). Higher =
    # finer dither grain. 0 leaves the built-in default (2.0). Passes --foliage-hash-scale=N.
    [double]$FoliageHashScale = 0
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
if ($FoliageHashScale -gt 0) {
    Write-Host "      foliage hash scale = $FoliageHashScale" -ForegroundColor Yellow
    $userArgs += "--foliage-hash-scale=$([string]::Format([cultureinfo]::InvariantCulture, '{0}', $FoliageHashScale))"
}
if ($userArgs.Count -gt 0) {
    $clientArgs += @('--') + $userArgs
}
godot @clientArgs *>&1 | Tee-Object -FilePath $transcriptPath
