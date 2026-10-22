---
tsg_artifact: prd
product: whisper
version: 0.1
status: approved
updated: 2026-10-21
capability: CAP-002
sources: backlog/capabilities.md@0.2, vision.md@0.2, context/domain-map.md@0.1, context/architecture-baseline.md@0.2, domains/acesso-e-contas/domain.md@0.1, domains/transcricao-e-jobs/domain.md@0.2, domains/entrega-de-notificacoes/domain.md@0.1
---

# Operação de contas, webhooks e fila

## Visão Geral

O operador do Whisper ainda prepara cada cliente e investiga fila e webhook fora de uma tela. Esta entrega dá a ele esse trabalho num console: criar a conta, emitir e administrar a API Key, configurar o destino de webhook e ver a fila e as falhas de entrega.

Quem usa é o operador do serviço, não o sistema cliente e não o usuário final desse cliente. O valor é colocar um cliente em condições de chamar a API e de receber o aviso terminal, e perceber job parado ou entrega falha sem abrir o procedimento manual nem o conteúdo da transcrição.

## Rastreabilidade

### Capacidade e fronteiras

- **Capacidade:** CAP-002 — Operação de contas, webhooks e fila
- **Escopo desta entrega:** a capacidade inteira. O operador cria a conta, emite e administra API Keys, configura e administra um destino de webhook por conta, vê tamanho e idade da fila, o estado dos jobs ainda retidos e as tentativas e falhas de entrega.
- **Fora desta entrega:** autoatendimento do cliente, saúde de workers, upload direto e idiomas além de pt-BR. Não há fase seguinte já comprometida para esses itens.
- **Domínios atravessados:** Acesso e Contas (`domains/acesso-e-contas/domain.md`), Transcrição e Jobs (`domains/transcricao-e-jobs/domain.md`), Entrega de Notificações (`domains/entrega-de-notificacoes/domain.md`).
- **Junta entre os domínios:** Acesso e Contas é dono da conta e da credencial e autoriza a operação. Transcrição e Jobs é dono do job, da fila e do resultado. Entrega de Notificações é dono do destino, do material de verificação e das tentativas. Falha de entrega não altera o job. O Domain Map não nomeia o operador; a visão o nomeia, e esta entrega o trata como perfil que lê dados dos três donos sem transferir a propriedade.
- **Dependências entre capacidades:** CAP-002 depende de CAP-001. A API assíncrona, a conta, o job e o aviso terminal já existem.
- **Restrições do baseline:** credencial de máquina distinta do privilégio operacional; segredo só na apresentação; trilha sem conteúdo; isolamento do cliente por conta e por credencial; falha de notificação não altera o job; a tela permanece no monólito modular, sem serviço novo.

### Vision Doc

- **Objetivos de negócio atendidos:** administrar o acesso de clientes e acompanhar a execução e a entrega, que é o valor esperado do operador na visão.
- **Restrições globais aplicáveis:** premissas de piloto já fixadas em CAP-001 (até duas horas, pt-BR, retenção terminal de 24 horas, volume inicial de até dez jobs por dia). Esta entrega não redefine essas premissas.
- **Non-Goals globais respeitados:** sem arquivo permanente, sem curso ou aula, sem busca textual, sem experiência de revisão para o usuário do cliente.

### Domain Docs

- **Entidades envolvidas:** Conta cliente, Credencial de API e Credencial operacional (Acesso e Contas); Job, Fila e Referência de mídia, esta última só para não ser exibida (Transcrição e Jobs); Destino, Material de verificação, Aviso terminal e Tentativa de entrega (Entrega de Notificações).
- **Regras de negócio referenciadas:** Acesso e Contas RN-01 a RN-10; Transcrição e Jobs RN-01 a RN-08; Entrega de Notificações RN-01 a RN-13.
- **Regras nascidas neste PRD:** nenhuma. As regras novas deste recorte já estão nos domain docs em draft.
- **Eventos consumidos:** `transcricao.job-terminal`, consumido por Entrega de Notificações para iniciar o aviso.
- **Eventos produzidos:** `acesso.conta-criada`, `acesso.credencial-emitida`, `acesso.credencial-rotacionada`, `acesso.credencial-revogada`, `notificacao.destino-configurado`, `notificacao.destino-desativado`, `notificacao.entrega-concluida`, `notificacao.entrega-falhou`. A tela observa as consequências desses fatos; não cria um fato paralelo de apresentação.

