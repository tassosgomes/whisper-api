---
tsg_artifact: prd
product: whisper
version: 1.1
status: approved
updated: 2026-10-21
capability: CAP-004
sources: vision.md@0.2, context/domain-map.md@0.1, context/architecture-baseline.md@0.2, backlog/capabilities.md@0.2, domains/transcricao-e-jobs/domain.md@0.2, domains/acesso-e-contas/domain.md@0.1
---

# Gravador de reuniões para Windows

## Visão Geral

Quem quer transcrever uma reunião hoje precisa gravá-la por outro meio, converter o arquivo e entregá-lo a um sistema que chame a API. O Whisper não tem uma ponta voltada à pessoa que participa da reunião.

Esta entrega cria um aplicativo desktop para Windows 11 que grava a reunião usando os dispositivos de áudio do próprio PC: o microfone e o áudio que sai do computador. Como captura o que o PC ouve e fala, funciona com qualquer ferramenta de reunião e não tem integração com Zoom, Teams ou similares. Depois da gravação, o usuário escolhe se envia o áudio à API do Whisper e acompanha o processamento. A transcrição volta ao app e é guardada no PC como arquivo de texto, ao lado do áudio.

A responsabilidade inicial do app é gravar, enviar o áudio, obter o retorno da API e guardar áudio e texto no computador do usuário. Edição do áudio e da transcrição é feature futura.

Quem usa é a pessoa que participa da reunião, na própria máquina. O valor é ir da reunião ao texto sem sair do PC, sem depender de plataforma de videoconferência e sem enviar nada antes de o usuário decidir.

## Rastreabilidade

### Capacidade e fronteiras

- **Capacidade:** CAP-004 — Gravador de reuniões para Windows. Registrada em `backlog/capabilities.md` v0.2 (Fase 3).
- **Escopo desta entrega:** a capacidade inteira em versão mínima: gravar microfone e áudio do sistema, gerenciar as gravações locais, enviar uma gravação à API, acompanhar o job e guardar a transcrição no PC como .txt. Inclui o **recebimento direto de áudio pela API** (RF-10), que o app exige e cujo contrato passa a ser definido por esta capacidade (DP-05). Um usuário e uma conta Whisper por instalação, sem multiusuário.
- **Fora desta entrega:** edição de áudio e da transcrição (feature futura, sem fase definida), identificação de quem fala, resumo, busca entre transcrições, sincronização entre máquinas, outros sistemas operacionais, atualização automática e envio automático ao terminar de gravar.
- **Domínios atravessados:**
  - Transcrição e Jobs (`domains/transcricao-e-jobs/domain.md`): o app é cliente do job e do resultado, e a API passa a aceitar o arquivo direto (RF-10).
  - Acesso e Contas (`domains/acesso-e-contas/domain.md`): o app autentica com a API Key da conta.
  - **Gravação de reuniões** não tem domain doc. Rende um único PRD, e as regras RN-G01 a RN-G08 nascem aqui.
- **Junta entre os domínios:** o Whisper continua dono do job e do resultado temporário (24 horas). O app é dono da gravação e da cópia local da transcrição, e, portanto, da guarda permanente. É o que a visão chama de "o cliente decide se e como guardará o resultado".
- **Dependências entre capacidades:** CAP-001 (job assíncrono e resultado por 24 horas, `done`) e CAP-002 (o operador emite a API Key, `done`). O fluxo de URL assinada de CAP-001 não muda.
- **Restrições do baseline:** a API Key é a única credencial de máquina; resultado e metadados expiram 24 horas após o estado terminal; mídia, transcrição e segredos não aparecem em logs, métricas ou mensagens de erro.

### Vision Doc

- **Objetivos de negócio atendidos:** oferecer o fluxo de transcrição a um novo tipo de consumidor, sem acoplar o processamento ao motor nem ao conteúdo do cliente.
- **Restrições globais aplicáveis:** pt-BR; mídia de até duas horas; retenção de 24 horas no serviço; o processamento permanece sob controle do Whisper.
- **Non-Goals globais respeitados:** o serviço não guarda a transcrição de forma permanente nem a indexa. A cópia local é do usuário, no PC dele.
- **Tensão com a visão, a resolver (Q-01):** a visão trata "upload direto" como evolução sem compromisso e deixa fora do escopo "apresentar uma experiência de revisão de mídia e transcrição como produto final ao usuário do cliente". Esta entrega inclui o upload direto e é um cliente do Whisper que exibe e exporta o texto, sem player sincronizado, edição nem busca. A visão v0.2 e o backlog v0.2 refletem isso.

