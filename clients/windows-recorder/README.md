# Gravador de reuniões para Windows

SDK .NET 10, WPF, Windows 11. O `global.json` da raiz seleciona o SDK 10
(`latestFeature`) e Microsoft.Testing.Platform. Execute os comandos abaixo da
raiz do repositório.

| Projeto | Alvo | Fronteira |
|---|---|---|
| `src/Core` | `net10.0` | Portas, gravação local, tempo, diagnóstico e cliente da API gerado |
| `src/Presentation` | `net10.0` | ViewModel de gravação, sem referências WPF |
| `src/Host` | `net10.0-windows` | App WPF com WPF-UI: captura WASAPI (NAudio), tela de gravação, barra de tarefas, bandeja e atalho global; demais adaptadores nas próximas fatias |
| `tests/Core.Tests` | `net10.0` | Núcleo e apresentação, xUnit/MTP e dublês das portas |
| `tests/Host.Tests` | `net10.0-windows` | Testes do host no Windows (captura com fontes sintéticas, indicadores, atalho) |

O host depende da apresentação, que depende do núcleo. O núcleo não importa
código Python nem tipos Windows. A API gerada consome apenas o contrato público.
As interfaces em `Core/Ports` pertencem ao núcleo; o host implementa captura,
cofre de credenciais, notificação e reprodução. `IClock` e `IScheduler` têm
implementações do sistema no núcleo e dublês controláveis nos testes.
O PCM é intercalado de 16 bits, com taxa/canais explícitos e timestamp do primeiro
frame. Microfone e loopback têm ciclos independentes e eventos de bloco, nível
e disponibilidade. Os buffers entregues permanecem válidos após o evento.

## Build e verificação

```bash
dotnet build clients/windows-recorder -warnaserror
dotnet format clients/windows-recorder --verify-no-changes
dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1
```

Os testes V-05 também precisam do ambiente Python 3.12 de desenvolvimento do
repositório, com as dependências de `.[dev]` instaladas em `.venv`. Para usar
outro ambiente, defina `WHISPER_TEST_PYTHON` com o caminho do interpretador.
Essa preparação também é necessária no CI que execute a suíte completa.
Os testes iniciam a API real em um processo filho e usam HTTP em loopback;
não pule esses testes quando o ambiente Python estiver ausente.

V-06 também usa PostgreSQL local de testes (o serviço `postgres-test`, porta
55432 por padrão) e `moto[server]` para o bucket simulado. A fixture cria e remove
um schema aleatório próprio, sem limpar tabelas de outras execuções. Aceita
`TEST_DATABASE_URL` ou as variáveis `TEST_POSTGRES_*` e `TEST_DATABASE_HOST`
usadas pela suíte Python; exige banco com `test` no nome e host local.

O build da solução inclui os cinco projetos, inclusive o XAML do host e o projeto
de testes Windows, com `EnableWindowsTargeting=true`. Foi verificado no Linux
com SDK 10.0.400: 0 erros, 0 warnings. Não houve necessidade de excluir o host
do build Linux. Executar WPF, WASAPI e testes do host exige uma sessão Windows;
esta tarefa não valida captura, dispositivos, Mica nem aparência em execução.

No Windows:

```powershell
dotnet build clients/windows-recorder -warnaserror
dotnet run --project clients/windows-recorder/src/Host
```

## Convenções de testes

Use xUnit com Microsoft.Testing.Platform v2 (pacote `xunit.v3.mtp-v2`),
sem `Microsoft.NET.Test.Sdk` ou adaptador VSTest. Os projetos de teste são
executáveis e recebem `--minimum-expected-tests 1` por padrão em
`Directory.Build.props`. Isso impede um falso sucesso por filtro vazio.
O comando canônico de filtro por classe, usado pelas próximas tasks, é:

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class Whisper.Recorder.Core.Tests.SmokeTests --minimum-expected-tests 1
```

Substitua somente o nome completo da classe pelo cenário da fatia. Para provar
que o runner reprova seleção vazia (exit diferente de zero):

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class Whisper.Recorder.Core.Tests.DoesNotExist --minimum-expected-tests 1
```

