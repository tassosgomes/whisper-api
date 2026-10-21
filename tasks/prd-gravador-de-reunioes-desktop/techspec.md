---
tsg_artifact: techspec
product: whisper
version: 1.0
status: approved
updated: 2026-10-21
capability: CAP-004
sources: tasks/prd-gravador-de-reunioes-desktop/prd.md@1.1, tasks/prd-gravador-de-reunioes-desktop/contracts.md@0.1
---

# Especificação técnica — Gravador de reuniões para Windows

> **Escopo:** Full-stack (API do serviço em Python e cliente desktop em .NET)
> **Modo:** API-First
> **PRD de origem:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (v1.1)
> **Contratos de integração:** `tasks/prd-gravador-de-reunioes-desktop/contracts.md` e `api-contract.yaml` (OpenAPI 3.1, 1.1.0, aprovado em 2026-10-21)
> **Data:** 2026-10-21
> **Status:** Aprovado em 2026-10-21
> **Handoff:** approved — pode alimentar o Task Creator

## Resumo Executivo

- **API:** o envio direto entra no processo da API existente, sem serviço novo. Um envio é um registro próprio, com idempotência própria. O job nasce só em `complete`, já em `queued`, apontando para o objeto que o cliente enviou ao bucket. O worker, o expurgo e o webhook não mudam de comportamento, exceto o expurgo de órfãos, que passa a olhar o prefixo dos envios.
- **Cliente:** aplicativo .NET em `clients/windows-recorder/` (ADR-005), dividido em duas metades. O **núcleo** (regras, gravação em disco, cliente da API, envio, acompanhamento, ViewModels) é .NET sem dependência de Windows e roda e testa no Linux. O **host Windows** (captura WASAPI, telas WPF, Credential Manager, notificação, bandeja, instalador) só compila com alvo Windows e é validado numa sessão Windows. O núcleo define as portas que o host implementa.
- **Gravação:** WAV PCM 16 kHz, mono, 16 bits, com o cabeçalho atualizado em intervalos curtos. Duas horas ocupam cerca de 230 MB e o arquivo é recuperável após queda. O formato é aceito pelo contrato (`audio/wav`).
- **Trade-off primário:** WAV sem compressão gera arquivos maiores e envios mais longos que AAC ou Opus, em troca de recuperação trivial após queda e nenhum codec nativo no host. A divisão em núcleo e host custa uma camada de portas, em troca de testar quase todo o comportamento do cliente no Linux, onde o desenvolvimento acontece.

---

## Arquitetura da Solução

### Fluxo ponta a ponta

1. O host captura microfone e áudio do sistema, e o núcleo grava o WAV em disco e mantém o estado da gravação (RF-02, RN-G01).
2. Ao pedir o envio, o núcleo calcula tamanho e SHA-256, chama `createTranscriptionUpload` com `Idempotency-Key` estável por gravação e tentativa, faz o `PUT` no S3 com os cabeçalhos devolvidos e chama `completeTranscriptionUpload`.
3. O núcleo consulta `getTranscription` em ritmo moderado até um estado terminal. Em `completed`, chama `getTranscriptionResult` e grava a transcrição (.txt e o JSON original) ao lado do áudio.
4. O host exibe a notificação do Windows e a transcrição.

```
Host Windows            Núcleo (.NET, roda no Linux)               API Whisper                S3 do Whisper
captura WASAPI  ──►  mixer ► WAV em disco ► estado da gravação
                     envio: createUpload ───────────────────────►  registra envio, presign
                            PUT (bytes) ───────────────────────────────────────────────────►  objeto uploads/…
                            complete ───────────────────────────►  confere objeto, cria job (queued)
                     acompanhamento: getTranscription / result ─►  worker existente processa
notificação  ◄──     .txt + result-v1.json ao lado do áudio
```

### Bloco Backend (API)