### Domain Docs

- **Entidades envolvidas:** Job e Resultado (Transcrição e Jobs); conta e credencial de API (Acesso e Contas). Nascem aqui: Gravação e Transcrição local.
- **Regras de negócio referenciadas:** Transcrição e Jobs RN-01 (estados downloading, queued, processing, completed e failed), RN-03 (resultado por até 24 horas), RN-04 (job de outra conta ou expirado não é revelado), RN-07 (o operador não vê mídia nem texto).
- **Regras nascidas neste PRD:** RN-G01 a RN-G08.
- **Eventos consumidos:** nenhum. O app não recebe webhook e acompanha o job por consulta.
- **Eventos produzidos:** nenhum.

## Termos Canônicos

| Termo | Definição de negócio | Escopo/Fonte |
|---|---|---|
| Gravação | Áudio de uma reunião capturado pelo app e guardado no PC, com título, data, duração e situação. Existe antes e independentemente de qualquer envio. | Esta entrega |
| Áudio do sistema | O som que o PC reproduz, ou seja, a voz dos demais participantes de uma reunião online. | Esta entrega |
| Microfone | O dispositivo de entrada que capta a voz do usuário. | Esta entrega |
| Envio | Ato explícito do usuário de mandar uma gravação à API para transcrição. Uma gravação pode ter mais de um envio. | Esta entrega |
| Transcrição local | Cópia do resultado de um job, guardada no PC como arquivo de texto (.txt) ligado à gravação. Sobrevive à expiração de 24 horas do serviço. | Esta entrega |
| Job | Trabalho de transcrição acompanhado até um estado terminal. | Domain doc de Transcrição e Jobs |
| Resultado | Representação versionada da transcrição concluída, disponível por 24 horas. | Domain doc de Transcrição e Jobs |

---

## Objetivos

- O usuário grava uma reunião inteira, com a voz própria e a dos demais participantes, sem configurar nada além da escolha de dispositivos.
- O usuário recebe a transcrição de uma gravação sem sair do app e sem passos manuais de conversão ou upload.
- Nenhum áudio sai do PC antes de o usuário pedir o envio.
- Uma gravação nunca é perdida por causa de falha no envio, na rede ou na API.
- A transcrição continua acessível depois das 24 horas de retenção do serviço.

## Histórias de Usuário

- Como **participante de reuniões**, quero gravar o áudio da reunião, incluindo as outras pessoas, para que eu tenha um registro sem depender do Zoom, do Teams ou de outra ferramenta.
- Como **participante de reuniões**, quero ouvir e conferir a gravação antes de enviá-la para que eu decida se ela deve ser transcrita.
- Como **participante de reuniões**, quero enviar a gravação e acompanhar o andamento para saber quando a transcrição estará pronta, mesmo se eu fechar o app nesse meio-tempo.
- Como **participante de reuniões**, quero ler, copiar e exportar a transcrição para usá-la em atas, e-mails ou outros documentos.
- Como **participante de reuniões**, quero que áudio e texto fiquem guardados no meu PC para consultá-los semanas depois.
- Como **responsável por dados sensíveis da reunião**, quero que nada seja enviado sem minha ação, e poder apagar gravação e transcrição do PC.
- Como **desenvolvedor de um sistema cliente**, quero enviar o arquivo de áudio direto à API, sem hospedá-lo para gerar uma URL assinada.

---

## Funcionalidades Principais

### RF-01: Configurar o acesso à API

**Descrição:** O usuário informa o endereço da API do Whisper e a API Key da conta nas configurações. O app confirma que a chave é aceita antes de salvar. A chave é um segredo: não é exibida de novo por inteiro e não fica visível a outros usuários do Windows.

**Critérios de Aceitação:**

