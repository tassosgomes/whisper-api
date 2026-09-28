---
status: pending
task_kind: vertical
blocked_by: [6.0]
gate: "rtk pytest -q -k v04_result"
gate_expect: "8 testes de resultado, autorização, retenção e expurgo passam"
---

# 7.0 Servir resultado versionado e expurgar dados vencidos

**Fatia:** V-04 · **Cobre:** RF-05, RF-06, US-03 · **Spec:** `techspec.md#v-04` · **ADR:** ADR-001

## Comportamento

`GET /v1/transcriptions/{jobId}/result` autentica a chave e verifica conta e `credential_id` criador antes de ler o resultado privado. Job `completed` dentro de 24 horas de `terminalAt` retorna `200` com JSON v1 independente do motor: `schemaVersion`, `jobId`, `language: pt-BR`, `durationMs` e segmentos `startMs`, `endMs`, `text`, com tempos relativos ao início da mídia. Job `downloading`, `queued`, `processing` ou `failed` retorna `409 RESULT_NOT_AVAILABLE`, sem conteúdo parcial. Outra conta, outra chave da mesma conta, job inexistente ou expirado recebe `404 NOT_FOUND` neutro tanto no status quanto no resultado. A borda exata de 24 horas segue o contrato de retenção.

A mídia é removida assim que deixa de ser necessária; rotina periódica remove resultado e metadados expirados em até 24 horas do terminal e localiza/apaga objetos temporários órfãos do bucket. Falha de expurgo produz métrica segura e permite retomada. Não restam versões ou cópias imutáveis incompatíveis com ADR-001. Logs e traces desse fluxo não contêm URL, mídia, transcrição ou segredos.

O gate usa banco e bucket isolados, migrations, conta/chave e relógio controlável. O smoke desta fatia percorre criação com origem HTTPS de teste até consulta pública do resultado pelo `statusUrl`/`Location` retornado, validando que o destino aponta para uma rota alcançável e que o JSON obedece ao schema. A composição real da aplicação inicia no ambiente de teste; adaptadores reais de S3 e banco são exercitados contra recursos controlados.

## Fora do escopo desta task

Entrega e assinatura de webhook entram em V-05. Persistência permanente e busca textual pertencem ao consumidor.

## Decisões fechadas

Aplicar `techspec.md#v-04`, a autorização por conta e credencial criadora e a janela de 24 horas do PRD. ADR-001 governa o bucket e a remoção física.

## Modificar / Referenciar

- **modificar:** `app/api/transcriptions.py` (resultado e estado autorizados)
- **modificar:** `app/jobs/executor.py` (expurgo e transição terminal)
- **modificar:** `app/main.py` (agendamento do expurgo)
- **modificar:** `app/service.py` (representação pública v1)
- **modificar:** `app/monitoring/metrics.py` (expurgo atrasado)
- **modificar:** `docker-compose.yml` (papel e recursos isolados para smoke)
- **modificar:** `.env.example` (placeholders de configuração S3)
- **ref:** `app/transcription/models.py` (segmentos internos)
- **ref:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (schema do resultado)
- **ref:** `docs/adr/adr-001.md` (exclusão de objetos e versões)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API e jobs Python | `rtk pytest -q -k v04_result` | exit 0; 8 testes passam; pytest falha se nenhum for coletado | `pyproject.toml`, dev dependency pytest; CI ausente |
| API e jobs Python | `rtk ruff check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| API e jobs Python | `rtk ruff format --check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| Compose | `rtk docker compose config -q` | exit 0 | `docker-compose.yml`; CI ausente |
| Imagem | `rtk docker compose build transcriber` | exit 0 | README e Dockerfile; CI ausente |

## Pronto quando

- [ ] Resultado v1 válido fica acessível ao dono dentro da retenção, com segmentos relativos e `pt-BR`.
- [ ] Job failed ou não concluído não entrega transcrição parcial; conta/chave alheia e job expirado não revelam existência.
- [ ] Expurgo remove resultado, metadados, mídia desnecessária e órfãos; a borda de 24 horas é comprovada.
- [ ] O smoke chega da criação ao resultado pela referência pública retornada; gate focalizado passa com exit 0.
