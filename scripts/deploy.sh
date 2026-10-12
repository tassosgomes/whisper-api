#!/usr/bin/env bash
# Atualiza o clone, reconstrói whisper-api:local e recria a API e o worker.
#
# Rode no host de produção, a partir deste arquivo ou de uma cópia ao lado
# do compose de produção (o diretório que contém compose.yml e app.env).
# Segredos ficam só nesse diretório e não entram no git.
#
# Uso:
#   scripts/deploy.sh
#   scripts/deploy.sh --force
#
# DEPLOY_DIR  diretório do compose de produção
# REPO_DIR    clone deste repositório

set -euo pipefail

force=0
if [[ "${1:-}" == "--force" ]]; then
  force=1
elif [[ $# -gt 0 ]]; then
  echo "deploy: uso: deploy.sh [--force]" >&2
  exit 2
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

if [[ -z "${REPO_DIR:-}" ]]; then
  if [[ -f "$script_dir/../Dockerfile" && -d "$script_dir/../.git" ]]; then
    REPO_DIR=$(cd "$script_dir/.." && pwd)
  elif [[ -f "$script_dir/src/Dockerfile" && -d "$script_dir/src/.git" ]]; then
    REPO_DIR=$script_dir/src
  else
    echo "deploy: não achei o clone; defina REPO_DIR" >&2
    exit 1
  fi
fi

if [[ -z "${DEPLOY_DIR:-}" ]]; then
  if [[ -f "$script_dir/compose.yml" ]]; then
    DEPLOY_DIR=$script_dir
  elif [[ -f "$REPO_DIR/../compose.yml" ]]; then
    DEPLOY_DIR=$(cd "$REPO_DIR/.." && pwd)
  else
    echo "deploy: defina DEPLOY_DIR com o compose de produção" >&2
    exit 1
  fi
fi

if [[ ! -f "$DEPLOY_DIR/compose.yml" || ! -f "$DEPLOY_DIR/app.env" ]]; then
  echo "deploy: $DEPLOY_DIR precisa de compose.yml e app.env" >&2
  exit 1
fi

if [[ "${DEPLOY_LOCK_HELD:-}" != 1 ]]; then
  exec 9>"$DEPLOY_DIR/.deploy.lock"
  if ! flock -n 9; then
    echo "deploy: outro deploy já está em andamento" >&2
    exit 1
  fi
  export DEPLOY_LOCK_HELD=1
fi

cd "$REPO_DIR"

if [[ -n "$(git status --porcelain)" ]]; then
  echo "deploy: o clone em $REPO_DIR tem alterações locais" >&2
  exit 1
fi

running_script=$(readlink -f "${BASH_SOURCE[0]}")
script_hash_before=$(sha256sum "$running_script" | awk '{print $1}')

echo "deploy: buscando origin/main"
GIT_TERMINAL_PROMPT=0 git fetch --depth=100 origin main

if ! git merge-base --is-ancestor HEAD origin/main; then
  echo "deploy: HEAD não é ancestral de origin/main; o clone precisa ser alinhado antes" >&2
  exit 1
fi

git merge --ff-only origin/main

if [[ -f "$REPO_DIR/scripts/deploy.sh" ]]; then
  repo_script=$(readlink -f "$REPO_DIR/scripts/deploy.sh")
  if [[ "$repo_script" != "$running_script" ]]; then
    exec env DEPLOY_REEXEC=1 DEPLOY_LOCK_HELD=1 DEPLOY_DIR="$DEPLOY_DIR" REPO_DIR="$REPO_DIR" "$repo_script" "$@"
  fi
fi

if [[ "${DEPLOY_REEXEC:-}" != 1 ]]; then
  script_hash_after=$(sha256sum "$running_script" | awk '{print $1}')
  if [[ "$script_hash_before" != "$script_hash_after" ]]; then
    exec env DEPLOY_REEXEC=1 DEPLOY_LOCK_HELD=1 DEPLOY_DIR="$DEPLOY_DIR" REPO_DIR="$REPO_DIR" "$running_script" "$@"
  fi
fi

sha=$(git rev-parse HEAD)
short=$(git rev-parse --short HEAD)

compose() {
  docker compose -f "$DEPLOY_DIR/compose.yml" --project-directory "$DEPLOY_DIR" "$@"
}

revision_of() {
  local service=$1
  local container_id
  container_id=$(compose ps -aq "$service")
  if [[ -z "$container_id" ]]; then
    echo ""
    return
  fi
  docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$container_id"
}

api_revision=$(revision_of whisper-api)
worker_revision=$(revision_of whisper-worker)

if [[ "$force" -eq 0 && "$api_revision" == "$sha" && "$worker_revision" == "$sha" ]]; then
  echo "deploy: API e worker já estão em $short"
  exit 0
fi

echo "deploy: construindo whisper-api:$short"
docker build \
  --pull \
  --label "org.opencontainers.image.revision=$sha" \
  --label "org.opencontainers.image.source=https://github.com/tassosgomes/whisper-api" \
  -t whisper-api:local \
  -t "whisper-api:$short" \
  "$REPO_DIR"

echo "deploy: recriando whisper-api"
compose up -d --no-deps --force-recreate --wait --wait-timeout 600 whisper-api

echo "deploy: recriando whisper-worker"
compose up -d --no-deps --force-recreate --wait --wait-timeout 600 whisper-worker

echo "deploy: concluído em $short"
compose ps whisper-api whisper-worker
