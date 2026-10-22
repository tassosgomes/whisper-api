---
tsg_artifact: contract
product: whisper
version: 0.1
status: approved
updated: 2026-10-21
capability: CAP-004
sources: tasks/prd-gravador-de-reunioes-desktop/prd.md@1.1
---

# Contratos de integração — Gravador de reuniões (envio direto de áudio)

> PRD: `tasks/prd-gravador-de-reunioes-desktop/prd.md`  
> Data: 2026-10-21  
> Estado do conjunto: Aprovado em 2026-10-21

Este conjunto registra o acordo para esta implementação. PRDs posteriores podem evoluí-lo. Não afirma qual contrato está em produção. Catalogação, armazenamento definitivo e mecanismos de atualização do acervo são definidos pelo time de plataforma.

## Seleção e escopo

O PRD exige uma interface HTTP nova: a API passa a aceitar áudio enviado pelo cliente (RF-10), consumida pelo app desktop (RF-05, RF-06, RF-07) e por qualquer outro cliente autenticado. Modalidade: OpenAPI. Não há mensagem nova (o app não recebe webhook, RN-G08) nem acordo de dados compartilhado. AsyncAPI e ODCS não se aplicam.

| Documento | Modalidade/versão do padrão | Versão do contrato | Escopo completo ou recorte | Status |
|---|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) | OpenAPI 3.1.0 | 1.1.0 | Recorte: o envio direto de áudio e as duas consultas que o cliente usa depois dele. Não substitui o contrato 1.0.0. | Aprovado |
| [api-contract.md](api-contract.md) | Documentação derivada do YAML | 1.1.0 | Mesmo recorte | Aprovado |

## Participantes e interfaces

| Documento e identificador técnico | Provedor/produtor | Consumidores conhecidos | Comportamento ou compromisso | Requisitos do PRD |
|---|---|---|---|---|
| `createTranscriptionUpload` | Whisper | App desktop; qualquer cliente com API Key | Registra o envio, valida tipo, tamanho e duração declarada, devolve URL de envio. Idempotente por 24 h (até 24 h após o estado terminal do job). | RF-10, RF-05; RN-G03, RN-G04 |
| PUT na URL de envio | Armazenamento S3 do Whisper (ADR-001) | App desktop | Recebe os bytes. Fora do documento OpenAPI, por ser mídia não JSON. | RF-05, RF-10 |
| `completeTranscriptionUpload` | Whisper | App desktop | Confere objeto, tamanho, SHA-256 e formato; cria o job em `queued`. Idempotente por `uploadId`. | RF-10, RF-05; RN-G04 |
| `getTranscription`, `getTranscriptionResult` | Whisper | App desktop | Inalterados em relação à 1.0.0. | RF-06, RF-07; RN-G05, RN-G06 |

## Origem e decisões

| Entrada consultada | Versão e revisão/data | Decisão herdada ou alterada | Motivo |
|---|---|---|---|
| `tasks/prd-gravador-de-reunioes-desktop/prd.md` | 1.1, approved em 2026-10-21 | Escopo da superfície | RF-05, RF-06, RF-07, RF-10 |
| `tasks/prd-api-transcricao-assincrona/api-contract.yaml` | 1.0.0, consultado em 2026-10-21 | Herdados: prefixo `/v1`, `X-API-Key`, Problem Details, limite de 5 GiB, formatos, 2 horas, isolamento por credencial (404 neutro), estados, resultado `schemaVersion` 1, retenção de 24 h. | Mesmo job, mesmo resultado e mesma API Key. |
| ADR-001 | Accepted em 2026-09-28 | Armazenamento é o bucket S3 privado sob controle do Whisper; o expurgo remove mídia e órfãos. | O envio direto grava nesse bucket. |
| ADR-003 | Accepted | Não alterada. Só vale para `sourceUrl` e webhook. | A URL de envio é emitida pelo próprio Whisper, não entrada do cliente. |
| Decisão do usuário, 2026-10-21 | — | Envio por URL pré-assinada direto ao S3 do Whisper (nova). | Evita passar os bytes pela API; reaproveita o S3 que o Whisper já opera. |
| Decisão do usuário, 2026-10-21 | — | Idempotência até o fim da retenção do job (nova). | A janela de 120 s não cobre RN-G04 para arquivo grande nem "tentar de novo" tardio. |
| Escolha de projeto | — | `checksumSha256` obrigatório; o S3 o verifica no PUT e a API o confere em `complete`. | Integridade do envio e equivalência da repetição sem reler o arquivo. |
| Escolha de projeto | — | Duração e formato reais só no processamento; o pedido recusa apenas o que o cliente declara. | Medir duração exige ler o arquivo. A falha é identificável (`failure.code`), mas o job existe. |

