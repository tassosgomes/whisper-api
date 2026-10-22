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

Ícone do app (`assets/icon/icon.ico`): usado no `.exe` (`ApplicationIcon`), no recurso `Assets/icon.ico` das janelas `MainWindow` e `ApiAccessWindow` (`Icon` e `TitleBar.Icon`) e na bandeja em repouso (`RecorderIcons.RenderTrayIcon`); gravação e pausa sobrepõem o indicador sobre ele, e a barra de tarefas mantém só o overlay.

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
A tela, os diálogos e a área de transferência reais estão no host (14.0, abaixo).

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

Depois do aceite a gravação fica em "Processando" e o acompanhamento (V-07, abaixo) segue o job. Uma gravação que
ficou em "Enviando" porque o app fechou no meio do envio é marcada pelo acompanhamento como envio interrompido
("Envio não concluído"), com "Transcrever" para enviar de novo (a retomada parcial do `PUT` está fora do escopo).

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V05*" --minimum-expected-tests 4
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V06*" --minimum-expected-tests 3
```

### Host: acompanhamento, lista e notificação (12.0)

`App.Compose` cria um único `JobTracker` com o mesmo `RecordingRepository` do envio, `TrackingTransport`,
`UploadTransport`, `SystemClock`, `SystemScheduler`, `WindowsConnectivity` e o `TrayIndicator` como `INotifier`.
O `TrackingViewModel` (Presentation) inicia `JobTracker.RunAsync` em segundo plano na abertura do app, o que retoma
as gravações `uploading`/`processing` do disco, e segue os envios novos sozinho (a passagem local é de 2 segundos).
`Wake()` é chamado quando um envio é aceito: só importa se o acompanhamento estiver parado esperando para recomeçar
depois de um erro inesperado (pausa de 10 s). Ele é cancelado antes de liberar a bandeja, no fechamento do app.

- **Notificação:** é o balão do `NotifyIcon` que já existe na bandeja, sem pacote novo. O toast nativo (Windows App
  SDK ou `Microsoft.Toolkit.Uwp.Notifications`) exigiria identidade de pacote/atalho com AppUserModelID e COM de
  ativação para o app desempacotado, e traria uma dependência pesada; no Windows 10/11 o balão aparece como notificação
  do sistema, fica na central de notificações e funciona com o app minimizado ou oculto. O `INotifier` ganhou o
  parâmetro opcional `recordingId`: o núcleo decide quando notificar e diz a qual gravação; o texto é fixo, sem
  conteúdo da reunião. Chamado de outra thread, o `TrayIndicator` repassa o aviso para a thread da interface.
- **Toque na notificação:** `TrayIndicator.NotificationActivated` leva a gravação do último aviso de conclusão/falha
  para `NotificationActivation` (Presentation), que mostra a janela (`ShowAndActivate`) e chama
  `UploadScreenViewModel.SelectRecording`; a janela destaca a linha e rola até ela.
- **Lista:** `RecordingListItem` ganhou `Detail` (mensagem do núcleo: processando, na fila, falha com a causa,
  resultado expirado, "Sem conexão") e `IsOffline`. `RecordingScreenViewModel.ApplyTracking` recebe cada
  `TrackingStatus` de qualquer thread, guarda só a última de cada gravação, e a cada 80 ms troca na lista em memória
  apenas as linhas afetadas, sem reler o disco (como `RefreshRecordings`, usa uma única atualização da interface).
  "Sem conexão" é aviso: o estado continua `processing`, sem falha e sem aviso do Windows.
- **Conectividade:** `WindowsConnectivity` guarda o valor atualizado por `NetworkChange.NetworkAvailabilityChanged` e
  `NetworkAddressChanged`, em vez de enumerar adaptadores a cada passagem.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V07*" --minimum-expected-tests 3
```

Validação manual: `tasks/prd-gravador-de-reunioes-desktop/12.0_manual_validation.md`.

