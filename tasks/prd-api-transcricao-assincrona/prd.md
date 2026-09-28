---
tsg_artifact: prd
product: whisper
version: 1.0
status: approved
updated: 2026-09-28
capability: standalone-api-transcricao-assincrona
sources: docs/plano-api-transcricao.md@sem-versao
---

# API assíncrona de transcrição

## Visão Geral

O serviço Whisper deve permitir que clientes de software solicitem transcrições sem manter uma conexão aberta durante o processamento. O cliente envia uma URL assinada para a mídia, acompanha um job, recebe um aviso quando ele termina e busca o resultado por tempo limitado.

A primeira integração prevista é o code-for-coders. O contrato deve continuar genérico: Whisper não interpreta conceitos como curso ou aula, nem guarda permanentemente o conteúdo do cliente. O cliente decide como associar, apresentar, armazenar e indexar a transcrição.

## Escopo desta entrega

**Capacidade:** standalone — API assíncrona de transcrição; nenhum ID CAP-XXX foi fornecido. **Origem:** docs/plano-api-transcricao.md, sem versão formal.

**Recorte proposto para o primeiro piloto:** fluxo de máquina a máquina de ponta a ponta: autenticar uma conta, criar um job para um arquivo de áudio ou vídeo por URL assinada de download (S3 no caminho inicial), iniciar a ingestão sem esperar a fila de transcrição, acompanhar estado, receber webhook de conclusão ou falha e buscar o resultado. O operador provisiona as API Keys e cadastra por conta o destino e o material de verificação do webhook; console de autoatendimento fica fora deste corte.

O corte adota como premissas iniciais do plano: mídia de até 2 horas, volume operacional inicial de até 10 jobs por dia, idioma pt-BR e retenção do resultado e metadados por 24 horas após o estado terminal. O limite em bytes, a simultaneidade e os prazos de início e conclusão ainda precisam de definição.

**Fora desta entrega:** upload direto ou URL temporária de upload; idiomas além de pt-BR; console de cliente para gerir chaves; painel operacional de fila, workers e tentativas de webhook; armazenamento permanente, busca ou indexação da transcrição; associação a conceitos de domínio do cliente.

## Objetivos

- Permitir que um cliente integre uma transcrição sem esperar a duração do processamento na chamada de criação.
- Concluir o fluxo de criação, processamento, notificação e busca do resultado usando uma referência opaca do cliente, sem conhecer seu modelo de negócio.
- Atender a premissa inicial de até 10 jobs por dia, com mídia de até 2 horas, condicionada à validação da capacidade nos benchmarks de processamento.
- Limitar a exposição de mídia e resultados ao tempo necessário para processar e permitir a busca posterior por até 24 horas após o estado terminal.

## Histórias de Usuário

- Como desenvolvedor de um cliente integrador, quero enviar uma mídia por URL assinada e receber um identificador de job para que minha aplicação não precise aguardar a transcrição na mesma conexão.
- Como desenvolvedor de um cliente integrador, quero consultar o estado e receber um webhook de conclusão ou falha para que minha aplicação possa reagir sem consultar continuamente o serviço.
- Como desenvolvedor de um cliente integrador, quero buscar um resultado versionado dentro da janela de retenção para que eu possa associá-lo e armazená-lo segundo as regras do meu produto.
- Como operador do serviço, quero provisionar, limitar por escopo, acompanhar o uso, rotacionar e revogar chaves vinculadas a uma conta para permitir o piloto e interromper acesso comprometido.

## Funcionalidades Principais

### RF-01: Acesso de contas cliente

**Descrição:** Chamadas de máquina usam API Key vinculada a uma conta genérica do serviço. Cada chave tem escopos, data de criação, último uso e pode ser rotacionada ou revogada. No piloto, o operador administra essas ações; o cliente não precisa de console de autoatendimento.

**Critérios de Aceitação:**

- **Given** uma API Key ativa vinculada a uma conta, **When** o cliente a usa em uma operação autorizada, **Then** o serviço processa a solicitação sob a conta correspondente.
- **Given** uma chave inválida ou revogada, **When** o cliente tenta criar ou consultar um job, **Then** o serviço recusa a chamada sem revelar dados do job.
- **Given** um job pertencente a outra conta, **When** o cliente tenta consultar seu estado ou resultado, **Then** o serviço não revela o estado nem o conteúdo desse job.
- **Given** uma API Key recém-provisionada, **When** o operador a entrega ao cliente, **Then** o segredo é apresentado uma única vez e armazenado somente como hash.
- **Given** uma API Key com escopo limitado, **When** ela é usada em uma operação fora do escopo, **Then** o serviço recusa a operação sem revelar dados protegidos.
- **Given** uma chave rotacionada, **When** a nova chave é entregue ao cliente, **Then** o segredo novo é apresentado uma única vez e a chave substituída deixa de autorizar chamadas.
- **Given** uma chave em uso, **When** o operador consulta seus metadados, **Then** consegue ver sua data de criação, escopos, último uso e situação de revogação sem recuperar o segredo.

