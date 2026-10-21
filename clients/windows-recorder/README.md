# Gravador de reuniões para Windows

SDK .NET 10, WPF, Windows 11. O `global.json` da raiz seleciona o SDK 10
(`latestFeature`) e Microsoft.Testing.Platform. Execute os comandos abaixo da
raiz do repositório.

| Projeto | Alvo | Fronteira |
|---|---|---|
| `src/Core` | `net10.0` | Portas, tempo, diagnóstico e cliente da API gerado |
| `src/Presentation` | `net10.0` | ViewModels das próximas fatias, sem referências WPF |
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
nos arquivos gerados. O endereço base gerado é `/v1`; a fatia de configurações
deverá fornecer o endereço absoluto da implantação no adaptador, assim como a
autenticação. O smoke não envia requisições.

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