Há exatamente um teste de fumaça, sem rede nem dependência de Windows. Ele
exercita os dublês das seis portas, o diagnóstico por eventos e a construção de
um pedido da API com o limite de 5 GiB. Adicione os cenários de cada fatia a
classes próprias, com o namespace `Whisper.Recorder.Core.Tests`. Os dublês
reutilizáveis ficam em `tests/Core.Tests/Doubles`; o agendador registra esperas
sem dormir, e o relógio é controlado pelo teste.
O projeto `Host.Tests` só roda no Windows (alvo `net10.0-windows`); o CI Linux
seleciona apenas `Core.Tests`.

## Gravação local (V-04)

Na abertura, o host cria `RecordingRepository` e chama `RecoverInterrupted()`
antes de criar a sessão. A raiz padrão é
`%LOCALAPPDATA%\Whisper\Recorder\recordings`; cada UUID contém `audio.wav` e
`metadata.json`. A recuperação recalcula os tamanhos do WAV pelo arquivo,
descarta um eventual byte incompleto e muda `recording` para `interrupted`.
Metadados são escritos em temporário, sincronizados e renomeados atomicamente.

`RecordingSession` recebe a porta de captura, `IClock`, `IScheduler`, repositório
e, opcionalmente, `IDiskSpace`. O host fornece dispositivos selecionados a
`StartAsync`, usa `Pause`, `Resume`, `StopAsync` e descarta a sessão no fechamento
normal. A sessão não descarta a porta de captura, que pertence ao host.
`SystemScheduler` dirige ticks de 100 ms, independentemente dos pacotes de áudio;
`Tick` também está disponível para relógios controlados nos testes.

Os timestamps posicionam cada fonte no tempo ativo, excluindo as pausas.
Os blocos PCM são convertidos para mono 16 kHz, cada fonte recebe ganho 0,5
e a soma vira WAV de 16 bits. Ausência de pacotes insere silêncio. O buffer
aceita até 5 s adiante. Como cada bloco chega depois do período que cobre,
o tick grava só até o relógio menos `JitterMargin` (500 ms); pausar, parar e
o limite de 2 h completam esse trecho. Um bloco entregue com mais de 500 ms de
atraso perde a parte já persistida, que não pode ser reescrita.
Cabeçalho e metadados têm checkpoint durável a cada segundo, e também ao
pausar/parar. O aviso de limite surge aos 115 min e o salvamento aos 120 min.
Há aviso abaixo de 50 MiB livres, parada antes de esgotar a margem de escrita
e reserva de 64 KiB por gravação, liberada na finalização/recuperação.

