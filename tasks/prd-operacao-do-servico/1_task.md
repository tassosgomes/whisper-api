---
status: in_progress
task_kind: vertical
blocked_by: []
gate: ".venv/bin/pytest -q -k v01_operator_session"
gate_expect: "ao menos 7 testes de sessão operacional passam, 0 falham"
---

# 1.0 Entrar no console com a credencial operacional

**Fatia:** V-01 · **Cobre:** RF-01, Acesso e Contas RN-09 · **Spec:** `techspec.md#v-01-sessão-do-operador` · **ADR:** ADR-004

## Comportamento

`GET /operator` serve a tela. Sem sessão válida, a página só oferece o formulário de entrada: não traz contas, credenciais, destinos, fila nem tentativas embutidas, e toda rota do operador do contrato chamada sem sessão responde `401 UNAUTHORIZED`.

`openOperatorSession` com o segredo operacional configurado no processo responde `204` e abre o cookie `operator_session`, HttpOnly, que carrega só um identificador opaco da sessão. A comparação é em tempo constante e não passa por `CredentialStore.authenticate` nem consulta a tabela de credenciais. Segredo errado, segredo ausente ou uma API Key válida de cliente no lugar do segredo respondem `401` sem abrir cookie. Com a sessão aberta, uma rota do operador deixa de responder `401`, e a tela passa a mostrar as áreas das jornadas, que as tasks seguintes preenchem.

O cookie de operador enviado a `/v1/transcriptions` (criar ou consultar) não autentica: a resposta continua a recusa da API pública. `closeOperatorSession` responde `204` e invalida o identificador; o mesmo cookie volta a receber `401`. Trocar o segredo do processo invalida as sessões abertas.

O corpo do login e o valor comparado não aparecem em log. Processo sem segredo operacional configurado não abre sessão para nenhum valor.

O README ganha o runbook: onde configurar o segredo, como distribuí-lo e rotacioná-lo, e a URL `/operator` atrás do mesmo proxy que `/v1`, sem gravar o segredo.

## Fora do escopo desta task

As jornadas de conta, chave, destino, fila e tentativas entram de 2.0 a 8.0. Operadores nomeados e trilha com pessoa não existem nesta entrega. A trilha administrativa nasce em 2.0; abrir ou fechar sessão não é ação administrativa sobre conta.

## Decisões fechadas

ADR-004 inteira: formulário no mesmo host, cookie `operator_session` HttpOnly, verificador separado de `X-API-Key`, sessão como estado do processo. A tela fica em `/operator` e as rotas seguem os paths de [api-contract.yaml](api-contract.yaml) sob `/v1`. O contrato público não muda.

## Modificar / Referenciar

- **modificar:** `app/main.py` (servir `/operator`, montar as rotas do operador com o verificador de sessão, sem reutilizar o autenticador da API pública)
- **modificar:** `README.md` (runbook do segredo operacional e da URL `/operator`)
- **modificar:** `.env.example` (placeholder do segredo operacional, sem valor real)
- **modificar:** `docker-compose.yml` (repasse do segredo operacional ao serviço `transcriber`)
- **modificar:** `tests/conftest.py` (segredo operacional de teste no ambiente do pytest e fixture de sessão aberta)
- **ref:** `app/access/credentials.py` (`authenticate`, que o verificador não usa)
- **ref:** `app/api/transcriptions.py` (autenticação da API pública que não pode aceitar o cookie)
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`openOperatorSession`, `closeOperatorSession`, scheme `OperatorSession`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API e tela Python | `.venv/bin/pytest -q -k v01_operator_session` | exit 0; ao menos 7 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API e tela Python | `.venv/bin/pytest -q` | exit 0; os 68 testes da base continuam passando | `pyproject.toml`; CI ausente |
| API e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| Compose | `docker compose config -q` | exit 0 | `docker-compose.yml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v01_operator_session`
- [x] Os testes sobem a aplicação real (`app.main.app`) com os registros reais, sem dublê do verificador.
- [x] Sem sessão, `/operator` não contém dados operacionais e as rotas do operador respondem `401`.
- [x] Segredo correto abre o cookie HttpOnly sem segredo nem API Key no valor; `DELETE` invalida o identificador.
- [x] API Key válida no login recebe `401`; cookie de operador em `/v1/transcriptions` é recusado.
- [x] Nenhum log do teste contém o segredo operacional.
