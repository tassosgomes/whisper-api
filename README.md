# Transcrição local de reuniões

PoC para transcrever arquivos de áudio e vídeo em português com `faster-whisper`, usando CPU e INT8. A API e a CLI carregam o modelo escolhido localmente; a aplicação não precisa acessar serviços de transcrição externos.

## Pré-requisitos

- Docker Engine com Docker Compose v2;
- conexão com a internet durante o primeiro build da imagem e o preparo dos modelos;
- espaço em disco para a imagem e os modelos que serão comparados.

Copie o arquivo de configuração e confira os diretórios locais:

```bash
cp .env.example .env
printf 'SOURCE_URL_ENCRYPTION_KEY=%s\n' "$(openssl rand -hex 32)" >> .env
```

O Compose publica a API somente em `127.0.0.1:8000`. Por padrão, usa o modelo `medium`, limita o container a oito CPUs e define o mesmo número de threads de inferência. Os arquivos de entrada ficam somente leitura no container; os resultados são gravados em `data/output`.

O Compose inicia PostgreSQL com volume próprio; a API aguarda o banco saudável
e aplica as migrations Alembic ao iniciar. Credenciais de conexão vêm das
variáveis de ambiente do `.env`. O arquivo `.env.example` contém somente
valores locais de exemplo, não segredos de implantação. A chave
`SOURCE_URL_ENCRYPTION_KEY` cifrará URLs assinadas persistidas para permitir
retomada do job; mantenha-a no `.env` e preserve-a junto aos backups do banco.

Se o usuário do host não tiver UID/GID `1000`, ajuste `LOCAL_UID` e `LOCAL_GID` em `.env` para os valores de `id -u` e `id -g`. Isso permite que o container grave os artefatos com a propriedade correta.

## Preparar a imagem e os modelos

Faça o build enquanto houver internet:

```bash
docker compose build transcriber
```

Baixe os snapshots CTranslate2 compatíveis com `faster-whisper` para `models/`:

```bash
docker compose --profile tools run --rm model-prep
```

O comando prepara `small`, `medium` e `large-v3`. Para baixar somente um modelo, informe o nome:

```bash
docker compose --profile tools run --rm model-prep medium
```

Os modelos ficam em `models/small`, `models/medium` e `models/large-v3`. O serviço de preparo usa a função oficial `faster_whisper.utils.download_model`; a aplicação recebe o caminho local, por exemplo `/models/medium`, e não usa o identificador remoto do modelo durante a inferência. Mantenha os diretórios dos modelos fora do Git.

## Iniciar a API

Com o modelo configurado e os modelos baixados:

```bash
docker compose up --build -d transcriber
```

Confira a prontidão do modelo:

```bash
curl http://127.0.0.1:8000/health
```

O endpoint deve retornar `modelLoaded: true`. A documentação interativa da API fica em <http://127.0.0.1:8000/docs>. Provisione uma conta e API Key conforme o runbook abaixo e use uma URL HTTPS assinada de leitura:

```bash
curl -X POST http://127.0.0.1:8000/v1/transcriptions \
  -H 'Content-Type: application/json' \
  -H 'X-API-Key: <api-key>' \
  -H 'Idempotency-Key: <chave-estavel-por-solicitacao>' \
  -d '{"sourceUrl":"https://storage.example.test/signed/audio.mp3?token=...", "clientReference":"asset-7891"}'
```

A resposta `202 Accepted` inclui `jobId`, estado `downloading`, `statusUrl` e
`Location`, depois que o serviço inicia o GET à origem. Repetir o mesmo JSON
semântico com a mesma `Idempotency-Key` por 120 segundos devolve o mesmo job;
reutilizar a chave com outro conteúdo retorna `409 IDEMPOTENCY_KEY_REUSED`.
Consulte o estado com a mesma API Key:

```bash
curl http://127.0.0.1:8000/v1/transcriptions/<jobId> \
  -H 'X-API-Key: <api-key>'
```

O endpoint da API segue o contrato assíncrono; a CLI local continua disponível
para transcrever arquivos em `data/input/` e gravar artefatos em `data/output/`.

Para parar a API:

```bash
docker compose down
```

## Executar a CLI sem rede

Depois que a imagem e o modelo estiverem disponíveis localmente, a CLI usa o mesmo serviço de transcrição, os mesmos volumes e as mesmas variáveis da API. O modo abaixo desabilita completamente a rede do container:

