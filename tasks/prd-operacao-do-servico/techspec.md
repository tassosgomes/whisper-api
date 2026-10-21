---
tsg_artifact: techspec
product: whisper
version: 0.1
status: approved
updated: 2026-10-21
sources: tasks/prd-operacao-do-servico/prd.md@0.1, tasks/prd-operacao-do-servico/contracts.md@0.1
---

# Especificação técnica — Console operacional

> **Escopo:** Full-stack
> **Modo:** API-First
> **PRD de origem:** `tasks/prd-operacao-do-servico/prd.md`
> **Contratos de integração:** `tasks/prd-operacao-do-servico/contracts.md` e `api-contract.yaml` (OpenAPI 3.1, aprovado)
> **Data:** 2026-10-14
> **Status:** Aprovado em 2026-10-21
> **Handoff:** tasks — `tasks.md`

## Resumo Executivo

- A tela e as rotas do operador entram no processo da API já existente. Não há serviço novo. A URL da tela é a origem da API com o caminho `/operator`.
- A autenticação segue a ADR-004, Accepted: formulário no mesmo host, cookie `operator_session`, verificador separado de `X-API-Key`.
- Conta, API Key e destino reutilizam as regras já implementadas para o piloto. O contrato HTTP desta entrega é a fachada. O segredo e o material de verificação só atravessam a resposta da operação que os cria ou rotaciona.
- A fila e as tentativas são leituras. Não descriptografam URL de origem, não leem o texto da transcrição e não alteram o estado do job.
- **Trade-off:** uma sessão no mesmo processo evita um segundo deploy e impede que a credencial operacional vire API Key. O custo é estado de sessão neste processo e uma trilha que não identifica pessoa.

## Arquitetura da Solução

A tela em `/operator` é servida pela mesma origem que `/v1`. Sem sessão válida, a única interação é o formulário de entrada. Com sessão, as jornadas chamam os `operationId`s do contrato. O estado de segredo mora só na resposta recém-recebida: a tela o mostra para cópia e não o busca de novo.

O baseline permanece: cada domínio aplica as próprias regras. A superfície do operador não lê tabelas de outro domínio por conta própria. A leitura da fila pede a Transcrição e Jobs; a leitura das tentativas pede a Entrega de Notificações. A trilha administrativa pertence a Acesso e Contas e é gravada no mesmo commit da mutação.

### Bloco Backend

- Acesso e Contas continua dono de conta e credencial. A sessão operacional não é uma linha de `Credential`.
- Entrega de Notificações continua dona do destino, do material cifrado e das tentativas. Trocar só o endereço não passa pela operação que recusa material já ativo.
- Transcrição e Jobs continua dona do ciclo do job. A vista operacional é uma projeção sem conteúdo. A consulta do cliente, limitada à credencial criadora, não muda.
- Mutação administrativa e trilha commitam juntas. A trilha guarda momento, credencial operacional, conta, tipo e identificador do objeto e, quando a ação exige, o motivo. Não guarda segredo, mídia, transcrição nem URL de origem.
- Revogação, rotação imediata do material e desativação sem motivo não alteram o recurso.

### Bloco Frontend

- Jornadas: entrar; criar conta e emitir chave; configurar destino; rotacionar ou revogar; olhar fila e tentativas.
- O segredo copiável existe só na memória da resposta atual. Recarregar a ficha da credencial ou do destino não o reconstrói.
- Revogar chave, rotacionar material por comprometimento e desativar destino pedem motivo antes de confirmar. Sem motivo, a tela não envia a confirmação como sucesso.
- A tela não oferece reprodução de mídia, texto da transcrição nem edição do conteúdo.

## Mapa de Fatias Verticais

### V-01: Sessão do operador

- **Cobre:** RF-01; Acesso e Contas RN-09; US de entrada
- **Entrada / gatilho:** formulário em `/operator` com a credencial compartilhada; também `DELETE` da sessão
- **Processamento:** compara o segredo com a configuração do processo, fora de `CredentialStore.authenticate`. Sessão válida abre o cookie. API Key no lugar do segredo não abre. Cookie nas rotas `/v1/transcriptions` não autentica. Encerrar a sessão invalida o identificador.
- **Saída observável:** sem sessão, a tela não mostra contas, credenciais, destinos, fila nem tentativas. Com sessão, essas áreas abrem. A API pública continua recusando a credencial operacional.
- **Evidência / checkpoint:** `pytest -q -k v01_operator_session` — exit 0; cobre 401 da API Key no console, 401 do cookie na API pública e a tela sem dados antes do login
- **Bloqueado por:** Nenhum

