---
status: pending
task_kind: enabling
blocked_by: [1.0]
gate: "rtk rg -q '^## Integração com code-for-coders$' README.md"
gate_expect: "seção do primeiro consumidor presente no README, exit 0"
---

# 3.0 Publicar o acordo de integração do primeiro consumidor

**Fatia:** EN-01 · **Cobre:** RF-02, RF-04, RF-05, US-01 · **Spec:** `techspec.md#habilitadores-inevitáveis` · **ADR:** ADR-001, ADR-002

## Comportamento

Usar a seção `## Integração com code-for-coders` para este guia.

O README documenta o fluxo de integração do code-for-coders: gerar URL HTTPS pré-assinada GET para objeto privado pouco antes do POST, garantir validade efetiva mínima de 60 minutos inclusive das credenciais temporárias, enviar `sourceUrl` com `X-API-Key` e `Idempotency-Key`, guardar o ID opaco, consultar o `statusUrl`, validar e deduplicar o webhook, e buscar o resultado dentro de 24 horas. Descreve o contrato de falha de origem: até três tentativas transitórias pelo Whisper; nova URL e nova chave de idempotência pelo consumidor para reprocessar após falha final. Explicita que o contrato Media existente no consumidor só oferece URL de escrita e que a geração de URL GET precisa ser implementada naquele produto antes do piloto integrado.

É um habilitador porque a orientação do consumidor abrange criação, notificação e resultado e é consumida por outro produto, sem comportamento novo no backend. Desbloqueia a integração de V-02 e o piloto. A implementação do consumidor é dependência externa rastreada no plano, fora do checkout Whisper.

## Fora do escopo desta task

Alterar o repositório code-for-coders, entregar credenciais reais ou implementar endpoints Whisper.

## Decisões fechadas

Seguir Q-03/Q-04 e `techspec.md#interfaces-entre-fatias-ou-times`; URL GET de leitura sem compartilhar credenciais S3. Usar OpenAPI atualizado por 1.0 como acordo público.

## Modificar / Referenciar

- **modificar:** `README.md` (guia do primeiro consumidor)
- **ref:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (request, respostas e webhook)
- **ref:** `tasks/prd-api-transcricao-assincrona/contracts.md` (participantes e dependência externa)
- **ref:** `tasks/prd-api-transcricao-assincrona/techspec.md` (decisões de integração)
- **ref:** `docs/adr/adr-001.md`, `docs/adr/adr-002.md` (armazenamento e assinatura)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Documentação | `rtk rg -q '^## Integração com code-for-coders$' README.md` | exit 0; seção presente | README; CI ausente |
| Documentação | `rtk git diff --check -- README.md` | exit 0; sem erro de whitespace | Git; não há lint Markdown nem CI no projeto |

## Pronto quando

- [ ] O guia permite ao primeiro consumidor preparar a URL de leitura, criar e acompanhar um job e buscar o resultado pelo destino público.
- [ ] A validação da assinatura, a deduplicação e a reação a falha definitiva de origem estão descritas sem depender de conceitos internos do consumidor.
- [ ] A dependência de implementação no code-for-coders está explícita antes do piloto integrado.
- [ ] O gate estático passa com exit 0.