- **Given** o app recém-instalado, **When** o usuário abre a tela de gravações, **Then** ele pode gravar sem configurar a API e é informado de que precisa configurá-la apenas para enviar.
- **Given** endereço e chave válidos, **When** o usuário salva, **Then** o app indica que o acesso foi confirmado e passa a permitir o envio.
- **Given** uma chave inválida, revogada ou de conta desativada, **When** o usuário tenta salvar ou enviar, **Then** o app informa em linguagem simples que a chave não foi aceita e não envia áudio.
- **Given** a API fora do ar ou sem rede, **When** o usuário tenta salvar, **Then** o app informa que não conseguiu verificar e permite tentar de novo, sem tratar isso como chave inválida.
- **Given** uma chave salva, **When** o usuário abre as configurações, **Then** vê só os últimos caracteres, e pode substituí-la ou removê-la.

**Prioridade:** Must Have

**Rastreabilidade:** Acesso e Contas (API Key por conta); RN-G07

---

### RF-02: Escolher dispositivos e gravar

**Descrição:** O usuário escolhe o microfone e inicia a gravação. Por padrão o app também captura o áudio do sistema, de modo que a gravação contenha os dois lados da conversa. O usuário pode desligar uma das duas fontes. O app usa os dispositivos padrão do Windows e permite trocar. Antes de gravar, mostra um indicador de nível para cada fonte, para o usuário verificar que há som. Ao iniciar, exibe o aviso de gravação de terceiros (RF-03).

**Critérios de Aceitação:**

- **Given** microfone e saída de áudio disponíveis, **When** o usuário inicia, **Then** a gravação começa com as duas fontes ligadas, e um contador de tempo e o nível de cada fonte ficam visíveis.
- **Given** uma reunião online em andamento em qualquer ferramenta, **When** o usuário grava, **Then** a gravação contém a voz dele e a dos demais participantes.
- **Given** o usuário desligou o áudio do sistema, **When** grava, **Then** só o microfone é capturado e isso fica visível na gravação.
- **Given** as duas fontes desligadas, **When** o usuário tenta iniciar, **Then** o app não inicia e explica que é preciso escolher ao menos uma.
- **Given** nenhum microfone disponível ou acesso ao microfone negado pelo Windows, **When** o usuário tenta gravar, **Then** o app explica o problema e como liberar o acesso, e permite gravar só o áudio do sistema se houver saída disponível.
- **Given** o dispositivo escolhido some durante a gravação (fone desconectado), **When** isso ocorre, **Then** a gravação não se perde: o app avisa, continua com o que estiver disponível e preserva o já gravado.
- **Given** uma gravação em curso, **When** o usuário pausa e retoma, **Then** o tempo parado não entra na duração nem no áudio.
- **Given** uma gravação em curso, **When** o usuário clica em parar, **Then** ela é salva localmente e aparece na lista, sem envio.
- **Given** a gravação chegando a duas horas, **When** faltarem cinco minutos, **Then** o app avisa. Ao atingir duas horas, **Then** salva e encerra, informando que o limite do serviço foi atingido (RN-G03).
- **Given** o app fechado, travado ou o Windows desligado durante a gravação, **When** o usuário reabre o app, **Then** a gravação parcial é recuperada e aparece na lista como interrompida, com o áudio capturado até o último momento persistido (RN-G01).

**Prioridade:** Must Have

**Rastreabilidade:** RN-G01, RN-G02, RN-G03

---

### RF-03: Informar que há gravação em curso

**Descrição:** A cada início de gravação, o app exibe um aviso curto na tela de gravação lembrando que o usuário é responsável por informar os participantes e cumprir a regra de gravação aplicável. O aviso não adia o início da gravação. Enquanto grava, o app deixa evidente que está gravando, inclusive minimizado: indicador visível na área de notificação e na barra de tarefas. O usuário pode parar sem abrir a janela principal.

**Critérios de Aceitação:**

- **Given** o usuário inicia uma gravação, **When** ela começa, **Then** o aviso de gravação de terceiros aparece na tela, em toda gravação, e a gravação começa sem exigir confirmação.
- **Given** uma gravação em curso e a janela minimizada, **When** o usuário olha a barra de tarefas ou a área de notificação, **Then** vê que há gravação em andamento e o tempo decorrido.
- **Given** uma gravação em curso, **When** o usuário tenta fechar o app, **Then** ele pergunta se quer parar e salvar, ou continuar gravando. Nunca descarta a gravação sem confirmação explícita.

**Prioridade:** Must Have

**Rastreabilidade:** RN-G01

---

### RF-04: Gerenciar gravações locais

