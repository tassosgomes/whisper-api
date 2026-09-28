# API Contract — API assíncrona de transcrição

> **Derivado de:** [api-contract.yaml](api-contract.yaml), OpenAPI 3.1.0, contrato 1.0.0  
> **PRD:** [prd.md](prd.md), v1.0, aprovado em 2026-09-28  
> **Estado:** Em Revisão

Este documento apresenta decisões e exemplos derivados do OpenAPI. O YAML é a fonte dos
schemas e restrições. O acordo cobre o recorte HTTP deste PRD, inclusive o webhook de saída.

## Premissas e decisões

| Decisão | Escolha registrada | Estado/origem |
|---|---|---|
| Autenticação cliente-servidor | API Key vinculada a conta; o cabeçalho proposto é **X-API-Key** | Cabeçalho ainda precisa de confirmação; PRD RF-01 |
| Configuração do webhook | Um destino por conta, provisionado pelo operador; não é informado por job | Herdado do PRD RF-04 |
| Assinatura do webhook | Independente da API Key | Herdado do PRD; cabeçalho, algoritmo e conteúdo assinado pendentes |
| Idempotência | Idempotency-Key; mesmo corpo retorna o mesmo job e corpo diferente resulta em 409 | Herdado do PRD RF-02; retenção e equivalência do corpo pendentes |
| Estados | downloading, queued, processing, completed, failed | Herdado do PRD RF-03 |
| Idioma e duração | pt-BR, até 2 horas por mídia | Herdado do PRD; sem suporte a outros idiomas neste corte |
| Retenção terminal | Metadados e resultado disponíveis por até 24 horas após terminalAt | Herdado do PRD RF-05/RF-06 |
| Versionamento e JSON | Prefixo efetivo /v1; request/response JSON e erros RFC 9457 | Convenção HTTP local; prefixo alinhado à rota da PoC |

## Resumo das operações

| Método/caminho | Identificador | Comportamento | Requisitos |
|---|---|---|---|
| POST /v1/transcriptions | createTranscription | Aceita URL assinada, inicia o download e responde 202 com ID e referência de consulta | RF-01, RF-02, RF-03 |
| GET /v1/transcriptions/{jobId} | getTranscription | Retorna estado e horários; job alheio ou expirado retorna 404 neutro | RF-01, RF-03, RF-06 |
| GET /v1/transcriptions/{jobId}/result | getTranscriptionResult | Retorna JSON versionado somente para job concluído dentro da retenção | RF-01, RF-05, RF-06 |
| Webhook HTTP transcriptionTerminal | receiveTranscriptionTerminalWebhook | Envia evento pequeno de estado terminal ao destino cadastrado; ao menos uma vez | RF-04 |

O operador provisiona/revoga API Keys e cadastra/rotaciona o destino e material do webhook fora
desta API. Esses fluxos de administração não fazem parte do recorte.

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
e clientReference quando fornecida; não inclui mídia nem transcrição. O consumidor deduplica
por eventId e busca o resultado pela API.

## Compatibilidade e dependências

Não foi localizado contrato OpenAPI anterior do Whisper nem evidência de produção; compatibilidade
com produção não foi verificada. A PoC em app/api/transcriptions.py expõe POST /v1/transcriptions
com path e model opcionais e consulta o job em memória. Este acordo mantém o prefixo efetivo,
mas troca o corpo para URL remota, exige credencial e idempotência e acrescenta consulta/resultados
com retenção e webhook. Portanto, é incompatível com a interface da PoC.

O primeiro consumidor previsto, code-for-coders, ainda não tem uma interface acordada para obter
a URL de download exigida por sourceUrl. O contrato media atual só emite URLs assinadas de
**escrita** para partes; elas não permitem leitura. A TechSpec deve fechar como o consumidor obtém
uma URL de leitura autorizada sem expor detalhes do provedor de mídia ao domínio de negócio.

## Pendências para aprovação

- Confirmar o cabeçalho de API Key (X-API-Key proposto ou Authorization: Bearer).
- Fechar limite de bytes, formatos aceitos e política de validação/erro para formatos incompatíveis.
- Definir pico de jobs simultâneos e metas observáveis para início do download e conclusão.
- Definir validade mínima da URL assinada e política de retentativa de download.
- Definir janela/limite das retentativas de webhook e o protocolo de assinatura: cabeçalho, algoritmo,
  conteúdo assinado, timestamp e proteção contra replay.
- Definir a retenção da chave de idempotência e equivalência de payload usada para detectar conflito.
- Resolver a obtenção de URL assinada de leitura pelo primeiro consumidor; a interface media consultada
  não fornece essa operação.

## Uso na implementação

Use o OpenAPI como fonte para tipos e mocks. A TechSpec deve mapear cada operação e webhook a cenários
de autorização por conta, idempotência, transições e falhas de download, expiração, resultado e entrega
repetida. A assinatura, as retentativas, os limites e o prazo de início precisam ter valores fechados
antes que esses cenários sejam tratados como acordo aprovado.
