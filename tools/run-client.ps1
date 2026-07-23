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
godot --path (Join-Path $PSScriptRoot "..\app") *>&1 | Tee-Object -FilePath $transcriptPath
