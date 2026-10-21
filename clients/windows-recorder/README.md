# Gravador de reuniões para Windows

SDK .NET 10, WPF, Windows 11. O `global.json` da raiz seleciona o SDK 10
(`latestFeature`) e Microsoft.Testing.Platform. Execute os comandos abaixo da
raiz do repositório.

| Projeto | Alvo | Fronteira |
|---|---|---|
| `src/Core` | `net10.0` | Portas, gravação local, tempo, diagnóstico e cliente da API gerado |
| `src/Presentation` | `net10.0` | ViewModel de gravação, sem referências WPF |
| `src/Host` | `net10.0-windows` | Shell WPF com WPF-UI; adaptadores Windows nas próximas fatias |
| `tests/Core.Tests` | `net10.0` | Núcleo e apresentação, xUnit/MTP e dublês das portas |
| `tests/Host.Tests` | `net10.0-windows` | Projeto reservado aos testes Windows; ainda sem testes |

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
O projeto `Host.Tests` fica sem testes até as fatias Windows; sua execução vazia
também reprova, por isso o CI Linux seleciona apenas `Core.Tests`.

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
