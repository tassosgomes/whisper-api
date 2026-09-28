---
status: pending
task_kind: vertical
blocked_by: [1.0, 3.0, 4.0]
gate: "rtk pytest -q -k v02_creation"
gate_expect: "7 testes de criação, idempotência, origem e recuperação passam"
---

# 5.0 Aceitar jobs idempotentes após iniciar aquisição

**Fatia:** V-02 · **Cobre:** RF-02, RF-03, US-01 · **Spec:** `techspec.md#v-02` · **ADR:** ADR-001

## Comportamento

`POST /v1/transcriptions` autenticado recebe `sourceUrl` HTTPS de leitura, `Idempotency-Key` e referência opaca opcional. Registra job e vínculo de idempotência duráveis por conta, `credential_id` e chave, inicia a conexão de download por capacidade própria e só então responde `202` com ID opaco, estado `downloading`, horários e `Location`/`statusUrl` relativos que apontam para a consulta pública. Não espera a inferência. Se a origem não puder ser assumida, não devolve um 202 falso; preserva estado recuperável ou erro do contrato. URL e credenciais não aparecem em resposta, log ou métrica.

Dentro de 120 segundos, repetição com JSON semanticamente igual retorna o mesmo job, inclusive com ordem/espaçamento distintos, sem iniciar outra transcrição. Mesmo valor de chave com conteúdo diferente retorna `409 IDEMPOTENCY_KEY_REUSED`, sem substituir o job. Depois da janela, a chave pode iniciar outro job. Repetições concorrentes são atômicas. Reinício entre aceite e worker preserva o job e a possibilidade de retomada. A URL que viola contrato, como esquema não HTTPS, retorna `422 INVALID_SOURCE_URL` seguro.

Esta fatia também estabelece banco/migrations e dados descartáveis reproduzíveis para as credenciais e jobs do teste, além de origem HTTPS controlada acessível pelo papel de downloader. O teste exercita o adaptador HTTP real de aquisição contra essa origem e inicia a aplicação com os registros reais, usando dublês apenas para serviço externo. O ambiente não reutiliza tabelas, filas ou chaves do piloto.

## Fora do escopo desta task

Download completo, validação de mídia, inferência, resultado e webhook entram em V-03 a V-05. A geração da URL GET pelo code-for-coders é dependência externa.

## Decisões fechadas

Janela idempotente de 120 segundos, equivalência por JSON semântico e 202 somente após início da conexão, conforme `techspec.md#v-02`. A validade efetiva mínima da URL é 60 minutos. Estado durável e isolamento do downloader seguem o baseline; bucket S3 segue ADR-001.

## Modificar / Referenciar

- **modificar:** `app/api/transcriptions.py` (criação e resposta pública)
- **modificar:** `app/jobs/executor.py` (job e aceite recuperáveis)
- **modificar:** `app/main.py` (composição de downloader e armazenamento)
- **modificar:** `app/transcription/paths.py` (fronteira inicial de URL remota)
- **modificar:** `app/monitoring/metrics.py` (tempo até início de download e fila)
- **modificar:** `docker-compose.yml` (papéis e banco isoláveis)
- **ref:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (createTranscription)
- **ref:** `context/architecture-baseline.md` (estado, isolamento e recuperação)
- **ref:** `docs/adr/adr-001.md` (armazenamento temporário)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API e jobs Python | `rtk pytest -q -k v02_creation` | exit 0; 7 testes passam; pytest falha se nenhum for coletado | `pyproject.toml`, dev dependency pytest; CI ausente |
| API e jobs Python | `rtk ruff check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| API e jobs Python | `rtk ruff format --check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| Compose | `rtk docker compose config -q` | exit 0 | `docker-compose.yml`; CI ausente |
| Imagem | `rtk docker compose build transcriber` | exit 0 | README e Dockerfile; CI ausente |

## Pronto quando

- [ ] 202 chega depois do início de conexão e `Location`/`statusUrl` alcançam a rota de consulta pública.
- [ ] Repetição equivalente, concorrente e após perda de resposta preserva o mesmo job; payload conflitante dá 409 e a janela encerra após 120 segundos.
- [ ] Reinício não perde job aceito; URL inválida recebe erro seguro; URL assinada não vaza.
- [ ] O teste controla a origem externa, exercita o adaptador real e inicia a aplicação composta; gate focalizado passa com exit 0.
