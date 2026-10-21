---
status: pending
task_kind: vertical
blocked_by: [2.0]
gate: ".venv/bin/pytest -q -k v05_operator_destination"
gate_expect: "ao menos 6 testes de configuração do destino passam, 0 falham"
---

# 5.0 Configurar e trocar o destino de webhook

**Fatia:** V-05 · **Cobre:** RF-01 (trilha), RF-04, US-02, US-03, Entrega de Notificações RN-01, RN-02, RN-07, RN-08, RN-11 · **Spec:** `techspec.md#v-05-configurar-o-destino-de-webhook` · **ADR:** ADR-002, ADR-003

## Comportamento

`putWebhookDestination` numa conta sem destino, com endereço que o piloto aceita, responde `201` com o destino e o `verificationMaterial`. A tela mostra o material uma vez para cópia. O próximo job terminal da conta é entregue nesse endereço com assinatura verificável pelo material.

Com destino já configurado, o mesmo `PUT` com outro endereço aceito responde `200`, troca só o endereço, conserva o material atual e não o devolve. As entregas seguintes usam o endereço novo e continuam verificáveis pelo mesmo material. A conta nunca fica com dois destinos.

Endereço que o piloto recusaria, inclusive pela allowlist da ADR-003, responde `422 DESTINATION_REJECTED`, não grava nada e deixa o destino anterior, se houver, exatamente como estava. Endereço privado na allowlist é aceito.

`getWebhookDestination` devolve endereço e situação sem `verificationMaterial`; conta sem destino responde `404 DESTINATION_NOT_FOUND`; conta inexistente, `404 NOT_FOUND`. Conta sem destino continua com a API Key aceita na API pública e não gera aviso.

Criação e troca de endereço gravam a trilha no mesmo commit, com o objeto e sem o material nem a URL de origem de mídia.

## Fora do escopo desta task

Rotação de material e desativação ficam em 6.0. O contrato do webhook de saída não muda.

## Decisões fechadas

Criação pelo fluxo que gera o material; troca pelo fluxo que não o substitui (`techspec.md#riscos-e-preocupações`, linha de `configure`). Critério de endereço da ADR-003. Assinatura da ADR-002.

## Modificar / Referenciar

- **modificar:** `app/jobs/webhooks.py` (criação e troca de endereço com trilha no mesmo commit)
- **modificar:** `app/main.py` (rotas `getWebhookDestination`, `putWebhookDestination`)
- **modificar:** a tela `/operator` (configuração do destino com cópia única do material)
- **ref:** `app/access/credentials.py` e `app/access/models.py` (trilha criada em 2.0)
- **ref:** `docs/adr/adr-002.md`, `docs/adr/adr-003.md`
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`getWebhookDestination`, `putWebhookDestination`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API, domínio e tela Python | `.venv/bin/pytest -q -k v05_operator_destination` | exit 0; ao menos 6 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/pytest -q` | exit 0; tasks anteriores continuam passando | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v05_operator_destination`
- [ ] Criação devolve o material uma vez e o próximo aviso terminal chega ao endereço com assinatura válida, usando o receptor HTTPS controlado do `conftest`.
- [ ] Troca de endereço não devolve material e a entrega seguinte usa o endereço novo com o mesmo material.
- [ ] Endereço recusado responde `422` e preserva o destino anterior.
- [ ] A consulta do destino não traz material; conta sem destino aceita a API Key e não gera aviso.
