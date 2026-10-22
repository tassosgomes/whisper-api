# API Contract — Envio direto de áudio para transcrição

> **Gerado a partir de:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (v1.1) e [api-contract.yaml](api-contract.yaml)  
> **Data:** 2026-10-21  
> **Status:** Aprovado em 2026-10-21  
> **Versão do contrato:** 1.1.0

Este documento descreve o acordo para a implementação deste PRD e deriva do YAML correspondente, que é a fonte dos schemas e das restrições. PRDs posteriores podem evoluí-lo. Catalogação e mecanismos de atualização cabem à plataforma.

---

## Premissas e Decisões

| Decisão | Escolha | Motivo |
|---------|---------|--------|
| Forma do envio | O cliente pede uma URL de envio, faz `PUT` direto no S3 do Whisper e confirma | A API não recebe os bytes; reaproveita o bucket da ADR-001 |
| Autenticação | `X-API-Key` nas chamadas à API; a URL assinada autoriza o `PUT` | Herdado da 1.0.0 |
| Idempotência | `Idempotency-Key` por arquivo, escopada por conta e `credential_id`, válida por 24 h e até 24 h após o estado terminal do job | RN-G04: repetição tardia não duplica o job |
| Integridade | `sizeBytes` e `checksumSha256` obrigatórios; o S3 verifica no `PUT` e a API confere em `complete` | Detectar arquivo truncado ou trocado |
| Limites | 5 GiB, 2 horas, tipos equivalentes a .wav .mp3 .m4a .mp4 .webm .mkv | Herdados da 1.0.0 |
| Duração | Declarada (opcional) é recusada no pedido; a real só é medida no processamento | Medir exige ler o arquivo |
| Estado inicial do job | `queued` (sem `downloading`) | A mídia já está no armazenamento |
| Erros | RFC 9457, `application/problem+json`, com `code` estável | Herdado da 1.0.0 |
| Versionamento | Prefixo `/v1` em `servers` | Herdado da 1.0.0 |
| Retomada parcial | Fora desta versão; repete-se o `PUT` do arquivo inteiro | Evita estado parcial e uma terceira operação |

O `PUT` ao armazenamento é a exceção de mídia não JSON da norma HTTP local e não é uma operação deste documento.

---

## Resumo de Endpoints

| Método | Path | Descrição | Auth | Status Possíveis |
|--------|------|-----------|------|-----------------|
| `POST` | `/v1/transcription-uploads` | Pedir URL de envio de áudio (`createTranscriptionUpload`) | ✅ | 200, 201, 400, 401, 403, 409, 413, 422, 429, 500 |
| `POST` | `/v1/transcription-uploads/{uploadId}/complete` | Concluir o envio e criar o job (`completeTranscriptionUpload`) | ✅ | 202, 401, 403, 404, 409, 422, 500 |
| `GET` | `/v1/transcriptions/{jobId}` | Consultar estado do job (inalterado) | ✅ | 200, 401, 403, 404, 500 |
| `GET` | `/v1/transcriptions/{jobId}/result` | Buscar resultado (inalterado) | ✅ | 200, 401, 403, 404, 409, 500 |

---

## Fluxo

1. `POST /v1/transcription-uploads` com `contentType`, `sizeBytes`, `checksumSha256` e `Idempotency-Key` → `201` com `uploadId`, `upload` (método, URL e cabeçalhos) e `expiresAt`.
2. `PUT` na `upload.url`, com os cabeçalhos devolvidos exatamente como vieram. Se falhar, repete o `PUT` até `expiresAt`.
3. `POST /v1/transcription-uploads/{uploadId}/complete` → `202` com o `jobId` em `queued`.
4. `GET /v1/transcriptions/{jobId}` até um estado terminal; depois, `GET .../result` durante as 24 horas de retenção.

Se o cliente perder a resposta de qualquer passo, repete o `POST /v1/transcription-uploads` com a mesma `Idempotency-Key`: recebe o mesmo `uploadId` com URL válida, ou o `jobId` se o envio já foi concluído.

---

## Endpoints Detalhados

### `POST /v1/transcription-uploads` — Pedir URL de envio de áudio

**Propósito:** registrar o envio e obter a URL pré-assinada.  
**Consumido por:** App desktop (RF-05); qualquer cliente com API Key.

#### Headers

| Header | Obrigatório | Descrição |
|---|---|---|
| `X-API-Key` | Sim | API Key da conta |
| `Idempotency-Key` | Sim | Chave estável por arquivo, até 128 caracteres |

#### Request Body

```json
{
  "contentType": "audio/wav",
  "sizeBytes": 230400000,
  "checksumSha256": "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=",
  "durationMs": 7200000,
  "clientReference": "rec_01J9M2YB8K4W6N7P3Q5R1S0ABC"
}
```

#### Response 201

```json
{
  "uploadId": "upl_01J9M2YB8K4W6N7P3Q5R1S0ABC",
  "expiresAt": "2026-10-21T15:30:00Z",
  "upload": {
    "method": "PUT",
    "url": "https://storage.example.test/whisper-temporary/uploads/upl_01J9M2YB8K4W6N7P3Q5R1S0ABC?signature=not-a-real-signature",
    "headers": {
      "Content-Type": "audio/wav",
      "Content-Length": "230400000",
      "x-amz-checksum-sha256": "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="
    }
  },
  "completeUrl": "/v1/transcription-uploads/upl_01J9M2YB8K4W6N7P3Q5R1S0ABC/complete",
  "clientReference": "rec_01J9M2YB8K4W6N7P3Q5R1S0ABC"
}
```