- **Envio como registro próprio.** Nova tabela de envios com: conta, credencial, hash da `Idempotency-Key`, impressão digital dos valores pedidos, tipo, tamanho, SHA-256, duração declarada, `clientReference`, chave do objeto, estado (`pending`, `completed`, `rejected`), código da recusa, `job_id` anulável, e o prazo do envio pendente. A unicidade é por conta, credencial e hash da chave, como em `IdempotencyRecord`. Não reaproveita `IdempotencyRecord`: aquele exige `job_id` e tem janela de 120 s.
- **Idempotência até o fim da retenção do job.** `job_id` referencia o job com exclusão em cascata. Quando o expurgo apaga o job (24 h após o estado terminal), o registro do envio some junto, e a chave volta a ser livre. Envio nunca concluído expira 24 h após o pedido, como no contrato.
- **Pedido (`createTranscriptionUpload`).** Valida os valores do contrato: tipo na lista, `sizeBytes` até 5 GiB (413), `durationMs` até 2 h (422) e `checksumSha256` em base64. Reaproveita as constantes `MAX_MEDIA_SIZE_BYTES` (`app/transcription/source.py:24`) e `MAX_MEDIA_DURATION_SECONDS` (`app/transcription/validation.py:10`). A repetição compara a impressão digital: igual devolve o mesmo envio, diferente retorna 409 `IDEMPOTENCY_KEY_REUSED`, envio recusado retorna 409 `UPLOAD_REJECTED`. Envios pendentes da credencial acima do limite configurado retornam 429 com `Retry-After`.
- **URL pré-assinada.** O objeto fica em `uploads/{uploadId}/source{sufixo}`, com o sufixo derivado do tipo (`.wav`, `.mp3`, `.m4a`, `.mp4`, `.webm`, `.mkv`). A assinatura cobre método `PUT`, tipo, tamanho e SHA-256 do contrato. A URL é gerada por um cliente S3 apontado ao endereço **público** do armazenamento. A URL nunca é persistida; uma repetição do pedido a gera de novo.
- **Conclusão (`completeTranscriptionUpload`).** Dentro de uma transação com bloqueio da linha do envio: se já concluído, devolve o mesmo job; se recusado, repete a recusa. Senão consulta o objeto (existência, tamanho, SHA-256 registrado pelo armazenamento) e lê os primeiros bytes para conferir a assinatura do formato. Não existindo, 409 `UPLOAD_NOT_RECEIVED`. Divergindo, marca recusado, apaga o objeto e retorna 422 (`UPLOAD_SIZE_MISMATCH`, `UPLOAD_CHECKSUM_MISMATCH` ou `UNSUPPORTED_MEDIA_FORMAT`). Conferindo, cria o job com estado `queued`, `media_object_key` apontando ao objeto, `accepted_at` e `queued_at` agora, sem `source_url_encrypted`, e liga o envio ao job. Responde 202.
- **Processamento sem mudança.** O worker já seleciona `queued` com `media_object_key` (`app/jobs/worker.py:519`), baixa pelo sufixo da chave (`app/jobs/worker.py:186`), mede duração e formato com `inspect_media` e falha com `MEDIA_DURATION_LIMIT_EXCEEDED` ou `UNSUPPORTED_MEDIA_FORMAT` (`app/jobs/worker.py:225`). É o comportamento que o contrato descreve para duração real e conteúdo não decodificável.
- **Expurgo.** O varredor de órfãos passa a incluir o prefixo `uploads/`: objeto não referenciado por nenhum job, cujo envio está ausente, recusado ou pendente vencido, é apagado. A mesma rotina remove linhas de envio pendente vencidas e recusadas com mais de 24 h.
- **Configuração nova.** Endereço público do armazenamento para assinar URLs (padrão: o endereço interno; no piloto, `https://s3.tasso.dev.br`), validade da URL (padrão 1 h) e limite de envios pendentes por credencial (padrão 5).
- **Mesmo bucket, prefixo próprio.** Os envios usam o bucket do Whisper já existente (ADR-001), no prefixo `uploads/`. Uma regra de ciclo de vida do armazenamento expira os objetos desse prefixo em 2 dias, como rede de segurança independente do expurgo da aplicação.

### Bloco Frontend (cliente Windows)

**Divisão em projetos** (nomes e pastas seguem a estrutura da solução definida no EN-01):

| Projeto | Alvo | Contém | Roda no Linux |
|---|---|---|---|
| Núcleo | `net10.0` | Estado e ciclo da gravação, gravação em WAV e recuperação, repositório local, cliente da API gerado do OpenAPI, coordenador de envio, acompanhador do job, formatação do .txt, configurações | Sim |
| Apresentação | `net10.0` | ViewModels (MVVM) das telas, sem tipos de WPF | Sim |
| Host | `net10.0-windows`, WPF | Telas XAML, captura WASAPI e mixagem, Credential Manager, notificação, bandeja, atalho global, instalador | Não; compila no Linux só para verificação |
| Testes do núcleo e da apresentação | `net10.0` | xUnit contra o núcleo, com a API real (moto ou MinIO) ou dublê | Sim |
| Testes do host | `net10.0-windows` | Captura, telas, notificação | Não |

**Onde mora o estado.** Tudo que o usuário vê vem do repositório local da gravação (um diretório por gravação, ver Contratos e Fronteiras). O servidor só é consultado para o job em andamento. Fechar o app não perde o acompanhamento: ao abrir, o acompanhador relê as gravações com envio em andamento.

**Estados da gravação** (para RF-04, RF-05, RF-06): `recording`, `saved`, `interrupted`, `uploading`, `processing`, `transcribed`, `failed`, `result-expired`. Mais um indicador de "sem conexão" que não altera o estado.

**Captura (host).**
- Duas fontes independentes: microfone (captura) e áudio do sistema (loopback do dispositivo de saída padrão). O mixer é dirigido pelo relógio de parede: quando uma fonte não entrega dados, o mixer insere silêncio nela. Isso é necessário porque o loopback só dispara dados enquanto algo toca (documentação do NAudio, consultada via Context7).
- Cada fonte é atenuada antes da soma para evitar saturação. O resultado é reamostrado para 16 kHz mono.
- Perda de dispositivo: o host emite o evento à porta de captura, o núcleo registra o aviso, a gravação continua com a fonte restante e o arquivo já escrito é preservado.
- Limite de 2 h: aviso aos 5 min restantes, encerramento e salvamento aos 2 h (RN-G03).

**Persistência contínua.** O WAV é escrito em blocos pequenos e o cabeçalho (tamanhos) é regravado em intervalos curtos, de modo que um processo morto deixa um arquivo quase válido. Na abertura, qualquer gravação em `recording` é marcada `interrupted` e o cabeçalho é recalculado pelo tamanho do arquivo (RN-G01). O arquivo de metadados é sempre gravado por escrita atômica (arquivo temporário e renomeação).

