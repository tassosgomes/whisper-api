# Contratos de integração — API assíncrona de transcrição

> PRD: [prd.md](prd.md), v1.0, aprovado em 2026-09-28  
> TechSpec: [techspec.md](techspec.md), aprovada em 2026-09-28 (Q-01 a Q-11)
> Data: 2026-09-28  
> Estado do conjunto: **Aprovado**

Este conjunto registra o acordo aprovado para esta implementação. Sua aprovação significa
acordo para implementar; não comprova implantação ou atualização de catálogo.

## Seleção e escopo

O Whisper recebe chamadas REST do cliente e envia um webhook HTTP ao destino cadastrado pela conta.
Ambas são interfaces HTTP e ficam no mesmo OpenAPI. O webhook não exige AsyncAPI porque não há
broker nem contrato de mensageria no escopo. ODCS não se aplica: o resultado é representação da API,
sem compromisso independente de produto de dados ou consumidores analíticos.

| Documento | Modalidade/versão do padrão | Versão do contrato | Escopo completo ou recorte | Status |
|---|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) e [api-contract.md](api-contract.md) | OpenAPI 3.1.0 | 1.0.0 | Recorte HTTP do PRD: criação, consulta, resultado e webhook terminal | Aprovado; Spectral 6.15.0 sem erros; Q-01 a Q-11 fechadas |

## Participantes e interfaces

| Documento e identificador técnico | Provedor/produtor | Consumidores conhecidos | Comportamento ou compromisso | Requisitos do PRD |
|---|---|---|---|---|
| [OpenAPI](api-contract.yaml) createTranscription | Whisper | Clientes de máquina; primeiro previsto: code-for-coders | 202 após início da conexão de download; tamanho conhecido acima de 5 GiB na inspeção inicial retorna 413 sem job; tamanho desconhecido segue sem rejeição por tamanho e é limitado durante o streaming; se o streaming exceder 5 GiB, job termina em `failed/MEDIA_SIZE_LIMIT_EXCEEDED`; X-API-Key e Idempotency-Key (120 s por conta e credencial); formatos aprovados; URL de leitura com validade mínima de 60 min e até 3 tentativas; URL de origem não é devolvida | RF-01 a RF-03 |
| [OpenAPI](api-contract.yaml) getTranscription | Whisper | Conta e credencial proprietárias do job | Estados públicos, horários disponíveis e falha resumida; isolamento por conta e credencial com 404 neutro; retenção terminal de até 24 h | RF-01, RF-03, RF-06 |
| [OpenAPI](api-contract.yaml) getTranscriptionResult | Whisper | Conta e credencial proprietárias do job concluído | Resultado JSON schema v1 em pt-BR; sem resultado parcial; indisponível após a retenção | RF-01, RF-05, RF-06 |
| [OpenAPI](api-contract.yaml) transcriptionTerminal / receiveTranscriptionTerminalWebhook | Whisper | URL configurada pelo operador para a conta | Evento HTTP ao menos uma vez, deduplicável por eventId, sem mídia ou transcrição; Standard Webhooks v1 com tolerância de ±300 s e retries por até 72 h; assinatura independente da API Key | RF-04 |

O encadeamento acordado é createTranscription → transcriptionTerminal (estado terminal) →
getTranscriptionResult (se completed). A consulta de estado pode ocorrer entre essas etapas.
Não há modelo ODCS ou mensagem de broker relacionada.

## Origem e decisões