A URL de envio é um segredo temporário e não deve ser registrada em logs.

#### Response 200 (repetição)

Mesmos valores e mesma chave: o mesmo `uploadId`, com URL válida se o envio está pendente. Se já foi concluído, traz `jobId` e não traz `upload` nem `completeUrl`.

#### Erros Possíveis

| Código HTTP | code | Quando ocorre |
|---|---|---|
| 400 | `INVALID_REQUEST` | Corpo malformado, campo obrigatório ou `Idempotency-Key` ausente |
| 401 | `UNAUTHORIZED` | API Key ausente, inválida ou revogada |
| 409 | `IDEMPOTENCY_KEY_REUSED` | Mesma chave com valores diferentes |
| 409 | `UPLOAD_REJECTED` | O envio dessa chave já foi recusado; use uma chave nova |
| 413 | `MEDIA_SIZE_LIMIT_EXCEEDED` | `sizeBytes` acima de 5 GiB |
| 422 | `UNSUPPORTED_MEDIA_FORMAT` | `contentType` fora da lista |
| 422 | `MEDIA_DURATION_LIMIT_EXCEEDED` | `durationMs` acima de 2 horas |
| 429 | `TOO_MANY_PENDING_UPLOADS` | Envios pendentes demais para a credencial; respeitar `Retry-After` |

---

### `POST /v1/transcription-uploads/{uploadId}/complete` — Concluir o envio e criar o job

**Propósito:** confirmar o `PUT` e criar o job.  
**Consumido por:** App desktop (RF-05).

Sem corpo. A API confere que o objeto existe, que tamanho e SHA-256 batem e que o início do arquivo corresponde a um formato aceito.

#### Response 202

```json
{
  "jobId": "trn_01J9M2YB8K4W6N7P3Q5R1S0ABC",
  "status": "queued",
  "createdAt": "2026-10-21T14:35:00Z",
  "statusUrl": "/v1/transcriptions/trn_01J9M2YB8K4W6N7P3Q5R1S0ABC",
  "clientReference": "rec_01J9M2YB8K4W6N7P3Q5R1S0ABC"
}
```

Repetir a chamada de um envio concluído devolve o mesmo job, também com `202`.

#### Erros Possíveis

| Código HTTP | code | Quando ocorre |
|---|---|---|
| 401 | `UNAUTHORIZED` | API Key ausente, inválida ou revogada |
| 404 | `NOT_FOUND` | Envio inexistente, expirado ou de outra conta ou credencial |
| 409 | `UPLOAD_NOT_RECEIVED` | O objeto ainda não chegou; repetir o `PUT` e chamar de novo até `expiresAt` |
| 422 | `UPLOAD_SIZE_MISMATCH` | Tamanho diferente do declarado; envio recusado e objeto removido |
| 422 | `UPLOAD_CHECKSUM_MISMATCH` | SHA-256 diferente do declarado; envio recusado e objeto removido |
| 422 | `UNSUPPORTED_MEDIA_FORMAT` | Início do arquivo não corresponde a um formato aceito; envio recusado |

Depois de um `422`, o envio fica recusado e o cliente recomeça com uma nova `Idempotency-Key`.

---

### `GET /v1/transcriptions/{jobId}` e `GET /v1/transcriptions/{jobId}/result`

Inalterados em relação à versão 1.0.0 (ver `tasks/prd-api-transcricao-assincrona/api-contract.md`). Diferenças de comportamento para um job de envio direto: o primeiro estado é `queued`, e `failure.code` pode ser `MEDIA_DURATION_LIMIT_EXCEEDED` ou `UNSUPPORTED_MEDIA_FORMAT` quando o processamento mede a duração ou decodifica o arquivo.

---

## Formato Padrão de Erro

RFC 9457, `Content-Type: application/problem+json`, com `code` estável e `traceId` opcional. O cliente trata o `code`, nunca o texto livre.

```json
{
  "type": "about:blank",
  "title": "Arquivo ainda não recebido",
  "status": 409,
  "code": "UPLOAD_NOT_RECEIVED",
  "instance": "/v1/transcription-uploads/upl_01J9M2YB8K4W6N7P3Q5R1S0ABC/complete"
}
```

---

## Como usar este contrato

### Cliente
Gere tipos a partir do YAML e simule a API:

```bash
npx openapi-typescript tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml -o api.ts
npx @stoplight/prism-cli mock tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml
```

O Prism não simula o `PUT` ao armazenamento.

### Testes de Contrato
Os cenários estão em `contracts.md`. O lint estrutural não substitui esses testes.

---

## Questões em Aberto

- [ ] Limite de envios pendentes por credencial e validade da URL de envio (TechSpec).
- [ ] Endpoint do armazenamento alcançável por HTTPS pelo PC do usuário, e política do bucket (arquitetura e operação).
