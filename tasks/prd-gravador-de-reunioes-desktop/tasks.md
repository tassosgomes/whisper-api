---
tsg_artifact: tasks
product: whisper
version: 0.1
status: approved
updated: 2026-10-21
capability: CAP-004
sources: tasks/prd-gravador-de-reunioes-desktop/prd.md@1.1, tasks/prd-gravador-de-reunioes-desktop/techspec.md@1.0
---

# Plano de Implementação — Gravador de reuniões para Windows

> **TechSpec de origem:** [techspec.md](techspec.md), aprovada em 2026-10-21
> **Escopo:** Full-stack (API Python no Linux; cliente .NET com núcleo no Linux e host no Windows)
> **ADRs pertinentes:** [ADR-001](../../docs/adr/adr-001.md), [ADR-005](../../docs/adr/adr-005.md)
> **Status do plano:** Aprovado em 2026-10-21; execução em andamento, tasks 1.0, 2.0 e 4.0 concluídas

## Visão Geral

O plano entrega o envio direto de áudio na API e o aplicativo que o usa: gravar a reunião com microfone e áudio do sistema, enviar, acompanhar o job e guardar áudio e texto no PC. As três primeiras tasks fecham a API do contrato [api-contract.yaml](api-contract.yaml). A 4.0 prepara a solução do cliente. Depois, cada fatia do cliente é entregue em duas tasks: o **núcleo**, que roda e se testa no Linux, e o **host Windows**, que só pode ser validado numa sessão do Claude no Windows. Cada uma tem gate próprio e compila sozinha; a divisão existe por causa do ambiente, e não por camada técnica.

## Ambientes de execução

| Ambiente | Tasks | O que a sessão precisa |
|---|---|---|
| Linux (WSL2) | 1.0, 2.0, 3.0, 4.0, 5.0, 7.0, 9.0, 11.0, 13.0, 15.0 | Repositório, `.venv`, SDK do .NET 10, Docker para o smoke do armazenamento |
| Windows 11 | 6.0, 8.0, 10.0, 12.0, 14.0, 16.0, 17.0 | Repositório, SDK do .NET 10, microfone e saída de áudio, acesso à API e ao armazenamento (`s3.tasso.dev.br` ou o MinIO do compose) |

O orquestrador deve entregar as tasks de Windows a uma sessão Windows. As tasks de Windows dependem do núcleo correspondente, que já está concluído e versionado quando elas começam.

## Fases

### Fase 1 — API aceita o envio direto

1.0 a 3.0 criam o pedido de envio, a conclusão que cria o job e o expurgo. Checkpoint: gates de 1.0 a 3.0 e o smoke contra o MinIO do compose; ao final, qualquer cliente com API Key envia áudio direto ao armazenamento e recebe a transcrição.

### Fase 2 — Gravar e guardar no PC

4.0 prepara a solução. 5.0 e 6.0 gravam microfone e áudio do sistema e guardam a gravação. 7.0 e 8.0 configuram o acesso. Checkpoint: gates de 4.0 a 8.0 e a validação manual de 5 minutos de gravação real em 6.0.

### Fase 3 — Enviar, acompanhar e ler

9.0 a 14.0 enviam a gravação, acompanham o job, guardam o `.txt` e mostram a transcrição. Checkpoint: gates de 9.0 a 14.0 e a validação manual do ciclo completo (gravar, enviar, notificação, ler) em 12.0 e 14.0.

### Fase 4 — Gerenciar e instalar

15.0 e 16.0 gerenciam gravações e apagam os dados. 17.0 instala e desinstala e pertence à Fase 2 do PRD (Should Have); pode ser adiada sem bloquear as demais.

## Mapa de Entrega