```bash
docker compose --profile tools run --rm --no-deps offline \
  python -m app.cli transcribe reuniao-arquitetura.mp4
```

O arquivo precisa existir em `data/input/reuniao-arquitetura.mp4`. Markdown e métricas JSON são gravados em `data/output/`. O serviço `offline` usa `network_mode: none`, portanto a API não fica acessível durante essa execução isolada.

Para usar a API local enquanto a máquina estiver desconectada da internet, mantenha a imagem e o modelo já preparados e inicie o serviço normalmente. O container recebe `HF_HUB_OFFLINE=1` e `TRANSFORMERS_OFFLINE=1` e carrega somente o caminho configurado no disco.

## Configuração

As variáveis do Compose podem ser definidas em `.env` ou no ambiente do comando:

| Variável | Padrão | Uso |
|---|---:|---|
| `API_PORT` | `8000` | Porta local publicada em `127.0.0.1` |
| `CPU_LIMIT` | `8` | Limite de CPUs do container e valor registrado nas métricas |
| `WHISPER_THREADS` | valor de `CPU_LIMIT` | Threads de inferência do `faster-whisper` |
| `WHISPER_MODEL_NAME` | `medium` | Nome registrado no job e nos artefatos |
| `WHISPER_MODEL_PATH` | `/models/<WHISPER_MODEL_NAME>` | Diretório local do snapshot do modelo |
| `WHISPER_DEVICE` | `cpu` | Dispositivo de inferência; a PoC exige CPU |
| `WHISPER_COMPUTE_TYPE` | `int8` | Quantização; a PoC exige INT8 |
| `JOB_CONCURRENCY` | `1` | Concorrência; a PoC exige um job por vez |
| `APP_INPUT_DIR` | `/data/input` | Diretório montado como somente leitura |
| `APP_OUTPUT_DIR` | `/data/output` | Diretório de Markdown e métricas |
| `POSTGRES_DB` | `whisper` | Banco PostgreSQL local da aplicação |
| `POSTGRES_USER` | `whisper` | Usuário PostgreSQL local |
| `POSTGRES_PASSWORD` | valor local de exemplo | Senha local PostgreSQL; substitua antes de qualquer implantação compartilhada |
| `TEST_POSTGRES_DB`, `TEST_POSTGRES_USER`, `TEST_POSTGRES_PASSWORD` | valores locais de exemplo | Credenciais do banco de integração isolado |
| `TEST_POSTGRES_PORT` | `55432` | Porta local do banco de integração isolado |

Para selecionar outro modelo, confirme que ele foi preparado e ajuste `WHISPER_MODEL_NAME`. O caminho padrão acompanha esse nome. Se definir `WHISPER_MODEL_PATH` manualmente, aponte para o diretório correspondente.

## Benchmark com a reunião real

Use o mesmo arquivo, as mesmas opções de transcrição e condições semelhantes para cada execução. Prepare também um trecho difícil de aproximadamente dez minutos e uma referência revisada para avaliar termos técnicos, nomes próprios, omissões e alucinações.

Execute cada limite de CPU reiniciando o serviço com o valor desejado. Por exemplo:

```bash
CPU_LIMIT=4 docker compose up --build -d --force-recreate transcriber
CPU_LIMIT=6 docker compose up --build -d --force-recreate transcriber
CPU_LIMIT=8 docker compose up --build -d --force-recreate transcriber
```

Se `WHISPER_THREADS` estiver explicitamente definido no `.env`, alinhe-o ao limite de CPU em cada cenário. Para comparar um modelo diferente, ajuste `WHISPER_MODEL_NAME` e recrie o serviço; o modelo é carregado uma vez na inicialização. Aguarde `modelLoaded: true` antes de enviar o job.

Registre modelo, limite e threads junto aos arquivos de saída. O JSON de métricas inclui duração da mídia, tempo de processamento, RTF, velocidade, memória, CPU, configuração e versão do `faster-whisper`. Avalie a qualidade separadamente das métricas de velocidade e memória. Para comparar resultados, não execute outros jobs em paralelo.

## Estrutura dos dados

```text
data/input/    arquivos de reunião (somente leitura no container)
data/output/   transcrições Markdown e métricas JSON
models/        snapshots locais de small, medium e large-v3
```

