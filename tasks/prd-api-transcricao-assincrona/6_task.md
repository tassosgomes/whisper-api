---
status: done
task_kind: vertical
blocked_by: [5.0]
gate: "rtk pytest -q -k v03_processing"
gate_expect: "10 testes de aquisição, processamento, limites e retomada passam"
---

# 6.0 Baixar, validar e transcrever mídia com estado recuperável

**Fatia:** V-03 · **Cobre:** RF-02, RF-03, RF-06, US-01, US-02 · **Spec:** `techspec.md#v-03` · **ADR:** ADR-001

## Comportamento

O downloader do job em `downloading` valida URL HTTPS, DNS e cada redirecionamento, rejeitando loopback, redes privadas, link-local e metadados. Com no máximo dois downloads simultâneos, obtém a mídia no bucket privado S3 sem esperar o worker serial de inferência. Aplica conexão de 10 segundos, inatividade de leitura de 60 segundos, limite de 5 GiB e até três tentativas totais para falhas transitórias, com as esperas e `Retry-After` definidos em Q-04. Não repete falha permanente nem inicia tentativa após expiração; não renova URL automaticamente. Depois do download, apaga a referência da URL assim que as tentativas deixam de ser necessárias, avança `queued`, e o worker publica `processing` antes de concluir `completed` ou `failed`.

A validação real de formato/codec aceita apenas os formatos aprovados; duração acima de duas horas, tamanho acima de 5 GiB, formato incompatível e origem indisponível produzem códigos terminais seguros do contrato sem iniciar inferência indevida. Falha do motor também termina com resumo seguro. Leases e transições duráveis permitem recuperação após encerramento abrupto sem transcrição duplicada. `GET /v1/transcriptions/{jobId}` mostra estados e horários disponíveis, sem URL, chave ou conteúdo. Métricas separam início de download, fila, RTF, falhas e uso de recursos, sem atributos sensíveis.

O ambiente de teste contém bucket S3 privado isolado com permissões mínimas, migrations e dados reproduzíveis, origem HTTPS controlada e mídia descartável. O gate exercita downloader e adaptador S3 reais com fronteiras externas controladas; a aplicação inicia com composição real de API e workers. Evidência de carga mede o alvo inicial p95 de 30 segundos até conexão e p95 de 24 horas até terminal sob até 10 jobs/dia; a avaliação de capacidade usa mídia representativa antes do piloto.

## Fora do escopo desta task

Consulta do JSON público e expurgo periódico completo entram em V-04. Webhook terminal entra em V-05.

## Decisões fechadas

Aplicar `techspec.md#v-03`, Q-02/Q-04, baseline e ADR-001. O bucket não pode reter versão ou cópia imutável incompatível com expurgo em 24 horas.

## Modificar / Referenciar

- **modificar:** `app/jobs/executor.py` (download, inferência, leases e estados)
- **modificar:** `app/main.py` (papéis e ciclo de vida)
- **modificar:** `app/service.py` (inferência e resultado temporário)
- **modificar:** `app/transcription/paths.py` (validação de URL e mídia)
- **modificar:** `app/monitoring/metrics.py` (tempos e falhas sem dados sensíveis)
- **modificar:** `docker-compose.yml` (papéis e bucket isoláveis)
- **modificar:** `.env.example` (nomes/placeholders do bucket)
- **ref:** `app/transcription/whisper.py`, `app/transcription/models.py` (adaptador do motor)
- **ref:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (estados e falhas)
- **ref:** `context/architecture-baseline.md`, `docs/adr/adr-001.md` (isolamento e bucket)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Jobs e serviço Python | `rtk pytest -q -k v03_processing` | exit 0; 10 testes passam; pytest falha se nenhum for coletado | `pyproject.toml`, dev dependency pytest; CI ausente |
| Jobs e serviço Python | `rtk ruff check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| Jobs e serviço Python | `rtk ruff format --check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| Compose | `rtk docker compose config -q` | exit 0 | `docker-compose.yml`; CI ausente |
| Imagem | `rtk docker compose build transcriber` | exit 0 | README e Dockerfile; CI ausente |

## Pronto quando

- [ ] Consulta observa `downloading → queued → processing → completed|failed`, com horários e falhas seguras.
- [ ] Origem transitória é repetida no máximo duas vezes; permanente, URL expirada e SSRF falham sem inferência nem vazamento.
- [ ] Tamanho, duração e formato fora do limite geram códigos definidos; encerramento abrupto recupera trabalho sem duplicação.
- [ ] Inferência ocupada não impede início oportuno do download; métricas e benchmark registram a capacidade para o piloto.
- [ ] Teste com origem e S3 controlados exercita adaptadores reais; gate focalizado passa com exit 0.
