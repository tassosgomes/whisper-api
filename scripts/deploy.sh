#!/usr/bin/env bash
# Atualiza o clone, reconstrói whisper-api:local e recria a API e o worker.
#
# Rode no host de produção, a partir deste arquivo ou de uma cópia ao lado
# do compose de produção (o diretório que contém compose.yml e app.env).
# Segredos ficam só nesse diretório e não entram no git.
#
# Uso, a partir do clone ou do diretório do compose:
#   ./deploy.sh
#   ./scripts/deploy.sh
#   ./deploy.sh --force
#   ./deploy.sh --branch feature/minha-feature
#
# Sem --branch, faz deploy de main.
#
# DEPLOY_DIR  diretório do compose de produção
# REPO_DIR    clone deste repositório

set -euo pipefail

force=0
branch=main
while [[ $# -gt 0 ]]; do
  case "$1" in
    --force)
      force=1
      shift
      ;;
    -b | --branch)
      if [[ $# -lt 2 ]]; then
        echo "deploy: --branch precisa de um nome de branch" >&2
        exit 2
      fi
      branch=$2
      shift 2
      ;;
    --branch=*)
      branch=${1#--branch=}
      shift
      ;;
    *)
      echo "deploy: uso: deploy.sh [--force] [--branch NOME]" >&2
      exit 2
      ;;
  esac
done

if [[ "$branch" == -* ]] || ! git check-ref-format --branch "$branch" >/dev/null 2>&1; then
  echo "deploy: nome de branch inválido: $branch" >&2
  exit 2
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

if [[ -z "${REPO_DIR:-}" ]]; then
  if [[ -f "$script_dir/Dockerfile" && -d "$script_dir/.git" ]]; then
    REPO_DIR=$script_dir
  elif [[ -f "$script_dir/../Dockerfile" && -d "$script_dir/../.git" ]]; then
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

echo "deploy: buscando origin/$branch"
GIT_TERMINAL_PROMPT=0 git fetch --depth=100 origin "+refs/heads/$branch:refs/remotes/origin/$branch" ||
  {
    echo "deploy: branch $branch não encontrada em origin" >&2
    exit 1
  }

if git show-ref --verify --quiet "refs/heads/$branch"; then
  if ! git merge-base --is-ancestor "$branch" "origin/$branch"; then
    echo "deploy: $branch local não é ancestral de origin/$branch; o clone precisa ser alinhado antes" >&2
    exit 1
  fi
  git checkout --quiet "$branch"
  git merge --ff-only "origin/$branch"
else
  git checkout --quiet -b "$branch" "origin/$branch"
fi

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
  echo "deploy: API e worker já estão em $short ($branch)"
  exit 0
fi

echo "deploy: construindo whisper-api:$short ($branch)"
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

echo "deploy: concluído em $short ($branch)"
compose ps whisper-api whisper-worker
