---
tsg_artifact: tasks
product: whisper
version: 0.1
status: approved
updated: 2026-10-21
capability: CAP-002
sources: tasks/prd-operacao-do-servico/prd.md@0.1, tasks/prd-operacao-do-servico/techspec.md@0.1, tasks/prd-operacao-do-servico/contracts.md@0.1
---

# Plano de Implementação — Console operacional

> **TechSpec de origem:** [techspec.md](techspec.md), aprovada em 2026-10-21
> **Escopo:** Full-stack
> **ADRs pertinentes:** [ADR-002](../../docs/adr/adr-002.md), [ADR-003](../../docs/adr/adr-003.md), [ADR-004](../../docs/adr/adr-004.md)
> **Status do plano:** Aprovado em 2026-10-21

## Visão Geral

O plano entrega o console do operador em `/operator`, na mesma origem da API: sessão própria, conta, ciclo da API Key, ciclo do destino de webhook, fila e tentativas de entrega. Cada task atravessa a tela, a rota do contrato [api-contract.yaml](api-contract.yaml), a regra do domínio dono e a trilha administrativa no mesmo commit da mutação. Não há serviço novo nem alteração no contrato público.

## Fases

### Fase 1 — Entrar e cadastrar

1.0 abre a sessão operacional e prova a separação entre cookie e `X-API-Key` nos dois sentidos. 2.0 e 3.0 criam a conta e emitem a API Key com segredo de exibição única. Checkpoint: gates de 1.0 a 3.0; ao final, um cliente novo pode chamar a API.

### Fase 2 — Conter e configurar entrega

4.0 rotaciona e revoga a chave com motivo. 5.0 e 6.0 configuram, trocam, rotacionam e desativam o destino de webhook. Checkpoint: gates de 4.0 a 6.0; ao final, o operador cadastra e contém acesso e entrega sem o procedimento manual.

### Fase 3 — Observar

7.0 mostra tamanho, idade e jobs retidos de todas as contas. 8.0 mostra tentativas e falhas de entrega sem alterar o job. Checkpoint: gates de 7.0 e 8.0.

## Mapa de Entrega

| Fatia | Task | Comportamento observável | Gate | Bloqueado por |
|---|---|---|---|---|
| V-01 | 1.0 | Segredo operacional abre `/operator`; API Key não abre o console e o cookie não autentica `/v1/transcriptions` | `.venv/bin/pytest -q -k v01_operator_session` | Nenhum |
| V-02 | 2.0 | Conta criada aparece na lista com trilha sem segredo; nome vazio não cria | `.venv/bin/pytest -q -k v02_operator_account` | 1.0 |
| V-03 | 3.0 | API Key mostrada uma vez; nova leitura traz só metadados | `.venv/bin/pytest -q -k v03_operator_credential` | 2.0 |
| V-04 | 4.0 | Rotação invalida a chave antiga e preserva id e material; revogação sem motivo não age | `.venv/bin/pytest -q -k v04_operator_credential_lifecycle` | 3.0 |
| V-05 | 5.0 | Destino criado com material uma vez; troca de endereço não reexibe; recusa preserva o anterior | `.venv/bin/pytest -q -k v05_operator_destination` | 2.0 |
| V-06 | 6.0 | Rotação planejada por 72 h, imediata e desativação só com motivo | `.venv/bin/pytest -q -k v06_operator_destination_lifecycle` | 5.0 |
| V-07 | 7.0 | Fila com tamanho e idade; jobs retidos sem conteúdo nem URL de origem | `.venv/bin/pytest -q -k v07_operator_queue` | 1.0 |
| V-08 | 8.0 | Tentativas distinguem retentativa de falha encerrada sem payload nem mudança no job | `.venv/bin/pytest -q -k v08_operator_delivery` | 1.0, 6.0 |

### Habilitadores

Nenhum. A trilha administrativa e sua migration nascem em 2.0, a primeira mutação que a grava; as tasks seguintes a reutilizam. O contrato já foi aprovado e validado com o Spectral em 2026-10-21, sem task própria.

## Tasks

- [x] 1.0 Entrar no console com a credencial operacional
- [x] 2.0 Criar e listar contas com trilha administrativa
- [x] 3.0 Emitir API Key com segredo de exibição única
- [x] 4.0 Rotacionar e revogar API Key com motivo
- [ ] 5.0 Configurar e trocar o destino de webhook
- [ ] 6.0 Rotacionar o material e desativar o destino
- [ ] 7.0 Ver a fila e os jobs retidos sem conteúdo
- [ ] 8.0 Ver tentativas e falhas de entrega

## Verificação herdada

Não há workflow de CI nem Makefile. As fontes são `pyproject.toml` (pytest 8.3.5 e Ruff 0.11.10 no extra `dev`), `docker-compose.yml` (perfil `test` com `postgres-test`) e o README, seção 9. `rtk` não está no PATH deste ambiente; os comandos usam `.venv/bin/`. Os gates exigem `.env` carregado no shell (`set -a; . ./.env; set +a`) e `postgres-test` em execução. O segredo operacional de teste vem do ambiente do pytest, nunca do repositório. Não há limite de cobertura configurado.

