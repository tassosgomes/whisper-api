# Plano de Implementação — API assíncrona de transcrição

> **TechSpec de origem:** [techspec.md](techspec.md), aprovada em 2026-09-28  
> **Escopo:** Backend  
> **ADRs pertinentes:** [ADR-001](../../docs/adr/adr-001.md), [ADR-002](../../docs/adr/adr-002.md), [ADR-003](../../docs/adr/adr-003.md)  
> **Status do plano:** Tasks 1.0 a 8.0 aprovadas em 2026-09-28. Tasks 9.0 a 15.0 acrescentadas em 2026-10-12 e ainda pendentes.

## Visão Geral

O plano entrega provisionamento operacional de credenciais, criação idempotente de jobs, aquisição e transcrição assíncronas, consulta de resultado durante 24 horas e webhook terminal. Cada fatia backend atravessa entrada, estado, integração e resposta. EN-01 documenta o acordo do primeiro consumidor; a implementação da URL de leitura no code-for-coders continua uma dependência externa explícita para o piloto integrado.

## Fases

### Fase 1 — Acordo e acesso

EN-03 fecha o contrato HTTP; EN-02 registra o procedimento de operação; EN-01 documenta a integração do consumidor. V-01 prova autenticação, rotação e isolamento no fluxo REST existente. Checkpoint: gate focalizado de 4.0.

### Fase 2 — Aceite e processamento

V-02 prova 202 após o início da conexão com a origem, idempotência e recuperação. V-03 prova aquisição protegida e estados até o terminal. Os testes usam banco e bucket isolados, origem HTTPS controlada, migrations e dados de teste reproduzíveis. Checkpoints: gates de 5.0 e 6.0.

### Fase 3 — Resultado e aviso

V-04 prova resultado v1, autorização, retenção e expurgo. V-05 prova aviso assinado, retries independentes do estado do job e smoke de criação até busca do resultado pelo destino de consulta público. Checkpoints: gates de 7.0 e 8.0.

### Fase 4 — Integração remota e correções posteriores

A ADR-003 já aceita destinos privados listados, mas o deploy, o contrato público e o baseline ainda descrevem a regra antiga. 9.0 liga a allowlist do MinIO no Coolify. 10.0 cadastra o webhook da conta code-for-coders quando o receptor do media existir. 11.0 alinha o contrato da `sourceUrl`. 12.0 trava as dependências transitivas e cobre a decodificação. 13.0 registra a exceção no baseline. 14.0 e 15.0 são opcionais e não bloqueiam as anteriores.

## Mapa de Entrega

| Fatia | Task | Comportamento observável | Gate | Bloqueado por |
|---|---|---|---|---|
| V-01 | 4.0 | Credencial ativa autoriza operações; rotação preserva proprietário, revogação e acesso cruzado falham | `rtk pytest -q -k v01_access` | 1.0, 2.0 |
| V-02 | 5.0 | Criação responde 202 após conectar à origem; repetição retorna o mesmo job | `rtk pytest -q -k v02_creation` | 1.0, 3.0, 4.0 |
| V-03 | 6.0 | Mídia atravessa download, fila, processamento e terminal com limites e retomada | `rtk pytest -q -k v03_processing` | 5.0 |
| V-04 | 7.0 | Resultado v1 fica acessível apenas ao dono e expira após 24 horas com expurgo | `rtk pytest -q -k v04_result` | 6.0 |
| V-05 | 8.0 | Webhook verificável e repetível preserva o job; smoke alcança resultado pelo link público | `rtk pytest -q -k v05_webhook` | 2.0, 6.0, 7.0 |
| EN-04 | 9.0 | API e worker no Coolify aceitam `sourceUrl` do MinIO privado e um job de prova chega a completed | `rtk rg -q 'TRUSTED_PRIVATE_SOURCES=http://192.168.0.5:9000' README.md` | Nenhum |
| EN-05 | 10.0 | Conta code-for-coders tem destino de webhook e um job de prova recebe 204 do receptor | `rtk rg -q '/webhooks/v1/transcription-events' README.md` | 9.0; CAP-032 fora deste repositório |
| EN-06 | 11.0 | Contrato da `sourceUrl` descreve http na allowlist e o lint Spectral passa | `rtk rg -q 'ADR-003' tasks/prd-api-transcricao-assincrona/api-contract.yaml` | Nenhum |
| V-06 | 12.0 | Build instala transitivas pinadas e uma mídia mínima decodifica com `decode_audio` | `rtk pytest -q -k decode_audio_fixture` | Nenhum |
| EN-07 | 13.0 | Baseline admite destino privado só quando está na allowlist da ADR-003 | `rtk rg -q 'ADR-003' context/architecture-baseline.md` | Nenhum |
| V-07 | 14.0 | Downloader envia User-Agent identificável; S3 e MinIO seguem iguais | `rtk pytest -q -k downloader_user_agent` | Nenhum |
| V-08 | 15.0 | `sourceUrl` e `clientReference` deixam de emitir o aviso do Pydantic e o JSON aceito permanece | `rtk pytest -q -k request_alias_warning` | Nenhum |

