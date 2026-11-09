#!/bin/sh
# Runs the dotnet CLI inside the dev container (no local SDK needed).
# Usage: scripts/dotnet.sh build | scripts/dotnet.sh test | PORT=5080 scripts/dotnet.sh run --project src/GenePdf.DevServer
set -e
cd "$(dirname "$0")/.."
export PATH="$PATH:$HOME/.docker/bin"
docker build -q -t genepdf-dev -f docker/dev.Dockerfile docker >/dev/null
PORT_ARGS=""
[ -n "$PORT" ] && PORT_ARGS="-p $PORT:$PORT -e ASPNETCORE_URLS=http://0.0.0.0:$PORT"
exec docker run --rm $DOCKER_ARGS $PORT_ARGS \
  -v "$PWD":/src -v genepdf-nuget:/root/.nuget/packages \
  -w /src genepdf-dev dotnet "$@"