## Termos Canônicos

| Termo | Definição de negócio | Escopo/Fonte |
|---|---|---|
| Credencial operacional | Acesso do operador ao console. Não é API Key e não autoriza chamada de máquina. | Acesso e Contas, RN-09 |
| Fila | Jobs que ainda não estão completed nem failed. Tamanho é a quantidade desses jobs. Idade é o tempo desde a aceitação do mais antigo entre eles. | Transcrição e Jobs, RN-06 |
| Material de verificação | Segredo que o cliente usa para confirmar a origem do aviso. Independente da API Key. | Entrega de Notificações, RN-03 |

## Objetivos

- O operador cadastra uma conta, a primeira API Key e o destino de webhook na tela, e copia os dois segredos na única vez em que aparecem.
- O operador revoga uma chave comprometida e desativa um destino sem recorrer ao procedimento manual.
- O operador identifica o job não terminal mais antigo e uma entrega que falhou e ainda será tentada de novo, sem ver mídia nem transcrição.

## Histórias de Usuário

- Como operador do Whisper, quero criar uma conta e emitir uma API Key para que o cliente passe a chamar o serviço com um segredo que só vejo uma vez.
- Como operador do Whisper, quero configurar o destino de webhook da conta e entregar o material de verificação para que o cliente receba o aviso terminal no endereço certo.
- Como operador do Whisper, quero rotacionar ou revogar uma chave, e trocar, rotacionar ou desativar o destino, para interromper acesso ou entrega comprometidos.
- Como operador do Whisper, quero ver a fila e as falhas de webhook de todas as contas para saber se o trabalho está parado ou se o aviso não chegou, sem abrir a transcrição.

## Funcionalidades Principais

### RF-01: Acesso do operador

**Descrição:** Só a credencial operacional abre o console. Ela é compartilhada por quem opera o serviço nesta entrega. A API Key do cliente não serve para entrar, e a credencial operacional não serve para criar ou consultar jobs na API.

**Critérios de Aceitação:**

- **Given** alguém sem credencial operacional, **When** tenta abrir o console, **Then** não vê contas, credenciais, destinos, fila nem tentativas.
- **Given** uma API Key válida de cliente, **When** alguém tenta usá-la para abrir o console, **Then** o acesso é recusado e nenhum dado operacional aparece.
- **Given** a credencial operacional, **When** o operador entra, **Then** alcança contas, destinos e fila, e essa mesma credencial é recusada na API pública de transcrição.
- **Given** uma ação de criar conta, emitir, rotacionar ou revogar credencial, configurar, trocar, rotacionar ou desativar destino, **When** ela termina, **Then** fica registrado o momento, o operador, a conta e o objeto afetado, sem segredo, mídia, transcrição ou URL de origem.

**Prioridade:** Must Have

**Rastreabilidade:** Acesso e Contas RN-09, RN-10

### RF-02: Conta cliente

**Descrição:** O operador cria uma conta cliente com nome que a distinga das demais. A conta passa a poder receber credenciais e um destino.

**Critérios de Aceitação:**

- **Given** o operador autenticado, **When** informa o nome de uma conta nova, **Then** a conta fica disponível para emissão de credencial e configuração de destino.
- **Given** duas contas, **When** o operador as lista, **Then** distingue uma da outra pelo nome e não vê segredo de chave nem de webhook.
- **Given** um nome vazio, **When** o operador tenta criar a conta, **Then** a conta não é criada.

**Prioridade:** Must Have

**Rastreabilidade:** Acesso e Contas RN-01, RN-09

### RF-03: API Key