**Aviso de terceiros (RF-03).** O aviso é exibido na tela de gravação a cada início, sem confirmação.

**Tema.** WPF-UI com tema Fluent e Mica; o pacote declara suporte a `net10.0-windows` (consultado via Context7). A licença não foi verificada.

---

## Mapa de Fatias Verticais

O mapa tem uma coluna **Ambiente**. As fatias do cliente que têm uma metade Windows indicam o que roda no Linux e o que exige uma sessão Windows. Um task só pode misturar as duas metades se o ambiente for Windows.

### V-01: Pedir a URL de envio de áudio

- **Ambiente:** Linux.
- **Cobre:** RF-10, RN-G03 (limites), RN-G04.
- **Entrada / gatilho:** `createTranscriptionUpload` com API Key, `Idempotency-Key` e os valores do contrato.
- **Processamento:** valida limites e tipo, aplica a idempotência descrita no bloco backend, limita pendentes por credencial, grava o envio e devolve a URL assinada com `expiresAt`.
- **Saída observável:** 201 com `uploadId`, `upload` e `completeUrl`; 200 em repetição (mesma `uploadId`, URL nova se vencida); 409, 413, 422 e 429 conforme o contrato.
- **Evidência / checkpoint:** testes da API com bucket simulado cobrindo cada resposta do contrato; um `PUT` real na URL devolvida (armazenamento do compose) com os cabeçalhos devolvidos tem sucesso e com cabeçalhos diferentes é recusado pelo armazenamento.
- **Bloqueado por:** Nenhum.

### V-02: Concluir o envio e criar o job

- **Ambiente:** Linux.
- **Cobre:** RF-10, RN-G04.
- **Entrada / gatilho:** `completeTranscriptionUpload` depois do `PUT`.
- **Processamento:** confere objeto, tamanho, SHA-256 e assinatura do formato; cria o job `queued` apontando ao objeto; liga o envio ao job. Repetição devolve o mesmo job. Divergência recusa o envio e apaga o objeto.
- **Saída observável:** 202 com o job em `queued`; 409 `UPLOAD_NOT_RECEIVED`; 422 com o motivo; 404 neutro para envio de outra conta ou credencial. O job segue até `completed` com o worker existente, e `GET .../result` devolve o resultado.
- **Evidência / checkpoint:** teste ponta a ponta com áudio real curto: pedido, `PUT`, `complete`, polling até `completed`, leitura do resultado. Um arquivo `.wav` renomeado como `.mp3` termina em 422 no `complete`. Um arquivo válido de mais de 2 h (sintético) termina o job em `failed/MEDIA_DURATION_LIMIT_EXCEEDED`.
- **Bloqueado por:** V-01.

### V-03: Envio abandonado ou recusado é expurgado e a idempotência acaba com o job

- **Ambiente:** Linux.
- **Cobre:** RF-10, RN-G04; retenção da ADR-001.
- **Entrada / gatilho:** rotina de retenção do worker.
- **Processamento:** apaga objetos de `uploads/` sem job e sem envio pendente vigente; apaga linhas pendentes vencidas e recusadas antigas. Quando o job é expurgado, a linha do envio some por cascata. O guia de operação passa a exigir a regra de ciclo de vida de 2 dias no prefixo `uploads/` e a conferência dela junto da política do bucket (ADR-001).
- **Saída observável:** bucket e tabela sem resíduos; a mesma `Idempotency-Key` volta a ser aceita como envio novo depois do expurgo.
- **Evidência / checkpoint:** teste com relógio controlado: envio pendente vencido apagado; envio pendente vigente preservado; objeto de job em andamento preservado; depois das 24 h do estado terminal, objeto, job e envio sumidos.
- **Bloqueado por:** V-02.

### V-04: Gravar a reunião e guardar a gravação no PC

- **Ambiente:** núcleo no Linux; captura, telas e indicadores no Windows.
- **Cobre:** RF-02, RF-03, RF-04 (listagem e situação), RN-G01, RN-G02, RN-G03.
- **Entrada / gatilho:** o usuário inicia a gravação, escolhe fontes, pausa, retoma e para.
- **Processamento:** o host entrega os blocos de áudio de cada fonte; o núcleo mixa por relógio, grava o WAV e atualiza os metadados. Aplica pausa, perda de dispositivo, limite de 2 h, aviso de espaço em disco e recuperação de gravação interrompida. A gravação nunca é enviada nesta fatia.
- **Saída observável:** a gravação aparece na lista com título, data, duração e situação; indicador de gravação na janela, na barra de tarefas e na bandeja; aviso de terceiros ao iniciar.
- **Evidência / checkpoint:** (Linux) testes do núcleo: WAV íntegro; matar o processo no meio da escrita e reabrir produz arquivo legível com a situação `interrupted`; limite de 2 h; pausa não entra na duração. (Windows) gravar 5 minutos de reunião real, com a fala própria e áudio tocando no PC, e confirmar as duas vozes na reprodução; desconectar o fone no meio e confirmar que a gravação continua; fechar o app à força e recuperar.
- **Bloqueado por:** EN-01.

### V-05: Configurar o acesso à API