### Host: tela de transcrição (14.0)

A tela é a janela `TranscriptWindow` (host), ligada ao `TranscriptScreenViewModel` (Presentation, sem tipos WPF), que
envolve o `TranscriptViewModel` da 13.0. Lê só os arquivos locais (`result-v1.json` e `transcription.txt`): não recebe
configuração, cofre nem cliente da API, então abre com o serviço desligado, sem rede e depois da retenção de 24 horas.

- **Abrir:** o botão **Abrir transcrição** (ou Enter / duplo clique na linha) fica ativo para a gravação selecionada
  com situação "Transcrita" (`UploadScreenViewModel.OpenTranscript` / `TranscriptRequested`). Tocar na notificação da
  12.0 seleciona a gravação, e daí o mesmo botão leva à transcrição. A leitura roda fora da thread da interface
  (`TranscriptViewModel.OpenAsync`); a tela mostra "Carregando a transcrição…" e só a leitura mais recente vale.
- **Trechos:** uma lista com o tempo (`HH:mm:ss.fff`) e o texto de cada trecho; cada item tem nome de acessibilidade
  ("tempo: texto"). Sem nenhuma fala, aparece a mensagem própria "Nenhuma fala foi reconhecida nesta gravação." e copiar
  e exportar ficam desligados. Versão futura do resultado ou arquivo ilegível mostram um texto fixo, sem caminhos.
- **Copiar:** `WindowsClipboard` (porta `ITranscriptClipboard`) usa `Clipboard.SetDataObject(texto, copy: true)` na thread
  da interface. Se outro programa está com a área de transferência (`COMException`, em geral `CLIPBRD_E_CANT_OPEN`),
  tenta 3 vezes com 100 ms de espera sem bloquear a tela; só então informa "Não foi possível copiar". O WPF também
  tenta internamente, então, no pior caso, a interface pode ficar parada por alguns instantes enquanto outro programa
  segura a área de transferência.
- **Exportar:** `WindowsTranscriptDialogs` (porta `ITranscriptDialogs`) abre o `SaveFileDialog` do sistema (aviso nativo
  de sobrescrita desligado) e, quando o núcleo responde `ConfirmationRequired`, pede **uma** confirmação própria
  ("Substituir o arquivo?"); só então chama a exportação com `overwriteConfirmed: true`. Cancelar mantém o arquivo.
  A gravação do arquivo também roda fora da thread da interface (`TranscriptViewModel.ExportAsync`).
- **Teclado:** Ctrl+C copia com tempos, Ctrl+Shift+C copia sem tempos, Ctrl+S exporta, Esc fecha; os atalhos usam os
  mesmos comandos dos botões. O foco vai para a lista de trechos (ou para **Fechar**, sem trechos) e o resultado de
  cada ação fica numa região de leitura educada para leitores de tela.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V08*" --minimum-expected-tests 3