O projeto não tem runner de navegador. A evidência da tela é a página servida em `/operator` testada pelo pytest (o que ela contém sem sessão e quais `operationId`s cada jornada chama) somada às regras impostas no servidor. Motivo obrigatório, segredo único e ausência de conteúdo são impostos pela API, não só pela tela.

| Componente | Fonte | Checks e limites | Estado da base | Resolução planejada |
|---|---|---|---|---|
| API, domínios e tela Python | `pyproject.toml`; CI ausente | `.venv/bin/pytest -q -k <fatia>`: exit 0, pytest sai 5 se nada for coletado; `.venv/bin/ruff check app tests`: exit 0; `.venv/bin/ruff format --check app tests`: exit 0. Sem limite de cobertura | Medido em 2026-10-21: `pytest -q` 68 passam; `ruff check app` exit 0; `ruff format --check app` exit 0 | Nenhuma falha herdada |
| Migrations | `alembic.ini`, `migrations/` | Testes aplicam as migrations no `postgres-test`; a nova revisão sobe sobre `0005_webhook_delivery` | Medido indiretamente pela suíte acima | 2.0 cria a revisão da trilha |
| Compose e imagem | `docker-compose.yml`, `Dockerfile`, README | `docker compose config -q`: exit 0; `docker compose build transcriber`: exit 0 quando a task altera Compose ou imagem | Não medido | 1.0 repassa o segredo operacional ao serviço |
| Contrato OpenAPI | `contracts.md` | `npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-operacao-do-servico/api-contract.yaml --ruleset .claude/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=warn`: exit 0 | Exit 0 em 2026-10-21, após a aprovação | Só reexecutar se uma task tocar o YAML, o que este plano não prevê |

Checks agregados para a validação full, cada um separado: `.venv/bin/pytest -q` (68 da base mais os novos), `.venv/bin/ruff check app tests`, `.venv/bin/ruff format --check app tests`, `docker compose config -q`, `docker compose build transcriber` e o lint Spectral acima. Recomenda-se um percurso manual no navegador em `/operator` (entrar, criar conta, emitir chave, configurar destino, olhar fila) antes da entrega, porque nenhum teste executa o JavaScript da tela.

## Cobertura

As histórias do PRD não têm IDs; US-01 a US-04 seguem a ordem em **Histórias de Usuário**.

| Requisito | Task(s) |
|---|---|
| RF-01 | 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 |
| RF-02 | 2.0 |
| RF-03 | 3.0, 4.0 |
| RF-04 | 5.0, 6.0 |
| RF-05 | 7.0 |
| RF-06 | 8.0 |
| US-01 | 2.0, 3.0 |
| US-02 | 5.0 |
| US-03 | 4.0, 5.0, 6.0 |
| US-04 | 7.0, 8.0 |

RF-01 aparece de 2.0 a 6.0 pelo critério da trilha: cada mutação grava a sua.

### Acesso e Contas

| Requisito | Task(s) |
|---|---|
| RN-01 | 2.0, 3.0 |
| RN-02 | 3.0 |
| RN-03 | 4.0 |
| RN-04 | 4.0, 7.0 |
| RN-05 | 3.0 |
| RN-06 | 4.0, 6.0 |
| RN-07 | 4.0 |
| RN-08 | 3.0 |
| RN-09 | 1.0 |
| RN-10 | 2.0, 3.0, 4.0 |

### Transcrição e Jobs

| Requisito | Task(s) |
|---|---|
| RN-01 | 7.0 |
| RN-02 | 7.0 |
| RN-03 | 7.0 |
| RN-04 | 7.0 |
| RN-05 | 8.0 |
| RN-06 | 7.0 |
| RN-07 | 7.0 |
| RN-08 | 7.0 |

### Entrega de Notificações

| Requisito | Task(s) |
|---|---|
| RN-01 | 5.0 |
| RN-02 | 5.0 |
| RN-03 | 4.0, 6.0 |
| RN-04 | 8.0 |
| RN-05 | 8.0 |
| RN-06 | 8.0 |
| RN-07 | 5.0 |
| RN-08 | 5.0 |
| RN-09 | 6.0 |
| RN-10 | 6.0 |
| RN-11 | 5.0 |
| RN-12 | 8.0 |
| RN-13 | 8.0 |

Arquivos **A modificar** da TechSpec e a task produtora: `app/main.py` (1.0 a 8.0), `app/access/credentials.py` (2.0 a 4.0), `app/jobs/webhooks.py` (5.0, 6.0, 8.0), `README.md` (1.0). Fora da lista da TechSpec, por consequência direta dela: `app/access/models.py` (2.0, entidade da trilha), `tests/conftest.py` (1.0 e 2.0, segredo de teste e limpeza da trilha), `.env.example` e `docker-compose.yml` (1.0, placeholder e repasse do segredo operacional).

Segurança atravessa todas as tasks: verificador separado (1.0), segredo único (3.0 a 6.0), projeções sem conteúdo (7.0, 8.0). Observabilidade nova não entra: o PRD exclui alerta e saúde de workers, e a fila é ela mesma a observação. Não há migração de dados: a trilha começa vazia e as tabelas existentes não mudam de forma.

Sequência crítica: 1.0 → 2.0 → 5.0 → 6.0 → 8.0. 3.0 → 4.0 e 7.0 são ramos independentes, mas todas tocam `app/main.py` e a tela; a execução segue a ordem numérica.