### V-02: Criar e listar conta

- **Cobre:** RF-02; Acesso e Contas RN-01, RN-10
- **Entrada / gatilho:** operador autenticado informa o nome na tela
- **Processamento:** nome vazio não cria conta. Nome aceito cria a conta e, no mesmo commit, a trilha sem segredo. A lista mostra o nome e não mostra segredo de chave nem de webhook.
- **Saída observável:** a conta aparece na lista e fica disponível para credencial e destino. Nome vazio permanece na tela de criação com o erro do contrato.
- **Evidência / checkpoint:** `pytest -q -k v02_operator_account` — exit 0; inclui a trilha sem segredo e a recusa de nome vazio
- **Bloqueado por:** V-01

### V-03: Emitir API Key e consultar metadados

- **Cobre:** RF-03; Acesso e Contas RN-02, RN-05, RN-08, RN-10
- **Entrada / gatilho:** emitir a primeira chave ou outra chave da conta
- **Processamento:** cada emissão tem `credentialId` próprio e as permissões iniciais do piloto. As chaves anteriores continuam válidas. O segredo volta só nessa resposta, é persistido só como hash e entra na trilha apenas como identificador. Consulta posterior devolve metadados e não devolve o segredo, inclusive para chave cujo segredo se perdeu.
- **Saída observável:** a tela mostra o segredo uma vez, com a conta dona. Ao reabrir a credencial, o segredo não está lá.
- **Evidência / checkpoint:** `pytest -q -k v03_operator_credential` — exit 0; a segunda leitura da mesma credencial não contém `apiKey`
- **Bloqueado por:** V-02

### V-04: Rotacionar e revogar API Key

- **Cobre:** RF-03; Acesso e Contas RN-03, RN-06, RN-07; Entrega de Notificações RN-03
- **Entrada / gatilho:** rotação, ou revogação com motivo
- **Processamento:** a rotação conserva o identificador, invalida a chave anterior, devolve o segredo novo uma vez e não mexe no material de webhook. Jobs já criados por essa credencial continuam associados a ela. Revogação sem motivo não altera a credencial. Com motivo, a revogação é imediata, o segredo não reaparece e chamadas seguintes com a chave antiga são recusadas sem revelar job. A trilha da revogação guarda o motivo.
- **Saída observável:** a tela da rotação mostra o segredo novo uma vez. A revogação sem motivo não confirma. Depois da revogação, a ficha mostra a situação e não o segredo.
- **Evidência / checkpoint:** `pytest -q -k v04_operator_credential_lifecycle` — exit 0; inclui chave antiga recusada, material de webhook inalterado e motivo ausente sem efeito
- **Bloqueado por:** V-03

### V-05: Configurar o destino de webhook

- **Cobre:** RF-04; Entrega de Notificações RN-01, RN-02, RN-07, RN-08, RN-11
- **Entrada / gatilho:** operador confirma um endereço para a conta
- **Processamento:** vale o mesmo critério do piloto, inclusive a ADR-003. Endereço recusado não grava nada e preserva o destino anterior. Sem destino, a gravação cria o único destino, devolve o material uma vez e os jobs terminais seguintes usam esse endereço. Com destino, a gravação troca só o endereço, conserva o material e não o reexibe. Não existe segundo destino. Conta sem destino continua aceitando a API Key e não envia aviso.
- **Saída observável:** a tela mostra o material só na criação. Na troca de endereço, mostra o endereço novo e não o material. Endereço recusado deixa o destino anterior como estava.
- **Evidência / checkpoint:** `pytest -q -k v05_operator_destination` — exit 0; cobre criação, troca sem reexibir material e recusa que preserva o anterior
- **Bloqueado por:** V-02

### V-06: Rotacionar material e desativar destino

- **Cobre:** RF-04; Entrega de Notificações RN-09, RN-10; Acesso e Contas RN-06
- **Entrada / gatilho:** rotação planejada, rotação imediata com motivo, ou desativação com motivo
- **Processamento:** planejada mostra o material novo uma vez e mantém o anterior válido por 72 horas. Imediata sem motivo não muda nada. Imediata com motivo invalida o anterior na hora e mostra o novo uma vez. Desativação sem motivo não muda nada. Desativação com motivo cessa novas entregas e invalida os materiais ativos. Rotação de API Key não entra nesta fatia e não altera o destino.
- **Saída observável:** a tela pede motivo antes da rotação imediata e da desativação. Depois da desativação, a consulta da conta mostra o destino inativo, sem material.
- **Evidência / checkpoint:** `pytest -q -k v06_operator_destination_lifecycle` — exit 0; inclui 72 horas, motivo ausente sem efeito e destino inativo
- **Bloqueado por:** V-05

