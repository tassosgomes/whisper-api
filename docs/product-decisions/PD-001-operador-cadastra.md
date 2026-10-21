# PD-001: O operador cadastra conta, chave e destino

- **Status**: Accepted
- **Escopo**: Domínio: Acesso e Contas
- **Data**: 2026-10-13
- **Responsável pela decisão**: Produto
- **Origem**: PRD CAP-002, decisão de 2026-10-12
- **Tags**: operador, conta, api-key, webhook
- **Substitui**: Não aplicável
- **Substituído por**: Não aplicável

## Contexto

O piloto já cria conta, API Key e destino de webhook, mas só pelo procedimento manual. A visão cita autoatendimento do cliente como evolução sem fase comprometida.

## Decisão

Nesta entrega, quem cadastra a conta, emite e administra a API Key e configura o destino de webhook é o operador do Whisper. O cliente não administra as próprias chaves nem o próprio destino.

## Alternativas consideradas

- **Autoatendimento do cliente** — exigiria identidade e permissão do consumidor antes de o operador ter a tela. Fica fora até uma capacidade nova.

## Consequências

- **Positivas**: o cliente passa a chamar a API e a receber o aviso sem um portal próprio.
- **Negativas ou riscos**: todo cadastro depende de quem opera o serviço.
- **Impacto em futuros PRDs**: autoatendimento não entra disfarçado de extensão deste console.

## Termos e documentos afetados

- **Termos canônicos**: Credencial operacional, distintos da API Key
- **Vision/Domain Docs**: `domains/acesso-e-contas/domain.md`, `domains/entrega-de-notificacoes/domain.md`
- **PRDs relacionados**: `tasks/prd-operacao-do-servico/prd.md` (DP-01)

## Histórico

- 2026-10-12 — decidido no recorte da CAP-002
- 2026-10-13 — Accepted com a aprovação do PRD
