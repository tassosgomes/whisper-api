# API Contract — Console operacional

> **Gerado a partir de:** `tasks/prd-operacao-do-servico/prd.md`  
> **Data:** 2026-10-13  
> **Status:** Aprovado em 2026-10-21  
> **Versão do contrato:** 1.0.0

Este documento deriva de [api-contract.yaml](api-contract.yaml). O YAML é a fonte dos schemas. Catalogação e atualização do acervo cabem à plataforma.

## Premissas e Decisões

| Decisão | Escolha | Motivo |
|---|---|---|
| Autenticação | Cookie `operator_session`, HttpOnly, aberto por `POST /operator-sessions` | A credencial operacional é compartilhada e não é API Key. Sessão no mesmo host fechada pela ADR-004. |
| API pública | Continua só com `X-API-Key` | Cookie de operador não autoriza transcrição. API Key não abre este console. |
| Segredos | `apiKey` e `verificationMaterial` só na resposta que os cria ou rotaciona | O PRD exige apresentação única. Os schemas de consulta não têm esses campos. |
| Destino | Um por conta. `PUT` cria ou troca só o endereço | Segundo destino não existe. Troca de endereço não reexibe o material. |
| Fila | `GET /queue` separada da lista retida | Tamanho e idade são dos jobs ainda não terminais. A lista inclui também os terminais dentro de 24 horas. |
| Paginação | `_page` / `_size`, padrão 10, máximo 100 | Coleções de contas, credenciais, jobs e tentativas não têm teto fixo. |
| Datas | ISO 8601 UTC | Mesma convenção do contrato público. |
| Versionamento | `servers.url: /v1` | Mesmo prefixo da API pública, sem colidir com `/transcriptions`. |

## Resumo de Endpoints

Os paths abaixo são relativos a `/v1`.

| Método | Path | Descrição | Auth | Status |
|---|---|---|---|---|
| `POST` | `/operator-sessions` | Abre a sessão | Não | 204, 401, 500 |
| `DELETE` | `/operator-sessions` | Encerra a sessão | Sessão | 204, 500 |
| `GET` | `/accounts` | Lista contas | Sessão | 200, 401, 500 |
| `POST` | `/accounts` | Cria conta | Sessão | 201, 401, 422, 500 |
| `GET` | `/accounts/{accountId}` | Consulta conta | Sessão | 200, 401, 404, 500 |
| `GET` | `/accounts/{accountId}/credentials` | Lista metadados | Sessão | 200, 401, 404, 500 |
| `POST` | `/accounts/{accountId}/credentials` | Emite API Key | Sessão | 201, 401, 404, 500 |
| `GET` | `/credentials/{credentialId}` | Metadados sem segredo | Sessão | 200, 401, 404, 500 |
| `POST` | `/credentials/{credentialId}/rotate` | Rotaciona a chave | Sessão | 200, 401, 404, 409, 500 |
| `POST` | `/credentials/{credentialId}/revoke` | Revoga com motivo | Sessão | 200, 401, 404, 422, 500 |
| `GET` | `/accounts/{accountId}/webhook-destination` | Consulta o destino | Sessão | 200, 401, 404, 500 |
| `PUT` | `/accounts/{accountId}/webhook-destination` | Cria ou troca o endereço | Sessão | 200, 201, 401, 404, 422, 500 |
| `POST` | `/accounts/{accountId}/webhook-destination/rotate` | Rotaciona o material | Sessão | 200, 401, 404, 422, 500 |
| `POST` | `/accounts/{accountId}/webhook-destination/deactivate` | Desativa com motivo | Sessão | 200, 401, 404, 422, 500 |
| `GET` | `/queue` | Tamanho e idade da fila | Sessão | 200, 401, 500 |
| `GET` | `/jobs` | Jobs ainda retidos | Sessão | 200, 401, 500 |
| `GET` | `/jobs/{jobId}` | Um job sem conteúdo | Sessão | 200, 401, 404, 500 |
| `GET` | `/delivery-attempts` | Tentativas de entrega | Sessão | 200, 401, 500 |

## Exemplos que o YAML fixa

Emissão, única resposta com segredo:

```json
{
  "credentialId": "9c2e1b70-1a44-4d2e-8f55-0a6b7c8d9e01",
  "accountId": "4b1d0c3e-6f2a-4a7b-9c10-2e5d8a7b6c11",
  "status": "active",
  "apiKey": "whsk_live_4f8a91c2e6b74d0a"
}
```

Fila vazia:

```json
{ "size": 0, "oldestAcceptedAt": null }
```

`GET /credentials/{credentialId}` e a lista de credenciais usam o schema sem `apiKey`. `GET` do destino não tem `verificationMaterial`.
