---
status: pending
task_kind: vertical
blocked_by: [1.0, 2.0]
gate: "rtk pytest -q -k v01_access"
gate_expect: "6 testes de autenticação, isolamento, rotação e revogação passam"
---

# 4.0 Autenticar e isolar chamadas por conta e credencial

**Fatia:** V-01 · **Cobre:** RF-01, US-04 · **Spec:** `techspec.md#v-01` · **ADR:** —

## Comportamento

Esta fatia estabelece armazenamento e migration duráveis para as credenciais; V-02 acrescenta a persistência de jobs ao mesmo ambiente.

O operador consegue provisionar uma conta e uma credencial para o piloto, receber o segredo uma única vez e consultar criação, permissões iniciais, último uso e revogação sem recuperá-lo. Chamadas REST com `X-API-Key` ativa resolvem conta e `credential_id`; segredo ausente, inválido, revogado ou antigo após rotação recebe `401` seguro. A rotação entrega novo segredo com o mesmo `credential_id`, preserva acesso aos jobs já criados e invalida o anterior. As operações iniciais são iguais para todas as chaves; leitura de job verifica conta e credencial criadora e responde `404` neutro para outra conta ou outra chave da mesma conta. A autorização protege os endpoints existentes para que a task tenha valor próprio antes da troca da entrada local por URL na V-02.

O teste focalizado provisiona dados descartáveis em armazenamento isolado, inicia a aplicação com os registros reais no ambiente de teste e chama as rotas HTTP. Prova também que somente hash/verificador do segredo é persistido e que respostas e logs não revelam chave nem informação do job alheio.

## Fora do escopo desta task

Criação idempotente por `sourceUrl`, armazenamento do job durável e entrega de webhook entram nas fatias seguintes. Permissões diferenciadas e API administrativa pública são da fase 2.

## Decisões fechadas

Todas as chaves do piloto têm o mesmo conjunto de operações; `403` fica reservado para futura diferenciação. Rotação mantém `credential_id`. Consultas usam conta e credencial proprietária, conforme `techspec.md#v-01` e baseline.

## Modificar / Referenciar

- **modificar:** `app/api/transcriptions.py` (autenticação e consulta isolada)
- **modificar:** `app/main.py` (composição de acesso e ciclo de vida)
- **modificar:** `app/jobs/executor.py` (vínculo do job à conta e credencial)
- **ref:** `tasks/prd-api-transcricao-assincrona/api-contract.yaml` (erros e cabeçalho)
- **ref:** `context/architecture-baseline.md` (domínio de Acesso e Contas)
- **ref:** `README.md` (procedimento operacional de 2.0)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API Python | `rtk pytest -q -k v01_access` | exit 0; 6 testes passam; pytest falha se nenhum for coletado | `pyproject.toml`, dev dependency pytest; CI ausente |
| API Python | `rtk ruff check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |
| API Python | `rtk ruff format --check app` | exit 0 | `pyproject.toml`, dev dependency Ruff; CI ausente |

## Pronto quando

- [ ] Segredo é entregue uma vez; armazenamento e metadados não permitem recuperá-lo.
- [ ] Chave ativa autoriza; inválida, revogada e segredo substituído falham sem vazamento.
- [ ] Outra conta e outra chave da mesma conta recebem `404` neutro; nova chave rotacionada mantém acesso aos jobs antigos.
- [ ] Aplicação inicia com os registros reais e o gate HTTP focalizado passa com exit 0.
