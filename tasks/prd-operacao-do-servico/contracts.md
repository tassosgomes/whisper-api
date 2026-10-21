---
tsg_artifact: contract
product: whisper
version: 0.1
status: approved
updated: 2026-10-13
capability: CAP-002
sources: tasks/prd-operacao-do-servico/prd.md@0.1
---

# Contratos de integração — Console operacional

> PRD: `tasks/prd-operacao-do-servico/prd.md`  
> Data: 2026-10-13  
> Estado do conjunto: Aprovado em 2026-10-21

Este conjunto registra o acordo para esta implementação. PRDs posteriores podem evoluí-lo. Não afirma qual contrato está em produção. Catalogação, armazenamento definitivo e mecanismos de atualização do acervo são definidos pelo time de plataforma.

## Seleção e escopo

O console cria uma interface HTTP nova, consumida pela tela no mesmo monólito. Modalidade: OpenAPI. Não há mensagem nova nem acordo de dados compartilhado com outro produto: o aviso terminal continua o webhook já descrito no contrato da API pública. ODCS e AsyncAPI não se aplicam.

| Documento | Modalidade/versão do padrão | Versão do contrato | Escopo completo ou recorte | Status |
|---|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) | OpenAPI 3.1.0 | 1.0.0 | Superfície do operador. Não substitui o contrato público de transcrição. | Aprovado |

## Participantes e interfaces

| Documento e identificador técnico | Provedor/produtor | Consumidores conhecidos | Comportamento ou compromisso | Requisitos do PRD |
|---|---|---|---|---|
| `openOperatorSession` | Whisper | Tela do operador | Credencial compartilhada abre sessão. API Key não abre. | RF-01 |
| `createAccount`, `listAccounts`, `getAccount` | Whisper | Tela do operador | Conta nomeada, sem segredos na lista. | RF-02 |
| `issueCredential`, `getCredential`, `rotateCredential`, `revokeCredential` | Whisper | Tela do operador | Segredo uma vez. Rotação preserva o id. Revogação exige motivo. | RF-03 |
| `putWebhookDestination`, `rotateWebhookMaterial`, `deactivateWebhookDestination`, `getWebhookDestination` | Whisper | Tela do operador | Um destino. Material uma vez. Rotação planejada por 72 h. | RF-04 |
| `getQueue`, `listOperationalJobs`, `getOperationalJob` | Whisper | Tela do operador | Tamanho, idade e jobs retidos, sem conteúdo. | RF-05 |
| `listDeliveryAttempts` | Whisper | Tela do operador | Falha e se haverá nova tentativa, sem corpo do aviso. | RF-06 |

O webhook que o Whisper envia ao destino do cliente não muda de contrato. Continua `receiveTranscriptionTerminalWebhook` em `tasks/prd-api-transcricao-assincrona/api-contract.yaml`.

## Origem e decisões

| Entrada consultada | Versão e revisão/data | Decisão herdada ou alterada | Motivo |
|---|---|---|---|
| `tasks/prd-operacao-do-servico/prd.md` | 0.1, approved em 2026-10-13 | Escopo da superfície | RF-01 a RF-06 |
| `tasks/prd-api-transcricao-assincrona/api-contract.yaml` | 1.0.0, consultado em 2026-10-13 | Não alterado. Prefixo `/v1` e Problem Details reutilizados no estilo. | A API pública já está aprovada. |
| ADR-003 | Accepted | Endereço http só na allowlist | O PUT recusa o que o piloto já recusaria. |
| PD-001, PD-002, PD-003 | Accepted em 2026-10-13 | Operador cadastra, fila e entregas, credencial compartilhada | Não reabertos aqui. |
| ADR-004 | Accepted em 2026-10-21 | Formulário no mesmo host, cookie HttpOnly | Fecha a forma de apresentar a credencial operacional. |

## Evolução e compatibilidade

Não há versão anterior deste console. A compatibilidade com um cliente já implantado desta superfície não foi verificada porque a superfície não existe.

O contrato público de transcrição não é editado. Paths novos não colidem com `/transcriptions`. Cookie de operador não passa a autorizar a API pública, e API Key não autoriza estas operações. Consumidor afetado da API pública: nenhum, enquanto a sessão permanecer um cookie distinto de `X-API-Key`.

## Validação e verificação

| Documento | Comando e versão da ferramenta | Schema/ruleset e versão | Resultado e avisos |
|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) | `npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-operacao-do-servico/api-contract.yaml --ruleset .claude/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=warn` | OpenAPI ruleset da skill, Spectral 6.15.0 | Executado em 2026-10-13. Exit 0. Nenhum erro e nenhum aviso. |

Cenários de verificação da implementação ficam para a TechSpec e para as tasks. Este lint não executa o serviço.

## Pendências e handoff

A sessão por cookie foi fechada pela ADR-004 em 2026-10-21. Não há pendência que bloqueie a implementação.

Handoff para `tsg-flow-techspec-creator` depois da aprovação: mapear estes `operationId`s à tela e ao monólito existente, sem copiar os schemas.
