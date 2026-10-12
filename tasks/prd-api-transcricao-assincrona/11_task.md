---
status: done
task_kind: enabling
blocked_by: []
gate: "rtk rg -q 'ADR-003' tasks/prd-api-transcricao-assincrona/api-contract.yaml"
gate_expect: "schema da sourceUrl cita a ADR-003, exit 0"
---

# 11.0 Corrigir o contrato da sourceUrl

**Fatia:** EN-06 · **Cobre:** RF-02 · **Spec:** `techspec.md#v-02` · **ADR:** ADR-003

## Comportamento

`tasks/prd-api-transcricao-assincrona/api-contract.yaml` ainda restringe `sourceUrl` com `pattern: '^https://'`. Desde a PR #2, a ADR-003 faz o código aceitar `http://` para destinos da allowlist. O schema e a descrição da `sourceUrl` no YAML e em `api-contract.md` passam a descrever essa exceção e a citar a ADR-003. Fora da allowlist, o contrato continua exigindo HTTPS para endereço público. O exemplo público existente pode permanecer HTTPS. Um exemplo ou a descrição deixa claro que `http://` só vale para destino listado.

É habilitador porque o mesmo schema é o acordo de todos os clientes da criação. Nenhuma fatia de download pode contradizer o contrato público sozinha. Desbloqueia a leitura correta do contrato por quem integra a origem privada.

## Fora do escopo desta task

Mudar a validação em código, configurar o Coolify e aceitar `http://` genérico.

## Decisões fechadas

Aplicar a ADR-003. Não reabrir a allowlist vazia por padrão nem a separação entre lista de origem e lista de webhook.

## Modificar / Referenciar

- **modificar:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (pattern e descrição de `sourceUrl`)
- **modificar:** `tasks/prd-api-transcricao-assincrona/api-contract.md` (a mesma descrição)
- **ref:** `docs/adr/adr-003.md`
- **ref:** `tasks/prd-api-transcricao-assincrona/contracts.md` (comando de lint)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| OpenAPI | `rtk npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-api-transcricao-assincrona/api-contract.yaml --ruleset .agents/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=error` | exit 0; 0 erros | `contracts.md` |
| Schema | `rtk rg -q 'ADR-003' tasks/prd-api-transcricao-assincrona/api-contract.yaml` | exit 0 | ADR-003 |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `rtk rg -q 'ADR-003' tasks/prd-api-transcricao-assincrona/api-contract.yaml`
- [x] YAML e `api-contract.md` descrevem a exceção `http://` só para destino da allowlist e citam a ADR-003
- [x] O lint Spectral acima termina com exit 0 e 0 erros