| Fatia | Task | Comportamento observável | Gate | Bloqueado por |
|---|---|---|---|---|
| V-01 | 1.0 | Pedido de envio devolve a URL assinada; repetição não duplica; limites e conflitos respondem como o contrato | `.venv/bin/pytest -q -k cap4_upload_request` | Nenhum |
| V-02 | 2.0 | Conclusão cria o job em `queued` e ele chega ao resultado; objeto divergente é recusado | `.venv/bin/pytest -q -k cap4_upload_complete` | 1.0 |
| V-03 | 3.0 | Envio abandonado e recusado é expurgado; a chave volta a ser livre depois do job | `.venv/bin/pytest -q -k cap4_upload_retention` | 2.0 |
| V-04 | 5.0 | Núcleo grava as duas fontes em WAV, recupera gravação interrompida e respeita 2 h | `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V04*" --minimum-expected-tests 12` | 4.0 |
| V-04 | 6.0 | Captura WASAPI, tela de gravação, indicador, atalho e aviso de terceiros no Windows | `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V04*" --minimum-expected-tests 6` | 5.0 |
| V-05 | 7.0 | Núcleo valida a chave sem efeito colateral e distingue recusada de não verificável | `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V05*" --minimum-expected-tests 8` | 4.0 |
| V-05 | 8.0 | Chave no Credential Manager e tela de configurações | `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V05*" --minimum-expected-tests 4` | 6.0, 7.0 |
| V-06 | 9.0 | Núcleo envia a gravação, repete sem duplicar job e trata cada erro do contrato | `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V06*" --minimum-expected-tests 14` | 2.0, 5.0, 7.0 |
| V-06 | 10.0 | Tela de envio com confirmação e progresso | `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V06*" --minimum-expected-tests 3` | 8.0, 9.0 |
| V-07 | 11.0 | Núcleo acompanha o job, grava `.txt` e JSON e trata falha, expiração e versão desconhecida | `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V07*" --minimum-expected-tests 12` | 9.0 |
| V-07 | 12.0 | Notificação do Windows e situação na lista | `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V07*" --minimum-expected-tests 3` | 10.0, 11.0 |
| V-08 | 13.0 | Núcleo lê, copia e exporta a transcrição local | `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V08*" --minimum-expected-tests 8` | 11.0 |
| V-08 | 14.0 | Tela de transcrição, área de transferência e exportação | `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V08*" --minimum-expected-tests 3` | 12.0, 13.0 |
| V-09 | 15.0 | Núcleo renomeia e apaga gravações e apaga todos os dados do app | `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V09*" --minimum-expected-tests 8` | 5.0 |
| V-09 | 16.0 | Player, lista e diálogos de apagar | `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V09*" --minimum-expected-tests 3` | 14.0, 15.0 |
| V-10 | 17.0 | Instalar, desinstalar e manter ou apagar os dados | `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V10*" --minimum-expected-tests 3` | 12.0, 16.0 |

### Habilitadores

| Enabler | Task | Por que não cabe numa fatia | Desbloqueia |
|---|---|---|---|
| EN-01 | 4.0 | Não há código do cliente no repositório; as fatias V-04 a V-10 compartilham a estrutura da solução, o cliente da API gerado do contrato, as portas do host e a execução dos testes no Linux | V-04 (5.0) e V-05 (7.0) |

## Tasks

- [x] 1.0 Pedir a URL de envio de áudio
- [x] 2.0 Concluir o envio e criar o job
- [ ] 3.0 Expurgar envio abandonado e encerrar a idempotência com o job
- [x] 4.0 Preparar a solução do cliente e a execução dos testes no Linux
- [x] 5.0 Gravar a reunião e guardar a gravação no PC (núcleo)
- [ ] 6.0 Gravar a reunião e guardar a gravação no PC (host Windows)
- [ ] 7.0 Configurar o acesso à API (núcleo)
- [ ] 8.0 Configurar o acesso à API (host Windows)
- [ ] 9.0 Enviar uma gravação para transcrição (núcleo)
- [ ] 10.0 Enviar uma gravação para transcrição (host Windows)
- [ ] 11.0 Acompanhar o job e guardar a transcrição (núcleo)
- [ ] 12.0 Acompanhar o job e guardar a transcrição (host Windows)
- [ ] 13.0 Ler, copiar e exportar a transcrição (núcleo e apresentação)
- [ ] 14.0 Ler, copiar e exportar a transcrição (host Windows)
- [ ] 15.0 Gerenciar gravações e apagar os dados do app (núcleo)
- [ ] 16.0 Gerenciar gravações e apagar os dados do app (host Windows)
- [ ] 17.0 Instalar e desinstalar o app