- **Ambiente:** núcleo no Linux; cofre de credenciais e tela no Windows.
- **Cobre:** RF-01, RN-G07.
- **Entrada / gatilho:** o usuário informa endereço e API Key nas configurações.
- **Processamento:** valida a chave consultando `getTranscription` com um identificador que não existe. `401` significa chave não aceita; `404` significa chave aceita; erro de rede ou `5xx` significa não foi possível verificar. Salva a chave no cofre do Windows e o endereço em configuração comum.
- **Saída observável:** confirmação de acesso, ou a mensagem correta para chave recusada ou API indisponível; chave mascarada com os últimos caracteres; substituir e remover.
- **Evidência / checkpoint:** (Linux) testes do núcleo contra a API real: chave válida, revogada, API fora do ar. (Windows) a chave sobrevive ao reinício do app, não aparece em arquivo de configuração nem em log, e a remoção apaga do cofre.
- **Bloqueado por:** EN-01.

### V-06: Enviar uma gravação para transcrição

- **Ambiente:** núcleo no Linux; telas no Windows.
- **Cobre:** RF-05, RF-08 (nada sai sem ação), RN-G02, RN-G03, RN-G04.
- **Entrada / gatilho:** o usuário confirma o envio de uma gravação.
- **Processamento:** o coordenador calcula tamanho e SHA-256, usa uma `Idempotency-Key` derivada do identificador da gravação e do número da tentativa, faz o `PUT` com progresso, chama `complete` e grava o `jobId`. Falha de rede em qualquer passo leva a "tentar de novo" manual, que reaproveita a mesma chave. Nova tentativa só incrementa o número quando o envio foi recusado (`UPLOAD_REJECTED`) ou o usuário reenvia uma gravação já transcrita. `PUT` com URL vencida repete o pedido (mesma chave) para obter outra URL. `401` orienta a atualizar a chave. Duração acima de 2 h, tipo ou tamanho recusados são explicados sem perder a gravação.
- **Saída observável:** progresso do envio e a gravação em `uploading`, depois `processing`; mensagens para cada falha do contrato; gravação sempre intacta.
- **Evidência / checkpoint:** (Linux) testes do núcleo contra a API real: envio completo; queda entre o `PUT` e o `complete` e retomada sem segundo job; repetição horas depois (relógio avançado) sem segundo job; recusa por checksum; URL vencida. (Windows) enviar uma gravação de 5 minutos com a API do compose e ver o job `queued`.
- **Bloqueado por:** V-02, V-04, V-05.

### V-07: Acompanhar o job e guardar a transcrição

- **Ambiente:** núcleo no Linux; notificação e lista no Windows.
- **Cobre:** RF-06, RF-07 (guarda do .txt), RN-G05, RN-G06, RN-G08.
- **Entrada / gatilho:** gravação em `processing`, no envio ou na abertura do app.
- **Processamento:** o acompanhador consulta o job em intervalos crescentes até o estado terminal, com recuo diante de erro e sem consulta enquanto não há conexão. Em `completed`, busca o resultado, grava `result-v1.json` e o `.txt` (um trecho por linha, com o tempo inicial relativo à gravação) ao lado do áudio e marca `transcribed`. Em `failed`, guarda o código e marca `failed`. Em 404 depois do envio aceito, marca `result-expired`. `schemaVersion` desconhecida guarda o JSON bruto e marca a gravação como precisando de atualização, sem descartar nada.
- **Saída observável:** a situação muda na lista sem recarregar; notificação do Windows ao concluir ou falhar, mesmo minimizado; mensagem "sem conexão" sem marcar falha.
- **Evidência / checkpoint:** (Linux) testes do núcleo: ciclo completo contra a API real; reabrir o app no meio do `processing` retoma; backoff após 5xx; 404 vira `result-expired`; resultado com `schemaVersion` desconhecida preservado. (Windows) a notificação aparece com o app minimizado.
- **Bloqueado por:** V-06.

### V-08: Ler, copiar e exportar a transcrição

- **Ambiente:** ViewModels no Linux; tela no Windows.
- **Cobre:** RF-07, RN-G05.
- **Entrada / gatilho:** o usuário abre uma gravação transcrita, copia ou exporta.
- **Processamento:** lê o `.txt` e o JSON locais, sem consultar o servidor. Copia com ou sem tempos. Exporta para o caminho escolhido e confirma antes de sobrescrever. Transcrição vazia mostra a mensagem própria.
- **Saída observável:** texto por trechos com tempo; área de transferência; arquivo exportado.
- **Evidência / checkpoint:** (Linux) testes dos ViewModels: cópia com e sem tempos, vazia, sobrescrita. (Windows) abrir uma transcrição com o serviço desligado.
- **Bloqueado por:** V-07.

### V-09: Gerenciar gravações e apagar os dados do app

- **Ambiente:** núcleo no Linux; lista, player e diálogos no Windows.
- **Cobre:** RF-04, RF-08, RN-G05.
- **Entrada / gatilho:** o usuário reproduz, renomeia ou apaga uma gravação, ou escolhe "apagar todos os dados".
- **Processamento:** apagar remove áudio, transcrição e metadados da gravação, com aviso específico se há envio em andamento (o job no servidor não é cancelado). "Apagar tudo" também remove a chave do cofre. Reprodução com avançar e voltar.
- **Saída observável:** a gravação some da lista; nada resta no diretório do app.
- **Evidência / checkpoint:** (Linux) testes do núcleo e dos ViewModels: apagar com envio em andamento exige a confirmação e não toca o servidor; "apagar tudo" deixa o diretório vazio. (Windows) reprodução e remoção da chave no cofre.
- **Bloqueado por:** V-04.