**Descrição:** O app lista as gravações do PC, da mais recente à mais antiga, com título, data, duração e situação (gravada, enviada, transcrita, falhou, interrompida). O usuário pode ouvir, renomear e apagar uma gravação. Dar título não é obrigatório: o padrão é data e hora.

**Critérios de Aceitação:**

- **Given** gravações salvas, **When** o usuário abre o app, **Then** vê a lista com a situação de cada uma.
- **Given** uma gravação, **When** o usuário a reproduz, **Then** ouve o áudio gravado com controle de avançar e voltar.
- **Given** uma gravação, **When** o usuário a apaga e confirma, **Then** o áudio e a transcrição local dela são removidos do PC, e o app diz que não é possível desfazer.
- **Given** uma gravação com envio em andamento, **When** o usuário tenta apagá-la, **Then** o app avisa que há transcrição em andamento e pede confirmação específica. Apagar não cancela o job no serviço.
- **Given** pouco espaço em disco, **When** o usuário inicia ou segue gravando, **Then** o app avisa antes de ficar sem espaço e, se o espaço acabar, salva o que já capturou (RN-G01).

**Prioridade:** Must Have

**Rastreabilidade:** RN-G01, RN-G05

---

### RF-05: Enviar uma gravação para transcrição

**Descrição:** O usuário escolhe uma gravação e pede explicitamente o envio. O app mostra o que será enviado (título, duração, tamanho aproximado) e avisa que o áudio será processado pelo serviço configurado. O app envia o áudio direto ao armazenamento do Whisper, por uma URL de envio emitida pela API (RF-10), e a API cria um job. O app mostra o progresso do envio e, ao ser aceito, o job passa a ser acompanhado.

**Critérios de Aceitação:**

- **Given** uma gravação e a API configurada, **When** o usuário pede o envio e confirma, **Then** o áudio é enviado com indicação de progresso e a gravação passa para "enviada".
- **Given** a API não configurada, **When** o usuário pede o envio, **Then** o app o leva à configuração e não envia nada.
- **Given** uma gravação com mais de duas horas ou com formato ou tamanho recusado pela API, **When** o usuário tenta enviar, **Then** o app explica o limite antes ou logo após a recusa e preserva a gravação (RN-G03).
- **Given** a rede cai ou a API falha durante o envio, **When** isso ocorre, **Then** o app informa a falha, mantém a gravação intacta e oferece "tentar de novo", sem reenviar sozinho em loop (RN-G04).
- **Given** o usuário toca em "tentar de novo" após uma falha ou timeout cuja entrega não se sabe se ocorreu, **When** o reenvio chega ao serviço, **Then** não gera dois jobs para o mesmo envio (RN-G04).
- **Given** uma gravação já transcrita, **When** o usuário pede novo envio, **Then** o app avisa que já existe transcrição e só envia após confirmação.
- **Given** a chave revogada durante o uso, **When** o envio é rejeitado por autorização, **Then** o app orienta a atualizar a chave e preserva a gravação.
- **Given** uma gravação interrompida (RF-02), **When** o usuário a envia, **Then** o app trata como qualquer outra e envia o que foi capturado.

**Prioridade:** Must Have

**Rastreabilidade:** RN-G03, RN-G04, RN-G07; Transcrição e Jobs RN-01

---

### RF-06: Acompanhar o processamento

**Descrição:** O app mostra o andamento do job nos estados públicos da API (obtendo, na fila, processando, concluído, falhou), em linguagem do usuário. Como o app não recebe aviso da API, ele consulta o job por conta própria enquanto não for terminal. O acompanhamento sobrevive ao fechamento do app: ao reabrir, os envios em andamento voltam a ser acompanhados.

**Critérios de Aceitação:**