### V-07: Fila e jobs retidos

- **Cobre:** RF-05; Transcrição e Jobs RN-01, RN-02, RN-03, RN-04, RN-06, RN-07, RN-08
- **Entrada / gatilho:** operador autenticado abre a fila
- **Processamento:** o tamanho conta jobs que não estão `completed` nem `failed`, em todas as contas. A idade parte da aceitação do mais antigo entre eles. Sem não terminal, o tamanho é zero e não há aceitação mais antiga. A lista inclui não terminais e terminais dentro de 24 horas, com conta, identificador, estado, horários e a referência opaca se existir. Terminal há mais de 24 horas não entra. A leitura não descriptografa URL de origem, não lê resultado e não toma lease do worker. A API do cliente continua devolvendo só os jobs da própria credencial.
- **Saída observável:** a tela mostra o resumo e a lista sem mídia, texto, URL de origem ou segredo. O trabalho em curso de um job não bloqueia a obtenção de mídia de outro; esta fatia só observa.
- **Evidência / checkpoint:** `pytest -q -k v07_operator_queue` — exit 0; cobre fila vazia, job expirado ausente e a consulta do cliente inalterada
- **Bloqueado por:** V-01

### V-08: Tentativas de entrega

- **Cobre:** RF-06; Entrega de Notificações RN-04, RN-05, RN-06, RN-12, RN-13; Transcrição e Jobs RN-05
- **Entrada / gatilho:** operador abre as tentativas, no contexto da conta ou do job
- **Processamento:** a lista mostra conta, job, identificador estável do evento, a falha e se ainda haverá tentativa. Não mostra material, corpo do aviso nem URL de origem. O estado do job permanece o do processamento. Tentativa ainda guardada depois da retenção do job continua visível, identificada pelo job, sem reconstruir conteúdo. Destino desativado aparece inativo na consulta da conta.
- **Saída observável:** a tela distingue falha que ainda será tentada de falha encerrada, sem sair do contexto do job e sem abrir o texto da transcrição.
- **Evidência / checkpoint:** `pytest -q -k v08_operator_delivery` — exit 0; inclui tentativa após a retenção do job e job cujo estado não muda com a falha de entrega
- **Bloqueado por:** V-01, V-06

## Contratos e Fronteiras

O acordo HTTP está em `tasks/prd-operacao-do-servico/api-contract.yaml`. Esta spec não copia schemas. O contrato público em `tasks/prd-api-transcricao-assincrona/api-contract.yaml` não muda de paths nem de scheme.

### Mapeamento do contrato de API

| operationId | Caminho de implementação |
|---|---|
| `openOperatorSession`, `closeOperatorSession` | Sessão operacional, sem passar por `CredentialStore.authenticate` |
| `createAccount`, `listAccounts`, `getAccount` | Conta em Acesso e Contas, mais a trilha no mesmo commit |
| `issueCredential`, `listCredentials`, `getCredential`, `rotateCredential`, `revokeCredential` | Regras atuais de emissão, rotação e revogação; a trilha carrega o motivo da revogação |
| `getWebhookDestination`, `putWebhookDestination` | Criação pelo fluxo que gera o material; troca de endereço pelo fluxo que não o substitui |
| `rotateWebhookMaterial`, `deactivateWebhookDestination` | Rotação planejada ou imediata, e desativação, já existentes; motivo na trilha |
| `getQueue`, `listOperationalJobs`, `getOperationalJob` | Projeção de Transcrição e Jobs, sem resultado e sem URL de origem |
| `listDeliveryAttempts` | Projeção de Entrega de Notificações, sem o payload do aviso |

**Validações além do contrato:**

| operationId | Regra | Camada |
|---|---|---|
| `openOperatorSession` | O segredo operacional não é procurado na tabela de API Keys | Acesso e Contas |
| `putWebhookDestination` | Endereço novo com material já ativo troca só a URL | Entrega de Notificações |
| `rotateWebhookMaterial` | `immediate` sem motivo é `REASON_REQUIRED` e não chama a rotação | Entrega de Notificações |
| `revokeCredential`, `deactivateWebhookDestination` | Motivo vazio não chama a mutação | Acesso e Contas / Entrega de Notificações |
| `listOperationalJobs` | Não usa a consulta do cliente, que filtra por credencial criadora | Transcrição e Jobs |
| `listDeliveryAttempts` | Não serializa `payload` nem o material cifrado | Entrega de Notificações |