| Entrada consultada | Versão e revisão/data | Decisão herdada ou alterada | Motivo |
|---|---|---|---|
| [PRD](prd.md) | v1.0, aprovado em 2026-09-28 | Define API Key por conta, URL assinada de download, idempotência, estados, webhook, resultado v1 e retenção de 24 h | Fonte primária do escopo e dos critérios de aceite |
| [Plano de produto](../../docs/plano-api-transcricao.md) | Sem versão formal; consultado em 2026-09-28 | Mantém entrada por URL assinada, início do download após aceite, webhook e resultado JSON desacoplado do motor | Fonte identificada no frontmatter do PRD |
| [TechSpec](techspec.md) | Aprovada em 2026-09-28 | Fecha Q-01 (bucket S3 privado), Q-02 (5 GiB, formatos, SLOs iniciais), Q-03/Q-04 (URL de leitura e política de aquisição), Q-05 (Standard Webhooks v1 e política operacional), Q-06 (idempotência 120 s), Q-07 (X-API-Key), Q-08 (admin fase 2), Q-09 (baseline), Q-10 (permissões iguais, leitura da própria chave), Q-11 (rotação preserva credential_id) | Acordo aprovado aplicado ao OpenAPI nesta task |
| [Baseline arquitetural](../../context/architecture-baseline.md) | Aprovado em 2026-09-28 | Fronteiras de Acesso e Contas, Transcrição e Jobs e Entrega de Notificações; estado durável; SSRF; retenção e expurgo | Direção estrutural da feature |
| [ADR-001](../../docs/adr/adr-001.md) | Accepted em 2026-09-28 | Bucket privado S3 para mídia temporária e resultados, sem versionamento/Object Lock incompatível | Decisão de armazenamento aplicada ao contrato |
| [ADR-002](../../docs/adr/adr-002.md) | Accepted em 2026-09-28 | Standard Webhooks v1 com HMAC-SHA256, tolerância de ±300 s, retries de 72 h e rotação definida | Decisão de assinatura aplicada ao contrato |
| [API PoC](../../app/api/transcriptions.py) e [README](../../README.md) | Código/documentação presentes no workspace em 2026-09-28; sem versão publicada identificada | Mantém o caminho efetivo /v1/transcriptions; substitui entrada por arquivo local por URL remota e acrescenta autenticação, idempotência, resultado e webhook | Referência de interface existente; não é contrato nem evidência de produção |
| code-for-coders/tasks/prd-ingestao-midia/contracts.md e internal-api-contract.yaml | Índice 1.1 de 2026-09-26; OpenAPI 3.1.0, contrato Media 1.0.1 | Acordo Q-03/Q-04 define que o consumidor gera a URL de leitura; a implementação desse fluxo no consumidor (EN-01) segue como dependência externa do piloto | Contexto do primeiro consumidor; o contrato Whisper não reabre Q-03/Q-04 |

## Evolução e compatibilidade

Não há versão anterior de OpenAPI do Whisper disponível; compatibilidade com produção não foi
verificada. Em relação à PoC local, a rota POST /v1/transcriptions mantém o prefixo efetivo, mas
request, autenticação e comportamento são incompatíveis: o path local dá lugar a sourceUrl,
chave idempotente obrigatória e resposta associada a conta. As rotas de consulta também passam a
ter isolamento entre contas e credenciais, retenção explícita e acesso separado ao resultado.
Nenhum contrato anterior ou externo foi alterado por este PRD.

## Validação e verificação

| Documento | Comando e versão da ferramenta | Schema/ruleset e versão | Resultado e avisos |
|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) | rtk npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-api-transcricao-assincrona/api-contract.yaml --ruleset .agents/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=error | OpenAPI 3.1.0; ruleset HTTP local | Executado em 2026-09-28; exit 0; 0 erros; 0 avisos |

O lint estrutural não verifica compatibilidade com produção, autorização real, comportamento de
idempotência, entrega de webhook ou conteúdo/qualidade da transcrição. Esses cenários pertencem à
verificação da implementação nas fatias V-01 a V-05.

## Histórico e handoff

Acordo aprovado em 2026-09-28 com Q-01 a Q-11 fechadas. A aprovação registra acordo para
implementar e desbloqueia V-01 e as demais fatias; não comprova implantação nem atualização de
catálogo.

Pendência externa de implementação (sem reabrir o acordo): implementar no code-for-coders a
geração da URL de leitura definida em Q-03 (EN-01) antes do piloto integrado. A API
administrativa de contas, API Keys e destinos de webhook fica para a fase 2 (Q-08); o
procedimento operacional do piloto é registrado em EN-02.

Encaminhamento para implementação: usar este índice, o [OpenAPI](api-contract.yaml),
a [documentação derivada](api-contract.md) e o [PRD](prd.md). A implementação deve mapear essas
operações a componentes e cenários sem copiar schemas.