- **Given** um envio aceito, **When** o job muda de estado, **Then** a situação da gravação na lista acompanha a mudança sem o usuário recarregar.
- **Given** o app fechado e depois reaberto com o job ainda dentro das 24 horas, **When** o app inicia, **Then** retoma o acompanhamento e atualiza a situação.
- **Given** o job concluído, **When** o app percebe, **Then** busca a transcrição, a guarda no PC (RF-07) e avisa o usuário com uma notificação do Windows, mesmo com o app minimizado.
- **Given** o job falhou, **When** o app percebe, **Then** informa o motivo em linguagem simples, mantém a gravação e oferece novo envio.
- **Given** o job expirado no serviço sem o app ter coletado o resultado (PC desligado por mais de 24 horas após o término), **When** o app consulta, **Then** informa que o resultado expirou e que é preciso enviar de novo, sem afirmar que a gravação se perdeu.
- **Given** a rede indisponível, **When** o app não consegue consultar, **Then** mostra "sem conexão" para o envio e retoma sozinho quando a rede voltar, sem marcar o job como falho.
- **Given** a API devolvendo erro temporário, **When** o app consulta, **Then** espera mais entre as consultas em vez de insistir (RN-G06).

**Prioridade:** Must Have

**Rastreabilidade:** Transcrição e Jobs RN-01, RN-03, RN-04; RN-G05, RN-G06

---

### RF-07: Guardar, ler, copiar e exportar a transcrição

**Descrição:** Quando o job conclui, o app guarda automaticamente a transcrição no PC como arquivo de texto (.txt) ligado à gravação, ao lado do áudio. O usuário a abre dentro do app, com o texto por trechos e os tempos relativos ao início da gravação. Pode copiar o texto e exportá-lo para outro local. A transcrição local não depende mais do serviço.

**Critérios de Aceitação:**

- **Given** um job concluído, **When** o app guarda a transcrição, **Then** existe um arquivo .txt legível, junto à gravação, com o texto e o tempo de cada trecho.
- **Given** uma gravação transcrita, **When** o usuário a abre, **Then** vê o texto em ordem, com o tempo de cada trecho.
- **Given** a transcrição aberta, **When** o usuário copia, **Then** o texto completo, com ou sem tempos conforme a escolha, vai para a área de transferência.
- **Given** a transcrição aberta, **When** o usuário exporta, **Then** escolhe o local e recebe um arquivo de texto legível, e um arquivo existente nunca é sobrescrito sem confirmação.
- **Given** uma transcrição local e o resultado já expirado no serviço, **When** o usuário a abre, **Then** o app a exibe normalmente (RN-G05).
- **Given** um resultado com versão de formato que o app não reconhece, **When** o app o busca, **Then** informa que precisa ser atualizado e **não** descarta o resultado nem a gravação.
- **Given** uma transcrição vazia (gravação sem fala), **When** o usuário a abre, **Then** o app diz que nenhuma fala foi reconhecida.

**Prioridade:** Must Have

**Rastreabilidade:** RN-G05; Transcrição e Jobs RN-03

---

### RF-08: Controle de privacidade e dados no PC

**Descrição:** Gravações e transcrições ficam em área do app, no perfil do usuário do Windows. O usuário sabe onde estão e pode apagar tudo. O app não envia nada sem a ação do usuário, nem telemetria com conteúdo.

**Critérios de Aceitação:**

- **Given** qualquer gravação, **When** o usuário não pediu envio, **Then** o app não transmite o áudio.
- **Given** as configurações, **When** o usuário escolhe "apagar todos os dados do app" e confirma, **Then** gravações, transcrições locais e a API Key são removidas.
- **Given** o app em uso, **When** registra eventos de diagnóstico, **Then** não registra áudio, texto da transcrição nem a API Key.

**Prioridade:** Must Have

**Rastreabilidade:** RN-G02, RN-G07; baseline (segurança)

---

### RF-09: Instalação no Windows

**Descrição:** O usuário instala o app em um PC com Windows 11 e o desinstala sem deixar o sistema em estado estranho. A desinstalação pergunta se as gravações e transcrições devem ser mantidas.

**Critérios de Aceitação:**

- **Given** um PC com Windows 11, **When** o usuário instala e abre o app, **Then** consegue gravar sem instalar driver, cabo virtual ou software de terceiros para capturar o áudio do sistema.
- **Given** o app desinstalado com a opção de manter dados, **When** o usuário o reinstala, **Then** as gravações e transcrições voltam a aparecer.

**Prioridade:** Should Have

**Rastreabilidade:** —

---

### RF-10: API receber o áudio diretamente

**Descrição:** A API do Whisper passa a aceitar o arquivo de áudio enviado pelo cliente autenticado, além da URL assinada, e cria o mesmo job de CAP-001, com os mesmos estados, resultado e retenção. O cliente pede uma URL de envio à API, envia o arquivo direto ao armazenamento do Whisper e confirma a conclusão, momento em que o job é criado (DP-09). O contrato está em `api-contract.yaml` e serve a qualquer consumidor, não só ao app.