### V-10: Instalar e desinstalar

- **Ambiente:** Windows.
- **Cobre:** RF-09 (Should Have; Fase 2 no PRD).
- **Entrada / gatilho:** o usuário executa o instalador e o desinstalador.
- **Processamento:** instala sem driver nem software de terceiros e pergunta, ao desinstalar, se mantém as gravações e transcrições.
- **Saída observável:** app instalado e funcional num Windows 11 limpo; reinstalar com dados mantidos mostra as gravações.
- **Evidência / checkpoint:** instalar e gravar num Windows 11 limpo; desinstalar mantendo e apagando os dados.
- **Bloqueado por:** V-04, V-05, V-06, V-07. Pode ser adiada para a Fase 2 sem bloquear as demais.

### Habilitadores inevitáveis

| Habilitador | Por que não cabe numa fatia | Menor escopo | Primeira fatia desbloqueada |
|---|---|---|---|
| EN-01 | Não há código do cliente no repositório; as fatias V-04 a V-09 compartilham a estrutura da solução, o cliente da API e a execução dos testes no Linux. | Solução em `clients/windows-recorder/` com núcleo, apresentação, host e projetos de teste; cliente da API gerado de `api-contract.yaml`; as portas do host definidas no núcleo; um comando de build e teste que roda no Linux e um que compila o host com alvo Windows; registro de diagnóstico sem conteúdo sensível; um fluxo de CI. Prova de que o host compila no Linux (incerto, ver Riscos). | V-04 e V-05 |

---

## Contratos e Fronteiras

### Mapeamento do contrato de API

Endpoints, schemas, autenticação e formato de erro vivem em `api-contract.yaml`. Esta spec não os duplica.

| operationId | Caminho de implementação |
|---|---|
| `createTranscriptionUpload` | rota nova no roteador de transcrições → componente de envios → tabela de envios e cliente S3 público |
| `completeTranscriptionUpload` | rota nova → componente de envios → consulta ao objeto no S3 e criação do job em `queued` |
| `getTranscription`, `getTranscriptionResult` | existentes, sem alteração (`app/api/transcriptions.py:148`, `:166`) |

**Validações além do contrato:**

| operationId | Regra | Camada |
|---|---|---|
| `createTranscriptionUpload` | Envio e job pertencem à conta e à credencial que os criaram; outro devolve 404 neutro | aplicação |
| `createTranscriptionUpload` | A impressão digital da repetição cobre tipo, tamanho, SHA-256, duração e `clientReference` | aplicação |
| `completeTranscriptionUpload` | A criação do job e a ligação com o envio ocorrem na mesma transação, com a linha bloqueada | aplicação |
| `completeTranscriptionUpload` | Um objeto que o armazenamento não registrou com SHA-256 é tratado como divergente | aplicação |

**Exceção → resposta HTTP** (o renderizador de Problem Details já aceita `detail` como dicionário, `app/main.py:215`):

| Exceção | HTTP | code do contrato |
|---|---|---|
| Chave reutilizada com outros valores | 409 | `IDEMPOTENCY_KEY_REUSED` |
| Envio já recusado | 409 | `UPLOAD_REJECTED` |
| Tamanho declarado acima de 5 GiB | 413 | `MEDIA_SIZE_LIMIT_EXCEEDED` |
| Tipo fora da lista | 422 | `UNSUPPORTED_MEDIA_FORMAT` |
| Duração declarada acima de 2 h | 422 | `MEDIA_DURATION_LIMIT_EXCEEDED` |
| Pendentes acima do limite | 429 (com `Retry-After`) | `TOO_MANY_PENDING_UPLOADS` |
| Objeto ausente | 409 | `UPLOAD_NOT_RECEIVED` |
| Tamanho ou SHA-256 divergente | 422 | `UPLOAD_SIZE_MISMATCH`, `UPLOAD_CHECKSUM_MISMATCH` |
| Assinatura do formato inválida | 422 | `UNSUPPORTED_MEDIA_FORMAT` |

Corpo malformado e campo ausente já saem como 400 `INVALID_REQUEST` (`app/main.py:286`).

### Dados sensíveis e credenciais transitórias

| Dado | Criado | Copiado e persistido | Lido | Descartado |
|---|---|---|---|---|
| URL de envio (segredo temporário) | Pela API, no pedido | Só na resposta HTTP. Não é gravada no banco, em log nem em métrica da API. No cliente fica em memória durante o envio | Pelo cliente, uma vez por `PUT` | Ao fim do envio ou quando vence. A repetição do pedido gera outra |
| API Key | Pelo operador (CAP-002) | No cofre do Windows, no cliente | Pelo núcleo, em memória, a cada chamada | Ao remover a chave ou apagar os dados do app |
| Áudio | No PC, pela captura | No diretório do app e, no envio, em `uploads/…` no bucket do Whisper | Pelo worker | O worker apaga ao concluir (a chave sai do job em `app/jobs/worker.py:595`); o varredor remove o resto (V-03). No PC, só quando o usuário apaga |
| Transcrição | Pelo worker | JSON no bucket por 24 h; `.txt` e JSON no PC | Pelo cliente | No bucket, pelo expurgo existente. No PC, quando o usuário apaga |
| SHA-256 do arquivo | Pelo cliente | Registro do envio | Pela API, no `complete` | Com o job, por cascata. Não é segredo |