**Prioridade:** Must Have

### RF-02: Criação idempotente de job

**Descrição:** O cliente pode solicitar uma transcrição por REST para um arquivo de áudio ou vídeo usando uma URL assinada de download, inicialmente do S3, opções compatíveis com pt-BR, uma chave de idempotência e, opcionalmente, uma referência opaca do próprio cliente. O serviço não recebe credenciais do armazenamento do cliente. Os formatos aceitos ainda precisam ser definidos.

A chamada responde com 202 Accepted, um ID opaco, o estado inicial e uma referência para consulta depois que a ingestão assumir o job e iniciar a conexão de download. A resposta não aguarda a transcrição.

**Critérios de Aceitação:**

- **Given** uma solicitação válida e autenticada, **When** a ingestão inicia a conexão para baixar a mídia, **Then** o cliente recebe 202 Accepted com o identificador, o estado e a referência de consulta do job.
- **Given** que o cliente não recebeu a primeira resposta, **When** repete a mesma solicitação com a mesma chave de idempotência, **Then** o serviço retorna o mesmo job e não cria uma transcrição duplicada.
- **Given** uma chave de idempotência já associada a outra solicitação, **When** o cliente a reutiliza com conteúdo diferente, **Then** o serviço rejeita a reutilização sem criar outro job nem substituir o original.
- **Given** uma URL expirada ou um download que falha, **When** o serviço tenta obter a mídia, **Then** o job termina com erro de origem indisponível e não inicia a transcrição.
- **Given** uma mídia cuja duração excede 2 horas, **When** o serviço detecta o limite excedido, **Then** o job não é transcrito e termina com falha identificável como limite excedido.

**Prioridade:** Must Have

### RF-03: Ingestão e ciclo de vida assíncrono

**Descrição:** O serviço inicia a obtenção da mídia logo após aceitar o job. A espera por workers ocupados com transcrições não deve impedir o início de downloads de jobs novos. O job percorre os estados públicos downloading, queued, processing e um estado terminal completed ou failed. A URL deixa de ser necessária após a mídia ser obtida.

**Critérios de Aceitação:**

- **Given** um job aceito, **When** o download termina com sucesso, **Then** o job avança para queued e depois para processing antes de alcançar seu estado terminal.
- **Given** que o processamento de outras mídias longas está ocupado, **When** um novo job é aceito, **Then** a capacidade de transcrição ocupada não o mantém aguardando para iniciar o download, respeitado o prazo de início que será definido antes do contrato final.
- **Given** que um job falha durante download ou transcrição, **When** o cliente consulta o job, **Then** recebe failed e um resumo seguro da falha, sem credenciais ou URL assinada.
- **Given** qualquer estado público do job, **When** o cliente autorizado consulta sua situação, **Then** recebe o estado atual e os horários relevantes disponíveis.

**Prioridade:** Must Have

### RF-04: Aviso de conclusão ou falha

**Descrição:** Ao alcançar completed ou failed, o serviço envia ao cliente um webhook pequeno com ID do evento, ID do job, estado terminal e referência opaca do cliente quando informada. O payload não contém a transcrição nem a mídia. A assinatura deve permitir verificar a origem; entregas são ao menos uma vez e têm retentativas com espera progressiva. Falhas do webhook são acompanhadas separadamente do estado do job.

**Configuração do piloto:** o operador cadastra um URL de webhook por conta e entrega ao cliente material de verificação da assinatura, independente da API Key. O cliente não envia nem substitui o URL em cada job. O operador administra cadastro e rotação enquanto não houver console.

**Critérios de Aceitação:**

