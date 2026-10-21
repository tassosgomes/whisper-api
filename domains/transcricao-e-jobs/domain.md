---
tsg_artifact: domain
product: whisper
version: 0.1
status: approved
updated: 2026-10-13
sources: vision.md@0.1, context/domain-map.md@0.1, backlog/capabilities.md@0.1, context/architecture-baseline.md@0.1
---

# Domain Document — Transcrição e Jobs

**Domínio:** Transcrição e Jobs
**Capacidades atendidas:** CAP-001, CAP-002
**Restrições arquiteturais pertinentes:** estado durável do job como fonte do ciclo de vida, retenção terminal de 24 horas, resultado separado do aviso, falha de notificação não altera o job.

---

## 1. Propósito do Domínio (Domain Purpose)

### Responsabilidade Principal

Conduzir uma solicitação desde a referência de mídia até o estado final e o resultado de transcrição disponível ao cliente.

### Problema que Resolve

O cliente precisa de um trabalho acompanhável até o fim, e o operador precisa ver se a fila anda, sem transformar o Whisper em arquivo permanente da transcrição.

### Fora do Escopo deste Domínio (Out of Scope)

- Autenticar credenciais ou administrar contas. Isso pertence a Acesso e Contas.
- Decidir o destino, o material de verificação ou as tentativas de aviso. Isso pertence a Entrega de Notificações.
- Guardar a transcrição em definitivo, indexá-la ou apresentá-la como experiência de revisão para o usuário do cliente.
- Saúde de workers. Fora do horizonte confirmado para CAP-002.

---

## 2. Usuários do Domínio (Domain Users)

| Perfil (Role) | O que faz neste domínio | Frequência de uso |
|---|---|---|
| Sistema cliente | Solicita a transcrição e consulta estado e resultado dos próprios jobs | A cada mídia e durante a retenção |
| Operador do Whisper | Acompanha tamanho, idade e estado da fila de todas as contas, sem conteúdo | Quando verifica se o serviço está andando |

---

## 3. Entidades Principais (Core Entities)

| Entidade | Descrição | Atributos Principais | Relacionamentos |
|---|---|---|---|
| Job | Trabalho solicitado pelo cliente e acompanhado até um estado terminal | Identificador opaco, conta, credencial criadora, estado, horários, referência opaca se enviada | Pertence à conta e à credencial que o criou |
| Referência de mídia | Indicação de onde o serviço obtém o áudio ou vídeo | Usada só na obtenção; não é dado de acompanhamento | Origina um job; não é exposta ao operador |
| Resultado | Representação versionada da transcrição concluída | Disponível ao cliente autorizado durante a retenção | Pertence ao job; não entra no acompanhamento operacional |
| Fila | Conjunto dos jobs que ainda não terminaram | Tamanho e idade do mais antigo | Projeção dos jobs deste domínio |

---

## 4. Capacidades Atendidas (Capabilities Served)

| Capacidade | O que este domínio entrega a ela |
|---|---|
| `CAP-001` | Aceita o job, conduz os estados até o terminal e entrega o resultado ao cliente durante a retenção |
| `CAP-002` | Mostra ao operador o tamanho e a idade da fila e o estado dos jobs ainda retidos, de todas as contas, sem conteúdo |

---

## 5. Juntas com Outros Domínios (Domain Joints)

### Depende de (Upstream)

| Domínio | O que consome | Tipo | Dono do dado | Criticidade |
|---|---|---|---|---|
| Acesso e Contas | Autorização da operação e conta proprietária | Dados (leitura) | Acesso e Contas é dono da conta e da credencial; este domínio é dono do job e do resultado | Alta |

### Fornece para (Downstream)

| Domínio | O que fornece | Tipo | Dono do dado | Criticidade |
|---|---|---|---|---|
| Entrega de Notificações | Fato de conclusão ou falha terminal e o contexto mínimo do aviso | Evento | Este domínio é dono do estado terminal; Entrega de Notificações é dona da configuração e das tentativas | Alta |

### Integrações Externas (External Integrations)

| Sistema Externo | Finalidade | Direção |
|---|---|---|
| Origem da mídia indicada pelo cliente | Obter o áudio ou vídeo da solicitação | Entrada |

O cliente continua dono da mídia original. Este domínio não passa a conhecer curso, aula ou outro conceito do consumidor.

---

## 6. Regras de Negócio (Business Rules)

| ID | Regra | Origem |
|---|---|---|
| RN-01 | Os estados públicos do job são downloading, queued, processing, completed e failed. | PRD da API, RF-03 |
| RN-02 | Trabalho de transcrição ainda em curso não impede o início da obtenção de mídia de um job novo. | PRD da API, RF-03 |
| RN-03 | Metadados e resultado permanecem consultáveis por até 24 horas após completed ou failed. Depois disso o conteúdo expirado não é disponibilizado. | PRD da API, RF-05 e RF-06 |
| RN-04 | O cliente autorizado vê estado e horários do próprio job. Job de outra conta, de outra credencial ou expirado não é revelado. | PRD da API, RF-01 e RF-05 |
| RN-05 | Falha de notificação não muda completed nem failed. | Domain Map; PRD da API, RF-04 |
| RN-06 | A fila operacional tem por tamanho a quantidade de jobs que ainda não estão completed nem failed, e por idade o tempo desde a aceitação do mais antigo entre esses. | Plano de origem; decisão de 2026-10-12 |
| RN-07 | O operador autenticado vê, de todas as contas, os jobs ainda dentro da retenção, com conta, identificador, estado, horários e a referência opaca quando existir. Não vê mídia, texto da transcrição, URL de origem nem segredo. | Decisão de 2026-10-12; PRD da API, RF-06 |
| RN-08 | Job que já passou das 24 horas após o estado terminal não aparece no acompanhamento do operador. | PRD da API, RF-06 |

---

## 7. Eventos do Domínio (Domain Events)

### Produz (Publishes)

- `transcricao.job-aceito` — quando a solicitação passa a ter um job acompanhável
- `transcricao.job-terminal` — quando o job chega a completed ou failed

### Consome (Subscribes)

Nenhum evento de outro domínio. A autorização chega como leitura de Acesso e Contas no momento da chamada.

---

## 8. Riscos de Fronteira (Boundary Risks)

| Risco | Probabilidade | Impacto | Mitigação |
|---|---|---|---|
| O acompanhamento operacional passar a mostrar resultado ou mídia | Média | Alto | RN-07; resultado continua disponível só ao cliente autorizado, pela consulta já definida |
| A visão entre contas do operador ser reutilizada como se fosse permissão de uma API Key | Média | Alto | RN-04 permanece para o cliente; RN-07 vale só para o operador autenticado |
| Idade da fila ser confundida com o prazo de conclusão, que o PRD da API ainda não fixou | Alta | Médio | RN-06 define idade como fato observável, não como meta de serviço |

---

## 9. Questões em Aberto (Open Questions)

- [ ] Metas de início e de conclusão continuam em aberto no PRD da API. CAP-002 mostra a idade real; não cria essas metas.
