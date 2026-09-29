---
status: done
task_kind: vertical
blocked_by: [2.0, 6.0, 7.0]
gate: "rtk pytest -q -k v05_webhook"
gate_expect: "9 testes de assinatura, retries, isolamento e jornada passam"
---

# 8.0 Entregar webhook terminal assinado e comprovar a jornada

**Fatia:** V-05 · **Cobre:** RF-04, US-02 · **Spec:** `techspec.md#v-05` · **ADR:** ADR-002

## Comportamento

Ao passar para `completed` ou `failed`, o serviço persiste evento terminal durável com ID estável e estado de entrega próprio. Para conta com destino HTTPS provisionado, envia payload mínimo com `eventId`, `jobId`, estado e `clientReference` opcional, sem mídia ou transcrição. Standard Webhooks v1 assina o corpo JSON exato com `webhook-id`, `webhook-timestamp` renovado por tentativa e `webhook-signature`; receptor consegue verificar origem, tolerância temporal de ±300 segundos e deduplicar pelo ID por 72 horas. Destino é sempre o da conta, nunca um valor do POST. O cliente usa o `jobId`/referência de consulta para buscar resultado pelo endpoint público quando o estado for `completed`.

Resposta 2xx encerra entrega. Falhas transitórias são repetidas com backoff exponencial e jitter por até 72 horas, preservando evento/ID; 3xx não segue redirecionamento, 410 desativa o destino, 429 reduz ritmo e `Retry-After` é respeitado quando aplicável. Falha definitiva deixa estado e métrica de entrega visíveis para operação sem mudar `completed` ou `failed`. Rotação planejada do segredo de webhook assina com material antigo e novo durante 72 horas; comprometimento revoga imediatamente o antigo. API Key rotacionada não altera o material do webhook e vice-versa. URL de destino passa pelas mesmas defesas de saída relevantes para impedir acesso interno.

O teste usa receptor HTTPS isolado que valida headers e corpo reais, conta/destino/segredos de teste, banco e bucket privados isolados, migrations e origem HTTPS controlada. O adaptador HTTP real de entrega é exercitado; a aplicação inicia com registros reais. O smoke percorre POST → estados → webhook terminal → GET do resultado pelo link público retornado e verifica que o resultado alcançado pertence ao job do evento. Para job failed, o mesmo caminho confirma indisponibilidade segura do resultado. Isso prova a jornada Whisper; o piloto com code-for-coders depende da URL GET implementada naquele repositório.

## Fora do escopo desta task

Console, painel de notificações, API administrativa e implementação do consumidor externo.

## Decisões fechadas

Aplicar `techspec.md#v-05`, ADR-002 e o contrato atualizado em 1.0. Estado do job e estado de entrega são independentes.

## Modificar / Referenciar

- **modificar:** `app/jobs/executor.py` (evento terminal e tentativas duráveis)
- **modificar:** `app/main.py` (composição e ciclo de vida do emissor)
- **modificar:** `app/monitoring/metrics.py` (tentativas e atraso sem conteúdo sensível)
- **modificar:** `docker-compose.yml` (emissor e receptor isolados para smoke)
- **ref:** `app/api/transcriptions.py` (referência pública de consulta e resultado)
- **ref:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (payload e headers)
- **ref:** `README.md` (procedimento de 2.0 e guia de 3.0)
- **ref:** `docs/adr/adr-002.md` (padrão de assinatura)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Jobs e notificação Python | `rtk pytest -q -k v05_webhook` | exit 0; 9 testes passam; pytest falha se nenhum for coletado | `pyproject.toml`, dev dependency pytest; CI ausente |
| Jobs e notificação Python | `rtk ruff check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| Jobs e notificação Python | `rtk ruff format --check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| Compose | `rtk docker compose config -q` | exit 0 | `docker-compose.yml`; CI ausente |
| Imagem | `rtk docker compose build transcriber` | exit 0 | README e Dockerfile; CI ausente |

## Pronto quando

- [ ] Receptor verifica assinatura e deduplica pelo ID estável; payload não contém mídia ou transcrição.
- [ ] Retries e falha definitiva preservam o estado terminal; rotação de uma credencial não afeta a outra.
- [ ] Destino só vem da conta e rejeita redirecionamento ou acesso a rede interna.
- [ ] Smoke acompanha o aviso até resultado ou indisponibilidade segura pelo link público; gate focalizado passa com exit 0.