`RecordingViewModel` expõe duração, estado, níveis, fontes, avisos, lista e aviso
de terceiros em cada início. Suas notificações usam o `SynchronizationContext`
da criação quando disponível. Não usa WPF nem rede. A captura real e as telas
continuam a cargo do host Windows (task 6.0).

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V04*" --minimum-expected-tests 12
```

Os testes usam fontes sintéticas, relógio controlado, arquivos temporários e um
agendador que aguarda pulsos explícitos. O teste de recuperação inicia outro
processo .NET, aguarda um checkpoint, encerra-o à força e reabre os arquivos.
Não usa rede, MinIO nem dispositivos reais.

## Host Windows: captura e tela de gravação (V-04, task 6.0)

Requer Windows 11, SDK .NET 10 e, para gravar de verdade, um microfone e uma saída de áudio.

```powershell
dotnet run --project clients/windows-recorder/src/Host
dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V04*" --minimum-expected-tests 6
```

O app abre na tela de gravação (tema Fluent, Mica). Os arquivos ficam em
`%LOCALAPPDATA%\Whisper\Recorder\recordings\<uuid>\` (`audio.wav` e `metadata.json`); o diário
de eventos fica em `%LOCALAPPDATA%\Whisper\Recorder\diagnostics.log`. Só uma instância roda por vez.
O roteiro da validação manual está em
[`6.0_manual_validation.md`](../../tasks/prd-gravador-de-reunioes-desktop/6.0_manual_validation.md).

**Captura (`src/Host/Audio`).** `WasapiAudioCapture` implementa `IAudioCapture` sobre uma fábrica
(`ICaptureDeviceCatalog`/`ICaptureSource`), que no app é o NAudio 3.3.0 (`WasapiCapture` para o
microfone e `WasapiLoopbackCapture` para a saída escolhida, a padrão por omissão) e nos testes é
sintética. O host só entrega PCM nativo (float 32, inteiro 16/24/32 bits, qualquer taxa e canais)
convertido para inteiro de 16 bits intercalado; não insere silêncio nem mixa, o que é do `AudioTimeline`.

- **Carimbo dos blocos.** O WASAPI não carimba pacotes. `BlockTimestamper` usa o relógio de parede
  na chegada (o bloco termina quando chega, então começa em `chegada - duração`) e a contagem de
  frames para manter blocos contínuos. Se a chegada se afastar mais de 200 ms da contagem
  (loopback que ficou em silêncio, deriva do dispositivo), reancora na chegada. Fica dentro da
  margem de 500 ms do núcleo.
- **Nível.** Pico por bloco (0..1). O loopback só entrega dados enquanto algo toca, então um
  temporizador zera o nível da fonte sem dados há 250 ms.
- **Pré-visualização.** A tela abre as fontes ao listar os dispositivos para mostrar o nível antes de
  gravar; a sessão descarta esses blocos até iniciar. `StartAsync` é idempotente por dispositivo.
- **Perda e volta.** O catálogo real escuta o `MMDeviceNotificationClient` (estado do dispositivo
  e remoção); uma parada inesperada do fluxo também conta como perda. A fonte perdida emite
  `DeviceAvailabilityChanged(false)` e o host tenta reabri-la a cada segundo (o mesmo dispositivo
  ou, se era o padrão, o novo padrão do Windows). Quando volta, emite `true` com o dispositivo
  pedido; a sessão (ajuste mínimo) volta a aceitar os blocos dessa fonte.
- **Permissão.** `E_ACCESSDENIED` vira `UnauthorizedAccessException`; a tela mostra como liberar
  o microfone (Configurações > Privacidade e segurança > Microfone) e oferece gravar só o
  áudio do sistema. Se o Windows entregar só silêncio sem erro, o nível não se move: confira a
  mesma configuração.

**Tela e indicadores.** `RecordingScreenViewModel` (em `src/Presentation`, sem tipos WPF) concentra
fontes, dispositivos, nível, comandos e lista. `RecordingIndicatorCoordinator` leva o estado à
barra de tarefas (`TaskbarIndicator`: ícone sobreposto, barra vermelha ou amarela e descrição com o
tempo) e à bandeja (`TrayIndicator`, que também é o `INotifier`, com menu abrir, iniciar/parar e sair).
Cada início de gravação mostra o aviso de terceiros na tela e em notificação da bandeja
(`RecorderNotification.RecordingStarted`), mesmo com o atalho e a janela minimizada. Fechar durante
a gravação pergunta "Parar e salvar" ou "Continuar gravando" (`ShutdownGuard`); nunca descarta.
A lista de gravações tem título, data, duração e situação em texto e símbolo (salva, interrompida,
gravando), navegável por teclado, com o nome acessível completo por linha.

**Atalho global.** `RegisterHotKey` com `GlobalHotkey`: tenta, nesta ordem, Ctrl+Alt+R,
Ctrl+Alt+Shift+R, Ctrl+Alt+F9 e Ctrl+Shift+F9, e usa a primeira que o Windows aceitar (outro
programa pode já tê-la reservado). A escolhida aparece na tela e no menu da bandeja. Inicia quando
parado e para, salvando, quando grava ou está pausado.

**Testes (`tests/Host.Tests`).** As classes `V04*` usam fontes WASAPI sintéticas, relógio controlado,
a sessão real do núcleo e arquivos temporários: mixagem das duas vozes, silêncio do loopback preenchido
pelo núcleo, carimbos, perda e volta de dispositivo, nível e troca de dispositivo, permissão negada,
indicador da barra de tarefas (em thread STA explícita), atalho e confirmação ao fechar.
Não usam hardware, rede nem interação. A captura real, a tela, a bandeja e o atalho global só são
verificados na validação manual.

Limitações conhecidas: a primeira janela de ~100 ms depois de iniciar pode ser descartada (o
bloco começa antes do início da sessão); uma fonte que volta depois de desconectada recomeça
sem preencher o intervalo, que fica em silêncio; e NAudio 3 marca `WasapiCapture` como obsoleto em
favor de `WasapiRecorder`: o uso fica isolado em `NAudioCaptureDeviceCatalog` com `#pragma` local.