- **Given** um job em estado terminal, **When** o serviço emite o aviso, **Then** o webhook contém os identificadores e o estado terminal, sem conteúdo da mídia ou da transcrição.
- **Given** um job terminal de uma conta com webhook configurado, **When** o serviço entrega o aviso, **Then** usa o destino cadastrado para essa conta, sem aceitar um destino diferente na solicitação do job.
- **Given** que a API Key ou o material de verificação do webhook é rotacionado, **When** a rotação termina, **Then** a outra credencial continua válida e independente.
- **Given** um webhook recebido, **When** o cliente valida sua assinatura, **Then** consegue verificar que o aviso veio do serviço e detectar uma repetição pelo ID do evento.
- **Given** que a entrega do webhook falha, **When** ainda houver tentativas previstas, **Then** o serviço tenta entregar novamente com espera progressiva e mantém o estado de entrega separado do estado da transcrição.
- **Given** que a entrega do webhook falha definitivamente, **When** o cliente consulta a transcrição, **Then** o job continua completed ou failed conforme seu processamento; a falha de notificação não o altera.

**Prioridade:** Must Have

### RF-05: Consulta e busca do resultado

**Descrição:** O cliente autorizado pode consultar o job e buscar seu resultado em uma operação separada do webhook. O resultado inicial é JSON versionado e independente do motor de transcrição, com schemaVersion, jobId, language, durationMs e segments. Cada segmento tem startMs, endMs e text; os tempos são relativos ao início da mídia.

**Critérios de Aceitação:**

- **Given** um job completed ainda dentro da retenção, **When** o cliente autorizado busca o resultado, **Then** recebe o JSON no formato versionado e os tempos dos segmentos são relativos ao início do arquivo.
- **Given** um job failed, **When** o cliente busca seu resultado, **Then** não recebe uma transcrição parcial como se fosse resultado concluído e recebe indicação segura de indisponibilidade.
- **Given** que passaram 24 horas do estado terminal, **When** o cliente tenta consultar metadados ou buscar o resultado, **Then** o conteúdo expirado não é disponibilizado.
- **Given** um job de outra conta ou uma chamada sem autorização, **When** o cliente tenta buscar o resultado, **Then** nenhum conteúdo é revelado.

**Prioridade:** Must Have

### RF-06: Retenção e expurgo

**Descrição:** O resultado e os metadados do job ficam disponíveis por até 24 horas após completed ou failed. A mídia baixada pode ser removida assim que não for mais necessária e deve ser removida, no máximo, ao fim dessa janela. O serviço remove dados expirados e detecta arquivos temporários órfãos.

**Critérios de Aceitação:**

- **Given** um job que alcançou estado terminal, **When** ainda está dentro das 24 horas seguintes, **Then** o resultado e os metadados ficam disponíveis para consulta autorizada.
- **Given** que a mídia não é mais necessária ao processamento, **When** o serviço encerra seu uso, **Then** ela pode ser removida antes do fim da janela de retenção.
- **Given** que a janela de 24 horas terminou, **When** a limpeza periódica é executada, **Then** resultado e metadados expirados são removidos e arquivos temporários órfãos são identificados para remoção.
- **Given** logs ou traces gerados durante o fluxo, **When** são consultados operacionalmente, **Then** não contêm conteúdo da mídia, transcrição, API Keys ou URLs assinadas.

**Prioridade:** Must Have

## Experiência do Usuário

A experiência desta entrega é de integração entre sistemas. O desenvolvedor recebe uma credencial provisionada, envia a URL assinada e uma chave de idempotência, guarda o ID opaco e a referência de consulta, e então acompanha o job por consulta REST ou webhook. Ao receber um evento terminal, valida a assinatura, deduplica pelo ID do evento e busca o resultado dentro da janela de 24 horas.

O cliente pode guardar permanentemente a transcrição, relacioná-la ao seu próprio conteúdo e oferecer busca. O serviço não apresenta telas para assistir mídia, revisar texto ou indexar conteúdo neste corte. O console de cliente e o painel operacional constam como evolução proposta, sujeita à decisão de escopo.

## Restrições Técnicas de Alto Nível

- Duração máxima inicial por arquivo: 2 horas. O limite máximo em bytes ainda não foi definido.
- Volume operacional inicial indicado no plano: até 10 jobs por dia, sujeito à validação por benchmark do fator de tempo real e da fila.
- Entrada inicial por URL assinada de download, inicialmente S3; o cliente é responsável por emitir uma URL válida para a tentativa de obtenção.
- Idioma inicial adotado neste draft: português do Brasil (pt-BR).
- Retenção de resultado e metadados: até 24 horas após estado terminal; a mídia pode ser removida antes.
- Conteúdo, credenciais e URLs assinadas não devem aparecer em logs ou traces.

