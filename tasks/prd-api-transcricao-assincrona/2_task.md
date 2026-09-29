---
status: done
task_kind: enabling
blocked_by: []
gate: "rtk rg -q '^## Operação do piloto$' README.md"
gate_expect: "seção operacional do piloto presente no README, exit 0"
---

# 2.0 Registrar o procedimento operacional do piloto

**Fatia:** EN-02 · **Cobre:** RF-01, RF-04, US-04 · **Spec:** `techspec.md#habilitadores-inevitáveis` · **ADR:** ADR-002

## Comportamento

Usar a seção `## Operação do piloto` para este procedimento.

O README passa a conter runbook para o operador criar conta e API Key, entregar o segredo uma única vez, consultar metadados sem recuperar o segredo, rotacionar com o mesmo `credential_id`, revogar, e cadastrar ou rotacionar o destino HTTPS e o material de assinatura do webhook de modo independente. O procedimento cobre auditoria mínima, proteção de segredo e URL, configuração de conta/chave/destino de teste e recuperação de acesso comprometido. Informa que todas as chaves do piloto têm as mesmas permissões e só leem jobs criados pela própria credencial.

É um habilitador porque a operação manual existe fora da API pública e atende tanto V-01 quanto V-05. Um procedimento dividido entre essas fatias deixaria o piloto sem instrução coerente para entrega e rotação das duas credenciais.

## Fora do escopo desta task

API administrativa, console, provisionamento automatizado ou envio de segredos reais.

## Decisões fechadas

Seguir `techspec.md#v-01`, `techspec.md#v-05` e ADR-002. O segredo de API Key não é o segredo do webhook; rotação planejada do segundo mantém sobreposição por 72 horas, comprometimento revoga imediatamente.

## Modificar / Referenciar

- **modificar:** `README.md` (runbook de operação do piloto)
- **ref:** `tasks/prd-api-transcricao-assincrona/prd.md` (RF-01 e RF-04)
- **ref:** `tasks/prd-api-transcricao-assincrona/techspec.md` (procedimento do piloto)
- **ref:** `docs/adr/adr-002.md` (assinatura e rotação)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Documentação | `rtk rg -q '^## Operação do piloto$' README.md` | exit 0; seção presente | README; CI ausente |
| Documentação | `rtk git diff --check -- README.md` | exit 0; sem erro de whitespace | Git; não há lint Markdown nem CI no projeto |

## Pronto quando

- [x] O operador consegue seguir o runbook para provisionar, consultar metadados, rotacionar e revogar sem revelar um segredo já entregue.
- [x] Cadastro e rotação do destino/segredo de webhook são independentes da API Key, com comportamento de comprometimento descrito.
- [x] O gate estático passa com exit 0.