## Acesso à API (V-05)

`ApiAccessService` recebe `ICredentialVault`, `ApiConfigurationStore`,
`ApiAccessProbe` e, opcionalmente, `DiagnosticLog`. O host cria uma instância
compartilhada do serviço e mantém o probe vivo até encerrar o app. O caminho
padrão da configuração é `ApiConfigurationStore.DefaultPath`. A configuração
comum contém apenas `apiBaseAddress`; a chave pertence à porta do cofre.

`ApiAccessViewModel.InitializeAsync` carrega e verifica o acesso salvo.
`SaveAsync(endereço, chave)` valida uma candidata e só então salva ou substitui
a chave. A ViewModel não guarda uma propriedade com a chave completa: o host
passa o valor do controle de senha e limpa esse controle após o uso.
`MaskedKey` mostra apenas o sufixo de quatro caracteres; chaves curtas são
inteiramente mascaradas. `RemoveAsync` remove a chave do cofre, preserva o
endereço e volta ao estado ausente. Uma falha de persistência produz mensagem
fixa; se a escrita atômica do endereço falhar, o serviço restaura a chave anterior.

O endereço aceita HTTP/HTTPS, inclusive implantação sob um prefixo, com `/v1`
opcional. Usuário/senha na URL, consulta, fragmento e endereço que contenha a
chave informada são recusados. O probe usa `getTranscription` do cliente gerado
com um UUID novo: `404` confirma, `401` recusa e os demais resultados deixam
o acesso não verificável. O timeout padrão é 10 s. Redirecionamentos não são
seguidos; o corpo da resposta e mensagens de exceção não chegam à apresentação
nem ao diário. Cancelamento pedido pelo chamador continua sendo cancelamento.