O cliente não registra a URL de envio, a API Key, o áudio nem o texto. A API não registra a URL de envio.

### Dados locais do cliente

- Raiz: `%LOCALAPPDATA%` do usuário, em um diretório do app, com uma pasta por gravação contendo o áudio, os metadados, o JSON do resultado e o `.txt`.
- Os metadados trazem o identificador da gravação (que também é o `clientReference`), o estado, o contador de tentativas de envio, o `uploadId` e o `jobId` quando existem, e o código de falha. Nunca a URL de envio.
- Endereço da API em um arquivo de configuração; a API Key somente no cofre.

### Interfaces entre fatias ou times (núcleo ↔ host Windows)

São o contrato entre a sessão que desenvolve no Linux e a que desenvolve no Windows. O núcleo as define e as testa com dublês; o host as implementa.

| Porta | Responsabilidade | Quem a implementa |
|---|---|---|
| Captura de áudio | Listar dispositivos; iniciar e parar cada fonte; entregar blocos PCM com marcação de tempo; emitir nível; emitir perda ou volta de dispositivo | Host |
| Cofre de credenciais | Gravar, ler e remover a API Key | Host |
| Notificador | Exibir a notificação do Windows e o indicador de gravação | Host |
| Relógio e agendador | Hora e esperas, substituíveis em teste | Núcleo, com dublê nos testes |
| Reprodutor | Tocar um WAV com posição | Host |

---

## Arquivos a Modificar e a Referenciar

Arquivos novos não são listados.

### A modificar

| Caminho | Fatia | Alteração |
|---|---|---|
| `app/api/transcriptions.py` | V-01, V-02 | Duas rotas novas, usando a mesma autenticação por API Key |
| `app/main.py` | V-01 | Configuração do endereço público do S3, validade da URL e limite de pendentes (`app/main.py:66-99`); montagem do componente de envios (`app/main.py:155-165`) |
| `app/storage/s3.py` | V-01, V-02, V-03 | Assinatura de `PUT` com o endereço público; consulta do objeto (tamanho, SHA-256, primeiros bytes); chave `uploads/` |
| `app/jobs/models.py` | V-01 | Modelo do envio, com `job_id` anulável e cascata |
| `app/jobs/worker.py` | V-03 | Varredor de órfãos inclui `uploads/` e envios vencidos (`app/jobs/worker.py:313`) |
| `app/jobs/executor.py` | V-02 | Criar job `queued` sem origem; manter `_public_job` válido (`app/jobs/executor.py:389`) |
| `migrations/versions/` | V-01 | Migration da tabela de envios |
| `docker-compose.yml`, `.env.example`, `README.md` | V-01 | Variáveis novas e o endereço público do armazenamento |
| `scripts/init_minio.sh` | V-01 | Nenhuma permissão nova é necessária: a política já cobre `s3:PutObject` em `bucket/*` (linha 40); conferir |
| `docs/adr/` | — | Nenhuma ADR nova |

### A referenciar (não alterar)

| Caminho | Por que consultar |
|---|---|
| `tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml` | Fonte dos schemas e dos códigos; o cliente da API é gerado dele |
| `app/jobs/worker.py:177-250` | Fluxo de processamento que o job de envio direto reaproveita sem mudança |
| `app/jobs/executor.py:92-160` | Padrão de reserva idempotente por bloqueio de linha |
| `app/transcription/validation.py` | Tipos, formatos e limite de duração |
| `tests/conftest.py` | Fixtures com bucket simulado e banco, e os usos de credencial |
| `docs/adr/adr-001.md`, `adr-005.md` | Retenção do bucket e a stack do cliente |
| `context/architecture-baseline.md` | Seção "Clientes do Whisper" |

---

## Análise de Impacto

| Componente | Tipo | Impacto e risco | Ação requerida |
|---|---|---|---|
| Bucket S3 | modificado | Recebe objetos de clientes sem passar pelo processo da API; mais bytes e um prefixo novo | Expurgo (V-03) e métrica de objetos órfãos |
| Endereço do armazenamento | modificado | Deve ser alcançável por HTTPS pelo PC do usuário. O endereço do piloto, `s3.tasso.dev.br`, tem certificado válido e resolve para um IP da rede local (192.168.0.5) | Piloto vale para PCs na mesma rede ou VPN; acesso externo é decisão futura. O MinIO do compose continua só para desenvolvimento (`docker-compose.yml:9`) |
| Tabela de jobs | modificado | Jobs sem origem; operações que supõem `accepted_at` e origem devem tolerar | Preencher `accepted_at` ao criar o job e cobrir a visão do operador em V-02 |
| Visão operacional (CAP-002) | modificado | Passa a ver jobs de envio direto em `queued` | Nenhuma ação; a visão já lê estado e idade |
| Contrato 1.0.0 | inalterado | Consumidor atual deve tolerar um job que começa em `queued` | Já registrado em `contracts.md` |
| Repositório | modificado | Primeiro código .NET; sem CI hoje (`.github/` não existe) | EN-01 inclui o fluxo de CI |

