# Revisão full — API de transcrição assíncrona

Modo full. Revisões focused de 9.0–15.0 não entram como aprovação do resultado integrado.

- `base_ref`: `b5201bb428532539c69efc889cb9d76460bb20b9`
- `target_ref`: `feature/api-transcricao-assincrona`
- `validated_commit`: `c0486877ecd4abe37f1941928092c850addcd39e`
- `validated_tree`: `e38cda005251895e48c9ef0e94ee6d961877b785`

HEAD permaneceu nesse commit e nessa árvore do início ao fim. A árvore de implementação não foi editada. Fora do diff, e ignorados: `flow-state.json`, `tasks/prd-api-transcricao-assincrona/flow-state.json`, `backlog/`, `domains/`, `tasks/prd-operacao-do-servico/`, `local_meeting_transcriber.egg-info/`, `.venv`. Não há workflow de CI; a fonte dos checks é a lista agregada em `tasks.md`.

Antes do pytest, `docker compose --profile test up -d postgres-test`. `docker compose --profile test ps` mostrou `postgres-test` healthy em `127.0.0.1:55432`.

## Matriz

Commit revisado em todas as linhas: `c0486877ecd4abe37f1941928092c850addcd39e`. Árvore: `e38cda005251895e48c9ef0e94ee6d961877b785`.

| Componente | Fonte | Comando | Exit | Resultado |
|---|---|---|---|---|
| API, jobs, serviço e métricas Python | CI ausente; `tasks.md` | `rtk proxy rtk pytest -q` | 0 | 72 passed |
| Lint Python | CI ausente; `tasks.md` | `rtk proxy rtk ruff check app` | 0 | No issues found |
| Formato Python | CI ausente; `tasks.md` | `rtk proxy rtk ruff format --check app` | 0 | 27 files already formatted |
| Compose | CI ausente; `tasks.md` | `rtk proxy rtk docker compose config -q` | 0 | configuração válida, sem saída |
| Imagem transcriber | CI ausente; `tasks.md` | `rtk proxy rtk docker compose build transcriber` | 0 | imagem `local-meeting-transcriber:dev` construída; `av` 18.0.0 |
| OpenAPI | CI ausente; `tasks.md` | `rtk proxy rtk npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-api-transcricao-assincrona/api-contract.yaml --ruleset .agents/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=error` | 0 | No results with a severity of 'error' found |

Não há limite de cobertura. O freeze da imagem tem os 53 pins de `constraints.txt`; a única linha extra é o próprio projeto.

## Escopo semântico (tasks 9.0–15.0)

O diff desde a base altera runbook, contrato, baseline, constraints, Dockerfile, o GET da origem e três testes. `app/api/transcriptions.py` não muda neste intervalo: a declaração já usa `Annotated` com `alias`.

- Allowlist: o README registra `TRUSTED_PRIVATE_SOURCES=http://192.168.0.5:9000` e `TRUSTED_PRIVATE_WEBHOOKS` vazia até a CAP-032. As listas seguem independentes. O compose local só repassa as duas variáveis a `transcriber` e `worker`.
- Webhook: a ordem no README é receptor da CAP-032, allowlist só se o host for privado, `webhook configure` na conta existente e entrega do segredo fora do git. O diff não grava segredo, host nem URL inventados.
- `sourceUrl`: o pattern passa a `^(?:https://|http://)` e a descrição cita a ADR-003. Endereço público continua descrito como HTTPS; `http://` só na allowlist de origens. `validate_source_url_syntax` recusa `http` sem `may_use_http`. O exemplo público permanece HTTPS. O mesmo texto está em `api-contract.md`.
- Pin: o Dockerfile instala com `pip install -c constraints.txt .`. `av==18.0.0` e `faster-whisper==1.2.1` coincidem com `pyproject.toml`. `decode_audio` abre a mídia com `metadata_errors="ignore"`, o argumento que o PyAV 19 removeu. O teste da fixture chama esse caminho e observa `float32` com shape `(1600,)`.
- Baseline: a frase de redes privadas mantém o bloqueio e acrescenta a exceção só para destinos listados na ADR-003. Fora da lista, loopback, link-local, metadados, IP privado genérico e `http` genérico continuam recusados.
- User-Agent: `SOURCE_USER_AGENT` é `Whisper/0.1.0 (transcription source downloader)`, sem `Mozilla/`, no único `_open_get`, inclusive redirecionamentos. S3 e MinIO de origem usam esse mesmo GET.
- Alias: com Pydantic 2.14.1 o modelo não emite `UnsupportedFieldAttributeWarning` na importação nem no `model_rebuild`. O schema público exige `sourceUrl` e trata `clientReference` como opcional; os nomes internos ficam fora de `properties`.

Design patterns: não há pressão estrutural que um padrão GoF resolva melhor do que o cabeçalho direto e o arquivo de constraints.

## Sensor de discriminação

Worktree descartável em `/tmp/whisper-full-sensor`, detached em `c0486877ecd4abe37f1941928092c850addcd39e`. Linha de base de `git status --porcelain` e HEAD registrada antes. Nenhum `git stash`. Depois da remoção, HEAD, árvore e porcelain voltaram à linha de base.

| Fatia | Mutação | Critério | Teste | Exit |
|---|---|---|---|---|
| V-06 | `av.open` rejeita `metadata_errors` | o caminho da fixture falha no modo do PyAV 19 | `pytest -q -k decode_audio_fixture` | 1 |
| V-07 | GET sem `User-Agent` em `app/transcription/source.py` | o download envia User-Agent identificável | `pytest -q -k downloader_user_agent` | 1 |
| V-08 | alias público `sourceUrl` trocado por `mediaUrl` | o JSON aceito permanece `sourceUrl` | `pytest -q -k request_alias_warning` | 1 |

Nenhum mutante sobreviveu. As tasks 9.0, 10.0, 11.0 e 13.0 são habilitadoras e não têm suíte comportamental; o sensor não foi aplicado a elas.

## Limitações

Não são aprovação silenciosa:

- Reinício da api e do worker no Coolify em 192.168.0.11, e um POST real com `sourceUrl` de `http://192.168.0.5:9000` até `completed`, estão fora deste repositório.
- Implantação da CAP-032, `webhook configure` na conta real, entrega do segredo e um 204 ao vivo do receptor estão fora deste repositório.
- Uma segunda construção fria da imagem não foi repetida. A construção obrigatória única produz o freeze igual a `constraints.txt`.
- O teste de decodificação rodou no venv, que está em `av` 18.0.0, e não dentro do `docker compose build`. Não há CI para executá-lo na imagem.

## Bloqueantes

Nenhum.

## Recomendações (2)

1. `README.md:335-338`: o passo genérico de cadastro ainda diz para recusar redes privadas, enquanto o baseline atualizado e a ordem nova da seção 6 aceitam destino privado listado na ADR-003. Vale alinhar o parêntese ao baseline para o operador não seguir a frase antiga.
2. `README.md:397-398`: "comando do passo 1 acima" pode ser lido como o item 1 da lista nova, que não é o comando. Vale apontar o bloco `webhook configure` da seção 6.

FULL VALIDATION APROVADA