Antes de iniciar o envio, o coordenador pode chamar
`ApiAccessViewModel.RequestSendAsync`: ele verifica novamente a chave salva,
devolve se o envio pode continuar e dispara `ConfigurationRequested` quando
o acesso está ausente ou recusado. `CanSend` reflete o último resultado;
a gravação local permanece independente das configurações da API.
As notificações da ViewModel respeitam o `SynchronizationContext` de criação.
O cofre Windows e a tela são entregues na task 8.0; o upload, na 9.0.

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V05*" --minimum-expected-tests 8
```

Os testes usam a aplicação FastAPI real do repositório, com middleware,
autenticação, resposta Problem Details, `CredentialStore` e `JobManager` reais,
em um processo Python com SQLite temporário. Substituem apenas o ciclo de
startup de serviços externos; não precisam de PostgreSQL, MinIO ou modelo.
As consultas cruzam o HTTP real em loopback. Falhas de rede, timeout e respostas
imprevistas usam a fronteira HTTP controlada. Credenciais de teste transitam
por pipes privados e cabeçalhos, sem arquivos ou logs de conteúdo.
O modelo atual da API não tem operação de desativação de conta; o cenário de
conta sem acesso revoga suas credenciais pelo ciclo real da API e verifica `401`.

## Regenerar o cliente da API

Kiota está fixado em `dotnet-tools.json`. O runtime usa `Microsoft.Kiota.Bundle`,
também em versão fixa. No Linux, com o ambiente Python de desenvolvimento do
repositório (Python 3.12+ e `PyYAML==6.0.3`):

```bash
.venv/bin/python clients/windows-recorder/scripts/generate_api.py
```

No Windows, com .NET 10 e Python disponíveis:

```powershell
py -3 -m pip install PyYAML==6.0.3
py -3 clients/windows-recorder/scripts/generate_api.py
```

O script restaura a ferramenta e gera todos os endpoints do
[`api-contract.yaml`](../../tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml)
em `src/Core/Api/Generated`, incluindo o lock do gerador. Não edite esse código
manualmente. A entrada intermediária em `obj/openapi-generation` atribui formato
`int64` aos inteiros sem formato: o padrão `int32` do Kiota não comporta os 5 GiB
aceitos por `sizeBytes`. Nenhuma rota, propriedade ou restrição do contrato
aprovado é alterada. Kiota avisa que `uri` e `uri-reference` viram `string`;
isso é esperado e preserva URLs absolutas e caminhos relativos do contrato.

Após regenerar, compile e revise o diff. O CI regenera e exige ausência de diff
nos arquivos gerados. O endereço base gerado é `/v1`; `ApiAccessProbe` fornece
o endereço absoluto da implantação no adaptador e a chave no cabeçalho da
consulta de validação. O smoke da estrutura não envia requisições.

## Envio para transcrição (V-06)

O host compartilha um `UploadCoordinator` entre as telas e mantém o
`UploadTransport` vivo até fechar o app. O coordenador recebe repositório,
configuração, cofre, transporte, relógio e diagnóstico opcional. O transporte
usa o cliente gerado para as duas operações da API e um cliente HTTP separado
para o armazenamento; a chave nunca acompanha o `PUT`. Não há middleware de
retry, redirecionamento nem log HTTP.

`UploadViewModel.Prepare(id)` obtém título, duração e tamanho para o diálogo de
confirmação. Só a ação explícita `ConfirmAndSendAsync` envia o áudio. Quando
`Confirmation.HasTranscription` é verdadeiro, o host também deve obter a
confirmação de reenvio e passar `existingTranscriptionConfirmed: true`.
`ConfigurationRequested` abre configurações na ausência de acesso ou em 401.
`Progress`, `IsSending`, `Result.Message` e `Result.RetryAt` alimentam a tela
Windows da task 10.0; criar o ViewModel ou reabrir arquivos não envia nada.

Os metadados guardam tentativa, `uploadId`, `jobId`, `PutCompleted`, recusa,
problema e prazo de retry. `saved`/`interrupted` passa a `uploading` e, depois do
aceite, `processing`. Em falha, o áudio permanece intacto e a etapa fica
disponível à próxima ação manual. A chave de idempotência deriva do UUID e da
tentativa: só uma recusa durável ou um reenvio de gravação transcrita incrementa
a tentativa existente. O pedido repetido recupera um job já criado mesmo se a
resposta do `complete` se perdeu; `PutCompleted` evita repetir bytes quando só
falta concluir. Em `UPLOAD_NOT_RECEIVED`, a próxima ação repete o arquivo todo.
Um 403 do armazenamento renova a URL uma única vez com a mesma chave. `429`
persiste `Retry-After` e impede nova chamada antes do prazo, inclusive ao reabrir.
URL assinada e chave ficam apenas em memória; erros expõem mensagens fixas.

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V06*" --minimum-expected-tests 14
```

Os testes usam a API, autenticação e gerenciador de envios reais, PostgreSQL
isolado e HTTP para o S3 simulado. Injetam quedas antes/depois das respostas,
avançam o relógio da API em cinco horas e verificam job único, checksum,
limites, confirmação, progresso e ausência de segredos em arquivos/diagnóstico.
Não iniciam worker de transcrição; o acompanhamento pertence à task 11.0.

## Diagnóstico e dependências