---

## Riscos e Preocupações

| Preocupação | Local (`arquivo:linha`) | Impacto | Mitigação |
|---|---|---|---|
| O varredor de órfãos só olha `media/` e `results/`. Objetos de `uploads/` abandonados nunca seriam apagados. | `app/jobs/worker.py:313` | Resíduo de áudio de usuário no bucket, contra a ADR-001 | V-03 inclui o prefixo e testa o expurgo |
| `IdempotencyRecord` exige `job_id` e vale 120 s. | `app/jobs/models.py:104`, `app/jobs/executor.py:36` | Reaproveitá-lo violaria RN-G04 para envios lentos | Tabela de envios própria, com a idempotência do contrato |
| O cliente S3 usa o endereço interno (`http://minio:9000`). A URL assinada sairia com um host que o PC do usuário não alcança. | `app/main.py:155`, `docker-compose.yml:80` | Envio impossível fora da rede do compose | Cliente de assinatura com o endereço público, configurável |
| O worker conta com `accepted_at` e `source_connection_started_at` para métricas de aceite. | `app/jobs/worker.py:612` | Métrica `acceptedToTerminal` ausente ou distorcida em jobs de envio | Preencher `accepted_at` em V-02 e checar a métrica no teste |
| O contrato diz que o envio "continua válido até `expiresAt`" depois do `409 UPLOAD_NOT_RECEIVED`, mas o registro pendente vale 24 h e a URL vale 1 h. | `api-contract.yaml` (descrição de `completeTranscriptionUpload`) | Cliente pode achar que o `PUT` repete até `expiresAt` sem obter outra URL | Implementação: depois do vencimento da URL, o cliente repete o pedido (mesma chave) e recebe outra URL. V-06 faz isso. Esclarecer o texto do contrato (não bloqueia) |
| Não há skill nem convenção .NET neste repositório. | `.claude/skills/` | Decisões de projeto (estrutura, testes) ficariam para cada task | EN-01 fixa a estrutura e as convenções do cliente |
| Não há CI. | `.github/` ausente | Nenhum gate automático para o cliente | EN-01 |
| A captura do sistema só entrega dados enquanto algo toca. | documentação do NAudio, via Context7 | Gravação com lacunas ou dessincronizada do microfone | Mixer por relógio com silêncio inserido; checkpoint de 5 min em V-04 |
| Dispositivos em modo exclusivo ou Bluetooth variam por PC. | — | Falha de captura em alguns PCs | Medidor de nível antes de gravar (RF-02); checkpoint em V-04 em mais de um dispositivo |

---

## Decisões Técnicas

- **Decisão:** registro de envio próprio, com chave de idempotência válida até o fim da retenção do job.
  - **Racional:** o contrato e o RN-G04 exigem que repetir tarde não duplique o job; o registro existente expira em 120 s e depende de um job.
  - **Trade-offs:** uma tabela a mais e mais uma rotina de expurgo.
  - **Alternativas rejeitadas:** ampliar a janela de `IdempotencyRecord` (afetaria o fluxo de URL assinada e exigiria job desde o pedido).
- **Decisão:** o objeto do envio vira o `media_object_key` do job, sem cópia.
  - **Racional:** evita copiar até 5 GiB e reaproveita o fluxo de processamento e de expurgo.
  - **Trade-offs:** o prefixo `uploads/` e o prefixo `media/` convivem, e o varredor precisa conhecer os dois.
  - **Alternativas rejeitadas:** copiar para `media/{job}` (custo de cópia e janela de resíduo).
- **Decisão:** validar a chave do cliente com `getTranscription` para um identificador inexistente.
  - **Racional:** o contrato não tem operação de "quem sou eu"; `401` distingue chave recusada de aceita (`404`), sem efeito colateral.
  - **Trade-offs:** não confere permissões além do acesso; hoje todas as chaves têm as mesmas.
  - **Alternativas rejeitadas:** criar um envio de teste (deixa registro); acrescentar uma operação ao contrato (mudança fora do escopo).
- **Decisão:** gravar WAV PCM 16 kHz mono 16 bits.
  - **Racional:** recuperável após queda reescrevendo o cabeçalho; 2 h ficam em cerca de 230 MB, bem abaixo de 5 GiB; reconhecimento de fala não precisa de mais amostragem; não exige codec.
  - **Trade-offs:** arquivos maiores que AAC ou Opus.
  - **Alternativas rejeitadas:** AAC/MP3 (cabeçalho e moov difíceis de recuperar, dependência de codec); Opus/WebM (formato aceito, mas sem ganho que justifique a complexidade).
- **Decisão:** ViewModels fora do alvo Windows.
  - **Racional:** o comportamento das telas fica testável no Linux.
  - **Trade-offs:** as portas do host precisam ser mantidas em dia.
  - **Alternativas rejeitadas:** ViewModels no projeto WPF (só testáveis no Windows).
- **Decisão:** .NET 10 (`net10.0`, `net10.0-windows`) e WPF-UI para o tema.
  - **Racional:** o SDK 10 está instalado no ambiente de desenvolvimento; o WPF-UI declara suporte a `net10.0-windows`.
  - **Trade-offs:** versão recente; licença do WPF-UI não verificada.
  - **Alternativas rejeitadas:** .NET 8/9 (sem vantagem aqui); tema próprio (mais XAML para manter).
