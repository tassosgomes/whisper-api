---
tsg_artifact: domain
product: whisper
version: 0.1
status: approved
updated: 2026-10-21
sources: vision.md@0.2, context/domain-map.md@0.1, backlog/capabilities.md@0.2, context/architecture-baseline.md@0.2
---

# Domain Document — Acesso e Contas

**Domínio:** Acesso e Contas
**Capacidades atendidas:** CAP-001, CAP-002
**Restrições arquiteturais pertinentes:** credencial de API pertence a uma conta, segredo só na criação, consultas isoladas por conta e por credencial, privilégio operacional distinto com trilha sem conteúdo.

---

## 1. Propósito do Domínio (Domain Purpose)

### Responsabilidade Principal

Representar as contas clientes e decidir quais operações cada credencial de máquina pode realizar.

### Problema que Resolve

Chamadas de sistemas diferentes precisam ficar isoladas, e o operador precisa conceder ou retirar esse acesso sem misturar a credencial de máquina com a administração do serviço.

### Fora do Escopo deste Domínio (Out of Scope)

- Criar ou processar jobs, guardar resultado ou expor a fila. Isso pertence a Transcrição e Jobs.
- Configurar destino, guardar material de verificação ou executar tentativas de aviso. Isso pertence a Entrega de Notificações.
- Autoatendimento em que o próprio cliente cria, rotaciona ou revoga chaves. Está fora do horizonte do backlog.

---

## 2. Usuários do Domínio (Domain Users)

| Perfil (Role) | O que faz neste domínio | Frequência de uso |
|---|---|---|
| Operador do Whisper | Cria contas, emite, consulta, rotaciona e revoga credenciais de API | A cada novo cliente e quando uma chave é perdida ou comprometida |
| Sistema cliente | Autentica chamadas de máquina com a API Key da própria conta | A cada solicitação e consulta de transcrição |

---

## 3. Entidades Principais (Core Entities)

| Entidade | Descrição | Atributos Principais | Relacionamentos |
|---|---|---|---|
| Conta cliente | Identidade lógica sob a qual chamadas e jobs são isolados | Nome, situação | Possui uma ou mais credenciais de API |
| Credencial de API | Segredo usado por uma aplicação cliente para autenticar chamadas de máquina | Identificador estável, permissões, criação, último uso, situação de revogação ou rotação | Pertence a uma conta cliente |
| Credencial operacional | Acesso do operador à administração e ao acompanhamento, distinto da API Key | Identidade do operador autenticado | Não autoriza chamada de máquina |

---

## 4. Capacidades Atendidas (Capabilities Served)

| Capacidade | O que este domínio entrega a ela |
|---|---|
| `CAP-001` | Autentica a chamada, liga a credencial à conta proprietária e recusa acesso fora dessa conta e dessa credencial |
| `CAP-002` | Permite ao operador criar a conta e emitir, consultar, rotacionar e revogar credenciais, com trilha e sem recuperar segredo |

---

## 5. Juntas com Outros Domínios (Domain Joints)

### Depende de (Upstream)

Nenhuma. A identidade da conta nasce aqui.

### Fornece para (Downstream)

| Domínio | O que fornece | Tipo | Dono do dado | Criticidade |
|---|---|---|---|---|
| Transcrição e Jobs | Autorização da operação e identificação da conta proprietária | Dados (leitura) | Acesso e Contas é dono da conta e da credencial; Transcrição e Jobs é dono do job | Alta |
| Entrega de Notificações | Identidade da conta à qual o destino será associado | Dados (leitura) | Acesso e Contas é dono da identidade da conta; Entrega de Notificações é dono do destino e da verificação | Alta |

### Integrações Externas (External Integrations)

Nenhuma. O sistema cliente é um chamador autenticado, não um sistema cujo cadastro este domínio administra.

---

## 6. Regras de Negócio (Business Rules)

| ID | Regra | Origem |
|---|---|---|
| RN-01 | Toda credencial de API pertence a uma única conta cliente. | PRD da API, RF-01 |
| RN-02 | O segredo da API Key é apresentado uma única vez. Depois disso só os metadados podem ser consultados. Segredo perdido não é recuperado; a saída é rotacionar. | PRD da API, RF-01; runbook do piloto |
| RN-03 | Credencial inválida ou revogada não autoriza a chamada e a recusa não revela job, estado nem conteúdo. | PRD da API, RF-01 |
| RN-04 | Uma credencial só autoriza leitura dos jobs criados por ela, na própria conta. Job de outra conta, de outra credencial ou já expirado não é revelado. | PRD da API, RF-01; runbook do piloto |
| RN-05 | No piloto, as credenciais têm as mesmas permissões iniciais: criar jobs e ler estado e resultado apenas dos jobs da própria credencial. | Runbook do piloto |
| RN-06 | Rotação conserva o identificador da credencial e o acesso aos jobs já criados por ela, apresenta o segredo novo uma única vez e faz a chave substituída deixar de autorizar. Não altera o material de verificação do webhook. | Runbook do piloto |
| RN-07 | Revogação é imediata, exige motivo, não reapresenta o segredo e passa a recusar chamadas feitas com essa credencial. | Runbook do piloto |
| RN-08 | Emitir outra credencial na mesma conta cria um identificador novo e não invalida as credenciais já existentes. | Runbook do piloto |
| RN-09 | Administrar conta e credencial é ação do operador, autenticado por uma credencial operacional compartilhada, distinta da API Key. A API Key não abre a administração, e a credencial operacional não autoriza chamada de máquina. | Visão, perfil do operador; decisão de 2026-10-12 |
| RN-10 | Toda ação administrativa registra momento, operador, conta, credencial afetada e, na revogação, o motivo. A trilha não contém segredo, mídia, transcrição nem URL de origem. | Runbook do piloto; baseline de privilégio operacional |

---

## 7. Eventos do Domínio (Domain Events)

### Produz (Publishes)

- `acesso.conta-criada` — quando o operador cria uma conta cliente
- `acesso.credencial-emitida` — quando uma API Key nova é apresentada
- `acesso.credencial-rotacionada` — quando o segredo de uma credencial existente é substituído
- `acesso.credencial-revogada` — quando uma credencial deixa de autorizar chamadas

### Consome (Subscribes)

Nenhum.

---

## 8. Riscos de Fronteira (Boundary Risks)

| Risco | Probabilidade | Impacto | Mitigação |
|---|---|---|---|
| Tratar a API Key como se ela também administrasse a conta ou abrisse a fila | Média | Alto | RN-09 separa credencial operacional e credencial de máquina |
| Confundir conta cliente com curso, aula ou outro conceito do consumidor | Média | Alto | A conta permanece identidade lógica do serviço; conceitos do cliente ficam fora do domínio |
| O Domain Map não nomeia o operador, embora a visão o nomeie | Alta | Médio | O operador é perfil deste domínio; a junta de dados com os vizinhos continua a do mapa |

---

## 9. Questões em Aberto (Open Questions)

Nenhuma. A credencial operacional desta entrega é uma só, compartilhada por quem opera o serviço. Operadores nomeados ficam fora do horizonte.
