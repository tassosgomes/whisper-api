---
status: done
task_kind: vertical
blocked_by: []
gate: "rtk pytest -q -k downloader_user_agent"
gate_expect: "1 teste passa"
---

# 14.0 Enviar User-Agent identificável no download (opcional)

**Fatia:** V-07 · **Cobre:** RF-03 · **Spec:** `techspec.md#v-03` · **ADR:** —

## Comportamento

O downloader não envia User-Agent. Algumas origens públicas, como o Wikimedia, respondem 403 por isso. A obtenção passa a enviar um User-Agent identificável do Whisper. S3 e MinIO não dependem desse cabeçalho e continuam aceitando o mesmo GET de antes.

Task opcional. Não bloqueia 9.0 a 13.0.

## Fora do escopo desta task

Negociar cabeçalho com o MinIO, mudar a allowlist ou tratar 403 de permissão de objeto como sucesso.

## Decisões fechadas

O cabeçalho identifica o serviço. Não copia User-Agent de navegador para contornar bloqueio de site. Não registra a `sourceUrl` por causa dessa mudança.

## Modificar / Referenciar

- **modificar:** o cliente HTTP do download (o ponto que hoje abre a `sourceUrl` sem User-Agent)
- **ref:** `docs/adr/adr-003.md` (a allowlist não muda com o cabeçalho)
- **ref:** `app/transcription/source.py` (obtenção da mídia)

## Verificações do projeto

Não há workflow de CI.

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Download | `rtk pytest -q -k downloader_user_agent` | exit 0; 1 teste passa; pytest sai 5 se nada for coletado | `pyproject.toml`; CI ausente |
| Lint | `rtk ruff check app` | exit 0 | `pyproject.toml`; CI ausente |
| Formato | `rtk ruff format --check app` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `rtk pytest -q -k downloader_user_agent`
- [x] O GET de origem leva um User-Agent identificável
- [x] Um GET equivalente ao S3 ou ao MinIO, que já funcionava sem esse cabeçalho, continua aceito
