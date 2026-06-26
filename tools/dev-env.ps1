# Prepares the SLNG dev shell. Source it:  . .\tools\dev-env.ps1
#
# The .NET 8 SDK is installed per-user at %USERPROFILE%\.dotnet (no admin needed).
# A runtime-only dotnet host in C:\Program Files\dotnet takes PATH precedence, so a
# bare `dotnet` would report "no SDK found". Sourcing this script makes `dotnet`
# resolve to the per-user SDK for the current session.

$dotnetRoot = Join-Path $env:USERPROFILE '.dotnet'

if (Test-Path (Join-Path $dotnetRoot 'sdk')) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    Write-Host ("dotnet -> " + (Get-Command dotnet).Source)
    dotnet --version
} else {
    Write-Warning "No per-user .NET SDK at $dotnetRoot. Install: winget install Microsoft.DotNet.SDK.8"
}
