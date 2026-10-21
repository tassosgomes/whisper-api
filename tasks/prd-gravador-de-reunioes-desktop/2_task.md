---
status: pending
task_kind: vertical
blocked_by: [1.0]
gate: ".venv/bin/pytest -q -k cap4_upload_complete"
gate_expect: "ao menos 14 testes de conclusão de envio passam, 0 falham"
---

# 2.0 Concluir o envio e criar o job

**Fatia:** V-02 · **Cobre:** RF-10, RN-G04 · **Spec:** `techspec.md#v-02` · **ADR:** ADR-001
· **Ambiente:** Linux

## Comportamento

`completeTranscriptionUpload` (`POST /v1/transcription-uploads/{uploadId}/complete`) é chamado depois do `PUT`. Dentro de uma transação, com a linha do envio bloqueada, a API: devolve o mesmo job se o envio já foi concluído; repete a mesma recusa `422` se já foi recusado; responde `409 UPLOAD_NOT_RECEIVED` se o objeto não existe; confere o tamanho e o SHA-256 registrado pelo armazenamento contra o declarado e os primeiros bytes contra a assinatura do formato do tipo informado. Divergência marca o envio como recusado, apaga o objeto e responde `422` com `UPLOAD_SIZE_MISMATCH`, `UPLOAD_CHECKSUM_MISMATCH` ou `UNSUPPORTED_MEDIA_FORMAT`. Objeto sem SHA-256 registrado conta como divergente.

Conferindo tudo, cria o job em `queued` com `media_object_key` apontando ao objeto do envio, sem cópia, `accepted_at` e `queued_at` agora, sem origem de URL e com a `clientReference`; liga o envio ao job na mesma transação e responde `202` com `Location` e o corpo de criação de job. Repetir a chamada devolve o mesmo job, também com `202`; duas chamadas simultâneas criam um só job. Envio de outra conta ou credencial, inexistente ou expirado responde `404` neutro.

O job criado segue sem mudança pelo worker existente: o worker baixa pelo sufixo da chave, mede duração e formato e chega a `completed` com o resultado do contrato 1.0.0, ou a `failed` com `MEDIA_DURATION_LIMIT_EXCEEDED` ou `UNSUPPORTED_MEDIA_FORMAT` quando a duração real excede 2 h ou o conteúdo não decodifica. `getTranscription` expõe esse job sem `downloading` no histórico. A fila e a visão do operador continuam mostrando tamanho e idade do job sem erro.

## Fora do escopo desta task

Expurgo de objeto abandonado e de linha de envio vencida (3.0). A criação do pedido e a URL (1.0). O fluxo de `sourceUrl` não muda.

## Decisões fechadas

O objeto do envio é a mídia do job; não há cópia para `media/`. O worker e o contrato 1.0.0 não mudam. Divergência recusa o envio e o cliente recomeça com chave nova. O objeto de um envio recusado é apagado no ato.

## Modificar / Referenciar

- **modificar:** `app/api/transcriptions.py` (rota `completeTranscriptionUpload`)
- **modificar:** `app/jobs/executor.py` (criação de job `queued` sem origem; `accepted_at` preenchido; `_public_job` continua válido)
- **modificar:** `app/storage/s3.py` (consulta do objeto: tamanho, SHA-256 registrado e primeiros bytes)
- **modificar:** `app/jobs/models.py` (ligação do envio ao job)
- **modificar:** `tests/conftest.py` (arquivos de áudio curtos e fixtures de envio)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml` e `contracts.md` (cenários de verificação)
- **ref:** `app/jobs/worker.py:177-250` (processamento que o job reaproveita)
- **ref:** `app/transcription/validation.py` (`inspect_media` e formatos esperados)
- **ref:** `app/jobs/executor.py:348-388` (consulta operacional que lê os jobs)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API Python | `.venv/bin/pytest -q` | exit 0; os 231 testes da base continuam passando | `pyproject.toml`; CI ausente |
| API Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k cap4_upload_complete`
- [ ] Um teste ponta a ponta com áudio real curto cobre pedido, `PUT`, `complete`, acompanhamento até `completed` e leitura do resultado.
- [ ] Um `.wav` informado como `.mp3` termina em `422 UNSUPPORTED_MEDIA_FORMAT` e o objeto some do bucket.
- [ ] Um arquivo válido de mais de 2 h (sintético) termina o job em `failed/MEDIA_DURATION_LIMIT_EXCEEDED`.
- [ ] Duas chamadas simultâneas a `complete` resultam em um único job.
- [ ] Envio de outra credencial recebe `404` neutro; `complete` antes do `PUT` recebe `409 UPLOAD_NOT_RECEIVED` e funciona depois do `PUT`.
- [ ] A métrica de aceite a terminal e a visão do operador funcionam para o job criado por envio direto.