## Não-Objetivos (Fora de Escopo)

- Upload direto do arquivo ou URL temporária de upload.
- Transcrição em idiomas além de pt-BR neste primeiro corte.
- Armazenamento permanente, indexação, busca textual ou apresentação de legendas pelo Whisper.
- Conhecimento de cursos, aulas ou outros conceitos internos do cliente.
- Console de autoatendimento para criar, rotacionar e revogar API Keys.
- Painel operacional de jobs, fila, saúde de workers e falhas de webhook.

## Plano de Rollout Faseado

### MVP — Piloto da API

- **Funcionalidades incluídas:** RF-01 a RF-06.
- **Critérios para iniciar o piloto:** limite em bytes e metas de início/conclusão definidos; benchmark confirma capacidade para o volume inicial; primeiro cliente consegue completar o fluxo de criação até a busca do resultado; provisionamento operacional de chaves e destino do webhook definidos.

### Evolução proposta

O plano de origem propõe um console com gestão de API Keys e acompanhamento de jobs, fila, workers e webhooks após a entrega do fluxo seguro de API. Esse console deve ser detalhado em uma fatia/PRD próprio se aprovado, pois não está no recorte proposto deste documento. Upload direto e suporte a outros idiomas também permanecem como evolução sem fase comprometida.

## Métricas de Sucesso

| Métrica | Definição | Meta e prazo |
|---|---|---|
| Volume processado | Jobs aceitos por dia, com mídia dentro dos limites | Premissa inicial de até 10 por dia durante o piloto; a capacidade precisa ser validada por benchmark antes do piloto |
| Início do download | Tempo entre a aceitação do job e o início da conexão de download | Meta ainda não definida; produto e operação devem defini-la antes da aprovação do contrato |
| Tempo de conclusão | Tempo entre aceitação e estado terminal, separado por duração da mídia | Meta ainda não definida; produto e operação devem defini-la após benchmark de RTF |
| Taxa de conclusão | Jobs completed sobre jobs aceitos, excluindo falhas de origem imputáveis ao cliente | Baseline e meta ainda não definidos; medir durante o piloto |
| Entrega de webhook | Eventos terminais entregues dentro da política de retentativas | Meta e janela ainda não definidas; produto deve defini-las antes do piloto |

## Riscos e Mitigações

- **Capacidade insuficiente para o volume previsto:** dez mídias de até duas horas podem representar até 20 horas de mídia por dia. Validar RTF e fila antes de comprometer o prazo de conclusão.
- **URL expirada ou download interrompido:** exigir que o cliente emita a URL com margem suficiente; definir a validade mínima e o comportamento de retentativa antes de fechar o contrato; terminar com erro de origem indisponível quando não for possível obter a mídia.
- **Repetição de webhooks:** entregar ao menos uma vez e incluir ID do evento para deduplicação pelo cliente.
- **Exposição de dados ou credenciais:** não incluir conteúdo, API Keys ou URLs assinadas em logs, traces e payloads de webhook; limitar consultas à conta proprietária.
- **Janela de retenção insuficiente para o cliente buscar o resultado:** comunicar o prazo de 24 horas no contrato; o cliente é responsável por persistir o resultado que precisar conservar.

## Questões em Aberto

| Questão | Responsável | Prazo desejável | Impacto se não resolvida |
|---|---|---|---|
| Qual o tamanho máximo em bytes por arquivo de até 2 horas? | Produto e operação | Antes de aprovar o contrato | Sem limite, não é possível definir rejeição, capacidade de armazenamento temporário e comportamento para arquivos grandes. |
| Quais formatos de áudio e vídeo serão aceitos no primeiro corte? | Produto e primeiro cliente | Antes de aprovar o contrato | Define quais arquivos podem ser enviados e como entradas incompatíveis terminam. |
| Quais são o pico de jobs simultâneos e os prazos máximos para iniciar download e concluir transcrição? | Produto e operação | Antes de aprovar o contrato | Sem metas, o aceite de latência e a capacidade necessária ficam indeterminados. |
| Qual validade mínima da URL assinada o cliente deve fornecer, considerando início e retentativas de download? | Produto e primeiro cliente | Antes de aprovar o contrato | Pode causar falhas de origem e mudar a responsabilidade por retentativas. |
| Qual política e janela encerram as retentativas de webhook? | Produto e operação | Antes de aprovar o contrato | Sem limite observável, clientes não sabem quando uma notificação deixou de ser entregue. |