**Critérios de Aceitação:**

- **Given** uma API Key válida e um áudio dentro dos limites, **When** o cliente o envia, **Then** a API cria um job que segue o fluxo de CAP-001 e devolve o identificador para consulta.
- **Given** tamanho acima do limite, formato não suportado ou duração declarada acima de 2 horas, **When** o cliente pede o envio, **Then** a API recusa com motivo identificável e não registra o envio nem cria job.
- **Given** um arquivo cujo conteúdo não confere com o declarado (tamanho, SHA-256 ou formato), **When** o cliente conclui o envio, **Then** a API recusa com motivo identificável, descarta o arquivo e não cria job.
- **Given** um arquivo aceito cuja duração real excede 2 horas, **When** o processamento a mede, **Then** o job termina em falha com motivo identificável.
- **Given** o mesmo envio repetido após falha ou timeout, mesmo horas depois, **When** a API o recebe, **Then** não cria um segundo job e devolve o mesmo envio ou job (RN-G04).
- **Given** uma chave inválida ou revogada, **When** o cliente envia, **Then** a API recusa sem processar o áudio.
- **Given** o fluxo de URL assinada de CAP-001, **When** esta entrega é implantada, **Then** ele continua funcionando sem alteração para quem o usa.
- **Given** o áudio recebido, **When** o job termina, **Then** o áudio segue a mesma retenção de CAP-001 e não aparece em logs nem para o operador.

**Prioridade:** Must Have

**Rastreabilidade:** Transcrição e Jobs RN-01, RN-03, RN-04, RN-07; RN-G03, RN-G04

---

### Regras de negócio nascidas neste PRD

| ID | Regra |
|---|---|
| RN-G01 | Uma gravação nunca é perdida por fechamento, travamento ou falta de espaço. O que foi capturado é persistido de forma contínua e recuperado ao reabrir. |
| RN-G02 | Nenhum áudio sai do PC sem ação explícita do usuário. |
| RN-G03 | O app respeita os limites do serviço: duração máxima de duas horas por gravação, e os formatos e o tamanho aceitos pela API (RF-10). Gravação acima do limite é encerrada e salva, nunca descartada. |
| RN-G04 | Enviar de novo após falha ou dúvida de entrega não pode criar dois jobs para o mesmo envio. Cada tentativa de envio é distinguível por quem a repete. |
| RN-G05 | A transcrição local pertence ao usuário e é independente da retenção do serviço. Expirar no serviço não apaga a cópia local, e apagar a cópia local não afeta o serviço. |
| RN-G06 | O acompanhamento consulta o serviço em ritmo moderado e recua diante de erro, sem sobrecarregar a API. Estados terminais encerram a consulta. |
| RN-G07 | A API Key é segredo do usuário: guardada de forma protegida no PC, mascarada na tela e nunca registrada. |
| RN-G08 | O app só reage ao estado público do job. Ele não depende de webhook nem de dado operacional do serviço. |

---

## Experiência do Usuário

**Persona:** profissional que participa de reuniões em Windows, sem perfil técnico. Não sabe o que é uma API; vê "chave de acesso".

**Fluxo principal:**

1. Instala e abre o app. A tela inicial já oferece **Gravar**.
2. Confere o nível do microfone e do áudio do sistema, e inicia a gravação. Ao iniciar, vê o aviso de gravação de terceiros.
3. Durante a reunião, o app fica minimizado com indicador de gravação.
4. Ao parar, a gravação aparece na lista. O usuário pode ouvi-la.
5. Clica em **Transcrever**, confirma o envio e vê o progresso.
6. Continua usando o PC. Quando termina, recebe uma notificação do Windows.
7. Abre a gravação, lê, copia ou exporta o texto. O .txt já está guardado ao lado do áudio.

**Primeiro uso:** a configuração da API Key só é pedida na hora do primeiro envio, não antes da primeira gravação.

**Acessibilidade:** operação por teclado, atalho para iniciar e parar a gravação mesmo com o app em segundo plano, contraste suficiente e estados que não dependam só de cor.

**Idioma da interface:** português do Brasil.

