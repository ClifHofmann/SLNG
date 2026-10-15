param(
    # The water fog density. 0 switches the fog off, leaving only the surface's own fresnel -- the baseline.
    [double]$Density = 2.0,
    # Camera height above the water, in metres. 3.0 looks ~30 degrees down at the poles, 1.6 ~13 (a beach).
    [double]$Eye = 3.0,
    [ValidateSet('both', 'opaque', 'blend', 'refl')]
    [string]$Mode = 'both',
    # Also save the frame here. Relative paths land in tools/waterfog-probe/.
    [string]$Shot = ''
)

# BUG-RENDER-48. Copies the viewer's CURRENT shaders in (a stale copy measures yesterday's shader), then runs the
# probe windowed. Reads the numbers from the PROBE / REFL lines it prints.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$materials = Join-Path $here 'materials'
if (Test-Path $materials) { Remove-Item -Recurse -Force $materials }
Copy-Item -Recurse (Join-Path $here '..\..\app\materials') $materials

$args = @('--path', $here, '--', "density=$Density", "eye=$Eye", "mode=$Mode")
if ($Shot -ne '') { $args += "shot=$(Join-Path $here $Shot)" }
godot @args 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match 'PROBE|REFL|ERROR|SHADER' }
