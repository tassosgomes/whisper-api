---
status: done
task_kind: vertical
blocked_by: [1.0, 6.0]
gate: ".venv/bin/pytest -q -k v08_operator_delivery"
gate_expect: "ao menos 5 testes de tentativas de entrega passam, 0 falham"
---

# 8.0 Ver tentativas e falhas de entrega

**Fatia:** V-08 · **Cobre:** RF-06, US-04, Entrega de Notificações RN-04, RN-05, RN-06, RN-12, RN-13, Transcrição e Jobs RN-05 · **Spec:** `techspec.md#v-08-tentativas-de-entrega` · **ADR:** ADR-002

## Comportamento

`listDeliveryAttempts`, filtrável por conta ou por job, lista as tentativas com conta, job, identificador estável do evento, o resumo da falha e se ainda haverá nova tentativa. Uma falha que será tentada de novo aparece como pendente de retentativa; uma falha definitiva aparece encerrada. Em ambos os casos, o job continua `completed` ou `failed`, conforme o processamento que originou o aviso, e a leitura não altera o agendamento da próxima tentativa.

A lista não devolve material de verificação, corpo do aviso (`payload`) nem URL de origem da mídia. Uma tentativa ainda guardada depois de o job ter saído da retenção de 24 horas continua listada, identificada pelo id do job, sem reconstruir conteúdo.

Na tela, o operador abre as tentativas a partir da conta ou do job sem sair desse contexto e sem abrir texto de transcrição. A ficha da conta mostra o destino inativo quando foi desativado pelo operador (6.0) ou pela resposta do receptor.

## Fora do escopo desta task

Reenviar manualmente um aviso ou mudar a política de retentativa não está no PRD. Alerta operacional está fora de escopo.

## Decisões fechadas

Projeção de Entrega de Notificações com o resumo da falha, nunca o `payload` (`techspec.md#riscos-e-preocupações`, linha de `NotificationEvent.payload`). Política de retentativa herdada do piloto.

## Modificar / Referenciar

- **modificar:** `app/jobs/webhooks.py` (projeção das tentativas sem payload nem material)
- **modificar:** `app/main.py` (rota `listDeliveryAttempts`)
- **modificar:** a tela `/operator` (tentativas no contexto da conta e do job; destino inativo)
- **ref:** `app/jobs/models.py` (`NotificationEvent` sem FK com o job após a retenção)
- **ref:** `app/jobs/retention.py` (expurgo do job que não apaga a tentativa)
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`listDeliveryAttempts`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API, domínio e tela Python | `.venv/bin/pytest -q -k v08_operator_delivery` | exit 0; ao menos 5 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/pytest -q` | exit 0; tasks anteriores continuam passando | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v08_operator_delivery`
- [x] Falha em retentativa e falha definitiva aparecem distintas, e o estado do job não muda em nenhum dos casos.
- [x] Nenhuma tentativa listada contém `payload`, material ou URL de origem.
- [x] Tentativa de job já expurgado continua listada pelo id do job.
- [x] Destino desativado aparece inativo na consulta da conta.