**Aviso sobre terceiros:** o app não sabe se os outros participantes foram avisados da gravação. Por isso o aviso aparece em todo início de gravação (RF-03), sem adiar a gravação.

## Decisões de Produto

| ID | Decisão confirmada | Alternativas descartadas e motivo | Impacto no PRD | Registro |
|---|---|---|---|---|
| DP-01 | Captura de microfone **e** áudio do sistema pelos dispositivos do PC, sem integração com ferramentas de reunião (2026-10-21). | Só microfone: não capta os outros participantes de uma reunião remota. Integração com Zoom/Teams: excluída pelo pedido. | RF-02; non-goals | — |
| DP-02 | O áudio chega à API por **envio direto**, e não por URL assinada (2026-10-21). | Publicar em storage próprio e passar URL assinada: exige storage e credenciais no app desktop. | RF-05, RF-10 | — |
| DP-03 | O app acompanha o job por consulta e guarda a transcrição localmente (2026-10-21). | Só exibir enquanto o serviço retém: a transcrição some em 24 horas. | RF-06, RF-07; RN-G05 | — |
| DP-04 | O app autentica com a API Key da conta, informada nas configurações (2026-10-21). | Login de usuário individual: exige contas de usuário final, que a visão deixa fora do escopo. | RF-01; RN-G07 | — |
| DP-05 | O recebimento direto de áudio pela API faz parte desta capacidade, e o contrato definido aqui é reaproveitável por outros consumidores (2026-10-21). Substitui a divisão em duas capacidades considerada antes. | Capacidade separada (CAP-003): adiaria a definição do contrato para outro PRD. | Rastreabilidade; RF-10 | — |
| DP-09 | O arquivo vai direto ao armazenamento do Whisper por URL de envio emitida pela API; a API não recebe o corpo do arquivo. A repetição do mesmo envio não duplica o job por até 24 horas após o estado terminal (2026-10-21). | Corpo bruto na própria API: passa os bytes pela API. Envio em partes com retomada: três operações e estado parcial. Janela de 120 s: não cobre arquivo grande nem "tentar de novo" tardio. | RF-10; RN-G04; Restrições | ADR-001 |
| DP-06 | Windows 11 é a versão suportada (2026-10-21). | Windows 10: restringiria a técnica de captura. | Restrições; RF-09 | — |
| DP-07 | Papel inicial do app: gravar, enviar, obter o retorno e guardar áudio e texto (.txt) no PC. Edição é feature futura (2026-10-21). | Incluir edição agora: amplia o escopo. | RF-07; non-goals | — |
| DP-08 | O aviso de gravação de terceiros aparece no início de cada gravação (2026-10-21). | Só no primeiro uso: o usuário esquece entre reuniões. | RF-03 | — |

---

## Restrições Técnicas de Alto Nível

- Somente Windows 11.
- O PC do usuário precisa alcançar o armazenamento do Whisper por HTTPS para enviar o áudio.
- Capturar o áudio do sistema não pode exigir driver nem software de terceiros para o usuário (RF-09).
- Idioma da transcrição: pt-BR, conforme o serviço.
- Gravação e transcrição locais contêm conteúdo potencialmente sensível. O app as mantém no perfil do usuário e não as envia a ninguém, exceto o envio pedido à API configurada.
- A API Key fica protegida no PC (RN-G07).

---

## Não-Objetivos (Fora de Escopo)

- Integração com Zoom, Teams, Meet ou outra ferramenta, inclusive entrar na reunião como participante ou ler o calendário.
- Transcrição no próprio PC, sem a API.
- Transcrição ao vivo, durante a reunião.
- Identificação de quem fala, resumo, atas, tradução ou busca entre transcrições.
- Edição do áudio ou da transcrição (feature futura); player sincronizado com o texto.
- Contas de usuário final, login individual e multiusuário numa mesma instalação.
- Sincronização com nuvem, backup ou compartilhamento de gravações.
- Envio automático ao fim da gravação.
- Outros sistemas operacionais; Windows 10; atualização automática.
- Outros idiomas além de pt-BR.
- Console, contas e webhooks: continuam em CAP-002.

## Plano de Rollout Faseado

### MVP (Fase 1)

- **Funcionalidades incluídas:** RF-01 a RF-08 e RF-10.
- **Critérios de sucesso para avançar à Fase 2:** ver Métricas de Sucesso. A Fase 2 só existe se houver decisão de produto.