`DiagnosticLog` recebe somente `DiagnosticEvent` e um relógio, escrevendo
timestamp UTC e um evento de uma lista fechada num `TextWriter` escolhido pelo
host. Não aceita mensagens livres, exceções, caminhos, áudio, transcrição,
URL de envio ou API Key. Valores de enum fora da lista são recusados.
Nenhum log HTTP ou de conteúdo é configurado nesta estrutura.
O host registra apenas início e saída em
`%LOCALAPPDATA%\Whisper\Recorder\diagnostics.log`. Falha ao abrir o arquivo não
impede o início do app; mensagens de exceção não são gravadas.

WPF-UI **4.3.0** usa licença **MIT**, verificada em
[`lepoco/wpfui/LICENSE`](https://github.com/lepoco/wpfui/blob/4.3.0/LICENSE).
O aviso está preservado em `THIRD-PARTY-NOTICES.md` e deverá acompanhar a
distribuição do instalador. Fonte e
ícones adicionais que venham a ser distribuídos devem ter suas próprias licenças
registradas na fatia do instalador. O shell usa os dicionários do tema e uma
`FluentWindow` com Mica.

Referências de configuração:
[xUnit/MTP](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform),
[dotnet test/MTP](https://learn.microsoft.com/dotnet/core/tools/dotnet-test-mtp),
[Kiota/.NET](https://learn.microsoft.com/openapi/kiota/quickstarts/dotnet).

## Acompanhamento e resultado local (V-07)

O host compartilha a mesma instância de `RecordingRepository` entre
`UploadCoordinator` e `JobTracker`, mantém os dois transportes vivos e inicia
`JobTracker.RunAsync` na abertura, com cancelamento no fechamento. O tracker
acompanha novos jobs aceitos e retoma metadados `uploading`/`processing` do disco.
`IConnectivity` permite suspender HTTP sem alterar o estado; `SystemConnectivity`
fornece a implementação padrão. `Changed` entrega `TrackingStatus` quando há
mudança; a tarefa do host deve encaminhar esse evento para a thread da interface.
`Status(id)` fornece a situação atual, a mensagem e `CanResend`.

As consultas crescem de 2 para 4, 8, 16 e 30 segundos, com teto de 30 segundos.
Falhas transitórias recuam para pelo menos 10 segundos, dobrando até 60 segundos.
O prazo e o intervalo são persistidos e respeitados ao reabrir. A passagem local
do scheduler é a cada 2 segundos e não faz HTTP antes do prazo. Não há retries
ocultos do cliente HTTP, webhook ou consulta a estado operacional.

Um PUT durável sem resposta de aceite recupera o mesmo job repetindo apenas
`complete`; um upload interrompido antes do PUT concluído mostra que precisa de
novo envio manual. O tracker nunca envia bytes de áudio. `failed` guarda o código
público e `404` de job aceito vira `result-expired`; ambos permitem reenvio com
nova tentativa pelo coordenador, sem reaproveitar um job terminal.

Em conclusão, `result-v1.json` preserva os bytes recebidos e `transcription.txt`
contém um trecho por linha, com `[HH:mm:ss.fff]` relativo ao início do áudio.
Cada arquivo usa temporário, flush durável e renomeação; o estado `transcribed`
só é persistido depois dos arquivos. Falha de armazenamento mantém o job em
acompanhamento para tentar novamente. Versão desconhecida guarda o JSON bruto
e sinaliza `UpdateRequired`, sem gerar texto daquela versão. Resultado vazio
guarda os arquivos e sinaliza `EmptyTranscript`. A cópia transcrita deixa de
consultar a API e independe da retenção. A notificação passa por `INotifier` após
a persistência; falha do host ao notificar não desfaz o resultado local.

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V07*" --minimum-expected-tests 12
```

V-07 usa a mesma preparação Python/PostgreSQL/S3 simulado de V-06, com HTTP real
para a API e comandos privados para dirigir a fronteira do worker e a retenção.
Relógio, scheduler e conectividade são controlados; erros de rede/5xx e um
formato futuro de resultado usam interceptação HTTP. Não executa modelo de
transcrição, dispositivo de áudio ou notificação real do Windows.

## Leitura, cópia e exportação local (V-08)

O host cria `TranscriptViewModel` com `LocalTranscriptStore` e o repositório de
gravações, e chama `Open(id)`. A leitura usa `result-v1.json` para os trechos e
seus tempos e `transcription.txt` para o texto com tempos. Não exige configuração
da API nem consulta o serviço, inclusive após a retenção de 24 horas. `Recording`
mantém a gravação acessível quando `UpdateRequired` pede uma versão nova do app;
nessa situação, o JSON bruto é preservado e cópia/exportação ficam indisponíveis.
`IsEmpty` e `Message` indicam quando nenhuma fala foi reconhecida.

`CopyText(includeTimestamps)` entrega o texto completo ao host, que o coloca na
área de transferência. `Export(path)` grava UTF-8 com tempos no destino escolhido.
Se retornar `ConfirmationRequired`, o host apresenta a confirmação para aquele
caminho; somente após o aceite chama `Export(path, overwriteConfirmed: true)`.
Sem aceite, o arquivo existente permanece intacto. `StorageError` fornece uma
mensagem fixa sem conteúdo nem detalhes do caminho. As operações da ViewModel
são síncronas e notificam `PropertyChanged` na thread do chamador.
A tela, os diálogos e a área de transferência reais pertencem à task 14.0.

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V08*" --minimum-expected-tests 8
```

Os testes V-08 usam apenas arquivos locais temporários no formato de V-07;
não fazem requisições de rede e não precisam de API, PostgreSQL ou MinIO.

## Gestão e remoção de dados locais (V-09)

O host cria `RecordingManagement` com a mesma instância de `RecordingRepository`
usada pelo envio/acompanhamento, a porta `ICredentialVault` e o diretório de
dados do app (`%LOCALAPPDATA%\Whisper\Recorder`). Esse diretório deve conter
as gravações, `settings.json` e os diagnósticos. `RenameAsync(id, title)` persiste
o título; vazio, espaços ou `null` restauram o padrão de data e hora de criação.
A lista e os diálogos devem usar `RecordingMetadata.DisplayTitle`.

`RecordingManagementViewModel.RequestDelete(id)` expõe `DeleteConfirmation`
e o aviso de remoção definitiva. Após o aceite, o host chama
`ConfirmDeleteAsync`; se `HasPendingTranscription`, precisa apresentar também
o aviso específico de envio/transcrição em andamento e passar
`pendingTranscriptionConfirmed: true` somente após esse aceite. A condição é
reavaliada ao apagar, inclusive quando um envio começa depois de abrir o diálogo.
`CancelConfirmation` descarta a intenção sem modificar arquivos.

`RequestDeleteAll` apresenta o aviso de apagar todas as gravações, transcrições,
configurações e a chave de acesso. `ConfirmDeleteAllAsync` remove a chave pela
porta do cofre e esvazia o diretório do app, incluindo subdiretórios e temporários.
Falha do cofre mantém os arquivos; falhas posteriores de disco podem deixar
uma remoção parcial e retornam `StorageError`, com mensagem fixa. A raiz do app
é preservada vazia; listar/pollar continua válido e o repositório pode ser usado
novamente. O host deve atualizar a lista e o estado de configuração após sucesso.

As operações aguardam envios/consultas em curso usando a exclusão mútua do
repositório. Não cancelam jobs nem chamam HTTP; gravações apagadas deixam de
ser acompanhadas. Uma captura ativa deve ser parada antes dessas operações
(`RecordingInProgress`). O host deve impedir novas capturas/configurações
durante a limpeza e liberar arquivos de diagnóstico/player que mantenha abertos.
Player, lista e diálogos Windows pertencem à task 16.0.

```bash
dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V09*" --minimum-expected-tests 8
```

V-09 usa arquivos temporários, dublê do cofre e interceptação HTTP. Cobre
confirmações, títulos, remoção completa e seletiva, falha do cofre e limpeza
enquanto uma consulta de job aguarda resposta, sem recriar dados depois.

## Configuração do acesso à API no host (V-05, Windows)

`src/Host/Access/WindowsCredentialVault.cs` implementa `ICredentialVault` sobre o Windows
Credential Manager (`CredWrite`/`CredRead`/`CredDelete` de `advapi32`, credencial genérica).
O alvo do app é `Whisper.Recorder/ApiKey`; os testes usam alvos `Whisper.Recorder.Test/...`
com GUID, removidos ao fim de cada teste. A persistência é `LOCAL_MACHINE`: sobrevive ao
reinício e ao logoff, é restrita ao usuário atual e não é replicada para outros computadores
(`ENTERPRISE` serviria apenas a perfis de domínio com roaming). O valor é gravado em UTF-8 e os
buffers intermediários são zerados; falhas só informam o código do Windows, nunca a chave.

A tela é `ApiAccessWindow` (WPF-UI), aberta pelo botão "Configurações" da janela principal.
Ela só coleta o endereço e a API Key (campo de senha, sem binding) e delega ao `ApiAccessViewModel`.
O campo de senha é limpo antes da primeira espera. A chave transita como `string` apenas até o
núcleo entregá-la ao cofre e ao probe; o `string` é imutável e fica sob o coletor de lixo, então
não há como zerá-lo. A janela mostra a chave mascarada (últimos 4 caracteres), a mensagem de
validação em linguagem simples, e os comandos de substituir (salvar uma nova) e remover.

O app grava sem configuração. O envio (10.0) chama `ApiAccessViewModel.RequestSendAsync()`
antes de mostrar a confirmação. Sem acesso confirmado ele dispara `ConfigurationRequested`,
que `App.Compose` liga a `MainWindow.ShowSettings()`.

## Tela de envio (task 10.0)

Abaixo da lista, "Transcrever" (ou "Enviar de novo para transcrição", se a gravação já está
transcrita) age sobre a gravação selecionada. O fluxo está em `UploadScreenViewModel`
(`src/Presentation/Uploading`, sem tipos WPF); o host só tem a view (`MainWindow.xaml`) e a composição em `App.Compose`.

1. **Transcrever** verifica o acesso. Sem endereço/chave (ou com a chave recusada) abre as configurações e
   não mostra confirmação. Sem rede para verificar, a confirmação aparece e o envio informa a falha.
2. **Confirmação**: título, duração, tamanho aproximado e o aviso de que o áudio será processado pelo serviço
   configurado. Nada sai antes de "Enviar para transcrição". Gravação já transcrita exige marcar o reenvio.
3. **Enviando**: barra de progresso (percentual e MB) e "Cancelar". A lista mostra "Enviando" e, depois do
   aceite, "Processando".
4. **Falhas** usam as mensagens do núcleo, sem corpo de erro, chave ou URL de envio: 401 abre as configurações
   ("Abrir configurações"), 413/422 explicam o limite, 429 mostra "Disponível a partir das HH:mm", e falha de
   rede ou do serviço oferece "Tentar de novo" (manual, mesma chave de idempotência, sem laço). A gravação e o
   ponto do envio permanecem; reabrir o app não envia nada.

O transporte é o `UploadTransport` do núcleo com `HttpClient` real e a validação de certificado do Windows,
sem exceção para certificado inválido. O arquivo é lido em fluxo (hash e `PUT`), nunca inteiro na memória;
o teste do host envia 200 MB sintéticos e confere a alocação.

Fora desta task (12.0): depois do aceite a gravação fica em "Processando" e ninguém consulta o job; o
acompanhamento (`JobTracker`) e a notificação ao concluir ainda não estão ligados ao host. Uma gravação que ficou em
"Enviando" porque o app fechou no meio do envio continua mostrando "Enviando" até a próxima ação manual de
"Transcrever" (a retomada parcial do `PUT` também está fora do escopo).

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V05*" --minimum-expected-tests 4
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V06*" --minimum-expected-tests 3
```