### Habilitadores

| Enabler | Task | Por que não cabe numa fatia | Desbloqueia |
|---|---|---|---|
| EN-03 | 1.0 | OpenAPI e índice são acordo compartilhado por todas as operações, sem comportamento executável do serviço | V-01 a V-05 |
| EN-02 | 2.0 | Procedimento manual de operação atende credenciais e webhook sem API administrativa no piloto | V-01 e V-05 |
| EN-01 | 3.0 | Acordo de integração do consumidor cruza criação, consulta e webhook e requer trabalho externo no code-for-coders | V-02 e piloto integrado |
| EN-04 | 9.0 | A stack Coolify não está no repositório; a allowlist é configuração dos processos já implantados | smoke remoto do MinIO e 10.0 |
| EN-05 | 10.0 | O cadastro usa a conta e a CLI já existentes; o receptor é da CAP-032, fora deste repositório | entrega real ao media |
| EN-06 | 11.0 | O schema público é compartilhado por todo cliente da `sourceUrl`; a exceção não pode ficar só no código | leitura do contrato |
| EN-07 | 13.0 | O baseline é guardrail de todas as features; não há comportamento novo de runtime nesta task | leitura do baseline por CAP-002 e pelo contrato |

## Tasks

- [x] 1.0 Atualizar e aprovar o contrato HTTP público
- [x] 2.0 Registrar o procedimento operacional do piloto
- [x] 3.0 Publicar o acordo de integração do primeiro consumidor
- [x] 4.0 Autenticar e isolar chamadas por conta e credencial
- [x] 5.0 Aceitar jobs idempotentes após iniciar aquisição
- [x] 6.0 Baixar, validar e transcrever mídia com estado recuperável
- [x] 7.0 Servir resultado versionado e expurgar dados vencidos
- [x] 8.0 Entregar webhook terminal assinado e comprovar a jornada
- [x] 9.0 Configurar a allowlist de destinos privados no deploy
- [x] 10.0 Cadastrar o destino do webhook da conta code-for-coders
- [ ] 11.0 Corrigir o contrato da sourceUrl
- [ ] 12.0 Pinar dependências transitivas e cobrir a decodificação
- [ ] 13.0 Atualizar o baseline arquitetural com a exceção da ADR-003
- [ ] 14.0 Enviar User-Agent identificável no download (opcional)
- [ ] 15.0 Calar o aviso de alias do Pydantic na criação (opcional)

## Verificação herdada

Não há workflow de CI, Makefile ou configuração de testes no checkout. `pyproject.toml` declara pytest 8.3.5 e Ruff 0.11.10 como dependências de desenvolvimento; o README documenta `docker compose build transcriber`. Os gates abaixo são comandos planejados para os testes que cada task produzirá, não evidência já executada. Não há limite de cobertura configurado. O lint Spectral 6.15.0 do contrato teve exit 0 em `contracts.md`, mas o contrato ainda está Em Revisão e precisa de atualização na task 1.0.

