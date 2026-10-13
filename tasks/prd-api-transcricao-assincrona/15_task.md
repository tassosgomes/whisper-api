---
status: done
task_kind: vertical
blocked_by: []
gate: "rtk pytest -q -k request_alias_warning"
gate_expect: "2 testes passam"
---

# 15.0 Calar o aviso de alias do Pydantic na criação (opcional)

**Fatia:** V-08 · **Cobre:** RF-02 · **Spec:** `techspec.md#v-02` · **ADR:** —

## Comportamento

O log da api mostra `UnsupportedFieldAttributeWarning` do Pydantic para alias em `sourceUrl` e `clientReference`. O aviso não muda o comportamento. A declaração dos dois campos passa a ser aceita pela versão de Pydantic em uso, e o JSON aceito continua o mesmo: `sourceUrl` obrigatória e `clientReference` opcional. Nome interno diferente não passa a ser a forma pública do corpo.

Task opcional. Não bloqueia 9.0 a 13.0. O achado está em `tasks/prd-api-transcricao-assincrona/5.0_task_review.md`.

## Fora do escopo desta task

Alterar o pattern da `sourceUrl`, outros modelos com alias e o formato do resultado.

## Decisões fechadas

O contrato público desses dois nomes permanece o de `api-contract.yaml`. Não renomear o JSON para snake_case.

## Modificar / Referenciar

- **modificar:** `app/api/transcriptions.py` (declaração de `sourceUrl` e `clientReference`)
- **ref:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (nomes aceitos no corpo)
- **ref:** `tasks/prd-api-transcricao-assincrona/5.0_task_review.md` (aviso já observado)

## Verificações do projeto

Não há workflow de CI.

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Criação | `rtk pytest -q -k request_alias_warning` | exit 0; 2 testes passam; pytest sai 5 se nada for coletado | `pyproject.toml`; CI ausente |
| Lint | `rtk ruff check app` | exit 0 | `pyproject.toml`; CI ausente |
| Formato | `rtk ruff format --check app` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `rtk pytest -q -k request_alias_warning`
- [x] Os dois testes cobrem `sourceUrl` e `clientReference`, e nenhum emite `UnsupportedFieldAttributeWarning`
- [x] O corpo JSON aceito continua com esses dois nomes, não com o nome interno dos campos
