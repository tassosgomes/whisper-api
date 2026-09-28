# API Contract — API assíncrona de transcrição

> **Derivado de:** [api-contract.yaml](api-contract.yaml), OpenAPI 3.1.0, contrato 1.0.0  
> **PRD:** [prd.md](prd.md), v1.0, aprovado em 2026-09-28  
> **TechSpec:** [techspec.md](techspec.md), aprovada em 2026-09-28 (Q-01 a Q-11)
> **Estado:** Aprovado

Este documento apresenta decisões e exemplos derivados do OpenAPI. O YAML é a fonte dos
schemas e restrições. O acordo cobre o recorte HTTP deste PRD, inclusive o webhook de saída.

## Premissas e decisões

| Decisão | Escolha aprovada | Origem |
|---|---|---|
| Autenticação cliente-servidor | API Key vinculada a conta no cabeçalho **X-API-Key** | TechSpec Q-07; PRD RF-01 |
| Permissões e isolamento | Permissões iniciais iguais para todas as chaves; leitura restrita aos jobs criados pela própria credencial; outra conta ou chave responde 404 neutro; 403 reservado para permissões futuras | TechSpec Q-10; PRD RF-01 |
| Rotação de credencial | Novo segredo mantém o `credential_id` e o acesso aos jobs existentes; chave antiga deixa de autorizar | TechSpec Q-11 |
| Configuração do webhook | Um destino por conta, provisionado pelo operador; não é informado por job | PRD RF-04; TechSpec Q-08 |
| Assinatura do webhook | Standard Webhooks v1, independente da API Key: `webhook-id`, `webhook-timestamp`, `webhook-signature` = `v1,<base64(HMAC-SHA256(secret, id + "." + timestamp + "." + raw_body))>`; assinaturas múltiplas durante rotação são separadas por espaço; tolerância de ±300 s; deduplicação por ID durante 72 h | ADR-002; TechSpec Q-05 |
| Retries do webhook | Falhas transitórias repetidas por até 72 h com backoff exponencial e jitter; 2xx confirma, 3xx falha sem redirecionamento, 410 desativa, 429 reduz o ritmo; rotação planejada com dupla assinatura por 72 h | ADR-002; TechSpec Q-05 |
| Idempotência | `Idempotency-Key` escopada por conta + `credential_id` + chave, retida por 120 s; mesmo JSON semântico retorna o mesmo job; corpo diferente resulta em 409 `IDEMPOTENCY_KEY_REUSED`; após a janela, nova chave de idempotência inicia novo job | TechSpec Q-06; PRD RF-02 |
| Limite e formatos | Máximo de 5 GiB por arquivo; formatos `.mp4`, `.mkv`, `.webm`, `.mp3`, `.wav`, `.m4a` com validação real de formato/codec; antes do aceite, tamanho conhecido acima do limite (por exemplo, `Content-Length`) retorna 413 `MEDIA_SIZE_LIMIT_EXCEEDED` sem criar job | TechSpec Q-02 |
| Aquisição da mídia | URL de leitura GET pré-assinada gerada pelo consumidor com validade efetiva mínima de 60 min; timeout de conexão de 10 s e inatividade de leitura de 60 s; quando o tamanho é desconhecido (por exemplo, chunked), a criação prossegue sem rejeição por tamanho e o limite é aplicado no streaming; se o streaming exceder 5 GiB, o job termina em `failed/MEDIA_SIZE_LIMIT_EXCEEDED`; até 3 tentativas totais para falhas transitórias (rede, timeout, HTTP 408/429/5xx) com full jitter de 0–5 s e 0–30 s; sem renovação automática; esgotadas as tentativas, `failed/SOURCE_UNAVAILABLE` | TechSpec Q-03/Q-04 |
| Estados | downloading, queued, processing, completed, failed | PRD RF-03 |
| Idioma e duração | pt-BR, até 2 horas por mídia | PRD; TechSpec Q-02 |
| Retenção terminal | Metadados e resultado disponíveis por até 24 horas após terminalAt; mídia removida assim que desnecessária | PRD RF-05/RF-06; ADR-001 |
| Armazenamento temporário | Bucket privado S3 sob controle do Whisper, separado dos metadados | ADR-001; TechSpec Q-01 |
| SLOs iniciais | p95 de até 30 s da requisição à primeira conexão com a origem; p95 de até 24 h de `acceptedAt` até estado terminal; até 10 jobs/dia; a reavaliar com métricas de produção assistida | TechSpec Q-02 |
| Versionamento e JSON | Prefixo efetivo /v1; request/response JSON e erros RFC 9457 | Convenção HTTP local; prefixo alinhado à rota da PoC |

## Resumo das operações