Mídias, modelos e saídas são ignorados pelo Git. Jobs, estado inicial e chaves
de idempotência ficam no volume PostgreSQL; URLs de origem são cifradas no
banco pela `SOURCE_URL_ENCRYPTION_KEY` para sobreviver a reinícios. Configure
uma `WEBHOOK_SECRET_ENCRYPTION_KEY` separada em `.env` para provisionar e
entregar webhooks; ela cifra o material de assinatura no banco.

## Operação do piloto

Runbook do operador para a primeira fase da API assíncrona de transcrição
(`tasks/prd-api-transcricao-assincrona/prd.md` RF-01 e RF-04,
`tasks/prd-api-transcricao-assincrona/techspec.md` V-01 e V-05, ADR-002).
Não há API administrativa, console nem provisionamento automatizado no piloto:
toda operação abaixo é manual, fora da API pública. A API administrativa fica
para a fase 2; nenhum segredo real trafega por este documento.

Permissões do piloto: todas as chaves têm as mesmas permissões iniciais —
criar jobs e ler estado/resultado somente dos jobs criados pela própria
credencial (`credential_id`). Leitura de job de outra conta, de outra chave
ou expirado retorna `404` neutro. O segredo da API Key não é o segredo
(material de assinatura) do webhook: os dois ciclos de vida são independentes.

### 1. Criar conta e API Key

1. Crie a conta e a primeira chave. O comando imprime a API Key uma vez;
   entregue somente o campo `apiKey` pelo canal seguro:

   ```bash
   docker compose exec transcriber python -m app.access.cli provision \
     --account-name "Cliente piloto"
   ```

2. Para emitir outra credencial para uma conta existente, use o `accountId`
   retornado na criação. Cada nova credencial recebe seu próprio
   `credential_id`:

   ```bash
   docker compose exec transcriber python -m app.access.cli issue \
     --account-id <account-id>
   ```

3. O serviço persiste somente o hash e metadados (conta, `credential_id`,
   permissões iniciais, criação, último uso, rotação e revogação). Nunca
   persista a chave em claro.
4. Cadastre o destino HTTPS do webhook da conta nesta mesma ocasião ou
   depois (seção 6); a chave funciona sem webhook, mas o aviso terminal só
   é entregue com destino configurado.

### 2. Entregar o segredo uma única vez

1. Apresente `apiKey` ao cliente uma única vez, por canal seguro (TLS).
2. Registre data/hora da entrega e quem recebeu. Após a entrega, o segredo
   não pode ser recuperado pelo operador: consultas futuras mostram apenas
   metadados.
3. Oriente o cliente a chamar a API com `X-API-Key: <segredo>` sobre HTTPS e
   a guardar o segredo em gerenciador próprio, fora de logs, traces e código.

### 3. Consultar metadados sem recuperar o segredo

1. Para auditoria ou suporte, consulte apenas metadados com o
   `credentialId` recebido na operação:

   ```bash
   docker compose exec transcriber python -m app.access.cli metadata \
     --credential-id <credential-id>
   ```

2. A consulta mostra conta,
   `credential_id`, data de criação, permissões iniciais, último uso e
   revogação.
3. Nunca reexiba, exporte ou registre o segredo em claro em logs, traces,
   erros, tickets ou payloads. Se o segredo foi perdido, não tente
   recuperá-lo: execute a rotação (seção 4).

### 4. Rotacionar a API Key (mesmo `credential_id`)

1. Gere a chave substituta mantendo o mesmo `credential_id`:

   ```bash
   docker compose exec transcriber python -m app.access.cli rotate \
     --credential-id <credential-id>
   ```

2. Entregue o `apiKey` exibido uma única vez (mesmo rito da seção 2) e confirme
   o recebimento antes de desativar o antigo, salvo comprometimento.
3. Invalide o segredo antigo: a chave antiga deixa de autorizar chamadas
   (`401`). A rotação da API Key não afeta o material de assinatura do
   webhook, e vice-versa.

### 5. Revogar a API Key

1. Revogue a credencial pelo `credentialId`. Chamadas
   com chave revogada recebem `401`, sem revelar dados de jobs.

   ```bash
   docker compose exec transcriber python -m app.access.cli revoke \
     --credential-id <credential-id>
   ```

2. Registre data/hora, motivo e responsável no processo operacional. A
   revogação é imediata e não
   reexpõe o segredo.

### 6. Cadastrar e rotacionar o destino HTTPS e o material do webhook

Independente da API Key, por conta:

