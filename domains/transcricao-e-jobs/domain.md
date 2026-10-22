---
tsg_artifact: domain
product: whisper
version: 0.2
status: approved
updated: 2026-10-21
sources: vision.md@0.2, context/domain-map.md@0.1, backlog/capabilities.md@0.2, context/architecture-baseline.md@0.2
---

# Domain Document — Transcrição e Jobs

**Domínio:** Transcrição e Jobs
**Capacidades atendidas:** CAP-001, CAP-002, CAP-004 (somente o envio direto de áudio, RF-10)
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
- O aplicativo desktop de gravação, a captura de áudio, a guarda local das gravações e da transcrição no PC do usuário. Pertencem ao cliente de CAP-004, fora do Domain Map do serviço; este domínio só o atende como cliente da API pública.

---

## 2. Usuários do Domínio (Domain Users)

| Perfil (Role) | O que faz neste domínio | Frequência de uso |
|---|---|---|
| Sistema cliente | Solicita a transcrição, por URL assinada ou por envio direto de áudio, e consulta estado e resultado dos próprios jobs. Inclui o aplicativo desktop Windows de CAP-004 | A cada mídia e durante a retenção |
| Operador do Whisper | Acompanha tamanho, idade e estado da fila de todas as contas, sem conteúdo | Quando verifica se o serviço está andando |

---

## 3. Entidades Principais (Core Entities)

| Entidade | Descrição | Atributos Principais | Relacionamentos |
|---|---|---|---|
| Job | Trabalho solicitado pelo cliente e acompanhado até um estado terminal | Identificador opaco, conta, credencial criadora, estado, horários, referência opaca se enviada | Pertence à conta e à credencial que o criou |
| Referência de mídia | Indicação de onde o serviço obtém o áudio ou vídeo (fluxo de URL assinada, `sourceUrl`) | Usada só na obtenção; não é dado de acompanhamento | Origina um job; não é exposta ao operador. No envio direto não há referência: a mídia é o objeto enviado |
| Resultado | Representação versionada da transcrição concluída | Disponível ao cliente autorizado durante a retenção | Pertence ao job; não entra no acompanhamento operacional |
| Envio | Envio direto de áudio: o cliente registra o que vai enviar, transfere o arquivo ao armazenamento do Whisper e confirma a conclusão | Conta e credencial criadora, tipo de conteúdo, tamanho, SHA-256, duração declarada (opcional), referência do cliente, estado (pending, completed ou rejected), ligação opcional ao job | Pertence à conta e à credencial que o criou. Ao ser concluído, origina um job; o objeto enviado vira a mídia do job, sem cópia. A URL de envio é segredo temporário e não é persistida |
| Fila | Conjunto dos jobs que ainda não terminaram | Tamanho e idade do mais antigo | Projeção dos jobs deste domínio |

---

## 4. Capacidades Atendidas (Capabilities Served)

| Capacidade | O que este domínio entrega a ela |
|---|---|
| `CAP-001` | Aceita o job, conduz os estados até o terminal e entrega o resultado ao cliente durante a retenção |
| `CAP-004` | Parte do envio direto de áudio (RF-10): registra o envio, confere o arquivo recebido e cria o mesmo job de CAP-001, com os mesmos estados, resultado e retenção. O aplicativo desktop em si não pertence a este domínio |
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
| Cliente externo (app desktop Windows, CAP-004) | Envio direto, job e resultado pela API pública, com API Key de Acesso e Contas. Fora do Domain Map do serviço | API pública | Este domínio é dono do envio, do job e do resultado; o cliente é dono do áudio original e da cópia local | Alta para CAP-004 |

### Integrações Externas (External Integrations)

| Sistema Externo | Finalidade | Direção |
|---|---|---|
| Origem da mídia indicada pelo cliente | Obter o áudio ou vídeo da solicitação | Entrada |
| Armazenamento do Whisper (S3, ADR-001) | Receber o envio direto do cliente por URL de envio emitida por este domínio; o objeto enviado vira a mídia do job | Entrada (o cliente grava direto no armazenamento; os bytes não passam pela API) |