### Fase 2

- **Funcionalidades adicionais:** RF-09 (instalação e desinstalação com opção de manter dados) e os itens de non-goal que o produto decidir sequenciar, como a edição. Nada está comprometido.

## Métricas de Sucesso

Não há base de uso para fixar metas numéricas. As metas abaixo são hipóteses a validar no piloto e dependem de Q-04.

| Métrica | Definição | Alvo inicial (hipótese) |
|---|---|---|
| Gravações sem perda | Gravações iniciadas que terminam salvas, incluindo as recuperadas após fechamento inesperado | 100% das iniciadas |
| Envio concluído | Envios aceitos pela API / envios pedidos, sem contar falha de autorização | A definir no piloto |
| Transcrições entregues | Envios aceitos que chegam a uma transcrição local sem ação além de abrir o app | A definir no piloto |
| Tempo da gravação ao texto | Fim da gravação até a transcrição local disponível, da perspectiva do usuário | Segue a meta do serviço (SLO de até 24 h no contrato atual); revisar com o benchmark |

## Riscos e Mitigações

- **Gravação de terceiros sem ciência (consentimento e LGPD):** o app capta outras pessoas. Mitigação: aviso em todo início de gravação, indicador visível (RF-03) e nenhum envio sem ação do usuário.
- **Reuniões longas e arquivos grandes:** duas horas de áudio podem gerar um arquivo grande e um envio lento em rede ruim. Mitigação: progresso, nova tentativa e limite de duração. Formato e taxa de bits ficam para a TechSpec.
- **Contrato de upload ainda não definido:** sem ele, o app só grava. Mitigação: o contrato é a primeira etapa após o PRD, e RF-01 a RF-04 são entregáveis em separado.
- **Resultado expira antes da coleta:** PC desligado por mais de 24 horas após o término. Mitigação: RF-06 explica o caso e oferece novo envio; a gravação local permanece.
- **Chave de conta compartilhada:** a API isola jobs por credencial (RN-04), então várias pessoas na mesma chave enxergam os jobs umas das outras no serviço. Mitigação: uma chave por instalação, emitida pelo operador.
- **Falhas de captura entre PCs:** configurações de áudio do Windows variam (dispositivos exclusivos, drivers). Mitigação: indicador de nível antes de gravar e mensagens claras (RF-02).

## Alternativas Consideradas

### Abordagem Escolhida: app desktop próprio que captura os dispositivos do PC

- **Descrição:** o app grava microfone e áudio do sistema, guarda localmente e envia sob demanda.
- **Por que foi escolhida:** funciona com qualquer ferramenta de reunião, mantém o áudio no PC até o usuário decidir e não depende de APIs de terceiros.

### Alternativa Rejeitada 1: integração com Zoom/Teams

- **Trade-offs:** áudio por participante e metadados melhores, mas uma integração por ferramenta, com permissões do organizador.
- **Por que foi rejeitada:** excluída explicitamente pelo pedido.

### Alternativa Rejeitada 2: extensão de navegador

- **Trade-offs:** instalação simples, mas só cobre reuniões em abas do navegador, e não aplicativos desktop.
- **Por que foi rejeitada:** a demanda é gravar pelos dispositivos do PC, qualquer que seja a ferramenta.

## Questões em Aberto

| ID | Questão | Quem responde | Quando | Impacto se não resolvida |
|---|---|---|---|---|
| Q-01 | Resolvida em 2026-10-21: visão v0.2 e backlog v0.2 atualizados. Resta decidir onde o cliente desktop entra no baseline (ver Riscos Estratégicos do backlog). | Produto e arquitetura | Antes da TechSpec | A TechSpec decidiria sem fronteira de dados e segurança para o cliente. |
| Q-02 | Resolvida em 2026-10-21: contrato em `api-contract.yaml` (v1.1.0). Retomada parcial de envio fica fora; repetir o PUT do arquivo inteiro é o caminho. | — | — | — |
| Q-04 | Metas numéricas das métricas e quem é o piloto. | Produto | Antes do rollout | As métricas permanecem hipóteses. |
| Q-06 | A API Key é emitida pelo operador em CAP-002 e entregue fora do app. É aceitável para o piloto? | Produto e operação | Antes do rollout | Fricção no primeiro uso. |