**Exceção → resposta HTTP:**

| Exceção | HTTP | code do contrato |
|---|---|---|
| Credencial operacional ausente ou API Key no console | 401 | `UNAUTHORIZED` |
| Nome de conta vazio | 422 | `ACCOUNT_NAME_REQUIRED` |
| Endereço de webhook recusado | 422 | `DESTINATION_REJECTED` |
| Motivo ausente | 422 | `REASON_REQUIRED` |
| Credencial já revogada na rotação | 409 | `CREDENTIAL_REVOKED` |
| Conta, credencial, destino ou job retido inexistente | 404 | `NOT_FOUND` ou `DESTINATION_NOT_FOUND` |

### Percurso dos segredos

- Segredo operacional: só na configuração do processo e no corpo do login, que não é logado. O cookie leva o identificador da sessão. Não entra em trilha, banco de credenciais nem cifra de webhook.
- API Key: gerada na emissão ou rotação, devolvida uma vez, persistida como hash. A consulta e a trilha usam `credentialId`.
- Material de webhook: gerado na criação ou rotação, devolvido uma vez, persistido cifrado. Troca de endereço e consultas não o descriptografam.
- URL de origem da mídia: continua cifrada por job. Nenhuma operação deste contrato a descriptografa.
- Corpo do aviso: permanece na tentativa durável. A lista do operador usa o resumo da falha, não o payload.

### Mapeamento de jornada

| User Story | Tela | operationId | Evidência |
|---|---|---|---|
| Entrar no console | `/operator` | `openOperatorSession` | V-01 |
| Criar conta e emitir chave | `/operator` | `createAccount`, `issueCredential` | V-02, V-03 |
| Configurar destino e copiar o material | `/operator` | `putWebhookDestination` | V-05 |
| Rotacionar ou revogar chave; rotacionar ou desativar destino | `/operator` | `rotateCredential`, `revokeCredential`, `rotateWebhookMaterial`, `deactivateWebhookDestination` | V-04, V-06 |
| Ver fila e falha de entrega | `/operator` | `getQueue`, `listOperationalJobs`, `listDeliveryAttempts` | V-07, V-08 |

Não há link público para mídia, resultado ou URL de origem.

### Interfaces entre fatias ou times

A única fronteira nova é o contrato HTTP deste PRD, consumido pela tela no mesmo processo. A API pública e o webhook de saída permanecem nos contratos já aceitos. Não há interface nova entre repositórios.

## Arquivos a Modificar e a Referenciar

### A modificar

| Caminho | Fatia | Alteração |
|---|---|---|
| `app/main.py` | V-01 | Servir `/operator` e montar as rotas do operador sem reutilizar o autenticador da API pública |
| `app/access/credentials.py` | V-03, V-04 | Continuar dono do hash e da revogação; a trilha com motivo precisa do mesmo commit da mutação |
| `app/jobs/webhooks.py` | V-05, V-06 | Reusar validação, criação, troca de endereço, rotação e desativação; a trilha entra no mesmo commit |
| `README.md` | V-01 | Runbook do segredo operacional e da URL `/operator`, sem gravar o segredo |

### A referenciar (não alterar)

| Caminho | Por que consultar |
|---|---|
| `app/access/cli.py` | O procedimento manual permanece; a tela não o remove |
| `app/api/transcriptions.py` | A consulta do cliente continua restrita à credencial criadora |
| `app/jobs/executor.py` | `_public_job` é o recorte seguro de estado; `source_url_for_recovery` não entra na vista do operador |
| `app/jobs/models.py` | `NotificationEvent.payload` e a ausência de FK com o job depois da retenção |
| `app/jobs/security.py` | A URL de origem permanece cifrada |
| `docs/adr/adr-002.md`, `docs/adr/adr-003.md` | Material de webhook e critério de endereço |
| `tasks/prd-operacao-do-servico/api-contract.yaml` | Schemas, códigos e `operationId`s |

## Análise de Impacto