## Caminho crítico e lanes

- **Caminho crítico do cliente:** 4.0 → 5.0 → 9.0 → 11.0 → 13.0, com os hosts 6.0 → 8.0 → 10.0 → 12.0 → 14.0 → 16.0 → 17.0 em paralelo no Windows.
- **Lane API (Linux):** 1.0 → 2.0 → 3.0, independente de 4.0 e 5.0 até 9.0, que precisa de 2.0.
- **Lane cliente (Linux):** 4.0 → 5.0, 7.0 → 9.0 → 11.0 → 13.0, 15.0.
- **Lane cliente (Windows):** cada host só começa depois do núcleo correspondente; 6.0 e 8.0 podem ser feitas enquanto a lane API avança.

## Verificação herdada

| Componente | Fonte | Checks e limites | Estado da base | Resolução planejada |
|---|---|---|---|---|
| API Python | `pyproject.toml`; **CI ausente** (`.github/` não existe) | `.venv/bin/pytest -q`, `.venv/bin/ruff check app tests`, `.venv/bin/ruff format --check app tests`; sem limite de cobertura | Medido em 2026-10-21: 231 testes coletados; `ruff check` e `ruff format --check` sem achados | Nenhuma |
| Compose | `docker-compose.yml`; CI ausente | `docker compose config -q` | Não medido | Nenhuma; só 1.0 altera o compose |
| Armazenamento (smoke) | TechSpec, seção Verificação; CI ausente | `S3_SMOKE_ENDPOINT=http://127.0.0.1:9000 .venv/bin/pytest -q -k cap4_presign_minio` | Não medido | Pré-requisitos em 1.0: `docker compose up -d minio minio-init` e as variáveis do `.env` |
| Cliente .NET | Nenhuma: **sem CI e sem convenção .NET no repositório** | `dotnet build clients/windows-recorder -warnaserror`, `dotnet format clients/windows-recorder --verify-no-changes`, `dotnet test --project …/Core.Tests` (e `Host.Tests` no Windows); sem limite de cobertura | Não existe código do cliente | 4.0 fixa a estrutura, os comandos, o runner Microsoft.Testing.Platform e o fluxo de CI. O comando de filtro por classe das demais tasks é confirmado em 4.0; se a sintaxe mudar, as tasks são ajustadas |

## Cobertura

| Requisito | Task(s) |
|---|---|
| RF-01 | 7.0, 8.0 |
| RF-02 | 5.0, 6.0 |
| RF-03 | 5.0, 6.0 |
| RF-04 | 5.0, 6.0, 15.0, 16.0 |
| RF-05 | 9.0, 10.0 |
| RF-06 | 11.0, 12.0 |
| RF-07 | 11.0, 13.0, 14.0 |
| RF-08 | 9.0, 15.0, 16.0 |
| RF-09 | 4.0, 17.0 |
| RF-10 | 1.0, 2.0, 3.0 |
| RN-G01 | 5.0, 6.0 |
| RN-G02 | 5.0, 9.0 |
| RN-G03 | 1.0, 5.0, 9.0 |
| RN-G04 | 1.0, 2.0, 3.0, 9.0 |
| RN-G05 | 11.0, 13.0, 15.0 |
| RN-G06 | 11.0 |
| RN-G07 | 7.0, 8.0 |
| RN-G08 | 11.0 |

As histórias de usuário do PRD não têm identificador. A cobertura delas: gravar com as outras pessoas (5.0, 6.0); ouvir e conferir antes de enviar (6.0, 16.0); enviar e acompanhar mesmo fechando o app (9.0, 11.0, 12.0); ler, copiar e exportar (13.0, 14.0); manter áudio e texto no PC (5.0, 11.0); privacidade e apagar (9.0, 15.0, 16.0); enviar o arquivo direto à API sem hospedá-lo (1.0, 2.0).

Sem task própria, com justificativa: observabilidade da API e do cliente entra em 1.0, 3.0 e 4.0 (contadores e diário sem conteúdo sensível), e não justifica uma task separada. Migração de dados: a migration da tabela de envios nasce em 1.0.
