---
status: done
task_kind: enabling
blocked_by: []
gate: "rtk rg -q 'ADR-003' context/architecture-baseline.md"
gate_expect: "baseline cita a exceção da ADR-003, exit 0"
---

# 13.0 Atualizar o baseline arquitetural com a exceção da ADR-003

**Fatia:** EN-07 · **Cobre:** guardrail de segurança do baseline, sem RF novo · **Spec:** `techspec.md#arquitetura-da-solução` · **ADR:** ADR-003

## Comportamento

`context/architecture-baseline.md` ainda manda bloquear redes privadas para URL de origem. A frase passa a manter esse bloqueio e a acrescentar a exceção controlada da ADR-003, só para destinos listados. Destino fora da lista, loopback, link-local e metadados continuam recusados. A exceção não vira permissão genérica de IP privado nem de `http`.

É habilitador porque o baseline é lido por todas as features, inclusive a CAP-002. Não produz comportamento de runtime. Evita que uma spec nova reimplemente a regra antiga e desfaça a ADR-003.

## Fora do escopo desta task

Alterar código de validação, variáveis do Coolify ou o schema OpenAPI.

## Decisões fechadas

A ADR-003 está aceita. Não reabrir o desenho das duas listas. Não copiar o texto da ADR para o baseline; citar a decisão e o limite da exceção.

## Modificar / Referenciar

- **modificar:** `context/architecture-baseline.md` (frase que hoje só manda bloquear redes privadas)
- **ref:** `docs/adr/adr-003.md`

## Verificações do projeto

Não há lint de markdown no CI.

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Baseline | `rtk rg -q 'ADR-003' context/architecture-baseline.md` | exit 0 | ADR-003 |
| Baseline | `rtk git diff --check -- context/architecture-baseline.md` | exit 0, sem erro de whitespace | git |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `rtk rg -q 'ADR-003' context/architecture-baseline.md`
- [x] A frase que bloqueia redes privadas inclui a exceção só para destinos listados na ADR-003
- [x] O texto não autoriza IP privado fora da lista