| Componente | Tipo | Impacto e risco | Ação requerida |
|---|---|---|---|
| API pública `/v1/transcriptions` | modificado só na recusa | Cookie de operador não pode passar a autenticar | V-01 prova a recusa |
| CLI de operação | inalterado | Dois caminhos gravam a mesma conta | A tela chama as mesmas regras; o CLI continua válido |
| Worker e retenção | leitura | Vista operacional não pode tomar lease nem apagar objeto | V-07 não chama o worker |
| Entrega de webhook | leitura e comandos já existentes | Desativar continua interrompendo pendências | V-06 reusa `disable` |
| Contrato público | nenhum | Paths novos não colidem com `/transcriptions` | Não editar o YAML público |

## Riscos e Preocupações

| Preocupação | Local | Impacto | Mitigação |
|---|---|---|---|
| `authenticate` aceita qualquer segredo que pareça API Key | `app/access/credentials.py:117` | O segredo operacional, se passar por aqui, vira credencial de máquina ou é recusado pelo motivo errado | V-01 usa verificador próprio e testa os dois lados |
| `revoke` não recebe motivo e abre a própria transação | `app/access/credentials.py:90` | O motivo fica fora do commit, ou a trilha e a revogação divergem | V-04 só confirma quando o motivo existe e a trilha commitou com a revogação |
| `configure` recusa material já ativo | `app/jobs/webhooks.py:130` | Um PUT de troca de endereço criaria segundo material ou falharia | V-05 usa a troca de endereço que não gera material |
| `disable` apaga segredos e pendências na própria transação | `app/jobs/webhooks.py:208` | A trilha do motivo pode ficar de fora desse commit | V-06 entra no mesmo commit; sem motivo, `disable` não é chamado |
| `source_url_for_recovery` descriptografa a origem | `app/jobs/executor.py:268` | A lista do operador vazaria a URL | V-07 não chama esse método |
| `NotificationEvent.payload` guarda o corpo do aviso | `app/jobs/models.py:169` | A lista de tentativas pode devolver o payload | V-08 projeta só o resumo da falha |
| `_public_job` não inclui mídia, mas a linha do job tem a URL cifrada ao lado | `app/jobs/executor.py:297` | Um serializer da linha inteira expõe campos a mais | A projeção operacional lista os campos do contrato e nada mais |

## Decisões Técnicas

- **Decisão:** sessão operacional conforme a ADR-004, Accepted.
- **Racional:** a tela e a API pública dividem o host, e os privilégios não podem dividir o mesmo verificador.
- **Trade-offs:** há estado de sessão; não há operador nomeado.
- **Alternativas rejeitadas:** header de longo prazo e API Key administrativa. Estão na ADR-004.

- **Decisão:** a tela fica em `/operator`, na mesma origem da API.
- **Racional:** o baseline não cria serviço para a tela. O caminho separado evita confundir o formulário com `/v1/transcriptions`.
- **Trade-offs:** um reverse proxy precisa encaminhar `/operator` e `/v1` ao mesmo processo.
- **Alternativas rejeitadas:** host só do console. Seria outro deploy.

Nenhuma dessas decisões altera ADR-001, ADR-002 ou ADR-003.

## Verificação

- **Cenários críticos não óbvios:** segredo ausente na segunda leitura; motivo vazio sem efeito; endereço recusado preservando o destino; fila vazia; job terminal fora de 24 horas invisível; tentativa ainda listada depois disso; cookie rejeitado na API pública; API Key rejeitada no console.
- **Dados ou ambiente especiais:** o mesmo Postgres isolado das fatias da API (`postgres-test`). Segredo operacional de teste só no ambiente do pytest, nunca no repositório.
- **Verificação dos contratos:** o YAML já passou no Spectral em 2026-10-13. Cada fatia prova o `operationId` correspondente contra o serviço, não só contra o arquivo.

## Questões em Aberto

- [x] ADR-004 aprovada em 2026-10-21; a sessão por cookie permanece. Nenhuma questão aberta bloqueia as tasks.

## Architecture Decision Records

- [ADR-001: Armazenamento temporário em bucket privado S3](../../docs/adr/adr-001.md) — a tela não lê o bucket nem o resultado.
- [ADR-002: Assinatura de webhook no padrão Standard Webhooks](../../docs/adr/adr-002.md) — o material continua independente da API Key; a tela só o apresenta uma vez.
- [ADR-003: Allowlist explícita de destinos privados confiáveis](../../docs/adr/adr-003.md) — o endereço do destino usa o mesmo critério do piloto.
- [ADR-004: Sessão operacional distinta da API Key](../../docs/adr/adr-004.md) — Accepted em 2026-10-21. Autentica o console e não a API pública.