## Evolução e compatibilidade

Mudança aditiva. Sem alteração em `POST /v1/transcriptions`, `GET /v1/transcriptions/{jobId}`, `GET /v1/transcriptions/{jobId}/result` nem no webhook. Dois paths novos (`/transcription-uploads` e `/transcription-uploads/{uploadId}/complete`) não colidem com `/transcriptions/{jobId}`; o prefixo `transcription-uploads` foi escolhido para não competir com o segmento `{jobId}`.

Pontos que o consumidor existente (code-for-coders) deve conhecer, sem impacto no que ele já usa:

- Um job criado por envio direto começa em `queued` e nunca passa por `downloading`. O enum não muda; o consumidor que supõe `downloading` primeiro deve tolerar a ausência.
- Os códigos de falha já existentes `MEDIA_DURATION_LIMIT_EXCEEDED` e `UNSUPPORTED_MEDIA_FORMAT` agora também ocorrem em jobs de envio direto.

A compatibilidade com produção não foi verificada: a versão 1.0.0 também não tem evidência de implantação. As operações repetidas aqui (`getTranscription`, `getTranscriptionResult`) são cópias da 1.0.0; se a 1.0.0 mudar, este recorte deve ser revisado.

## Validação e verificação

| Documento | Comando e versão da ferramenta | Schema/ruleset e versão | Resultado e avisos |
|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) | `npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml --ruleset .claude/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=warn` | OpenAPI ruleset da skill, Spectral 6.15.0 | Executado em 2026-10-21. Exit 0. Nenhum erro e nenhum aviso. |

O lint verifica estrutura e convenções, não o comportamento do serviço nem a compatibilidade. Cenários de verificação da implementação, para a TechSpec e as tasks:

- Repetição de `createTranscriptionUpload` com os mesmos valores, antes e depois do `complete`, minutos e horas depois: mesmo `uploadId`, sem segundo job.
- Mesma chave com valores diferentes: 409 `IDEMPOTENCY_KEY_REUSED`.
- `complete` antes do PUT: 409 `UPLOAD_NOT_RECEIVED`; após o PUT, 202.
- `complete` repetido: mesmo `jobId`.
- Objeto com tamanho, SHA-256 ou formato diferente do declarado: 422 e objeto removido; recomeço só com nova chave.
- PUT com cabeçalhos diferentes dos devolvidos, ou depois de `expiresAt`: recusado pelo armazenamento.
- Envio ou job de outra credencial: 404 neutro.
- A URL de envio, o conteúdo e o `checksumSha256` não aparecem em logs, traces nem ao operador.
- O fluxo de `sourceUrl` continua passando nos testes da 1.0.0.

## Pendências e handoff

| Pendência | Responsável | Bloqueia? |
|---|---|---|
| Aprovação do conjunto: concluída em 2026-10-21. | Produto | Não. |
| Limite de envios pendentes por credencial (429 `TOO_MANY_PENDING_UPLOADS`) e validade da URL de envio (`expiresAt`). O contrato fixa o comportamento, não os valores. | TechSpec | Não. |
| O armazenamento precisa ser alcançável por HTTPS pelo PC do usuário, o que não vale para o MinIO local (`127.0.0.1`). Endpoint público, TLS e política do bucket (ADR-001) são decisão de implantação. | Arquitetura e operação | Sim, para o piloto, não para a TechSpec. |
| O PRD diz que formato e duração inválidos são recusados sem criar job. O contrato recusa só o que é declarado ou verificável no início do arquivo; a duração real falha o job. O PRD v1.1 já reflete isso. | Produto | Não. |
| Retomada parcial de envio fica fora desta versão. Pode entrar de forma aditiva (partes do S3). | Produto | Não. |

Handoff para `tsg-flow-techspec-creator` depois da aprovação, referenciando este índice e `api-contract.yaml`, sem copiar os schemas. Antes dela, a decisão sobre onde o cliente desktop entra no baseline (NA7) continua aberta.
