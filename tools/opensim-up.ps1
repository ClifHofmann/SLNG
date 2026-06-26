$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location -Path "$scriptPath\opensim"
docker compose up -d
Write-Host "OpenSim started! It may take a minute to fully initialize the database."