| Componente | Fonte | Checks e limites | Estado da base | Resolução planejada |
|---|---|---|---|---|
| API, jobs, serviço e métricas Python | `pyproject.toml`; CI ausente | `rtk pytest -q -k <fatia>`: exit 0 e testes selecionados; `rtk ruff check app`: exit 0; `rtk ruff format --check app`: exit 0. Sem limite de cobertura | Testes não encontrados; Ruff não medido | Cada fatia cria seus testes; correções de formatação/lint em arquivos tocados acompanham a fatia. Dívida alheia comprovada deve ser resolvida antes da integração, sem atribuição automática à primeira fatia |
| Runtime e ambiente de smoke | `Dockerfile`, `docker-compose.yml`, README; CI ausente | `rtk docker compose config -q`: exit 0; `rtk docker compose build transcriber`: exit 0 nas fatias que alteram a imagem/Compose. Smoke usa banco, bucket e receptor isolados | Configuração e build não medidos; não existe infraestrutura de smoke | 4.0 prepara armazenamento durável de credencial e dados de teste; 5.0 acrescenta estado/migrations de jobs e origem controlada; 6.0 prepara bucket S3 e papéis separados; 8.0 inclui receptor HTTPS e jornada completa |
| Contrato OpenAPI | `contracts.md`, ruleset da skill de contratos; CI ausente | `rtk npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-api-transcricao-assincrona/api-contract.yaml --ruleset .agents/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=error`: exit 0; 0 erros | Exit 0 registrado em 2026-09-28 para versão anterior às decisões fechadas | 1.0 atualiza e valida contrato e documentação derivada |
| Guias do piloto | README e TechSpec; CI ausente | `rtk rg -q` para seções requeridas: exit 0; `rtk git diff --check -- README.md`: exit 0, sem erro de whitespace; conteúdo revisto pelos critérios das tasks | Não medido | 2.0 e 3.0 registram os procedimentos e acordo |

Checks agregados para a validação full: `rtk pytest -q`, `rtk ruff check app`, `rtk ruff format --check app`, `rtk docker compose config -q`, `rtk docker compose build transcriber` e o lint Spectral acima, todos separadamente. Benchmark de até 10 jobs/dia, p95 de início de conexão até 30 s e p95 de terminal até 24 h exige mídia representativa e ambiente de produção assistida; 6.0 prepara métricas e evidência de carga antes do piloto, sem tratar teste local como garantia de produção.

## Cobertura

As histórias do PRD não têm IDs no texto; US-01 a US-04 seguem a ordem em **Histórias de Usuário**. O PRD não declara requisitos RN numerados.

| Requisito | Task(s) |
|---|---|
| RF-01 | 1.0, 2.0, 4.0 |
| RF-02 | 1.0, 3.0, 5.0, 6.0, 9.0, 11.0, 15.0 |
| RF-03 | 5.0, 6.0, 12.0, 14.0 |
| RF-04 | 1.0, 2.0, 3.0, 8.0, 10.0 |
| RF-05 | 1.0, 3.0, 7.0 |
| RF-06 | 6.0, 7.0 |
| US-01 | 3.0, 5.0, 6.0 |
| US-02 | 6.0, 8.0 |
| US-03 | 7.0 |
| US-04 | 2.0, 4.0, 10.0 |

Todos os arquivos da seção **A modificar** da TechSpec têm task produtora: `app/api/transcriptions.py` (4.0, 5.0, 7.0), `app/jobs/executor.py` (4.0 a 8.0), `app/main.py` (4.0 a 8.0), `app/service.py` (6.0, 7.0), `app/transcription/paths.py` (5.0, 6.0), `app/monitoring/metrics.py` (5.0, 6.0, 8.0), `docker-compose.yml` (5.0 a 8.0), `.env.example` (6.0, 7.0). EN-03 também altera os três documentos de contrato existentes. Segurança atravessa 4.0 a 8.0; observabilidade atravessa 5.0, 6.0 e 8.0. Não há migração de dados legados: a PoC não tem estado durável a migrar.

Sequência crítica: 1.0 → 4.0 → 5.0 → 6.0 → 7.0 → 8.0. 2.0 antecede 4.0; 3.0 antecede 5.0. Cada gate roda após a respectiva task e a validação full usa os checks agregados acima. O piloto integrado ainda depende da implementação da URL GET pré-assinada no code-for-coders e da medição de capacidade.

13.0 não cobre um RF novo: alinha o guardrail de segurança do baseline à ADR-003 já aceita. 14.0 e 15.0 são optativas. 9.0 e 10.0 têm evidência conclusiva fora do repositório (POST no Coolify e resposta 204 do receptor); o gate no plano só fixa o runbook que torna essa evidência rastreável.
