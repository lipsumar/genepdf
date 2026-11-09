#!/bin/sh
# Validates PDFs against PDF/A-2b with veraPDF (the reference validator), via Docker.
# Usage: scripts/verify-pdfa.sh file1.pdf [file2.pdf ...]
set -e
export PATH="$PATH:$HOME/.docker/bin"
dir=$(cd "$(dirname "$1")" && pwd)
args=""
for f in "$@"; do args="$args /w/$(basename "$f")"; done
docker run --rm --platform linux/amd64 -v "$dir":/w verapdf/cli:latest --flavour 2b --format text $args 2>/dev/null
