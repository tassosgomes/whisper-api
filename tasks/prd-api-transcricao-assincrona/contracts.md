# Contratos de integração — API assíncrona de transcrição

> PRD: [prd.md](prd.md), v1.0, aprovado em 2026-09-28  
> Data: 2026-09-28  
> Estado do conjunto: **Em Revisão**

Este conjunto registra o acordo proposto para esta implementação. Sua aprovação significará
acordo para implementar; não comprova implantação ou atualização de catálogo.

## Seleção e escopo

O Whisper recebe chamadas REST do cliente e envia um webhook HTTP ao destino cadastrado pela conta.
Ambas são interfaces HTTP e ficam no mesmo OpenAPI. O webhook não exige AsyncAPI porque não há
broker nem contrato de mensageria no escopo. ODCS não se aplica: o resultado é representação da API,
sem compromisso independente de produto de dados ou consumidores analíticos.

| Documento | Modalidade/versão do padrão | Versão do contrato | Escopo completo ou recorte | Status |
|---|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) e [api-contract.md](api-contract.md) | OpenAPI 3.1.0 | 1.0.0 | Recorte HTTP do PRD: criação, consulta, resultado e webhook terminal | Em Revisão; Spectral 6.15.0 sem erros ou avisos; decisões materiais abertas |

## Participantes e interfaces

| Documento e identificador técnico | Provedor/produtor | Consumidores conhecidos | Comportamento ou compromisso | Requisitos do PRD |
|---|---|---|---|---|
| [OpenAPI](api-contract.yaml) createTranscription | Whisper | Clientes de máquina; primeiro previsto: code-for-coders | 202 após início da conexão de download; API Key e Idempotency-Key; URL de origem não é devolvida | RF-01 a RF-03 |
| [OpenAPI](api-contract.yaml) getTranscription | Whisper | Conta proprietária do job | Estados públicos, horários disponíveis e falha resumida; isolamento por conta e retenção terminal de até 24 h | RF-01, RF-03, RF-06 |
| [OpenAPI](api-contract.yaml) getTranscriptionResult | Whisper | Conta proprietária do job concluído | Resultado JSON schema v1 em pt-BR; sem resultado parcial; indisponível após a retenção | RF-01, RF-05, RF-06 |
| [OpenAPI](api-contract.yaml) transcriptionTerminal / receiveTranscriptionTerminalWebhook | Whisper | URL configurada pelo operador para a conta | Evento HTTP ao menos uma vez, deduplicável por eventId, sem mídia ou transcrição; assinatura independente da API Key | RF-04 |

O encadeamento acordado é createTranscription → transcriptionTerminal (estado terminal) →
getTranscriptionResult (se completed). A consulta de estado pode ocorrer entre essas etapas.
Não há modelo ODCS ou mensagem de broker relacionada.

## Origem e decisões

| Entrada consultada | Versão e revisão/data | Decisão herdada ou alterada | Motivo |
|---|---|---|---|
| [PRD](prd.md) | v1.0, aprovado em 2026-09-28 | Define API Key por conta, URL assinada de download, idempotência, estados, webhook, resultado v1 e retenção de 24 h | Fonte primária do escopo e dos critérios de aceite |
| [Plano de produto](../../docs/plano-api-transcricao.md) | Sem versão formal; consultado em 2026-09-28 | Mantém entrada por URL assinada, início do download após aceite, webhook e resultado JSON desacoplado do motor | Fonte identificada no frontmatter do PRD |
| [API PoC](../../app/api/transcriptions.py) e [README](../../README.md) | Código/documentação presentes no workspace em 2026-09-28; sem versão publicada identificada | Mantém o caminho efetivo /v1/transcriptions; substitui entrada por arquivo local por URL remota e acrescenta autenticação, idempotência, resultado e webhook | Referência de interface existente; não é contrato nem evidência de produção |
| code-for-coders/tasks/prd-ingestao-midia/contracts.md e internal-api-contract.yaml | Índice 1.1 de 2026-09-26; OpenAPI 3.1.0, contrato Media 1.0.1 | Não há operação de leitura de mídia; as URLs existentes são de escrita e não podem atender sourceUrl | Contexto do primeiro consumidor e dependência para completar o fluxo ponta a ponta |
| code-for-coders/context/architecture-baseline.md e ADR-0002 | Baseline v1.2 de 2026-09-21; ADR aceita em 2026-09-21 | Contexto do consumidor reserva S3 + CloudFront para mídia; não define acesso do Whisper nem autenticação entre os serviços | Evita assumir infraestrutura, autorização ou interface ausentes do contrato do consumidor |

Não foi encontrado baseline arquitetural ou ADR próprio do Whisper. O baseline e a ADR-0002 do
consumidor foram consultados no workspace vizinho; não foram tratados como decisões de arquitetura
do Whisper.

## Evolução e compatibilidade

Não há versão anterior de OpenAPI do Whisper disponível; compatibilidade com produção não foi
verificada. Em relação à PoC local, a rota POST /v1/transcriptions mantém o prefixo efetivo, mas
request, autenticação e comportamento são incompatíveis: o path local dá lugar a sourceUrl,
chave idempotente obrigatória e resposta associada a conta. As rotas de consulta também passam a
ter isolamento entre contas, retenção explícita e acesso separado ao resultado.

O primeiro consumidor conhecido ainda não consegue derivar a URL de leitura a partir do contrato de
Media consultado: createVideoUploadPartUrlsInternal emite URLs assinadas somente para escrita. A
obtenção de uma URL HTTPS assinada de leitura e sua autorização permanecem uma dependência de
integração aberta. Nenhum contrato anterior ou externo foi alterado por este PRD.

## Validação e verificação

| Documento | Comando e versão da ferramenta | Schema/ruleset e versão | Resultado e avisos |
|---|---|---|---|
| [api-contract.yaml](api-contract.yaml) | rtk npx --yes @stoplight/spectral-cli@6.15.0 lint tasks/prd-api-transcricao-assincrona/api-contract.yaml --ruleset .agents/skills/tsg-flow-contract-creator/rulesets/openapi.yaml --fail-severity=error | OpenAPI 3.1.0; ruleset HTTP local | Executado em 2026-09-28; exit 0; sem erros nem avisos reportados |

O lint estrutural não verifica compatibilidade com produção, autorização real, comportamento de
idempotência, entrega de webhook ou conteúdo/qualidade da transcrição. Esses cenários pertencem à
verificação da implementação depois que as pendências forem decididas.

## Pendências e handoff

- Confirmar o cabeçalho de API Key (X-API-Key proposto ou Authorization: Bearer).
- Definir o limite em bytes, formatos aceitos, simultaneidade e metas de início/conclusão.
- Definir validade mínima da URL e retentativas de download.
- Definir a janela e o limite de retentativas do webhook e os detalhes de sua assinatura independente.
- Definir retenção da chave de idempotência e equivalência de payload.
- Resolver como code-for-coders obterá uma URL de leitura autorizada; a Media 1.0.1 consultada só
  fornece URL assinada de escrita. A dependência bloqueia a integração ponta a ponta do primeiro piloto.

Encaminhamento para tsg-flow-techspec-creator: usar este índice, o [OpenAPI](api-contract.yaml),
a [documentação derivada](api-contract.md) e o [PRD](prd.md). A TechSpec deve mapear essas operações
a componentes e cenários sem copiar schemas. O conjunto continua **Em Revisão** até que as decisões
materiais e a dependência de URL de leitura sejam fechadas.
