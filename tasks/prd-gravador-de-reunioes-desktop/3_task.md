---
status: pending
task_kind: vertical
blocked_by: [2.0]
gate: ".venv/bin/pytest -q -k cap4_upload_retention"
gate_expect: "ao menos 7 testes de expurgo de envio passam, 0 falham"
---

# 3.0 Expurgar envio abandonado e encerrar a idempotência com o job

**Fatia:** V-03 · **Cobre:** RF-10, RN-G04 · **Spec:** `techspec.md#v-03` · **ADR:** ADR-001
· **Ambiente:** Linux

## Comportamento

A rotina de retenção do worker passa a tratar o prefixo `uploads/`. Objeto de `uploads/` que nenhum job referencia, cujo envio não existe, está recusado ou está pendente com o prazo vencido (24 h desde o pedido), é apagado do bucket. Linhas de envio pendentes vencidas e recusadas com mais de 24 h são removidas. Objeto referenciado por um job em andamento, e objeto de envio pendente ainda vigente, são preservados.

Quando o job criado por um envio é expurgado, 24 h após o estado terminal, a linha do envio some junto, por cascata. Depois disso a mesma `Idempotency-Key` volta a ser aceita como envio novo (`201`). Antes disso, a repetição ainda devolve o mesmo envio ou job. Um envio nunca concluído e vencido é tratado como inexistente: `complete` responde `404` e a chave pode ser reutilizada.

O objeto de um job concluído ou falho é removido do bucket pelo ciclo existente; se restar algum, o varredor o apaga como órfão. Os contadores de métrica existentes recebem os envios abandonados e recusados, sem conta, chave, URL nem nome de arquivo. O guia de operação do README descreve a regra de ciclo de vida de 2 dias do prefixo `uploads/` e como conferir, junto de versionamento e Object Lock desligados (ADR-001).

## Fora do escopo desta task

Configurar a regra de ciclo de vida no bucket de produção (NA11, operação). A criação e a conclusão do envio (1.0, 2.0).

## Decisões fechadas

O varredor da aplicação é a garantia principal; a regra de ciclo de vida do armazenamento é rede de segurança independente. Retenção de 24 h após o estado terminal vem da ADR-001 e não muda.

## Modificar / Referenciar

- **modificar:** `app/jobs/worker.py` (varredor de órfãos inclui `uploads/` e envios vencidos, hoje em `app/jobs/worker.py:313`; rotina de retenção em `:252`)
- **modificar:** `app/storage/s3.py` (listagem do prefixo e exclusão com as versões, já existentes)
- **modificar:** `README.md` (regra de ciclo de vida e conferência da política do bucket)
- **ref:** `app/jobs/retention.py` (janela de 24 h e cálculo do próximo ciclo)
- **ref:** `app/jobs/models.py` (envio com cascata a partir do job)
- **ref:** `tests/conftest.py` (relógio e bucket simulado)
- **ref:** `docs/adr/adr-001.md` (retenção e política do bucket)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API Python | `.venv/bin/pytest -q` | exit 0; os 231 testes da base continuam passando | `pyproject.toml`; CI ausente |
| API Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k cap4_upload_retention`
- [ ] Com o relógio controlado: envio pendente vencido some do bucket e do banco; envio pendente vigente é preservado; objeto de job em andamento é preservado.
- [ ] Depois de 24 h do estado terminal, objeto, job e linha do envio sumiram e a mesma `Idempotency-Key` cria um envio novo.
- [ ] Envio recusado há mais de 24 h é removido; antes disso a repetição ainda responde `409 UPLOAD_REJECTED`.
- [ ] Nenhum log do teste contém URL de envio, chave ou nome de arquivo.