**Descrição:** O operador emite a primeira credencial e credenciais adicionais da conta, consulta metadados, rotaciona e revoga. As permissões iniciais continuam as do piloto: criar jobs e ler estado e resultado apenas dos jobs da própria credencial. O segredo aparece uma única vez.

**Critérios de Aceitação:**

- **Given** uma conta sem credencial, **When** o operador emite a primeira, **Then** o segredo aparece uma única vez, com as permissões iniciais do piloto, e a conta dona fica identificada.
- **Given** o segredo já exibido, **When** o operador abre de novo essa credencial, **Then** vê identificador, conta, permissões, criação, último uso e situação de revogação ou rotação, e não vê o segredo.
- **Given** uma credencial cujo segredo se perdeu, **When** o operador tenta recuperá-lo, **Then** o console não o reexibe.
- **Given** uma conta que já tem credencial, **When** o operador emite outra, **Then** a nova tem identificador próprio e as anteriores continuam válidas.
- **Given** uma credencial ativa com jobs já criados por ela, **When** o operador rotaciona, **Then** o identificador permanece, o segredo novo aparece uma única vez, a chave substituída deixa de autorizar chamadas, os jobs já criados continuam associados a essa credencial e o material de webhook não muda.
- **Given** uma credencial ativa, **When** o operador a revoga informando o motivo, **Then** chamadas com ela passam a ser recusadas sem revelar job, o segredo não reaparece e a trilha guarda o motivo.
- **Given** uma revogação sem motivo, **When** o operador confirma, **Then** a credencial não é revogada.

**Prioridade:** Must Have

**Rastreabilidade:** Acesso e Contas RN-02, RN-03, RN-04, RN-05, RN-06, RN-07, RN-08, RN-10

### RF-04: Destino de webhook

**Descrição:** O operador configura um destino por conta, troca só o endereço, rotaciona o material de forma planejada ou imediata e desativa o destino. Vale o mesmo critério de aceite de endereço já usado no piloto. O material aparece uma única vez e é independente da API Key.

**Critérios de Aceitação:**

- **Given** uma conta sem destino, **When** o cliente usa uma API Key válida, **Then** a chamada é aceita e nenhum aviso terminal é enviado.
- **Given** um endereço que o piloto já aceitaria, **When** o operador o confirma para a conta, **Then** esse passa a ser o único destino, o material de verificação aparece uma única vez e os jobs terminais seguintes usam esse destino.
- **Given** um endereço que o piloto recusaria, **When** o operador tenta gravá-lo, **Then** nada é gravado e um destino anterior, se existir, permanece.
- **Given** um destino já configurado, **When** o operador troca só o endereço por outro aceito, **Then** o material atual permanece, não é reexibido e as entregas seguintes usam o endereço novo.
- **Given** uma rotação planejada, **When** ela termina, **Then** o material novo aparece uma única vez e o anterior continua válido por 72 horas.
- **Given** comprometimento do material, **When** o operador rotaciona de imediato e informa o motivo, **Then** o material anterior deixa de valer na hora e o novo aparece uma única vez.
- **Given** uma rotação imediata ou uma desativação sem motivo, **When** o operador confirma, **Then** o material e o destino não mudam.
- **Given** um destino ativo, **When** o operador o desativa informando o motivo, **Then** novas entregas cessam e os materiais ativos deixam de valer.
- **Given** uma rotação de API Key da mesma conta, **When** ela termina, **Then** o destino e o material de webhook permanecem como estavam.
- **Given** uma conta que já tem destino, **When** o operador tenta gravar um segundo destino sem substituir o primeiro, **Then** a conta continua com um só destino.

**Prioridade:** Must Have

**Rastreabilidade:** Entrega de Notificações RN-01, RN-02, RN-03, RN-07, RN-08, RN-09, RN-10, RN-11; Acesso e Contas RN-06

### RF-05: Fila de jobs

**Descrição:** O operador vê, de todas as contas, quantos jobs ainda não terminaram, a idade do mais antigo entre eles e a lista dos jobs ainda dentro da retenção. A lista não é o resultado nem a mídia.

