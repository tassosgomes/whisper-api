---
tsg_artifact: vision
product: whisper
version: 0.1
status: approved
updated: 2026-09-28
sources:
---

# Visão do produto: Whisper

## Problema

Produtos que precisam oferecer transcrição de áudio e vídeo acabam incorporando processamento de mídia, operação de modelos e entrega de resultados ao próprio sistema. Isso aumenta o custo de integração e mistura a transcrição com conceitos que pertencem ao cliente.

## Solução proposta

Whisper oferece transcrição como serviço para clientes de software. O cliente solicita o processamento, acompanha sua conclusão, recebe uma notificação e busca o resultado durante uma janela limitada. O cliente continua dono da relação entre a transcrição e seu próprio conteúdo, além de decidir se e como guardará o resultado permanentemente.

## Públicos e fluxos de valor

| Público | Necessidade | Valor esperado |
|---|---|---|
| Desenvolvedor de um sistema cliente | Integrar transcrição sem esperar o processamento na mesma interação e sem acoplar seu produto ao motor de transcrição | Receber um resultado utilizável e relacioná-lo ao conteúdo do próprio produto |
| Operador do Whisper | Administrar o acesso de clientes e acompanhar a execução e a entrega de resultados | Operar o serviço com isolamento entre contas e exposição limitada de dados |

## Objetivos

- Oferecer um fluxo genérico de transcrição para diferentes sistemas clientes.
- Manter o processamento desacoplado das chamadas que iniciam e consultam jobs.
- Entregar resultados versionados, sem exigir que clientes adotem o modelo de dados interno do Whisper.
- Limitar a retenção de mídias e resultados ao necessário para processar e permitir a coleta pelo cliente.

## Recorte e limites atuais

O primeiro recorte previsto é uma integração máquina a máquina com autenticação por conta, envio de uma referência de mídia, acompanhamento do job, notificação terminal e busca temporária do resultado. O primeiro consumidor previsto é o code-for-coders; o serviço não deve incorporar conceitos como curso ou aula.

As premissas iniciais registradas no PRD são áudio ou vídeo de até duas horas, até dez jobs por dia, idioma pt-BR e retenção de metadados e resultado por até 24 horas após o estado terminal. São limites de piloto sujeitos a benchmark e às decisões pendentes registradas no PRD.

## Fora do escopo estratégico atual

- Armazenar transcrições permanentemente em nome do cliente.
- Associar resultados a cursos, aulas ou outros conceitos específicos de um consumidor.
- Indexar ou oferecer busca textual do conteúdo para o cliente.
- Apresentar uma experiência de revisão de mídia e transcrição como produto final ao usuário do cliente.

Console de autoatendimento, upload direto e outros idiomas estão descritos como possíveis evoluções, sem compromisso de escopo ou sequência neste documento.

## Fatos, premissas e pontos abertos

**Fatos documentados:** o PRD v1.0 foi aprovado em 2026-09-28 e define o serviço para integração entre sistemas; `docs/plano-api-transcricao.md` registra o objetivo de produto e as premissas iniciais do piloto; a implementação existente é uma prova de conceito local.

**Premissas de piloto:** duração de até duas horas, até dez jobs por dia, pt-BR e retenção terminal de 24 horas. O volume e o prazo de conclusão dependem do benchmark do motor e da fila.

**Pontos abertos:** limite em bytes, formatos, simultaneidade e metas de início/conclusão; validade mínima e leitura autorizada da URL de origem; escolha do armazenamento temporário. Esses pontos não alteram o problema central ou o público desta visão, mas restringem a aprovação do contrato e o dimensionamento do serviço.

## Base documental

Visão inicial reconstruída a partir de [tasks/prd-api-transcricao-assincrona/prd.md](tasks/prd-api-transcricao-assincrona/prd.md), v1.0 aprovada, [docs/plano-api-transcricao.md](docs/plano-api-transcricao.md), sem versão formal, e da prova de conceito presente no repositório. Este documento está em revisão porque a visão estratégica do produto ainda não existia como artefato próprio.
