# Full validation — CAP-002 Console operacional

Run: run.DAJBFEBn
Modo: full · Tentativa: 1/3 · Data: 2026-10-21

## Resultado

**FULL VALIDATION APROVADA** — 0 bloqueantes, 4 recomendações.

| Campo | Valor |
|---|---|
| base_ref | `2dfdb94d0660c9e263d510ce11ce642b11a47458` (= `main`, = merge-base) |
| target_ref | `main` |
| validated_commit | `62480e895f80f005d56ba16ffae8b8c1c68dc21a` |
| validated_tree | `57a0e6ab575f04a753565618dfd82eb58e986d64` |
| Branch | `feature/operacao-do-servico` (8 commits, tasks 1.0–8.0) |

HEAD, árvore e `git status --porcelain` conferidos antes e depois da revisão: idênticos. A árvore
de trabalho só tem itens fora do escopo autorizados a ficar de fora (`routing.default.json` do
usuário, `local_meeting_transcriber.egg-info/`, `tasks/.../flow-state.json` — este entra no commit final).

## Matriz de verificação

Não há workflow de CI nem Makefile (registrado em `tasks.md`, "Verificação herdada"). Fontes:
`pyproject.toml`, `docker-compose.yml`, README §9 e `contracts.md`. Ambiente: `.env` carregado,
`postgres-test` (perfil `test`, porta 55432) saudável; `rtk` fora do PATH, comandos via `.venv/bin`.

| Componente | Passo obrigatório | Comando | Exit | Resultado |
|---|---|---|---|---|
| API, domínios e tela | Suíte agregada | `.venv/bin/pytest -q` | 0 | 229 passed (68 da base + 161 novos), 3 warnings de terceiros |
| API, domínios e tela | Lint | `.venv/bin/ruff check app tests` | 0 | All checks passed |
| API, domínios e tela | Format | `.venv/bin/ruff format --check app tests` | 0 | 46 files already formatted |
| Migrations | Aplicação de `0006_administrative_audit` sobre `0005` | indireto via suíte (upgrade no `postgres-test`) | 0 | ok |
| Compose | Config | `docker compose config -q` | 0 | ok |
| Imagem | Build | `docker compose build transcriber` | 0 | `local-meeting-transcriber:dev` Built |
| Contrato OpenAPI | Lint Spectral | `npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-operacao-do-servico/api-contract.yaml --ruleset .claude/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=warn` | 0 | sem achados ≥ warn |

Sem limite de cobertura configurado. Não reproduzível/observacional: percurso manual no navegador
em `/operator` (nenhum teste executa o JavaScript da tela; recomendado em `tasks.md` antes da entrega).

## Rastreabilidade

- **Contrato × implementação:** os 18 `operationId`s de `api-contract.yaml` existem em `app.openapi()`
  com os mesmos paths sob `/v1` e os códigos de erro mapeados (401/404/409/422). `openOperatorSession`
  e `closeOperatorSession` não declaram `operation_id` no FastAPI (ver R-4).
- **RF-01:** sessão própria (`app/operator.py`), `hmac.compare_digest` contra `OPERATOR_SECRET`,
  cookie HttpOnly só com identificador; middleware protege os prefixos do operador; `/v1/transcriptions`
  segue só com `X-API-Key`. Trilha `administrative_audit` gravada no mesmo `session.begin()` de cada
  mutação, com `actor="operational_credential"`, conta, objeto e motivo; sem segredo/URL de origem.
- **RF-02/RF-03:** nome vazio → 422 `ACCOUNT_NAME_REQUIRED`; segredo só na resposta de emissão/rotação;
  rotação conserva id e invalida hash antigo; revogada não é rotacionada (409); revogação exige motivo
  na rota e no domínio.
- **RF-04:** `put_destination` cria (material uma vez, 201) ou troca só o endereço (200, sem material);
  recusa antes de qualquer gravação; rotação planejada 72 h; imediata/desativação exigem motivo na rota
  e no domínio; desativação apaga materiais e encerra pendências.
- **RF-05:** fila conta não terminais com `accepted_at`; lista projeta só campos do contrato, sem
  `source_url_encrypted`, resultado ou lease; terminal > 24 h excluído; consulta do cliente intacta.
- **RF-06:** projeção de `NotificationEvent` sem `payload`, com resumos fixos de falha (nunca o texto
  armazenado), `willRetry`/`destinationActive` derivados; sem join com job (visível após retenção).
