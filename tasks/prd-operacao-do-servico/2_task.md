---
status: done
task_kind: vertical
blocked_by: [1.0]
gate: ".venv/bin/pytest -q -k v02_operator_account"
gate_expect: "ao menos 5 testes de conta e trilha passam, 0 falham"
---

# 2.0 Criar e listar contas com trilha administrativa

**Fatia:** V-02 · **Cobre:** RF-01 (trilha), RF-02, US-01, Acesso e Contas RN-01, RN-10 · **Spec:** `techspec.md#v-02-criar-e-listar-conta` · **ADR:** ADR-004

## Comportamento

Na tela, o operador autenticado informa o nome e confirma. `createAccount` com nome não vazio responde `201` com a conta, e no mesmo commit grava a entrada da trilha administrativa: momento, o fato de ter sido feita pela credencial operacional, conta, tipo e identificador do objeto. A trilha não guarda segredo, mídia, transcrição nem URL de origem. Se a gravação da trilha falhar, a conta também não existe.

Nome vazio, ou só espaços, responde `422 ACCOUNT_NAME_REQUIRED`, não cria conta nem trilha, e a tela permanece no formulário de criação com o erro.

`listAccounts` devolve as contas paginadas (`_page`, `_size`, padrão 10, máximo 100) e permite distinguir duas contas pelo nome. Nenhuma resposta de conta traz segredo de API Key nem material de webhook. `getAccount` de conta inexistente responde `404 NOT_FOUND`. A conta criada fica disponível para emissão de credencial e configuração de destino nas tasks seguintes.

A trilha é a estrutura que 3.0 a 6.0 reutilizam: o tipo de ação e o objeto variam, e há lugar para o motivo quando a ação o exige. A nova migration sobe sobre `0005_webhook_delivery` e a limpeza dos testes a inclui.

O CLI do piloto (`app/access/cli.py`) continua funcionando como antes.

## Fora do escopo desta task

Emissão de chave fica em 3.0; destino em 5.0. Editar ou excluir conta não está no PRD. Consulta da trilha pela tela não está no contrato.

## Decisões fechadas

A trilha pertence a Acesso e Contas e commita junto com a mutação (`techspec.md#bloco-backend`). Códigos de erro e schemas vêm de [api-contract.yaml](api-contract.yaml). Criar conta não emite credencial: `createAccount` é só a conta, diferente de `provision_account` do CLI.

## Modificar / Referenciar

- **modificar:** `app/access/credentials.py` (criação de conta sozinha, com a trilha no mesmo commit)
- **modificar:** `app/access/models.py` (entidade da trilha administrativa)
- **modificar:** `app/main.py` (rotas `createAccount`, `listAccounts`, `getAccount` sob a sessão operacional)
- **modificar:** `tests/conftest.py` (limpeza da trilha entre testes)
- **modificar:** a tela `/operator` criada em 1.0 (formulário de conta e lista)
- **ref:** `app/access/cli.py` (procedimento manual que permanece)
- **ref:** `migrations/versions/0005_webhook_delivery.py` (revisão anterior)
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`createAccount`, `listAccounts`, `getAccount`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API, domínio e tela Python | `.venv/bin/pytest -q -k v02_operator_account` | exit 0; ao menos 5 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/pytest -q` | exit 0; base e 1.0 continuam passando | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v02_operator_account`
- [x] Conta criada aparece na lista pelo nome, com uma entrada de trilha sem segredo no mesmo commit.
- [x] Nome vazio responde `422 ACCOUNT_NAME_REQUIRED` e não deixa conta nem trilha.
- [x] Falha forçada na gravação da trilha não deixa conta criada.
- [x] Nenhuma resposta de conta contém segredo; conta inexistente responde `404`.
