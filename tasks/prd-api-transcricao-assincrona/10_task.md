---
status: done
task_kind: enabling
blocked_by: ["9.0"]
gate: "rtk rg -q '/webhooks/v1/transcription-events' README.md"
gate_expect: "runbook registra o caminho do receptor, exit 0"
---

# 10.0 Cadastrar o destino do webhook da conta code-for-coders

**Fatia:** EN-05 · **Cobre:** RF-04, US-04 · **Spec:** `techspec.md#v-05` · **ADR:** ADR-002, ADR-003

## Comportamento

Esta task só começa depois que o receptor do media estiver implantado (CAP-032, fora deste repositório) e depois da 9.0. A 9.0 deixa `TRUSTED_PRIVATE_WEBHOOKS` vazia de propósito. Se o receptor estiver em IP privado, a lista de webhooks da api e do worker no Coolify precisa passar a incluir o host e a porta vindos da CAP-032 antes do cadastro. O caminho é `/webhooks/v1/transcription-events`. Em produção o media usa AWS S3 público na origem da mídia; isso não dispensa a allowlist se o receptor de webhook for privado.

Pela CLI do operador (`app/access/cli.py`, `webhook configure`; README, "Operação do piloto", seção 6):

- cadastrar a URL do receptor na conta code-for-coders, que já existe e é a dona da API Key em uso;
- gerar o segredo de assinatura e entregá-lo ao time do code-for-coders por canal seguro. Ele vira configuração do media. O segredo não entra no repositório, no README, em log nem em task.

O README passa a citar o caminho `/webhooks/v1/transcription-events` e a ordem: receptor implantado, allowlist de webhook se o host for privado, `webhook configure` na conta existente, entrega do segredo fora do git. Esse texto é o gate do plano. A evidência conclusiva é um job de teste que gera a entrega assinada e o receptor responde 204.

É habilitador porque o cadastro opera uma conta real já provisionada e depende de um receptor que este repositório não implementa. Não cabe numa fatia de código da API.

## Fora do escopo desta task

Implementar o receptor no media, rotacionar a API Key da conta, e preencher `TRUSTED_PRIVATE_SOURCES`.

## Decisões fechadas

Um destino por conta. O material de verificação é independente da API Key e aparece uma vez. Seguir ADR-002 para a assinatura e ADR-003 para aceitar o destino privado só se estiver na lista de webhooks. Não gravar o segredo no repositório.

## Modificar / Referenciar

- **modificar:** `README.md` (caminho do receptor e a ordem do cadastro, sem segredo e sem host inventado)
- **ref:** `app/access/cli.py` (`webhook configure`)
- **ref:** `docs/adr/adr-002.md`, `docs/adr/adr-003.md`

## Verificações do projeto

Não há check automatizado contra o receptor da CAP-032.

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Runbook | `rtk rg -q '/webhooks/v1/transcription-events' README.md` | exit 0 | README |
| CLI | `rtk python -m app.access.cli webhook configure --help` | exit 0 e o comando existe | `app/access/cli.py`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `rtk rg -q '/webhooks/v1/transcription-events' README.md`
- [ ] O receptor da CAP-032 está implantado e, se o host for privado, `TRUSTED_PRIVATE_WEBHOOKS` na api e no worker contém esse host e porta
- [ ] `webhook configure` gravou a URL na conta code-for-coders já existente
- [ ] O segredo foi entregue ao time do code-for-coders por canal seguro e não aparece no repositório
- [ ] Um job de teste gera a entrega assinada e o receptor responde 204