- **Segurança da tela:** renderização por `textContent` (47 usos), sem `innerHTML`/`insertAdjacentHTML`.

## Sensor de discriminação

Executado em `git worktree` temporário (detached em HEAD), nunca na árvore real; worktree removido
e baseline (HEAD + `git status --porcelain`) confirmado idêntico ao final. Suíte focalizada por fatia.

| Mutante | Fatia / critério | Arquivo | Mutação | Resultado |
|---|---|---|---|---|
| M1 | V-01 / API Key não abre console | `app/operator.py` | login aceita qualquer segredo | morto |
| M13 | V-01 / encerrar sessão invalida | `app/operator.py` | `DELETE` não remove a sessão | morto |
| M2 | V-02 / nome vazio não cria | `app/access/credentials.py` | nome vazio vira `' '` | morto |
| M2b | V-02 / trilha sem dado extra | `app/access/credentials.py` | nome gravado em `reason` | morto |
| M3 | V-03 / segredo não reexibido | `app/main.py` | `getCredential` devolve `apiKey` | morto |
| M4 | V-04 / chave antiga recusada | `app/access/credentials.py` | rotação não troca o hash | morto |
| M9 | V-04 / trilha guarda motivo | `app/main.py` | revogação sem auditoria | morto |
| M5 | V-05 / troca usa endereço novo | `app/jobs/webhooks.py` | `_set_endpoint` não grava URL | morto |
| M6 | V-06 / sobreposição de 72 h | `app/jobs/webhooks.py` | planejada expira na hora | morto |
| M10 | V-06 / desativação exige motivo (domínio) | `app/jobs/webhooks.py` | motivo vazio vira `x` | morto |
| M10b | V-06 / imediata exige motivo (rota) | `app/main.py` | guarda removida | morto (3 FAILED) |
| M7 | V-07 / terminal > 24 h ausente | `app/jobs/executor.py` | retenção ×3 | morto |
| M11 | V-07 / fila exige sessão | `app/operator.py` | `/v1/queue` fora dos prefixos | morto |
| M8 | V-08 / destino inativo visível | `app/jobs/webhooks.py` | ignora `webhook_disabled_at` | morto |
| M8b | V-08 / falha encerrada sem retentativa | `app/jobs/webhooks.py` | ignora `retry_deadline` | morto |
| M12 | V-08 / falha sem texto armazenado | `app/jobs/webhooks.py` | ecoa `last_error_code` cru | morto |

16 mutantes, 16 mortos, 0 sobreviventes.

## Bloqueantes

Nenhum.

## Recomendações (não bloqueiam)

- **R-1 — Cookie sem `Secure`.** `app/operator.py:92` emite `operator_session` com HttpOnly e
  SameSite=Lax, mas sem `secure=True`. ADR-004 e o contrato pedem só HttpOnly; atrás de TLS, marcar
  `Secure` (ou condicioná-lo à configuração) evita envio do identificador em HTTP claro.
- **R-2 — Sessões sem expiração.** `application.state.operator_sessions` cresce sem TTL e só é limpo no
  shutdown ou por `DELETE`/troca de segredo. Aceitável no piloto (processo único, `uvicorn` sem
  `--workers`); gatilho futuro: múltiplos workers/réplicas ou console exposto por longos períodos —
  então mover para expiração por inatividade ou armazenamento compartilhado.
- **R-3 — Destino desativado pelo receptor (410).** O dispatcher marca `webhook_disabled_at` mas mantém
  URL e material; `rotateWebhookMaterial`/`deactivateWebhookDestination` passam a responder 404 nesse
  estado (`_locked_destination`, `app/jobs/webhooks.py:354`), e um `PUT` com o mesmo endereço o
  reativa com o material antigo. Comportamento herdado do piloto (`set_endpoint`) e coerente com o
  contrato, mas o operador não consegue invalidar esse material sem antes reativar. Vale registrar no
  runbook ou numa próxima capacidade.
- **R-4 — `operationId` das sessões.** As rotas `POST/DELETE /v1/operator-sessions` saem com
  `operationId` gerado pelo FastAPI, divergente de `openOperatorSession`/`closeOperatorSession` do
  contrato. Não afeta o comportamento; afeta só o OpenAPI exposto.

Observação: `CredentialStore.rotate` deixou de reativar credencial revogada (antes zerava
`revoked_at`); o CLI `rotate` sobre chave revogada agora sai com código 2. É coerente com RN-07 e com
o 409 `CREDENTIAL_REVOKED` do contrato.
