#!/usr/bin/env bash
#
# Builds the three vNext images the integration-test stack needs
# (orchestrator, execution, db-migrator) from a local vnext checkout,
# tagged so the Resilience.IntegrationTests environment picks them up.
#
# Usage:
#   ./scripts/build-vnext-branch-images.sh <path-to-vnext-repo> [tag] [branch]
#
# Examples:
#   ./scripts/build-vnext-branch-images.sh ~/src/vnext
#   ./scripts/build-vnext-branch-images.sh ~/src/vnext resilience-local feature/resilience-hardening
#
# Defaults: tag=resilience-local, branch=<current checkout, unchanged>.
# When a branch is given, the script checks it out (fails on a dirty tree).

set -euo pipefail

VNEXT_SRC="${1:?Usage: build-vnext-branch-images.sh <path-to-vnext-repo> [tag] [branch]}"
TAG="${2:-resilience-local}"
BRANCH="${3:-}"

REGISTRY_BASE="ghcr.io/burgan-tech/vnext"

cd "$VNEXT_SRC"

if [[ -n "$BRANCH" ]]; then
  if [[ -n "$(git status --porcelain)" ]]; then
    echo "ERROR: working tree at $VNEXT_SRC is dirty — commit/stash before switching to $BRANCH." >&2
    exit 1
  fi
  git fetch origin "$BRANCH"
  git checkout "$BRANCH"
  git pull --ff-only origin "$BRANCH"
fi

echo "Building from $(git rev-parse --abbrev-ref HEAD) @ $(git rev-parse --short HEAD) with tag :$TAG"

build() {
  local name="$1" dockerfile="$2"
  echo ""
  echo "=== Building $name ==="
  docker build \
    -f "$dockerfile" \
    -t "$REGISTRY_BASE/$name:$TAG" \
    .
}

build orchestrator orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Dockerfile
build execution    execution/BBT.Workflow.Execution.HttpApi.Host/Dockerfile
build db-migrator  workers/BBT.Workflow.DbMigrator/Dockerfile

echo ""
echo "Done. Images:"
docker images --format '  {{.Repository}}:{{.Tag}}  ({{.Size}})' | grep ":$TAG"
echo ""
echo "Run the suite with:"
echo "  export VNEXT_IMAGE_VERSION=$TAG   # optional — $TAG is already the default for resilience-local"
echo "  dotnet test tests/Resilience.IntegrationTests"
