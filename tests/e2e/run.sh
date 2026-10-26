#!/usr/bin/env bash
# Runs the disaster-recovery e2e in a throw-away Debian 13 container. Docker is used only as a test sandbox.
#   dotnet publish src/OneTimeRedirect.Web -c Release -r linux-x64 --self-contained true -p:PublishDir=<dir>/
#   tests/e2e/run.sh <dir>
set -euo pipefail
here="$(cd "$(dirname "$0")/../.." && { pwd -W 2>/dev/null || pwd; })"  # native path for docker on Git Bash
publish="${1:?path to the linux-x64 publish directory}"
docker build -q -t otr-e2e "$here/tests/e2e" >/dev/null
MSYS_NO_PATHCONV=1 docker run --rm -v "$here:/src:ro" -v "$publish:/publish:ro" otr-e2e bash /src/tests/e2e/inside.sh
