---
status: done
task_kind: vertical
blocked_by: []
gate: ".venv/bin/pytest -q -k cap4_upload_request"
gate_expect: "ao menos 14 testes de pedido de envio passam, 0 falham"
---

# 1.0 Pedir a URL de envio de áudio

**Fatia:** V-01 · **Cobre:** RF-10, RN-G03, RN-G04 · **Spec:** `techspec.md#v-01` · **ADR:** ADR-001
· **Ambiente:** Linux

## Comportamento

`createTranscriptionUpload` (`POST /v1/transcription-uploads`) com API Key, `Idempotency-Key` e o corpo do contrato registra o envio e responde `201` com `uploadId`, `expiresAt`, `upload` (método `PUT`, URL pré-assinada e cabeçalhos a enviar), `completeUrl` e `clientReference` quando informado. O objeto fica em `uploads/{uploadId}/source{sufixo}`, com o sufixo derivado do tipo de conteúdo. A URL é gerada pelo cliente S3 apontado ao endereço público do armazenamento e sua validade é configurável (padrão 1 h). A URL não é gravada no banco, em log nem em métrica.

Validação sem registrar nada: tipo fora da lista → `422 UNSUPPORTED_MEDIA_FORMAT`; `sizeBytes` acima de 5 GiB → `413 MEDIA_SIZE_LIMIT_EXCEEDED`; `durationMs` acima de 2 h → `422 MEDIA_DURATION_LIMIT_EXCEEDED`; corpo malformado, campo ausente, SHA-256 fora do formato ou `Idempotency-Key` ausente → `400 INVALID_REQUEST`; sem chave ou chave inválida → `401`. Credencial com envios pendentes acima do limite configurado (padrão 5) → `429 TOO_MANY_PENDING_UPLOADS` com `Retry-After`.

Idempotência por conta, credencial e hash da chave, comparando a impressão digital de tipo, tamanho, SHA-256, duração e `clientReference`: mesmos valores → `200` com o mesmo `uploadId`; enquanto pendente, a resposta traz uma URL válida, reemitida se a anterior venceu; concluído, traz o `jobId` e nenhuma URL; valores diferentes → `409 IDEMPOTENCY_KEY_REUSED`; envio recusado → `409 UPLOAD_REJECTED`. Envio de outra conta ou credencial nunca é devolvido: a mesma chave em outra credencial cria outro envio.

O envio pendente vale 24 h a partir do pedido. A migration cria a tabela de envios, com ligação ao job anulável e exclusão em cascata a partir do job. A configuração nova (endereço público do armazenamento, validade da URL, limite de pendentes) entra no ambiente, no `.env.example`, no compose e no README, e o guia de operação do README registra a regra de ciclo de vida de 2 dias no prefixo `uploads/` do bucket.

## Fora do escopo desta task

`completeTranscriptionUpload` e a criação do job (2.0); expurgo de envios e objetos abandonados (3.0). Nenhum job é criado aqui.

## Decisões fechadas

Registro de envio próprio, não `IdempotencyRecord`. Mesmo bucket do Whisper e prefixo `uploads/` (decisão da TechSpec; ADR-001 continua valendo). Valores padrão de 1 h e 5 pendentes são configuráveis. A assinatura cobre método `PUT`, tipo, tamanho e SHA-256; se o armazenamento do compose não validar esses cabeçalhos, a decisão volta à TechSpec antes de seguir. O contrato `api-contract.yaml` é a fonte dos schemas e dos códigos; não o duplique.

## Modificar / Referenciar

- **modificar:** `app/api/transcriptions.py` (rota `createTranscriptionUpload`, autenticação por API Key)
- **modificar:** `app/main.py` (configuração nova e montagem do componente de envios, linhas 66-99 e 155-165)
- **modificar:** `app/storage/s3.py` (assinatura de `PUT` com o endereço público; chave de `uploads/`)
- **modificar:** `app/jobs/models.py` (modelo do envio)
- **modificar:** `migrations/versions/` (migration da tabela de envios)
- **modificar:** `docker-compose.yml`, `.env.example`, `README.md` (configuração nova e guia de operação)
- **modificar:** `tests/conftest.py` (fixtures de credencial e bucket simulado para os testes novos)
- **modificar:** `scripts/init_minio.sh` (conferir que a política já cobre `s3:PutObject` em `bucket/*`)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml` (`createTranscriptionUpload`, schemas e códigos)
- **ref:** `app/jobs/executor.py:92-160` (padrão de reserva idempotente com bloqueio de linha)
- **ref:** `app/jobs/models.py:IdempotencyRecord` (restrição de unicidade por conta, credencial e hash da chave)
- **ref:** `app/transcription/source.py:24` e `app/transcription/validation.py:10` (limites de tamanho e duração)
- **ref:** `app/main.py:215-285` (Problem Details; `detail` como dicionário e cabeçalhos de erro)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API Python | `.venv/bin/pytest -q` | exit 0; os 231 testes da base continuam passando | `pyproject.toml`; CI ausente |
| API Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| Compose | `docker compose config -q` | exit 0 | `docker-compose.yml`; CI ausente |
| Smoke de armazenamento | `S3_SMOKE_ENDPOINT=http://127.0.0.1:9000 .venv/bin/pytest -q -k cap4_presign_minio` | exit 0; 2 testes: PUT com os cabeçalhos devolvidos é aceito e PUT com tamanho ou SHA-256 alterado é recusado. Pré-requisitos: `docker compose up -d minio minio-init` e as variáveis de MinIO do `.env`. Sem `S3_SMOKE_ENDPOINT` os 2 testes ficam ignorados | TechSpec, seção Verificação |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k cap4_upload_request`
- [x] Os testes sobem a aplicação real (`app.main.app`) com banco e bucket simulado, sem dublê do componente de envios.
- [x] Cada resposta de `createTranscriptionUpload` do contrato (201, 200, 400, 401, 409 nos dois casos, 413, 422 nos dois casos, 429) é exercitada, e a resposta confere com o schema do `api-contract.yaml`.
- [x] Repetição com os mesmos valores devolve o mesmo `uploadId`, inclusive com o relógio avançado em horas, e emite uma URL nova quando a anterior venceu.
- [x] A URL de envio não aparece em nenhum log capturado pelo teste nem no banco.
- [ ] O smoke contra o MinIO do compose passa, ou fica registrado que o armazenamento não valida os cabeçalhos assinados e a TechSpec é reaberta.

Conclusão aprovada na [revisão focused](1.0_task_review.md), Run: run.J34OaE83, sem bloqueantes.
O último item continua pendente: os 2 smokes passaram em outra versão do MinIO; repetir com a
imagem do compose antes da validação full é a recomendação não bloqueante R1 do validator.
O [resumo da implementação](1.0_task_summary.md) preserva as verificações e limitações.
