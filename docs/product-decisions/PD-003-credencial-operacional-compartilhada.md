# PD-003: Credencial operacional compartilhada

- **Status**: Accepted
- **Escopo**: Domínio: Acesso e Contas
- **Data**: 2026-10-13
- **Responsável pela decisão**: Produto
- **Origem**: PRD CAP-002, decisão de 2026-10-12
- **Tags**: operador, credencial, console
- **Substitui**: Não aplicável
- **Substituído por**: Não aplicável

## Contexto

O console precisa de um acesso que não seja a API Key do cliente. A visão não descreve um cadastro de operadores nomeados.

## Decisão

Uma credencial operacional compartilhada autentica o console nesta entrega. Operadores nomeados ficam fora. A API Key do cliente não abre o console, e a credencial operacional não cria nem consulta jobs na API pública.

## Alternativas consideradas

- **Operadores nomeados, cada um com credencial própria** — exigiriam um cadastro que a visão não detalha.

## Consequências

- **Positivas**: o operador entra sem um diretório de usuários.
- **Negativas ou riscos**: quem possui a credencial vê a fila de todas as contas e pode emitir segredos. A trilha não identifica uma pessoa.
- **Impacto em futuros PRDs**: identidade de operador é capacidade nova, não um campo a mais nesta tela.

## Termos e documentos afetados

- **Termos canônicos**: Credencial operacional
- **Vision/Domain Docs**: `domains/acesso-e-contas/domain.md`
- **PRDs relacionados**: `tasks/prd-operacao-do-servico/prd.md` (DP-04)

## Histórico

- 2026-10-12 — decidido no recorte da CAP-002
- 2026-10-13 — Accepted com a aprovação do PRD
