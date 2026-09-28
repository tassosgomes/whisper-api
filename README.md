# Transcrição local de reuniões

PoC para transcrever arquivos de áudio e vídeo em português com `faster-whisper`, usando CPU e INT8. A API e a CLI carregam o modelo escolhido localmente; a aplicação não precisa acessar serviços de transcrição externos.

## Pré-requisitos

- Docker Engine com Docker Compose v2;
- conexão com a internet durante o primeiro build da imagem e o preparo dos modelos;
- espaço em disco para a imagem e os modelos que serão comparados.

Copie o arquivo de configuração e confira os diretórios locais:

```bash
cp .env.example .env
```

O Compose publica a API somente em `127.0.0.1:8000`. Por padrão, usa o modelo `medium`, limita o container a oito CPUs e define o mesmo número de threads de inferência. Os arquivos de entrada ficam somente leitura no container; os resultados são gravados em `data/output`.

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

Com o modelo configurado em `.env` já preparado:

```bash
docker compose up --build -d transcriber
```

Confira a prontidão do modelo:

```bash
curl http://127.0.0.1:8000/health
```

O endpoint deve retornar `modelLoaded: true`. A documentação interativa da API fica em <http://127.0.0.1:8000/docs>.

Coloque a mídia em `data/input/` e crie um job usando um caminho relativo. As extensões aceitas são `.mp4`, `.mkv`, `.webm`, `.mp3`, `.wav` e `.m4a`:

```bash
curl -X POST http://127.0.0.1:8000/v1/transcriptions \
  -H 'Content-Type: application/json' \
  -d '{"path":"reuniao-arquitetura.mp4"}'
```

A resposta `202 Accepted` inclui o identificador do job. O campo `model` é opcional no POST; se for informado, precisa corresponder ao modelo que já está carregado no container. Para comparar outro modelo, configure `WHISPER_MODEL_NAME` e `WHISPER_MODEL_PATH` e recrie o serviço. Consulte o estado substituindo `<id>` pelo valor retornado:

```bash
curl http://127.0.0.1:8000/v1/transcriptions/<id>
```

Ao concluir, o JSON do job informa os nomes dos arquivos em `data/output/`. Cada execução recebe um UUID, formando nomes como `reuniao.<id>.md` e `reuniao.<id>.metrics.json`, para preservar os resultados de benchmarks repetidos. Caminhos absolutos e caminhos que saem de `data/input` são rejeitados.

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

Mídias, modelos e saídas são ignorados pelo Git. A PoC mantém os jobs em memória; reiniciar o container apaga o estado dos jobs, mas preserva os arquivos nos volumes locais.

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

1. Crie a conta do cliente no armazenamento operacional de credenciais
   (conta genérica, sem conceito de curso/aula).
2. Gere um `credential_id` estável por chave e um segredo de alta entropia.
   Persista somente verificador/hash e metadados (conta, `credential_id`,
   data de criação, último uso, situação de revogação). Nunca persista o
   segredo em claro.
3. Cadastre o destino HTTPS do webhook da conta nesta mesma ocasião ou
   depois (seção 6); a chave funciona sem webhook, mas o aviso terminal só
   é entregue com destino configurado.

### 2. Entregar o segredo uma única vez

1. Apresente o segredo ao cliente uma única vez, por canal seguro (TLS).
2. Registre data/hora da entrega e quem recebeu. Após a entrega, o segredo
   não pode ser recuperado pelo operador: consultas futuras mostram apenas
   metadados.
3. Oriente o cliente a chamar a API com `X-API-Key: <segredo>` sobre HTTPS e
   a guardar o segredo em gerenciador próprio, fora de logs, traces e código.

### 3. Consultar metadados sem recuperar o segredo

1. Para auditoria ou suporte, consulte apenas metadados: conta,
   `credential_id`, data de criação, permissões iniciais, último uso e
   revogação.
2. Nunca reexiba, exporte ou registre o segredo em claro em logs, traces,
   erros, tickets ou payloads. Se o segredo foi perdido, não tente
   recuperá-lo: execute a rotação (seção 4).

### 4. Rotacionar a API Key (mesmo `credential_id`)

1. Gere um novo segredo mantendo o mesmo `credential_id`. O novo segredo
   conserva o acesso aos jobs existentes daquela credencial.
2. Entregue o novo segredo uma única vez (mesmo rito da seção 2) e confirme
   o recebimento antes de desativar o antigo, salvo comprometimento.
3. Invalide o segredo antigo: a chave antiga deixa de autorizar chamadas
   (`401`). A rotação da API Key não afeta o material de assinatura do
   webhook, e vice-versa.

### 5. Revogar a API Key

1. Marque a credencial como revogada no armazenamento operacional. Chamadas
   com chave revogada recebem `401`, sem revelar dados de jobs.
2. Registre data/hora, motivo e responsável. A revogação é imediata e não
   reexpõe o segredo.

### 6. Cadastrar e rotacionar o destino HTTPS e o material do webhook

Independente da API Key, por conta:

1. Cadastre um destino HTTPS por conta (URL do callback vem da configuração
   do operador, nunca do job). Exija HTTPS e valide destino e
   redirecionamentos contra SSRF (sem loopback, redes privadas, link-local
   ou metadados), conforme o baseline. Quando o receptor mudar, valide a nova
   URL com os mesmos critérios, confirme que o novo receptor valida com o
   material de assinatura já provisionado e atualize o destino na configuração
   operacional da conta. Essa troca não depende da API Key nem exige rotacionar
   ou reentregar o material de assinatura: mantenha a chave e o material
   atuais. As entregas seguintes usam a URL atualizada. Se também for necessário
   trocar o material de assinatura, faça essa operação separadamente.
2. Gere material de assinatura de alta entropia, único por destino
   (recomendação Standard Webhooks: 24 a 64 bytes, formato identificável
   `whsec_` em Base64). Guarde-o em armazenamento de segredos; somente a
   Entrega de Notificações o lê para assinar. Nunca o inclua em payload,
   log ou erro.
3. Entregue o material ao cliente uma única vez, como na seção 2. O evento
   usa Standard Webhooks v1 (`webhook-id` estável por evento,
   `webhook-timestamp` renovado por tentativa, `webhook-signature`
   `v1,<base64(HMAC-SHA256(segredo, id + "." + timestamp + "." + corpo))>`),
   tolerância de ±300 s e deduplicação pelo ID por 72 h.
4. Rotação planejada: cadastre o novo material mantendo o antigo válido e
   assine com os dois segredos por 72 horas (janela máxima de retry); depois
   remova o antigo. Comprometimento: revogue o material antigo imediatamente,
   sem sobreposição, e emita material novo.
5. Códigos do destino: `2xx` encerra a entrega; `3xx` é falha sem
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
