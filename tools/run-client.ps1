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
Write-Host "[4/4] Starting Godot Client..." -ForegroundColor Green
godot --path (Join-Path $PSScriptRoot "..\app")
