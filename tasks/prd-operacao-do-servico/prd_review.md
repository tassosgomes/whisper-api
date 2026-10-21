# Full validation — CAP-002 Console operacional

Run: run.ZIItYu5z
Modo: full · Tentativa: 2/3 · Data: 2026-10-21

## Resultado

**FULL VALIDATION APROVADA** — 0 bloqueantes, 3 recomendações.

| Campo | Valor |
|---|---|
| base_ref | `2dfdb94d0660c9e263d510ce11ce642b11a47458` (= `main`, = merge-base) |
| target_ref | `main` |
| validated_commit | `f728b0bfc83fe1edbfd6c749917c797702fe4436` |
| validated_tree | `c7c8a15f5cb8fe6dd49f82816eb7e8703754b776` |
| Branch | `feature/operacao-do-servico` (11 commits: tasks 1.0–8.0, registro da full anterior, reabertura e correção da 1.0) |

Motivo desta tentativa: o Quality Gate do SonarCloud (PR #5) rejeitou a entrega pela regra
`python:S2092` em `app/operator.py:92` (cookie `operator_session` sem `Secure`), que era a
recomendação R-1 da full anterior (run.DAJBFEBn, `62480e8`). A task 1.0 foi reaberta (`afd8dc1`) e
corrigida (`f728b0b`); a revalidação da task está em `1.0_task_review.md` (run.CF83hiwr).

HEAD, árvore e `git status --porcelain` conferidos antes e depois da revisão: idênticos. A árvore de
trabalho só tem itens autorizados a ficar de fora (`routing.default.json` do usuário,
`local_meeting_transcriber.egg-info/`, `tasks/.../flow-state.json`, que entra no commit final).

## Delta revisado desde a full anterior (`62480e8..f728b0b`)

Código e configuração: `app/main.py`, `app/operator.py`, `.env.example`, `README.md`,
`docker-compose.yml`, `tests/conftest.py`, `tests/test_v01_operator_session.py`. Contrato OpenAPI
inalterado desde a full anterior. Conferência contra `context.txt` desta chamada:

| Decisão | Evidência | Situação |
|---|---|---|
| Cookie com `Secure` por padrão, HttpOnly e SameSite=Lax | `app/operator.py:92-99` usa `secure=settings.OPERATOR_COOKIE_SECURE`; `Settings` lê `OPERATOR_COOKIE_SECURE` com padrão `"true"` (`app/main.py:90-93`); só `false`/`0`/`no` desligam | atendida |
| `OPERATOR_COOKIE_SECURE` documentada | `.env.example:44`, tabela do README e parágrafo na seção do segredo operacional; repassada no `docker-compose.yml` com padrão `true` | atendida |
| Testes exercitam o padrão seguro e a configuração desligada, no seletor `v01_operator_session` | `TestClient(base_url="https://testserver")` nos fixtures; login prova `Secure`+`HttpOnly`; cookie seguro não volta em HTTP; teste parametrizado com `false` sobre HTTP | atendida |
| `delete_cookie` do logout com os mesmos atributos | `app/operator.py:109-115`; testes conferem `Secure`, `HttpOnly`, `SameSite`, `Path` no `Set-Cookie` de remoção, nos dois modos | atendida |
| Não alterar contrato nem outras fatias | `git diff 62480e8 HEAD` só toca os arquivos acima; `api-contract.yaml` sem diff | atendida |

O fixture `operator_cookie_secure_env` remove a variável quando não parametrizado, então um `.env`
local com `false` não mascara o teste do padrão seguro.

## Matriz de verificação

Não há workflow de CI nem Makefile no repositório (registrado em `tasks.md`, "Verificação herdada").
Fontes: `pyproject.toml`, `docker-compose.yml`, README §9 e `contracts.md`. Ambiente: `.env`
carregado, `postgres-test` (perfil `test`, porta 55432) saudável; comandos via `.venv/bin`.

| Componente | Passo obrigatório | Comando | Exit | Resultado |
|---|---|---|---|---|
| API, domínios e tela | Suíte agregada | `.venv/bin/pytest -q` | 0 | 231 passed (229 + 2 novos da 1.0), 3 warnings de terceiros |
| API, domínios e tela | Lint | `.venv/bin/ruff check app tests` | 0 | All checks passed |
| API, domínios e tela | Format | `.venv/bin/ruff format --check app tests` | 0 | 46 files already formatted |
| Migrations | `0006_administrative_audit` sobre `0005` | indireto via suíte (upgrade no `postgres-test`) | 0 | ok |
| Compose | Config | `docker compose config -q` | 0 | ok |
| Imagem | Build | `docker compose build transcriber` | 0 | `local-meeting-transcriber:dev` Built |
| Contrato OpenAPI | Lint Spectral | `npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-operacao-do-servico/api-contract.yaml --ruleset .claude/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=warn` | 0 | sem achados ≥ warn |

Sem limite de cobertura configurado. Não reproduzível localmente:

- **Quality Gate SonarCloud (PR #5):** análise externa, sem `sonar-project.properties` nem scanner
  local. A causa apontada (`S2092`, argumento `secure` ausente) foi removida: os dois pontos que
  emitem o cookie passam `secure` explícito com padrão verdadeiro. A confirmação depende da nova
  análise após o push; se o gate voltar a reprovar, a entrega volta para correção.
- **Observacional:** percurso manual no navegador em `/operator` (nenhum teste executa o JavaScript
  da tela). Com `Secure`, navegadores aceitam o cookie em `https://` e em `http://localhost`; outro
  host em HTTP exige `OPERATOR_COOKIE_SECURE=false`, como documentado.

## Rastreabilidade

A rastreabilidade de RF-01 a RF-06, contrato × implementação (18 `operationId`s) e segurança da tela
foi verificada na full anterior sobre `62480e8`. O delta desta tentativa não toca as fatias 2.0–8.0,
o contrato nem as migrations; a suíte agregada e o sensor abaixo reconfirmam o comportamento de todas
as fatias sobre `f728b0b`. RF-01 passa a cobrir também o atributo `Secure` do identificador de sessão.

## Sensor de discriminação

Executado em `git worktree` temporário (detached em HEAD) na scratchpad, nunca na árvore real;
import de `app` conferido a partir do worktree; worktree removido e baseline (HEAD +
`git status --porcelain`) confirmado idêntico ao final. Suíte focalizada por fatia.

| Mutante | Fatia / critério | Arquivo | Mutação | Resultado |
|---|---|---|---|---|
| MS1 | V-01 / cookie `Secure` por padrão | `app/operator.py` | `set_cookie(secure=False)` | morto |
| MS2 | V-01 / logout apaga com os mesmos atributos | `app/operator.py` | `delete_cookie` sem `secure` | morto |
| MS3 | V-01 / padrão verdadeiro | `app/main.py` | padrão de `OPERATOR_COOKIE_SECURE` vira `"false"` | morto |
| MS4 | V-01 / desligar só por configuração | `app/main.py` | `false` deixa de desligar | morto |
| M1 | V-01 / API Key não abre console | `app/operator.py` | login aceita segredo errado | morto |
| M2 | V-02 / nome vazio não cria | `app/access/credentials.py` | nome vazio vira `' '` | morto |
| M3 | V-03 / segredo exibido na emissão | `app/main.py` | emissão sem `apiKey` | morto |
| M4 | V-04 / chave antiga recusada | `app/access/credentials.py` | rotação não troca o hash | morto |
| M5 | V-05 / troca usa endereço novo | `app/jobs/webhooks.py` | URL não gravada | morto |
| M6 | V-06 / sobreposição de 72 h | `app/jobs/webhooks.py` | planejada expira na hora | morto |
| M7 | V-07 / terminal > 24 h ausente | `app/jobs/executor.py` | retenção ×3 | morto |
| M11 | V-07 / fila exige sessão | `app/operator.py` | `/v1/queue` fora dos prefixos | morto |
| M8 | V-08 / destino inativo visível | `app/jobs/webhooks.py` | ignora `webhook_disabled_at` | morto |

13 mutantes, 13 mortos, 0 sobreviventes.

## Bloqueantes

Nenhum. O bloqueio externo `S2092` (antiga R-1) foi corrigido conforme a decisão registrada.

## Recomendações (não bloqueiam)

- **R-2 — Sessões sem expiração.** `application.state.operator_sessions` cresce sem TTL e só é limpo
  no shutdown ou por `DELETE`/troca de segredo. Aceitável no piloto (processo único); gatilho futuro:
  múltiplos workers/réplicas ou console exposto por longos períodos.
- **R-3 — Destino desativado pelo receptor (410).** `rotateWebhookMaterial`/`deactivateWebhookDestination`
  respondem 404 nesse estado (`app/jobs/webhooks.py:354`) e um `PUT` com o mesmo endereço reativa com o
  material antigo. Herdado do piloto; registrar no runbook ou numa próxima capacidade.
- **R-4 — `operationId` das sessões.** `POST/DELETE /v1/operator-sessions` saem com `operationId`
  gerado pelo FastAPI, divergente de `openOperatorSession`/`closeOperatorSession` do contrato. Só afeta
  o OpenAPI exposto.
