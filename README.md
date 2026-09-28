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