```

O teste com o clipboard real devolve o texto anterior da área de transferência e não toca em conteúdo que não seja
texto; gerenciadores de histórico da área de transferência (Win+V) podem guardar o texto do teste.
Validação manual: `tasks/prd-gravador-de-reunioes-desktop/14.0_manual_validation.md`.

### Host: player e gestão dos dados (16.0)

**Player.** `NAudioAudioPlayer` (`src/Host/Playback`) implementa `IAudioPlayer` sobre o NAudio: `WaveFileReader` lê o WAV
(16 kHz, mono, 16 bits) e `WaveOut` (NAudio.WinMM, o Windows adapta o formato ao dispositivo) toca. Foi escolhido no lugar do
`MediaPlayer` do WPF porque não depende de dispatcher nem de janela, o arquivo só fica aberto entre `OpenAsync` e
`DisposeAsync` (apagar a gravação exige o WAV fechado, e o `MediaPlayer` o solta sem prazo definido), a posição é exata e a
busca é síncrona, e a saída é injetável (`Func<IWavePlayer>`): os testes usam uma saída de mentira, sem alto-falantes.
`PlayerScreenViewModel` (Presentation) cria **um player por gravação aberta**; `ReleaseAsync` fecha o arquivo e só termina
quando ele foi liberado. A janela `PlayerWindow` tem **Reproduzir/Pausar** (Espaço), **Voltar 10 s** e **Avançar 10 s**
(Ctrl+setas), um controle deslizante de posição (setas movem 5 s), a posição e a duração e a situação em texto
("Reproduzindo", "Pausado", "Parado", "Fim da gravação"). Um temporizador de 250 ms só existe com a janela aberta. O botão
**Reproduzir** da lista (Ctrl+P) abre a gravação selecionada.

**Renomear e apagar.** Na lista: **Renomear** (F2), **Apagar** (Delete) e **Reproduzir**, todos comandos do
`RecordingManagementScreenViewModel`. Renomear abre `RenameWindow` (Enter salva, Esc cancela, vazio volta ao título padrão de
data e hora). Apagar mostra a confirmação do núcleo (caixa nativa com "Não" como padrão) e, se `HasPendingTranscription`, um
segundo aviso: só a cópia local sai e o job no serviço **não** é cancelado. O envio local dessa gravação é interrompido, a
janela de transcrição e o player dela são fechados e liberados, e só então o núcleo apaga (`pendingTranscriptionConfirmed`
vale apenas depois do segundo aceite; se um envio começar depois do primeiro diálogo, o aviso é mostrado de novo). A lista
usa `DisplayTitle` e é atualizada por `RefreshRecordings` (coalescido, com sequência).

**Apagar todos os dados.** Fica nas **Configurações**, junto com o texto "onde os dados ficam" (pasta do app e Gerenciador de
Credenciais). Após o aceite: o envio local é cancelado, a janela de transcrição e o player são fechados, o diário é solto e
o núcleo remove a chave do Credential Manager e esvazia a pasta do app. Depois do sucesso a lista é atualizada, o estado
de configuração é reverificado (volta a "Absent") e os campos da tela de configuração são limpos.

**Diário.** `ReleasableLogWriter` substitui o `StreamWriter` do `diagnostics.log`: abre sob demanda (acrescentando, leitura
compartilhada), grava cada linha direto e nunca lança erro de disco. `Release()` fecha o arquivo e descarta as linhas
até `Resume()`. Após "apagar tudo" com sucesso o diário **não** é retomado até o app ser reiniciado, para a pasta
permanecer vazia (inclusive o `ApplicationStopped` na saída); se a limpeza falhar, `Resume()` religa o diário.

**Bloqueios.** `DataOperationGate` (Presentation) é única e compartilhada: enquanto uma operação sobre os dados roda (dos
diálogos ao fim), `RecordingScreenViewModel` não inicia gravação (botão, atalho global e bandeja passam por `StartAsync`),
`UploadScreenViewModel` não permite Transcrever, `ApiAccessViewModel` ignora salvar/remover a chave e a janela de
configurações desliga os botões. Gravação ativa desliga renomear, apagar e apagar tudo, com o motivo em texto; o núcleo
ainda responde `RecordingInProgress` se a gravação começar depois do diálogo.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V09*" --minimum-expected-tests 3
```

Os testes V09 do host usam pasta temporária, o `NAudioAudioPlayer` com WAV sintético e saída de mentira, o diário real em
pasta temporária e, só no "apagar tudo", um alvo de teste do Credential Manager (`Whisper.Recorder.Test/ApiKey/V09/...`)
removido ao fim; nunca tocam o alvo `Whisper.Recorder/ApiKey` nem `%LOCALAPPDATA%\Whisper\Recorder`. O caminho com o
dispositivo de áudio real (`WaveOut`) só é exercitado na validação manual:
`tasks/prd-gravador-de-reunioes-desktop/16.0_manual_validation.md`.

