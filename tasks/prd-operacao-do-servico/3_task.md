---
status: done
task_kind: vertical
blocked_by: [2.0]
gate: ".venv/bin/pytest -q -k v03_operator_credential"
gate_expect: "ao menos 5 testes de emissão e metadados passam, 0 falham"
---

# 3.0 Emitir API Key com segredo de exibição única

**Fatia:** V-03 · **Cobre:** RF-01 (trilha), RF-03, US-01, Acesso e Contas RN-01, RN-02, RN-05, RN-08, RN-10 · **Spec:** `techspec.md#v-03-emitir-api-key-e-consultar-metadados` · **ADR:** ADR-004

## Comportamento

Na ficha da conta, o operador emite uma chave. `issueCredential` responde `201` com `credentialId`, conta dona, situação ativa, as permissões iniciais do piloto e o `apiKey`. Essa é a única resposta que contém o segredo. A tela o mostra para cópia, avisa que não haverá segunda exibição e que perda implica rotação, e não o guarda fora da memória dessa resposta. O segredo é persistido só como hash. A trilha registra a emissão com o `credentialId`, sem o segredo, no mesmo commit.

A chave emitida autentica `/v1/transcriptions`. Emitir outra chave na mesma conta gera `credentialId` distinto, e a anterior continua autenticando.

`getCredential` e `listCredentials` devolvem identificador, conta, permissões, criação, último uso e situação de revogação ou rotação, sem `apiKey`. Reabrir a ficha na tela não reconstrói o segredo, inclusive para uma chave cujo segredo se perdeu: não existe operação que o devolva. Conta ou credencial inexistente responde `404 NOT_FOUND`.

## Fora do escopo desta task

Rotação e revogação ficam em 4.0. Permissões diferentes das do piloto não existem nesta entrega.

## Decisões fechadas

Regras atuais de emissão e hash de `CredentialStore` (`techspec.md#mapeamento-do-contrato-de-api`). Schemas com e sem `apiKey` vêm de [api-contract.yaml](api-contract.yaml).

## Modificar / Referenciar

- **modificar:** `app/access/credentials.py` (emissão com trilha no mesmo commit)
- **modificar:** `app/main.py` (rotas `issueCredential`, `listCredentials`, `getCredential`)
- **modificar:** a tela `/operator` (emissão com cópia única e ficha da credencial)
- **ref:** `app/api/transcriptions.py` (a chave emitida autentica a API pública)
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`issueCredential`, `listCredentials`, `getCredential`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API, domínio e tela Python | `.venv/bin/pytest -q -k v03_operator_credential` | exit 0; ao menos 5 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/pytest -q` | exit 0; tasks anteriores continuam passando | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v03_operator_credential`
- [x] A emissão devolve o segredo uma vez; a chave autentica a API pública; o banco guarda só o hash.
- [x] A segunda leitura da mesma credencial e a lista não contêm `apiKey`.
- [x] Segunda emissão tem id próprio e a primeira chave continua válida.
- [x] A trilha da emissão contém o `credentialId` e não o segredo.