- **Decisão:** os envios usam o bucket existente do Whisper, no prefixo `uploads/`, e não um bucket dedicado (2026-10-21).
  - **Racional:** o objeto do envio vira a mídia do job sem cópia (decisão anterior). A regra de ciclo de vida por prefixo dá o isolamento de limpeza que um bucket dedicado daria, sem tornar o armazenamento e o worker cientes de dois buckets.
  - **Trade-offs:** não há política de acesso nem cota separadas para os envios. O assinante e o worker usam as mesmas credenciais, e a URL assinada já limita o acesso a um objeto.
  - **Alternativas rejeitadas:** bucket dedicado (cada `media_object_key` passaria a carregar o bucket ou o armazenamento a conhecer dois buckets, e a verificação da ADR-001 teria de cobrir ambos). Reconsiderar se surgir exigência de cota ou política separadas.
- **Decisão:** valores padrão de envio: URL válida por 1 h, 5 envios pendentes por credencial.
  - **Racional:** 1 h cobre um envio lento de centenas de MB, e a repetição do pedido renova a URL; 5 limita o resíduo por credencial.
  - **Trade-offs:** valores sem medição; ajustáveis por configuração.
  - **Alternativas rejeitadas:** validade de 24 h (URL de longa duração é um segredo maior).

---

## Verificação

- **Cenários críticos não óbvios:**
  - Concorrência: dois `complete` simultâneos do mesmo envio criam um só job.
  - Queda entre o `PUT` e o `complete`, e entre o `complete` e a resposta: a repetição devolve o mesmo job.
  - Repetição horas depois do pedido, com o relógio avançado.
  - Arquivo cuja extensão ou tipo não bate com o conteúdo.
  - Matar o processo do cliente no meio da escrita do WAV e reabrir.
  - Loopback sem nada tocando por vários minutos: a duração do arquivo bate com o relógio.
  - Fechar e reabrir o app com envios em andamento.
- **Dados ou ambiente especiais:**
  - API: Postgres e bucket simulado (`moto`) como nos testes atuais. O comportamento do `PUT` pré-assinado com tamanho e SHA-256 assinados não pôde ser confirmado em documentação e precisa de uma execução contra o MinIO do compose (V-01).
  - Cliente: os testes do núcleo e da apresentação rodam no Linux com o SDK 10 instalado no ambiente.
  - O host Windows exige uma sessão do Claude no Windows, com Windows 11, um microfone e uma saída de áudio. O armazenamento precisa estar alcançável a partir dela (endereço público ou o MinIO do compose exposto na rede).
- **Observabilidade além do padrão:** contadores de envios pedidos, concluídos, recusados por motivo e abandonados, no mecanismo de métricas existente (`app/monitoring/metrics.py:73`). Nenhum contador leva conta, chave, URL ou nome de arquivo.
- **Verificação dos contratos:**
  - HTTP: cada resposta de `createTranscriptionUpload` e `completeTranscriptionUpload` é exercitada contra o `api-contract.yaml`; o fluxo de `sourceUrl` continua passando nos testes atuais.
  - Cenários do `contracts.md` (repetição, conflito, `complete` antes e depois do `PUT`, objeto divergente, isolamento por credencial, ausência da URL em logs).
  - A validação do YAML é distinta da conformidade da implementação.
- **Compilação do host fora do Windows:** `dotnet build` do host com `EnableWindowsTargeting` serve de verificação de compilação no Linux. Se a compilação de XAML falhar nesse ambiente, a verificação do host passa a ser exclusiva do Windows, sem mudar as fatias.

---

## Questões em Aberto

- [ ] Regra de ciclo de vida de 2 dias no prefixo `uploads/` do bucket de produção, e confirmação de que versionamento e Object Lock estão desligados em `s3.tasso.dev.br` (ADR-001) — operação — sem a regra, só o expurgo da aplicação limpa abandonos.
- [ ] O endereço `s3.tasso.dev.br` resolve para um IP da rede local; PCs fora dela ou sem VPN não conseguem enviar — produto e operação — limita o piloto à rede local.
- [ ] Texto do contrato sobre a validade do envio depois do `409 UPLOAD_NOT_RECEIVED` (ver Riscos) — produto — esclarecimento apenas.
- [ ] Licença do WPF-UI e política de bibliotecas de terceiros — produto — se for restritiva, troca-se o tema sem afetar o núcleo.
- [ ] Domain doc de Transcrição e Jobs ainda sem a entidade "Envio" (NA9) — produto — a fatia V-01 pode começar; o domain doc deve ser atualizado antes da aprovação final.
- [ ] Valores de validade da URL e de pendentes por credencial (1 h e 5) — produto e operação — ajustáveis por configuração.
- [ ] O ambiente Windows para as sessões do host (máquina, acesso ao repositório e ao armazenamento) — usuário — define quando V-04, V-05 e V-10 podem ser validadas.

---

## Architecture Decision Records

Nenhuma ADR nova é necessária.

- [ADR-001: Armazenamento temporário em bucket privado S3](../../docs/adr/adr-001.md) — o objeto do envio vive no mesmo bucket e segue o mesmo expurgo.
- [ADR-005: Cliente desktop Windows em .NET e WPF](../../docs/adr/adr-005.md) — plataforma, local do código e papel do cliente.