**Ajustes da validação da 16.0.** `ForgetRecording(id)` só cancela o envio e fecha o painel quando `id` é a gravação ativa
do painel (ou em "apagar tudo"); apagar a gravação selecionada não interrompe o envio de outra. A trava
(`DataOperationGate`) é liberada num `finally` mesmo se o host ou a lista falharem. O player lê e busca pelo mesmo
`LockedProvider` (a thread da saída lê o `WaveFileReader` enquanto a interface busca). `WaveOut` do NAudio 3 já é o antigo
`WaveOutEvent` (callback por evento, sem janela), criável e descartável em qualquer thread, e o descarte roda fora da
interface (`ReleaseAsync`). O núcleo remove junções e links simbólicos dentro dos dados do app **como links**, sem seguir
para o destino (`Directory.Delete` recursivo do .NET falha com acesso negado em link aninhado); o teste usa `mklink /J`.

## Instalador do Windows (V-10, task 17.0)

Pacote feito com o Inno Setup 6, instalado **por usuário, sem direitos de administrador**, só para Windows 11 x64. O
`dotnet publish` é self-contained (.NET embutido, sem trimming porque o WPF não o suporta): um Windows 11 limpo não precisa
instalar driver, cabo virtual nem runtime.

**Gerar o pacote.** Pré-requisito: Inno Setup 6 (`winget install JRSoftware.InnoSetup --scope user`).

```powershell
powershell -File clients/windows-recorder/installer/build-installer.ps1 -Version 1.0.0
```

O script publica o Host em `clients/windows-recorder/artifacts/publish/win-x64`, procura o `ISCC.exe` (`-IsccPath`, PATH,
`%LOCALAPPDATA%\Programs\Inno Setup 6`, `Program Files (x86)`), compila `installer/WhisperRecorder.iss` e imprime o caminho
`artifacts/installer/WhisperRecorder-Setup-<versão>.exe` (pasta ignorada pelo git).

**Instalação.** Em `%LOCALAPPDATA%\Programs\Whisper Recorder` (`{autopf}` vira a pasta de programas do usuário com
`PrivilegesRequired=lowest`), atalho no menu Iniciar, atalho opcional na área de trabalho e opção de abrir o app ao final.
Instalar sobre uma versão anterior atualiza no lugar (`AppId` fixo). Com o app aberto o instalador pede para fechá-lo
(`AppMutex` = o mutex da instância única, `UninstallDataPurge.AppMutexName`). O pacote nunca inclui dados do usuário.

**Desinstalação.** Pergunta "Manter as gravações e transcrições neste PC?" (padrão: Sim).
- **Sim:** remove só o programa; gravações, transcrições, configurações e diário continuam em
  `%LOCALAPPDATA%\Whisper\Recorder` e a chave no Credential Manager, e voltam a aparecer ao reinstalar.
- **Não:** o desinstalador roda `WhisperRecorder.exe --purge-data` (modo sem janela, `UninstallDataPurge`): recusa se o app estiver
  aberto (código 2), recupera gravações interrompidas, reaproveita o "apagar tudo" da 16.0 (chave primeiro, junções sem
  seguir o destino) e remove a pasta de dados e a pasta `Whisper` vazias. Se falhar (código diferente de 0), o
  desinstalador avisa onde estão os dados para apagar à mão.
- **Silenciosa** (`/SILENT`, `/VERYSILENT`): sempre mantém os dados e nunca apaga.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V10*" --minimum-expected-tests 3
```

Os testes V10 usam pasta temporária, alvo de teste do Credential Manager (`Whisper.Recorder.Test/Installer/...`), um mutex
de teste e conferem o contrato do `.iss` (sem elevação, Windows 11, mesmo mutex e argumento do app, guarda de
desinstalação silenciosa). A instalação e a desinstalação de verdade só são exercitadas na validação manual (Windows 11
limpo). O `.iss` é UTF-8 com BOM (necessário para os acentos no Inno Setup).