**Critérios de Aceitação:**

- **Given** jobs não terminais em mais de uma conta, **When** o operador abre a fila, **Then** o tamanho é a quantidade desses jobs e a idade é o tempo desde a aceitação do mais antigo.
- **Given** nenhum job não terminal, **When** o operador abre a fila, **Then** o tamanho é zero e não há idade de item mais antigo.
- **Given** jobs ainda dentro de 24 horas após o estado terminal, **When** o operador abre a lista, **Then** cada um mostra conta, identificador, estado, horários disponíveis e a referência opaca se o cliente a enviou.
- **Given** um job terminal há mais de 24 horas, **When** o operador abre a lista, **Then** esse job não aparece.
- **Given** qualquer job na lista, **When** o operador o examina, **Then** não vê mídia, texto da transcrição, URL de origem nem segredo.
- **Given** uma API Key de cliente, **When** o cliente consulta jobs, **Then** continua vendo só os jobs criados por essa credencial, na própria conta.

**Prioridade:** Must Have

**Rastreabilidade:** Transcrição e Jobs RN-01, RN-03, RN-04, RN-06, RN-07, RN-08

### RF-06: Falhas e retentativas de webhook

**Descrição:** O operador acompanha tentativas e falhas sem confundir isso com o estado do job. Vê se ainda haverá nova tentativa e se o destino está ativo. Não vê material de verificação nem corpo do aviso.

**Critérios de Aceitação:**

- **Given** uma entrega que falhou e ainda será tentada, **When** o operador abre as notificações, **Then** vê a conta, o job, o identificador do evento, a falha e que haverá nova tentativa.
- **Given** essa falha ainda em retentativa, **When** o operador olha o job, **Then** o estado continua completed ou failed, conforme o processamento que originou o aviso.
- **Given** uma entrega que falhou de forma definitiva, **When** o operador a consulta, **Then** vê a falha encerrada e o job permanece no estado do processamento.
- **Given** uma tentativa, **When** o operador a examina, **Then** não vê material de verificação, corpo do aviso nem URL de origem da mídia.
- **Given** o destino foi desativado pelo operador ou deixou de estar ativo pela resposta do receptor, **When** o operador consulta a conta, **Then** vê que o destino não está ativo.
- **Given** uma tentativa que o serviço ainda guarda depois de o metadado do job ter saído da retenção, **When** o operador consulta as notificações, **Then** ainda vê a tentativa identificada pelo job, sem mídia, texto ou URL de origem.

**Prioridade:** Must Have

**Rastreabilidade:** Entrega de Notificações RN-04, RN-05, RN-06, RN-12, RN-13; Transcrição e Jobs RN-05

## Experiência do Usuário

O operador entra com a credencial operacional. Na preparação de um cliente, cria a conta, emite a chave, copia o segredo na hora e configura o destino, copiando o material de verificação na hora. As duas cópias são o momento de entrega ao cliente; o console não volta a mostrá-las.

No dia a dia, o operador olha o resumo da fila e a lista de jobs retidos. Se uma entrega falhou, abre as tentativas daquela conta ou daquele job e vê se ainda há retentativa, sem sair do contexto do job. Revogar chave, rotacionar material por comprometimento e desativar destino pedem motivo antes de confirmar.

A tela não reproduz mídia, não mostra o texto da transcrição e não oferece revisão de conteúdo. Não há descoberta para o usuário final do cliente: ele não tem acesso a este console.

## Decisões de Produto

