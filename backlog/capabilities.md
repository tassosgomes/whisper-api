---
tsg_artifact: capability-backlog
product: whisper
version: 0.2
status: approved
updated: 2026-10-21
sources: vision.md@0.2, context/domain-map.md@0.1, context/architecture-baseline.md@0.2
---

# Backlog de Capacidades

## Estratégia de Sequenciamento

O MVP é a transcrição assíncrona de máquina a máquina. Ele já tem PRD aprovado e implementação no repositório. A capacidade seguinte é a operação do serviço pelo operador do Whisper: cadastrar conta, API Key e destino de webhook, e acompanhar a fila e as entregas.

A visão cita console de autoatendimento e outros idiomas como evoluções sem fase comprometida. Este backlog não lhes dá ID. Entram de novo só quando houver decisão de as sequenciar. Saúde de workers também fica fora do horizonte: não foi incluída no recorte confirmado em 2026-10-12.

Em 2026-10-21 o upload direto deixou de ser evolução sem fase: a visão v0.2 o compromete, junto do aplicativo desktop para Windows. Os dois formam uma só capacidade, CAP-004, na Fase 3. Não há capacidade separada para o upload, porque ele não tem valor de negócio sem um consumidor e o contrato é definido junto do app.

A divergência em relação à visão está em CAP-002. A visão não comprometia fase nem sequência para o console. O backlog a coloca na Fase 2 porque o MVP já entrega a API e o operador ainda cumpre o mesmo trabalho no procedimento manual. O baseline não muda de unidade de deploy: a tela entra no monólito modular já definido, sem serviço novo.

## Definição do MVP

O MVP é CAP-001. Cobre autenticar uma conta, criar um job, obter a mídia, transcrever, avisar o término e permitir a busca do resultado por 24 horas. O operador desse piloto provisiona chaves e destino fora da tela.

CAP-002 não é MVP. Depende da conta, do job e do aviso terminal que CAP-001 já estabelece.

CAP-004 também não é MVP. Reaproveita o job, o resultado e a API Key de CAP-001 e CAP-002.

## Capacidades por Domínio

### Domínio: Transcrição e Jobs

#### CAP-001 — Transcrição assíncrona sob demanda

**Objetivo:** Um sistema cliente solicita uma transcrição, acompanha o job, recebe o aviso terminal e busca o resultado durante a retenção, sem manter a conexão aberta durante o processamento.

**Valor de negócio:** O cliente integra transcrição sem incorporar motor, fila nem o modelo de conteúdo do Whisper.

**Resumo do fluxo:** A chamada autenticada informa uma referência de mídia. O serviço obtém o arquivo, transcreve, publica o estado e um aviso terminal, e entrega o resultado versionado por até 24 horas após o término. O cliente associa esse resultado ao próprio conteúdo.

**Dependências:** nenhuma

**Prioridade:** Alta

**Fase:** MVP

PRD já aprovado: `tasks/prd-api-transcricao-assincrona/prd.md`. CAP-002 também lê o estado e a idade da fila deste domínio, sem passar a ser dono do job.

### Domínio: Acesso e Contas

#### CAP-002 — Operação de contas, webhooks e fila

**Objetivo:** O operador do Whisper cadastra uma conta cliente, emite e administra a API Key, configura o destino de webhook e acompanha a fila de jobs e as falhas de entrega.

**Valor de negócio:** O operador coloca um cliente em condições de chamar a API e de receber o aviso terminal, e percebe fila parada ou entrega falha sem consultar o banco nem o procedimento de terminal.

**Resumo do fluxo:** O operador entra com credencial própria, distinta da API Key. Cria a conta, emite a chave e vê o segredo uma única vez. Configura um destino de webhook por conta e vê o material de verificação uma única vez. Consulta metadados, emite outra chave, rotaciona, revoga e desativa o destino. Na mesma tela, vê o tamanho e a idade da fila, o estado dos jobs ainda retidos e as tentativas e falhas de webhook, sem mídia nem texto da transcrição.

**Dependências:** CAP-001

**Prioridade:** Alta

**Fase:** Fase 2

Esta capacidade atravessa os três domínios. Acesso e Contas é dono da conta e da credencial. Entrega de Notificações é dono do destino, do material de verificação e das tentativas. Transcrição e Jobs é dono do job e da fila. A tela não cria um domínio novo.

### Cliente: Gravação de reuniões (fora do Domain Map do serviço)

#### CAP-004 — Gravador de reuniões para Windows

