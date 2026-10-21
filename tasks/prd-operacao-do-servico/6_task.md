---
status: pending
task_kind: vertical
blocked_by: [5.0]
gate: ".venv/bin/pytest -q -k v06_operator_destination_lifecycle"
gate_expect: "ao menos 5 testes de rotação e desativação do destino passam, 0 falham"
---

# 6.0 Rotacionar o material e desativar o destino

**Fatia:** V-06 · **Cobre:** RF-01 (trilha), RF-04, US-03, Entrega de Notificações RN-03, RN-09, RN-10, Acesso e Contas RN-06 · **Spec:** `techspec.md#v-06-rotacionar-material-e-desativar-destino` · **ADR:** ADR-002

## Comportamento

`rotateWebhookMaterial` planejada responde `200` com o material novo, mostrado uma vez. O anterior continua válido por 72 horas: avisos assinados nesse intervalo são verificáveis pelos dois, e depois disso só pelo novo.

A rotação imediata exige motivo. Sem motivo, responde `422 REASON_REQUIRED` e a rotação não é chamada: o material atual continua o único válido. Com motivo, o anterior deixa de valer na hora, o novo aparece uma vez e a trilha guarda o motivo no mesmo commit.

`deactivateWebhookDestination` exige motivo. Sem motivo, responde `422 REASON_REQUIRED` e o destino continua ativo e entregando. Com motivo, novas entregas cessam, as pendências deixam de ser enviadas, os materiais ativos deixam de valer, e `getWebhookDestination` passa a mostrar o destino inativo, sem material. A trilha guarda o motivo no mesmo commit da desativação; se a trilha falhar, o destino continua ativo.

Rotacionar a API Key da conta não altera o destino nem o material, e rotacionar o material não altera a API Key. Na tela, a rotação imediata e a desativação pedem o motivo antes de confirmar.

## Fora do escopo desta task

Reativar destino desativado não está no PRD; uma nova configuração segue 5.0. A lista de tentativas fica em 8.0.

## Decisões fechadas

Reusar a rotação e o `disable` existentes, com a trilha dentro do mesmo commit (`techspec.md#riscos-e-preocupações`, linha de `disable`). Janela de 72 horas herdada do piloto e da ADR-002.

## Modificar / Referenciar

- **modificar:** `app/jobs/webhooks.py` (rotação e desativação com motivo e trilha no mesmo commit)
- **modificar:** `app/main.py` (rotas `rotateWebhookMaterial`, `deactivateWebhookDestination`)
- **modificar:** a tela `/operator` (rotação planejada e imediata, desativação com motivo)
- **ref:** `app/access/credentials.py` (rotação de chave que não toca o destino)
- **ref:** `docs/adr/adr-002.md`
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`rotateWebhookMaterial`, `deactivateWebhookDestination`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API, domínio e tela Python | `.venv/bin/pytest -q -k v06_operator_destination_lifecycle` | exit 0; ao menos 5 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/pytest -q` | exit 0; tasks anteriores continuam passando | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v06_operator_destination_lifecycle`
- [ ] Rotação planejada mantém o material anterior válido por 72 horas, comprovado com relógio controlado.
- [ ] Rotação imediata e desativação sem motivo respondem `422` e não alteram material nem destino.
- [ ] Desativação com motivo cessa entregas, invalida materiais e mostra o destino inativo, com motivo na trilha.
- [ ] Rotação de API Key e rotação de material não interferem uma na outra.
