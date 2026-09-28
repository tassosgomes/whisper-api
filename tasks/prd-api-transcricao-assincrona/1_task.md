---
status: pending
task_kind: enabling
blocked_by: []
gate: "rtk npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-api-transcricao-assincrona/api-contract.yaml --ruleset .agents/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=error"
gate_expect: "OpenAPI válido, 0 erros de lint"
---

# 1.0 Atualizar e aprovar o contrato HTTP público

**Fatia:** EN-03 · **Cobre:** RF-01 a RF-05 · **Spec:** `techspec.md#habilitadores-inevitáveis` · **ADR:** ADR-001, ADR-002

## Comportamento

O acordo HTTP do piloto passa a descrever exatamente as operações e respostas aprovadas na TechSpec: `X-API-Key`, permissões iniciais iguais com leitura restrita à credencial criadora, idempotência de 120 segundos por conta e credencial, limite de 5 GiB, formatos aceitos, política de validade e tentativas da URL GET, estados, resultado v1, retenção, SLOs iniciais e Standard Webhooks v1 com rotação e retries. `api-contract.md` e `contracts.md` refletem o OpenAPI atualizado e deixam de apresentar decisões já fechadas como pendentes. O histórico distingue a aprovação do acordo de qualquer implantação.

É um habilitador compartilhado porque a mesma definição pública governa criação, consulta, resultado e notificação; nenhuma fatia isolada pode fixar essas interfaces para as demais sem produzir contrato inconsistente. Desbloqueia V-01 e as demais fatias.

## Fora do escopo desta task

Implementação das rotas, cliente consumidor e API administrativa da fase 2.

## Decisões fechadas

Aplicar Q-01 a Q-11 já resolvidas na TechSpec, o baseline, a ADR-001 e a ADR-002. Não reabrir limites, autenticação ou política do webhook.

## Modificar / Referenciar

- **modificar:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (acordo HTTP)
- **modificar:** `tasks/prd-api-transcricao-assincrona/api-contract.md` (documentação derivada)
- **modificar:** `tasks/prd-api-transcricao-assincrona/contracts.md` (estado, decisões e validação do acordo)
- **ref:** `tasks/prd-api-transcricao-assincrona/prd.md` (aceite de produto)
- **ref:** `tasks/prd-api-transcricao-assincrona/techspec.md` (Q-01 a Q-11 e operações)
- **ref:** `context/architecture-baseline.md` (fronteiras e guardrails)
- **ref:** `docs/adr/adr-001.md`, `docs/adr/adr-002.md` (decisões aceitas)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| OpenAPI | `rtk npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-api-transcricao-assincrona/api-contract.yaml --ruleset .agents/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=error` | exit 0; 0 erros | `contracts.md` e ruleset local |
| Documentação | `rtk git diff --check -- tasks/prd-api-transcricao-assincrona/api-contract.md tasks/prd-api-transcricao-assincrona/contracts.md` | exit 0; sem erro de whitespace | Git; CI ausente |

## Pronto quando

- [ ] O OpenAPI e a documentação derivada descrevem as mesmas operações, códigos e políticas aprovadas.
- [ ] O índice de contratos registra aprovação do acordo e as pendências de implementação externa sem reapresentar Q-01 a Q-11 como abertas.
- [ ] O gate Spectral passa com exit 0.
