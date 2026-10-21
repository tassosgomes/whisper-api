---
status: done
task_kind: vertical
blocked_by: [3.0]
gate: ".venv/bin/pytest -q -k v04_operator_credential_lifecycle"
gate_expect: "ao menos 6 testes de rotação e revogação passam, 0 falham"
---

# 4.0 Rotacionar e revogar API Key com motivo

**Fatia:** V-04 · **Cobre:** RF-01 (trilha), RF-03, US-03, Acesso e Contas RN-03, RN-04, RN-06, RN-07, RN-10, Entrega de Notificações RN-03 · **Spec:** `techspec.md#v-04-rotacionar-e-revogar-api-key` · **ADR:** ADR-004

## Comportamento

`rotateCredential` numa credencial ativa responde `200` com o mesmo `credentialId` e o segredo novo, mostrado uma vez na tela. A chave anterior passa a receber a recusa da API pública; a nova autentica. Jobs criados antes da rotação continuam associados à credencial e legíveis com a chave nova. Material e destino de webhook da conta não mudam. A trilha registra a rotação, sem segredo, no mesmo commit. Rotacionar credencial revogada responde `409 CREDENTIAL_REVOKED` sem alterar nada.

`revokeCredential` exige motivo. Motivo ausente ou vazio responde `422 REASON_REQUIRED`, a revogação não é chamada e a chave continua autenticando; a tela não apresenta isso como sucesso. Com motivo, a revogação é imediata: chamadas seguintes com a chave recebem a recusa da API pública sem revelar job, estado nem conteúdo; a resposta e a ficha mostram a situação revogada sem o segredo; a trilha guarda o motivo no mesmo commit da revogação. Se a trilha falhar, a credencial não fica revogada.

Credencial inexistente responde `404 NOT_FOUND` nas duas operações.

## Fora do escopo desta task

Rotação de material de webhook fica em 6.0. Reativar credencial revogada não existe.

## Decisões fechadas

Rotação e revogação seguem as regras atuais de `CredentialStore`; o motivo e a trilha precisam do mesmo commit da mutação (`techspec.md#riscos-e-preocupações`, linha de `revoke`). Códigos de [api-contract.yaml](api-contract.yaml).

## Modificar / Referenciar

- **modificar:** `app/access/credentials.py` (rotação e revogação com trilha e motivo no mesmo commit)
- **modificar:** `app/main.py` (rotas `rotateCredential`, `revokeCredential`)
- **modificar:** a tela `/operator` (rotação com cópia única; revogação que pede motivo antes de confirmar)
- **ref:** `app/jobs/webhooks.py` (material que a rotação da chave não toca)
- **ref:** `app/api/transcriptions.py` (recusa neutra e leitura limitada à credencial criadora)
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`rotateCredential`, `revokeCredential`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API, domínio e tela Python | `.venv/bin/pytest -q -k v04_operator_credential_lifecycle` | exit 0; ao menos 6 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/pytest -q` | exit 0; tasks anteriores continuam passando | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v04_operator_credential_lifecycle`
- [x] Rotação conserva o id, invalida a chave antiga, mantém os jobs legíveis com a nova e não altera o material de webhook.
- [x] Revogação sem motivo responde `422` e a chave continua válida.
- [x] Revogação com motivo recusa a chave sem revelar job e grava o motivo na trilha no mesmo commit.
- [x] Rotação de credencial revogada responde `409 CREDENTIAL_REVOKED`.
