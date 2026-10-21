---
tsg_artifact: domain
product: whisper
version: 0.1
status: approved
updated: 2026-10-21
sources: vision.md@0.2, context/domain-map.md@0.1, backlog/capabilities.md@0.2, context/architecture-baseline.md@0.2
---

# Domain Document — Entrega de Notificações

**Domínio:** Entrega de Notificações
**Capacidades atendidas:** CAP-001, CAP-002
**Restrições arquiteturais pertinentes:** entrega ao menos uma vez, identificador estável, falha de notificação não altera o job, material de assinatura protegido e distinto da API Key.

---

## 1. Propósito do Domínio (Domain Purpose)

### Responsabilidade Principal

Configurar e acompanhar o envio de avisos sobre estados terminais de jobs às contas clientes.

### Problema que Resolve

O cliente precisa saber que um job terminou sem consultar o tempo todo, e uma falha desse aviso não pode ser confundida com falha da transcrição.

### Fora do Escopo deste Domínio (Out of Scope)

- Alterar o estado da transcrição ou guardar mídia e resultado. Isso pertence a Transcrição e Jobs.
- Autenticar a chamada da API do cliente. Isso pertence a Acesso e Contas.
- Escolher o destino em cada job. O destino é da conta, não da solicitação.

---

## 2. Usuários do Domínio (Domain Users)

| Perfil (Role) | O que faz neste domínio | Frequência de uso |
|---|---|---|
| Operador do Whisper | Cadastra, troca, rotaciona e desativa o destino e acompanha tentativas e falhas | Ao preparar um cliente e quando uma entrega falha |
| Sistema cliente | Recebe o aviso terminal e verifica a origem | A cada job que termina, enquanto houver destino ativo |

---

## 3. Entidades Principais (Core Entities)

| Entidade | Descrição | Atributos Principais | Relacionamentos |
|---|---|---|---|
| Destino | Endereço de notificação configurado para uma conta | Endereço, situação ativo ou desativado | Um por conta cliente |
| Material de verificação | Segredo que permite ao cliente confirmar a origem do aviso | Situação de vigência; o valor só existe na apresentação inicial e na rotação | Independente da credencial de API; associado ao destino da conta |
| Aviso terminal | Mensagem de que um job concluiu ou falhou | Identificador estável do evento, job, estado terminal, referência opaca se houver | Não contém mídia nem transcrição |
| Tentativa de entrega | Uma execução de envio, com sucesso ou falha | Momento, resultado, se ainda haverá nova tentativa | Pertence a um aviso; não altera o job |

---

## 4. Capacidades Atendidas (Capabilities Served)

| Capacidade | O que este domínio entrega a ela |
|---|---|
| `CAP-001` | Envia o aviso terminal ao destino da conta e repete a entrega sem mudar o estado do job |
| `CAP-002` | Permite ao operador configurar e desativar o destino, rotacionar o material e ver falhas e retentativas |

---

## 5. Juntas com Outros Domínios (Domain Joints)

### Depende de (Upstream)

| Domínio | O que consome | Tipo | Dono do dado | Criticidade |
|---|---|---|---|---|
| Transcrição e Jobs | Fato de conclusão ou falha terminal e o contexto mínimo do aviso | Evento | Transcrição e Jobs é dono do estado terminal; Entrega de Notificações é dona da configuração e das tentativas | Alta |
| Acesso e Contas | Identidade da conta à qual o destino pertence | Dados (leitura) | Acesso e Contas é dono da identidade da conta; Entrega de Notificações é dono do destino e do material | Alta |

### Fornece para (Downstream)

Nenhum domínio do Whisper consome o resultado da entrega para mudar dado próprio. O sistema cliente recebe o aviso.

### Integrações Externas (External Integrations)

| Sistema Externo | Finalidade | Direção |
|---|---|---|
| Receptor de webhook do cliente | Receber o aviso terminal no destino cadastrado para a conta | Saída |

---

## 6. Regras de Negócio (Business Rules)

| ID | Regra | Origem |
|---|---|---|
| RN-01 | Cada conta tem no máximo um destino de webhook. | PRD da API, RF-04 |
| RN-02 | O destino é o cadastrado para a conta. A solicitação do job não informa nem substitui o destino. | PRD da API, RF-04 |
| RN-03 | O material de verificação é independente da API Key. Rotacionar um não altera o outro. | PRD da API, RF-04; runbook do piloto |
| RN-04 | O aviso terminal leva identificador do evento, identificador do job, estado terminal e a referência opaca quando ela foi informada. Não leva mídia nem transcrição. | PRD da API, RF-04 |
| RN-05 | Falha, retentativa ou desativação da entrega não alteram o estado do job. | PRD da API, RF-04; Domain Map |
| RN-06 | A entrega é ao menos uma vez. O mesmo aviso conserva o identificador do evento entre as tentativas. | PRD da API, RF-04 |
| RN-07 | Uma conta pode chamar a API sem destino. O aviso terminal só é enviado quando há destino ativo. | Runbook do piloto |
| RN-08 | O material de verificação é apresentado uma única vez. Trocar só o endereço não reapresenta o material nem o substitui. | Runbook do piloto |
| RN-09 | Rotação planejada apresenta o material novo uma vez e mantém o anterior válido por 72 horas. Rotação por comprometimento, com motivo, revoga o anterior na hora. | Runbook do piloto |
| RN-10 | Desativar o destino, com motivo, interrompe novas entregas e revoga os materiais ativos. | Runbook do piloto |
| RN-11 | Destino recusado pelo critério já vigente no piloto não é gravado; o destino anterior permanece. | Runbook do piloto; baseline de destino de notificação |
| RN-12 | O operador vê tentativas, falhas e a situação do destino. Não vê o material de verificação depois da apresentação única, nem o corpo do aviso, nem URL de origem da mídia. | Decisão de 2026-10-12 |
| RN-13 | A tentativa continua visível enquanto o serviço ainda a guarda, mesmo depois que o metadado do job tenha saído da retenção. Nesse caso a tentativa identifica o job e não reconstrói o conteúdo dele. | PRD da API, retenção de 24 horas do job e janela de retentativa do aviso |

---

## 7. Eventos do Domínio (Domain Events)

### Produz (Publishes)

- `notificacao.destino-configurado` — quando a conta passa a ter um destino ativo
- `notificacao.destino-desativado` — quando o destino deixa de receber avisos
- `notificacao.entrega-concluida` — quando uma tentativa termina em sucesso
- `notificacao.entrega-falhou` — quando uma tentativa falha, com ou sem nova tentativa prevista

### Consome (Subscribes)

- `transcricao.job-terminal` (de: Transcrição e Jobs) — inicia o aviso se a conta tem destino ativo

---

## 8. Riscos de Fronteira (Boundary Risks)

| Risco | Probabilidade | Impacto | Mitigação |
|---|---|---|---|
| Tratar falha de webhook como falha do job | Alta | Alto | RN-05; a fila e as tentativas são acompanhamentos separados |
| O endereço do destino ser confundido com a URL de origem da mídia | Média | Alto | O destino é configuração da conta; a URL de origem não entra neste domínio nem na tela de tentativas |
| Retentativa ainda em curso quando o job já saiu da retenção de 24 horas | Média | Médio | RN-13 mantém a tentativa visível sem recuperar mídia ou transcrição |

---

## 9. Questões em Aberto (Open Questions)

Nenhuma decisão de comportamento deste domínio ficou sem regra para CAP-002. A política de aceite do endereço continua a do piloto; esta entrega não a redefine.