1. Gere uma chave de cifragem diferente da `SOURCE_URL_ENCRYPTION_KEY` e
   configure `WEBHOOK_SECRET_ENCRYPTION_KEY` em `.env` antes de provisionar
   webhooks. Gere o material inicial e valide o destino pelo CLI. O comando
   imprime o segredo uma única vez; entregue o campo `webhookSecret` ao cliente
   por canal seguro:

   ```bash
   docker compose exec transcriber python -m app.access.cli webhook configure \
     --account-id <account-id> --url https://cliente.example/webhooks/transcription
   ```

   Para trocar somente o callback, preserve o segredo e configure o novo destino:

   ```bash
   docker compose exec transcriber python -m app.access.cli webhook set-endpoint \
     --account-id <account-id> --url https://novo-cliente.example/webhooks/transcription
   ```

   `configure` e `set-endpoint` aceitam somente HTTPS e rejeitam nomes que
   resolvam para loopback, redes privadas, link-local ou metadados. A entrega
   repete a validação e fixa a conexão ao endereço público aprovado.

2. Cadastre um destino HTTPS por conta (URL do callback vem da configuração
   do operador, nunca do job). Exija HTTPS e valide destino e
   redirecionamentos contra SSRF (sem loopback, redes privadas, link-local
   ou metadados), conforme o baseline. Quando o receptor mudar, valide a nova
   URL com os mesmos critérios, confirme que o novo receptor valida com o
   material de assinatura já provisionado e atualize o destino na configuração
   operacional da conta. Essa troca não depende da API Key nem exige rotacionar
   ou reentregar o material de assinatura: mantenha a chave e o material
   atuais. As entregas seguintes usam a URL atualizada. Se também for necessário
   trocar o material de assinatura, faça essa operação separadamente.
3. O CLI gera material de assinatura de alta entropia, único por destino
   (recomendação Standard Webhooks: 24 a 64 bytes, formato identificável
   `whsec_` em Base64). Guarde-o em armazenamento de segredos; somente a
   Entrega de Notificações o lê para assinar. Nunca o inclua em payload,
   log ou erro.
4. Entregue o material ao cliente uma única vez, como na seção 2. O evento
   usa Standard Webhooks v1 (`webhook-id` estável por evento,
   `webhook-timestamp` renovado por tentativa, `webhook-signature`
   `v1,<base64(HMAC-SHA256(segredo, id + "." + timestamp + "." + corpo))>`),
   tolerância de ±300 s e deduplicação pelo ID por 72 h.
5. Rotação planejada: emita o material novo mantendo o antigo válido e assine
   com ambos por 72 horas. O segredo novo é exibido uma vez:

   ```bash
   docker compose exec transcriber python -m app.access.cli webhook rotate \
     --account-id <account-id>
   ```

   Em caso de comprometimento, revogue o anterior imediatamente:

   ```bash
   docker compose exec transcriber python -m app.access.cli webhook rotate \
     --account-id <account-id> --immediate
   ```

   Para desativar o destino e revogar os materiais ativos, use `webhook disable`.
6. Códigos do destino: `2xx` encerra a entrega; `3xx` é falha sem
   redirecionamento; `410` desativa o destino; `429` reduz o ritmo
   (considerar `Retry-After`). Retentativas de falhas transitórias seguem por
   até 72 h com backoff exponencial e jitter, sem alterar o estado do job.

### 7. Auditoria mínima

Registre para cada ação (criação, entrega, consulta de metadados, rotação,
revogação, cadastro/rotação do destino e do material): data/hora, operador,
conta e `credential_id` afetados e motivo. Auditoria contém metadados e
nunca segredos, URLs assinadas (`sourceUrl`), mídia ou transcrição.

### 8. Proteção de segredo e URL

- Segredos (API Key e material do webhook) e `sourceUrl` nunca aparecem em
  logs, traces, métricas, erros públicos (RFC 9457), respostas ou payloads
  de webhook.
- `sourceUrl` é lida somente pelo downloader autorizado, não é devolvida ao
  cliente e é descartada após download bem-sucedido ou esgotamento das
  tentativas.
- Configuração local sensível fica em `.env` (ignorado pelo Git); nunca em
  `.env.example` ou no repositório.

### 9. Conta, chave e destino de teste

O serviço `postgres-test` usa uma base e o volume `postgres_test_data`,
separados do PostgreSQL local da aplicação. Para executar os testes de
autenticação, carregue as variáveis do exemplo no shell e inicie somente o
serviço de teste:

