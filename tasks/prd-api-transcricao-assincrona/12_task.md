---
status: done
task_kind: vertical
blocked_by: []
gate: "rtk pytest -q -k decode_audio_fixture"
gate_expect: "1 teste passa"
---

# 12.0 Pinar dependências transitivas e cobrir a decodificação

**Fatia:** V-06 · **Cobre:** RF-03 · **Spec:** `techspec.md#v-03` · **ADR:** —

## Comportamento

O bug do PyAV veio de uma dependência sem versão fixa (`av`), que resolveu para uma versão incompatível no build. `pyproject.toml` já declara `av==18.0.0` porque o `faster-whisper` 1.2.1 passa `metadata_errors` a `av.open` e o PyAV 19 removeu esse argumento. Isso não impede que outra transitiva quebre o próximo build.

O Dockerfile passa a instalar com constraints pinadas, na forma `pip install -c constraints.txt .`, geradas a partir da resolução que produz a imagem. Um teste de regressão decodifica uma mídia mínima de fixture com `faster_whisper.audio.decode_audio` e roda no build ou no CI. Esse teste é o que falharia com `av==19.0.1`. O gate do plano roda a suíte com a resolução pinada, não instala o PyAV 19.

## Fora do escopo desta task

Trocar a versão do `faster-whisper`, adicionar uma matriz de CI que instale `av==19.0.1`, e alterar o fluxo de download ou de transcrição além da decodificação da fixture.

## Decisões fechadas

Manter `faster-whisper==1.2.1` e `av==18.0.0` já declarados. Não subir o PyAV para 19 para "resolver" o pin. O constraints cobre o conjunto resolvido, não só o pacote `av`.

## Modificar / Referenciar

- **modificar:** `Dockerfile` (instalação com constraints)
- **modificar:** `pyproject.toml` (só se o pin direto precisar ser a fonte do constraints; não relaxar `av`)
- **ref:** `pyproject.toml` (comentário que explica a incompatibilidade com PyAV 19)

## Verificações do projeto

Não há workflow de CI. A fonte dos checks é `pyproject.toml` e o `Dockerfile`.

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Decodificação | `rtk pytest -q -k decode_audio_fixture` | exit 0; 1 teste passa; pytest sai 5 se nada for coletado | `pyproject.toml`, pytest 8.3.5; CI ausente |
| Lint | `rtk ruff check app` | exit 0 | `pyproject.toml`, Ruff; CI ausente |
| Formato | `rtk ruff format --check app` | exit 0 | `pyproject.toml`, Ruff; CI ausente |
| Imagem | `rtk docker compose build transcriber` | exit 0 e a imagem instala via constraints | `Dockerfile`, `docker-compose.yml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `rtk pytest -q -k decode_audio_fixture`
- [ ] O build usa constraints pinadas e duas construções com a mesma constraints resolvem as mesmas versões
- [x] O teste chama `faster_whisper.audio.decode_audio` numa mídia mínima de fixture
- [x] Esse caminho é o que falha com `av==19.0.1`; a suíte do gate permanece em `av==18.0.0`