| Método/caminho | Identificador | Comportamento | Requisitos |
|---|---|---|---|
| POST /v1/transcriptions | createTranscription | Aceita URL assinada, inicia o download e responde 202 com ID e referência de consulta | RF-01, RF-02, RF-03 |
| GET /v1/transcriptions/{jobId} | getTranscription | Retorna estado e horários; job alheio, de outra chave ou expirado retorna 404 neutro | RF-01, RF-03, RF-06 |
| GET /v1/transcriptions/{jobId}/result | getTranscriptionResult | Retorna JSON versionado somente para job concluído dentro da retenção | RF-01, RF-05, RF-06 |
| Webhook HTTP transcriptionTerminal | receiveTranscriptionTerminalWebhook | Envia evento pequeno de estado terminal ao destino cadastrado; ao menos uma vez, assinado no padrão Standard Webhooks v1 | RF-04 |

O operador provisiona/revoga API Keys e cadastra/rotaciona o destino e material do webhook fora
desta API. Esses fluxos de administração não fazem parte do recorte; a API administrativa fica
para a fase 2 (Q-08).

## Exemplos derivados

### Criar job

    POST /v1/transcriptions HTTP/1.1
    X-API-Key: <api-key>
    Idempotency-Key: 01J9M2YB8K4W6N7P3Q5R1S0ABC
    Content-Type: application/json

    {
      "sourceUrl": "https://media.example.test/signed/arquivo.mp4?sig=not-a-real-signature",
      "clientReference": "asset-7891"
    }

    HTTP/1.1 202 Accepted
    Location: /v1/transcriptions/trn_01J9M2YB8K4W6N7P3Q5R1S0ABC
    Content-Type: application/json

    {
      "jobId": "trn_01J9M2YB8K4W6N7P3Q5R1S0ABC",
      "status": "downloading",
      "createdAt": "2026-09-28T14:30:00Z",
      "statusUrl": "/v1/transcriptions/trn_01J9M2YB8K4W6N7P3Q5R1S0ABC",
      "clientReference": "asset-7891"
    }

Antes desse `202`, o serviço conecta à origem e inspeciona o tamanho disponível. Se informação
conhecida nesse início (por exemplo, `Content-Length`) exceder 5 GiB, responde 413
`MEDIA_SIZE_LIMIT_EXCEEDED` sem criar job. Se o tamanho for desconhecido (por exemplo, chunked),
a criação prossegue sem rejeição por tamanho e o limite é aplicado durante o streaming. Se o
streaming exceder 5 GiB, o job aceito termina em `failed/MEDIA_SIZE_LIMIT_EXCEEDED`.

### Resultado

    {
      "schemaVersion": 1,
      "jobId": "trn_01J9M2YB8K4W6N7P3Q5R1S0ABC",
      "language": "pt-BR",
      "durationMs": 3600000,
      "segments": [
        { "startMs": 1240, "endMs": 4100, "text": "Exemplo de transcrição." }
      ]
    }

Os tempos são relativos ao início da mídia. O webhook inclui eventId, jobId, estado terminal
e clientReference quando fornecida; não inclui mídia nem transcrição. O consumidor valida a
assinatura Standard Webhooks sobre o corpo exato, deduplica por eventId e busca o resultado
pela API.

### Webhook terminal

    POST <destino-cadastrado-pelo-operador> HTTP/1.1
    webhook-id: evt_01J9M2YB8K4W6N7P3Q5R1S0ABC
    webhook-timestamp: 1759074600
    webhook-signature: v1,<base64(HMAC-SHA256(secret, id + "." + timestamp + "." + corpo))>
    Content-Type: application/json

    {
      "eventId": "evt_01J9M2YB8K4W6N7P3Q5R1S0ABC",
      "jobId": "trn_01J9M2YB8K4W6N7P3Q5R1S0ABC",
      "status": "completed",
      "clientReference": "asset-7891"
    }

## Compatibilidade e dependências

Não foi localizado contrato OpenAPI anterior do Whisper nem evidência de produção; compatibilidade
com produção não foi verificada. A PoC em app/api/transcriptions.py expõe POST /v1/transcriptions
com path e model opcionais e consulta o job em memória. Este acordo mantém o prefixo efetivo,
mas troca o corpo para URL remota, exige credencial e idempotência e acrescenta consulta/resultados
com retenção e webhook. Portanto, é incompatível com a interface da PoC.

O acordo (Q-03/Q-04) define que o code-for-coders gera e envia a URL de leitura; a implementação
dessa geração no consumidor (EN-01) continua como dependência externa para o piloto integrado,
sem reabrir o contrato.

## Histórico

Aprovação do acordo em 2026-09-28, com Q-01 a Q-11 fechadas na TechSpec. A aprovação registra
acordo para implementar; não comprova implantação nem atualização de catálogo.

## Uso na implementação

Use o OpenAPI como fonte para tipos e mocks. A implementação deve cumprir autorização por conta
e credencial criadora, idempotência de 120 s, transições, falhas de download, expiração,
resultado v1 e entrega repetida com assinatura verificável sobre o corpo exato.
