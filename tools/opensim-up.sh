#!/usr/bin/env bash
cd "$(dirname "$0")/opensim"
docker compose up -d
echo "OpenSim started! It may take a minute to fully initialize the database."
