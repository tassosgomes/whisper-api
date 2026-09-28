---
tsg_artifact: domain-map
product: whisper
version: 0.1
status: approved
updated: 2026-09-28
sources: vision.md@0.1
---

# Domain Map

## Visão Geral da Decomposição

O mapa separa três responsabilidades conceituais presentes na visão: proteger o acesso dos clientes, conduzir o ciclo de vida da transcrição e entregar notificações terminais. A decomposição descreve fronteiras de negócio; não determina serviços, processos, banco de dados ou tecnologia.

## Lista de Domínios

## Acesso e Contas

### 1. Responsabilidade Principal

Representar as contas clientes e decidir quais operações cada credencial de máquina pode realizar.

### 2. O Que NÃO Faz

Não cria nem processa jobs de transcrição, não guarda resultados e não executa tentativas de notificação.

### 3. Entidades Principais (conceituais)

Conta cliente, credencial de API, escopo de acesso e estado de revogação.

### 4. Linguagem Ubíqua

- **Conta cliente:** identidade lógica sob a qual chamadas e jobs são isolados.
- **Credencial de API:** segredo usado por uma aplicação cliente para autenticar chamadas de máquina.
- **Escopo:** conjunto de operações permitidas a uma credencial.

### 5. Eventos ou Interações

Autoriza a chamada e estabelece a conta proprietária que o domínio Transcrição e Jobs deve usar. Mantém a associação de conta necessária para proteger o acesso ao destino de notificação.

### 6. Justificativa da Separação

Autenticação, revogação e isolamento de contas têm regras de segurança próprias e não devem depender do processamento de mídia.

## Transcrição e Jobs

### 1. Responsabilidade Principal

Conduzir uma solicitação desde a referência de mídia até o estado final e o resultado de transcrição disponível ao cliente.

### 2. O Que NÃO Faz

Não autentica credenciais, não administra contas, não decide o conteúdo de domínios do cliente e não controla as tentativas de entrega de notificações.

### 3. Entidades Principais (conceituais)

Solicitação de transcrição, job, referência de mídia, transcrição, segmento e resultado.

### 4. Linguagem Ubíqua

- **Job:** trabalho solicitado pelo cliente e acompanhado até um estado terminal.
- **Referência de mídia:** indicação de onde o serviço pode obter o áudio ou vídeo de origem.
- **Resultado:** representação versionada da transcrição concluída.
- **Retenção terminal:** período após conclusão ou falha durante o qual os metadados do job permanecem consultáveis.

### 5. Eventos ou Interações

Recebe a conta autorizada do domínio Acesso e Contas. Ao concluir ou falhar um job, comunica o fato ao domínio Entrega de Notificações. Expõe ao cliente autorizado o estado e o resultado enquanto estiverem retidos.

### 6. Justificativa da Separação

O ciclo de trabalho e o resultado são o principal valor do produto e têm estados e regras de retenção próprios. A aquisição da mídia e o uso do motor são etapas desse ciclo, não domínios independentes nesta decomposição.

## Entrega de Notificações

### 1. Responsabilidade Principal

Configurar e acompanhar o envio de avisos sobre estados terminais de jobs às contas clientes.

### 2. O Que NÃO Faz

Não altera o estado da transcrição, não contém a mídia ou o resultado completo e não autentica chamadas da API do cliente.

### 3. Entidades Principais (conceituais)

Destino de notificação, configuração de verificação, aviso terminal e tentativa de entrega.

### 4. Linguagem Ubíqua

- **Destino:** endereço de notificação configurado para uma conta.
- **Aviso terminal:** mensagem que informa conclusão ou falha de um job.
- **Tentativa de entrega:** uma execução de envio, independente do resultado do job.

### 5. Eventos ou Interações

Recebe do domínio Transcrição e Jobs o fato terminal e o contexto mínimo necessário. Usa o destino e a configuração associados à conta e informa o resultado da entrega sem alterar o job.

### 6. Justificativa da Separação

Uma notificação pode falhar ou ser repetida depois de o job terminar. Seu ciclo de retentativas e seus segredos devem permanecer isolados do ciclo e do resultado da transcrição.

## Dependências Entre Domínios

| Origem | Destino | Interação de negócio | Responsabilidade dos dados |
|---|---|---|---|
| Acesso e Contas | Transcrição e Jobs | Autorizar operação e identificar a conta proprietária | Acesso e Contas é dono de contas e credenciais; Transcrição e Jobs é dono do job e resultado |
| Transcrição e Jobs | Entrega de Notificações | Comunicar conclusão ou falha terminal | Transcrição e Jobs é dono do estado terminal; Entrega de Notificações é dona da configuração e das tentativas de envio |
| Entrega de Notificações | Acesso e Contas | Associar o destino e a configuração à conta | Entrega de Notificações é dona dos dados de destino e verificação; Acesso e Contas é dono da identidade da conta |

## Pontos de Atenção

- A referência de mídia vem do consumidor; disponibilidade e autorização para leitura são uma dependência de integração, não propriedade do Whisper.
- A configuração do destino de notificação pertence a Entrega de Notificações, mesmo quando a configuração é provisionada por um operador.
- Mídia temporária e execução do motor permanecem dentro do domínio Transcrição e Jobs enquanto não surgir uma regra de negócio independente que justifique outra fronteira.
- A integração inicial com o code-for-coders ainda não tem uma origem acordada para URL assinada de leitura; isso está registrado em `tasks/prd-api-transcricao-assincrona/contracts.md`.
- Não há divisão adicional proposta. Entrega de Notificações é o menor domínio, mas permanece separado porque tentativas e falhas têm ciclo próprio; sua proximidade operacional com Transcrição e Jobs não exige fusão conceitual.

## Decisões Estruturais Tomadas

- Conta, job e entrega de notificação têm proprietários conceituais distintos.
- Falha de notificação não muda o estado do job.
- A transcrição não assume conceitos de domínio do consumidor.
- A decomposição é conceitual e não estabelece ordem de implementação nem unidade de deploy.