```bash
set -a
. ./.env
set +a
docker compose --profile test up -d postgres-test
rtk pytest -q -k v01_access
```

Para fumaça e integração do piloto, provisione conta, chave e destino
isolados (banco, bucket e receptor HTTPS de teste), com material de
assinatura de teste descartável. Não reutilize credenciais, buckets,
tabelas ou destinos de outro produto. Limpe objetos e versões de teste e
confirme permissões mínimas antes do piloto.

### 10. Recuperação de acesso comprometido

1. Se a API Key vazar: revogue-a imediatamente (seção 5), provisione novo
   segredo com o mesmo `credential_id` se a continuidade dos jobs exigir, e
   revise a auditoria de último uso para estimar exposição.
2. Se o material do webhook vazar: revogue-o imediatamente sem sobreposição
   de 72 h, emita material novo e oriente o cliente a rejeitar assinaturas
   do material antigo.
 3. Se ambos puderem estar comprometidos, trate os dois ciclos de forma
    independente e simultânea: a rotação de um não renova nem invalida o
    outro.
 4. Comunique o cliente pelo canal operacional, nunca incluindo segredos em
    claro além da entrega única do novo material.

## Integração com code-for-coders

Guia do primeiro consumidor da API assíncrona de transcrição
(`tasks/prd-api-transcricao-assincrona/prd.md` RF-02, RF-04, RF-05 e US-01,
`tasks/prd-api-transcricao-assincrona/techspec.md#habilitadores-inevitáveis` e
`#interfaces-entre-fatias-ou-times`, ADR-001, ADR-002).
Acordo público vigente: `tasks/prd-api-transcricao-assincrona/api-contract.yaml`
(OpenAPI 3.1.0, contrato 1.0.0) e documentação derivada em
`tasks/prd-api-transcricao-assincrona/api-contract.md`. O YAML é a fonte dos
schemas; este guia não o duplica. Abrange criação, notificação e resultado;
não cria comportamento novo no backend Whisper.

Dependência externa antes do piloto integrado: o contrato Media existente no
consumidor só oferece URL de escrita. A geração de URL GET de leitura descrita
abaixo precisa ser implementada no code-for-coders antes do piloto integrado.
A implementação do consumidor é rastreada no plano, fora do checkout Whisper.

### 1. Gerar a URL de leitura pouco antes do POST

1. O code-for-coders gera uma URL HTTPS pré-assinada de leitura (GET) para o
   seu objeto privado e a envia como `sourceUrl`. Seguir Q-03/Q-04:
   URL GET de leitura, sem compartilhar credenciais do bucket S3 do
   consumidor com o Whisper.
2. Gere a URL pouco antes de chamar `POST /v1/transcriptions` e garanta
   validade efetiva mínima de 60 minutos desde a emissão, incluindo validade
   suficiente das credenciais temporárias que a assinam. O S3 verifica a
   expiração no início de cada request: um download já iniciado pode terminar
   depois dela, mas uma retomada iniciada depois da expiração falha.
3. Envie somente a URL; nunca envie credenciais do bucket do consumidor. A URL
   não é devolvida em respostas, webhooks, logs, traces, métricas ou erros, e
   é descartada após download bem-sucedido ou esgotamento das tentativas.

### 2. Criar o job

```bash
curl -X POST https://whisper.example.test/v1/transcriptions \
  -H 'X-API-Key: <segredo>' \
  -H 'Idempotency-Key: 01J9M2YB8K4W6N7P3Q5R1S0ABC' \
  -H 'Content-Type: application/json' \
  -d '{"sourceUrl":"https://media.example.test/signed/arquivo.mp4?sig=exemplo","clientReference":"asset-7891"}'
```

1. Envie `sourceUrl` com `X-API-Key` (sobre HTTPS) e `Idempotency-Key`
   estável. `clientReference` é opcional e opaco: o Whisper o devolve sem
   interpretar.
2. Guarde o ID opaco (`jobId`), o `statusUrl` (`/v1/transcriptions/{jobId}`) e
   o cabeçalho `Location`. A resposta é `202 Accepted` depois que a ingestão
   assume o job e inicia a conexão de download; não aguarda a transcrição.