| ID | Decisão confirmada | Alternativas descartadas e motivo | Impacto no PRD | Registro |
|---|---|---|---|---|
| DP-01 | O operador cadastra conta, API Key e destino. O cliente não administra as próprias chaves. | Autoatendimento do cliente nesta entrega aumentaria identidade e permissão do consumidor antes de o operador ter a tela. | RF-01 a RF-04; non-goal de autoatendimento | [PD-001](../../docs/product-decisions/PD-001-operador-cadastra.md) |
| DP-02 | A tela administra o ciclo já existente da chave e do destino: emitir outra chave, consultar metadados, rotacionar, revogar, trocar endereço, rotação planejada, rotação imediata e desativar. | Deixar rotação e revogação no procedimento manual manteria a ação de contenção fora da tela que passa a criar o segredo. | RF-03 e RF-04 | Herdado do runbook do piloto |
| DP-03 | A visão operacional é a fila de jobs mais falhas e retentativas de webhook. | O painel completo, com saúde de workers, não foi escolhido. Só a fila, sem as entregas, deixaria de fora o acompanhamento que a decisão pediu. | RF-05 e RF-06 | [PD-002](../../docs/product-decisions/PD-002-fila-e-entregas.md) |
| DP-04 | Uma credencial operacional compartilhada autentica o console nesta entrega. | Usuários nomeados exigiriam um cadastro de operadores que a visão não detalha. | RF-01 | [PD-003](../../docs/product-decisions/PD-003-credencial-operacional-compartilhada.md) |
| DP-05 | A capacidade sai inteira neste PRD. O contrato administrativo e a tela são o mesmo valor de CAP-002. | Especificar só um contrato sem tela, ou só uma tela sem o comportamento de cadastro, não completa o ciclo do operador. A ordem interna de construção fica para a especificação técnica. | Escopo da entrega | Reconcilia a separação anterior entre contrato administrativo e frontend |

## Restrições Técnicas de Alto Nível

- Permissões iniciais das chaves, isolamento por credencial, um destino por conta, validade de 72 horas na rotação planejada do material e retenção de 24 horas do job vêm do piloto e não são reabertos aqui.
- O critério que aceita ou recusa um destino continua o do piloto, inclusive a exceção já decidida para destinos privados confiáveis.
- Segredo, material de verificação, mídia, transcrição e URL de origem não aparecem em trilha, erro apresentado ao operador nem na lista operacional, fora da apresentação única do segredo e do material.
- A idade da fila é observação. Não substitui meta de início ou de conclusão, que continuam em aberto em CAP-001.

## Não-Objetivos (Fora de Escopo)

- Console em que o cliente cria, rotaciona ou revoga as próprias chaves ou troca o próprio destino.
- Saúde de workers, uso de CPU ou memória, e alerta operacional além do que a fila e as tentativas já mostram.
- Exibir, buscar ou revisar mídia e texto da transcrição.
- Upload direto, outros idiomas, armazenamento permanente e conceitos do cliente como curso ou aula.
- Cadastro de vários operadores nomeados, cada um com credencial própria.
- Redefinir permissões por chave, retenção, formato do resultado ou política de retentativa do aviso.

## Métricas de Sucesso

| Métrica | Definição | Meta e prazo |
|---|---|---|
| Cadastro pela tela | Conta nova com primeira API Key e destino ativo, com os dois segredos copiados na apresentação única | A primeira conta criada depois da entrega é feita pela tela, sem o procedimento manual |
| Contenção pela tela | Revogação de chave e desativação de destino concluídas com motivo e trilha | Na primeira ocorrência depois da entrega, as duas ações são feitas pela tela |
| Fila e entrega visíveis | Operador aponta o job não terminal mais antigo e uma falha de webhook que ainda será tentada | Na primeira verificação operacional depois da entrega, os dois fatos são lidos na tela |

## Riscos e Mitigações

- **Credencial operacional compartilhada:** quem a possui vê a fila de todas as contas e pode emitir segredos. Mitigação: é distinta da API Key, não mostra conteúdo e deixa trilha. Operadores nomeados ficam fora desta entrega.
- **Segredo copiado para um canal inseguro:** a tela só consegue mostrá-lo uma vez. Mitigação: a apresentação deixa claro que não haverá segunda exibição e que perda implica rotação.
- **Fila do piloto é pequena:** até dez jobs por dia não exercita volume. Mitigação: tamanho e idade continuam definidos do mesmo modo quando o volume crescer; esta entrega não promete meta de conclusão.