O cliente continua dono da mídia original, inclusive no envio direto. Este domínio não passa a conhecer curso, aula ou outro conceito do consumidor.

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
| RN-09 | O job criado por envio direto nasce em queued e nunca passa por downloading. O fluxo de URL assinada (`sourceUrl`) não muda. | PRD do gravador, RF-10; contracts.md |
| RN-10 | A repetição do envio com os mesmos valores devolve o mesmo envio e, se já concluído, o mesmo job, sem criar segundo job. A idempotência vale até 24 horas após o estado terminal do job (fim da retenção, RN-03). Envio nunca concluído expira 24 horas após o pedido. | PRD do gravador, RF-10, RN-G04, DP-09 |
| RN-11 | A mesma chave de idempotência com valores diferentes é conflito. Envio recusado exige chave nova. | PRD do gravador, RF-10, RN-G04; api-contract.yaml 1.1.0 |
| RN-12 | Envio ou job de outra conta ou de outra credencial, ou envio expirado, não é revelado (404 neutro). Extensão de RN-04 ao envio. | PRD do gravador, RF-10; RN-04 |
| RN-13 | O número de envios pendentes por credencial é limitado; acima do limite o pedido é recusado. O valor é configuração, não regra de negócio fixada aqui. | api-contract.yaml 1.1.0; TechSpec do gravador |
| RN-14 | O pedido de envio recusa, sem registrar envio nem criar job, o tamanho acima do limite, o formato não suportado e a duração declarada acima de 2 horas. | PRD do gravador, RF-10, RN-G03 |
| RN-15 | A conclusão do envio confere existência do objeto, tamanho, SHA-256 e assinatura do formato. Divergência recusa o envio, descarta o objeto e não cria job. | PRD do gravador, RF-10 |
| RN-16 | A duração real e a decodificação só são medidas no processamento. Duração real acima de 2 horas termina o job em falha com MEDIA_DURATION_LIMIT_EXCEEDED; arquivo que não decodifica, com UNSUPPORTED_MEDIA_FORMAT. | PRD do gravador, RF-10; contracts.md |
| RN-17 | Objeto enviado e abandonado (envio nunca concluído ou vencido) ou recusado é expurgado. | PRD do gravador, RF-10; TechSpec, V-03 |
| RN-18 | A mídia enviada segue a retenção e o expurgo de CAP-001 e nunca aparece ao operador nem em logs. A URL de envio é segredo temporário: não é persistida nem registrada. | PRD do gravador, RF-10; RN-07 |

---

## 7. Eventos do Domínio (Domain Events)

### Produz (Publishes)

- `transcricao.job-aceito` — quando a solicitação passa a ter um job acompanhável (no envio direto, na conclusão do envio)
- `transcricao.job-terminal` — quando o job chega a completed ou failed

Nenhum evento novo é necessário para o envio direto: o envio é interno a este domínio e o job criado emite os mesmos eventos de CAP-001. O envio recusado ou expirado não gera evento nem aviso.

### Consome (Subscribes)

Nenhum evento de outro domínio. A autorização chega como leitura de Acesso e Contas no momento da chamada.

---

## 8. Riscos de Fronteira (Boundary Risks)

| Risco | Probabilidade | Impacto | Mitigação |
|---|---|---|---|
| O acompanhamento operacional passar a mostrar resultado ou mídia | Média | Alto | RN-07; resultado continua disponível só ao cliente autorizado, pela consulta já definida |
| A visão entre contas do operador ser reutilizada como se fosse permissão de uma API Key | Média | Alto | RN-04 permanece para o cliente; RN-07 vale só para o operador autenticado |
| Idade da fila ser confundida com o prazo de conclusão, que o PRD da API ainda não fixou | Alta | Médio | RN-06 define idade como fato observável, não como meta de serviço |
| O endereço do armazenamento para o envio direto só ser alcançável na rede local no piloto, inviabilizando o PUT a partir do PC do usuário | Alta | Alto | Registrado como pendência de implantação (Questões em Aberto); não altera as regras |
| A mídia enviada ficar retida além do prazo se o expurgo falhar | Baixa | Médio | RN-17 e RN-18; regra de ciclo de vida do prefixo `uploads/` como rede de segurança (Questões em Aberto) |
| Consumidor existente supor que todo job passa por downloading | Média | Baixo | RN-09; contracts.md orienta o consumidor a tolerar a ausência |

---

## 9. Questões em Aberto (Open Questions)

- [ ] Metas de início e de conclusão continuam em aberto no PRD da API. CAP-002 mostra a idade real; não cria essas metas.
- [ ] O armazenamento precisa ser alcançável por HTTPS pelo PC do usuário (hoje, no piloto, só na rede local). Endpoint público, TLS e política do bucket são decisão de implantação, de Arquitetura e operação; bloqueia o piloto de CAP-004, não as regras deste documento.
- [ ] A regra de ciclo de vida do prefixo `uploads/` no armazenamento é configuração da operação (a TechSpec cita 2 dias); este documento não fixa o valor.
- [ ] Os valores do limite de envios pendentes por credencial (RN-13) e da validade da URL de envio ficam para a TechSpec e a configuração (a TechSpec propõe 5 e 1 hora).
