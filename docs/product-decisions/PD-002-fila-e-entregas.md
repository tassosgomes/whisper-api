# PD-002: A visão operacional é a fila e as entregas

- **Status**: Accepted
- **Escopo**: Domínio: Transcrição e Jobs
- **Data**: 2026-10-13
- **Responsável pela decisão**: Produto
- **Origem**: PRD CAP-002, decisão de 2026-10-12
- **Tags**: fila, webhook, operador
- **Substitui**: Não aplicável
- **Substituído por**: Não aplicável

## Contexto

O operador precisa saber se o trabalho está parado ou se o aviso não chegou. A visão não fixa um painel de saúde da plataforma.

## Decisão

A tela mostra a fila de jobs, com estado, tamanho e idade, e as falhas e retentativas de webhook. Não mostra saúde de workers, mídia nem texto da transcrição.

## Alternativas consideradas

- **Painel com saúde de workers** — não foi o recorte pedido e não há fase para isso.
- **Só a fila, sem as entregas** — deixaria de fora o acompanhamento do aviso terminal.

## Consequências

- **Positivas**: o operador distingue job parado de entrega que ainda será tentada.
- **Negativas ou riscos**: falha de máquina que não aparece na fila continua invisível nesta tela.
- **Impacto em futuros PRDs**: saúde de workers e revisão de conteúdo exigem capacidade nova.

## Termos e documentos afetados

- **Termos canônicos**: Fila
- **Vision/Domain Docs**: `domains/transcricao-e-jobs/domain.md`, `domains/entrega-de-notificacoes/domain.md`
- **PRDs relacionados**: `tasks/prd-operacao-do-servico/prd.md` (DP-03)

## Histórico

- 2026-10-12 — decidido no recorte da CAP-002
- 2026-10-13 — Accepted com a aprovação do PRD