**Objetivo:** Uma pessoa grava uma reunião pelos dispositivos de áudio do próprio PC, envia o áudio direto à API, acompanha o job e fica com o áudio e a transcrição (.txt) guardados no computador.

**Valor de negócio:** Leva o Whisper a quem participa da reunião, sem depender de Zoom, Teams ou de um sistema cliente que integre a API, e sem enviar áudio antes de o usuário decidir.

**Resumo do fluxo:** O usuário grava microfone e áudio do sistema, vê o aviso de gravação de terceiros e salva a gravação localmente. Ao pedir o envio, o app manda o arquivo direto à API, que cria o mesmo job de CAP-001. O app consulta o job até o estado terminal, busca o resultado, guarda o texto em .txt ao lado do áudio e avisa o usuário. A cópia local sobrevive às 24 horas de retenção do serviço. Edição de áudio e de transcrição é feature futura.

**Dependências:** CAP-001 e CAP-002 (a API Key é emitida pelo operador)

**Origem na visão:** a visão v0.2 não numera capacidades próprias. Deriva do recorte de 2026-10-21 e dos objetivos "envio direto de áudio" e "cliente desktop".

**Prioridade:** Média

**Fase:** Fase 3. A visão v0.1 não dava fase ao upload direto; a v0.2 o compromete, e o app é seu primeiro consumidor. A fase vem depois de CAP-002 porque a API Key do usuário é emitida pelo operador nessa tela, e o piloto atual continua operável pelo procedimento manual.

PRD aprovado: `tasks/prd-gravador-de-reunioes-desktop/prd.md`. A capacidade atravessa dois domínios do serviço e um cliente novo. Transcrição e Jobs passa a aceitar o arquivo direto e continua dono do job e do resultado. Acesso e Contas continua dono da API Key. O app é dono da gravação e da cópia local, sem dado do serviço. O Domain Map não prevê cliente desktop; ver Riscos Estratégicos.

### Domínio: Entrega de Notificações

Nenhuma capacidade adicional neste horizonte. CAP-001 já emite o aviso terminal. CAP-002 configura o destino e acompanha tentativas e falhas. As regras desse acompanhamento estão no domain doc deste domínio.

## Dependências entre Capacidades

CAP-002 depende de CAP-001. Sem conta, job e aviso terminal, a tela não tem o que cadastrar nem o que mostrar. CAP-001 não depende de CAP-002: o piloto continua operável pelo procedimento manual até a tela existir.

CAP-004 depende de CAP-001, porque usa job, resultado e retenção, e de CAP-002, porque o operador emite a API Key do usuário. Nenhuma outra capacidade depende de CAP-004. O contrato de upload direto sai dela e fica reaproveitável.

## Riscos Estratégicos

| Risco | Dono | Consequência | Efeito no baseline |
|---|---|---|---|
| A visão não comprometia o console; o backlog o fixa na Fase 2 a pedido de 2026-10-12. | Produto | Se a Fase 2 for adiada, o operador permanece no procedimento manual. CAP-001 não é desfeita. | Nenhum. A tela permanece no monólito modular. Não há serviço novo nem reagrupamento de deploy. |
| O operador passa a ver jobs de todas as contas. | Produto e segurança | Uma credencial operacional comprometida enxerga a fila inteira. | Continua valendo o menor privilégio do baseline: credencial distinta da API Key, trilha sem conteúdo e sem expor mídia, transcrição ou segredo. |
| Outros idiomas, autoatendimento do cliente e saúde de workers continuam sem fase. | Produto | Podem ser pedidos de novo como se já estivessem no console. | Nenhum até uma capacidade nova ser aceita. |
| A visão v0.1 não comprometia upload direto; o backlog o inclui em CAP-004, Fase 3, a pedido de 2026-10-21. A visão v0.2 foi atualizada. | Produto | Se a Fase 3 for adiada, o piloto segue só com URL assinada. CAP-001 e CAP-002 não são desfeitas. | O upload entra no monólito modular, sem serviço novo. Aumenta o volume de bytes recebidos pela API, que o baseline hoje só dimensiona para URL assinada. |
| O app desktop é um cliente novo que o Domain Map do serviço não cobre. | Produto e arquitetura | Resolvido em 2026-10-21: o baseline v0.2 ganhou a seção "Clientes do Whisper" e a ADR-005 fixou .NET/WPF em `clients/`. | O cliente é consumidor externo da API, sem domínio nem unidade de deploy do serviço. |
| O app grava terceiros que podem não saber da gravação. | Produto e jurídico | Exposição a consentimento e LGPD. | Nenhum. O aviso e o indicador de gravação são requisitos do PRD (RF-03). |