3. Idempotência: escopo por conta + `credential_id` + `Idempotency-Key`,
   retida por 120 segundos. Mesmo JSON semântico (ordem/whitespace podem
   variar; valores, incluindo `sourceUrl`, iguais) retorna o mesmo job.
   Reuso com corpo diferente retorna `409 IDEMPOTENCY_KEY_REUSED`.
4. Limites na criação: até 5 GiB e formatos `.mp4`, `.mkv`, `.webm`, `.mp3`,
   `.wav`, `.m4a` com validação real; mídia acima de 2 horas termina em
   `failed/MEDIA_DURATION_LIMIT_EXCEEDED`. Tamanho conhecido e inspecionável
   no início da conexão (por exemplo, `Content-Length`) acima de 5 GiB recebe
   `413 MEDIA_SIZE_LIMIT_EXCEEDED` sem criar job; tamanho desconhecido
   (por exemplo, chunked) segue para streaming com o limite aplicado depois.

### 3. Acompanhar o estado

```bash
curl https://whisper.example.test/v1/transcriptions/<jobId> \
  -H 'X-API-Key: <segredo>'
```

1. Consulte o `statusUrl` com a mesma API Key (mesma conta e `credential_id`).
   Estados: `downloading → queued → processing → completed|failed`, com
   horários disponíveis e resumo seguro de falha (`failure.code`/`message`).
2. Job de outra conta, de outra chave ou expirado retorna `404 NOT_FOUND`
   neutro. O estado terminal permanece consultável por até 24 horas após
   `terminalAt`.

### 4. Validar e deduplicar o webhook

1. O Whisper envia o evento `transcriptionTerminal` ao destino HTTPS
   cadastrado pelo operador para a conta (nunca via job), ao menos uma vez,
   com `eventId`, `jobId`, estado terminal e `clientReference` quando enviada.
   O corpo não contém mídia nem transcrição.
2. Verifique a assinatura Standard Webhooks v1 sobre os bytes exatos do corpo
   (`webhook-id + "." + webhook-timestamp + "." + raw_body`, HMAC-SHA256 em
   Base64, `webhook-signature: v1,<assinatura>`), em tempo constante, com
   tolerância de ±300 s no timestamp. Em rotação planejada pode haver duas
   assinaturas `v1` separadas por espaço por 72 h.
3. Deduplique pelo `webhook-id`/`eventId` estável (igual em todas as
   tentativas) durante a janela de retry de 72 h. Responda `2xx` para
   confirmar; `3xx` é falha sem redirecionamento; `410` desativa o destino;
   `429` reduz o ritmo (considerar `Retry-After`). Falhas transitórias são
   repetidas por até 72 h sem alterar o estado do job.

### 5. Buscar o resultado dentro de 24 horas

```bash
curl https://whisper.example.test/v1/transcriptions/<jobId>/result \
  -H 'X-API-Key: <segredo>'
```

1. Busque `GET /v1/transcriptions/{jobId}/result` com a conta e a credencial
   proprietárias, dentro de 24 horas após `terminalAt`. Somente job
   `completed` retorna `200` com JSON `schemaVersion: 1`, `pt-BR`, duração e
   segmentos em milissegundos relativos ao início.
2. Job não concluído ou `failed` retorna `409 RESULT_NOT_AVAILABLE` (sem
   parcial); expirado, de outra conta ou de outra chave retorna `404`.
   Persista o resultado no seu produto: após o expurgo ele não volta.

### 6. Contrato de falha de origem

1. O Whisper faz até 3 tentativas totais (inicial + 2 retentativas) somente
   para falhas transitórias de rede, timeout e HTTP 408/429/5xx, com timeout
   de conexão de 10 s, timeout de inatividade de leitura de 60 s (não limitam
   um download que continua progredindo), full jitter de 0–5 s e 0–30 s nas
   retentativas e respeito a `Retry-After` quando a nova tentativa ainda pode
   começar antes do vencimento da URL.
2. Não repete falhas permanentes (URL inválida/expirada, 4xx exceto 408/429)
   nem inicia tentativa após a expiração; não renova nem solicita nova URL
   automaticamente.
3. Esgotadas as tentativas, o job termina em `failed/SOURCE_UNAVAILABLE`
   (ou `failed/MEDIA_SIZE_LIMIT_EXCEEDED` se o streaming exceder 5 GiB).
   Para reprocessar após falha final, o consumidor gera outra URL de leitura
   e envia uma nova solicitação com nova `Idempotency-Key`.
