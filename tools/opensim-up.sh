#!/usr/bin/env bash
cd "$(dirname "$0")/opensim"
if docker compose version > /dev/null 2>&1; then
    docker compose up -d
else
    docker-compose up -d
fi
echo "OpenSim started! It may take a minute to fully initialize the database."
