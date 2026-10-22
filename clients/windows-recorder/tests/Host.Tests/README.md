# Testes do host

Testes do host Windows (`net10.0-windows`), executados somente no Windows. As classes `V04*` cobrem a
captura WASAPI com fontes sintéticas, os indicadores, o atalho global e a confirmação ao fechar.
As classes `V05*` usam o Credential Manager real do usuário com alvos próprios de teste, removidos ao fim de cada teste.
Não dependem de hardware nem de interação; o que usa WPF roda em thread STA explícita.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V04*" --minimum-expected-tests 6
```

As classes `V07*` cobrem o acompanhamento no host: notificação ao concluir ou falhar com o app "minimizado" (dublê do
Windows), o toque na notificação selecionando a gravação, a lista trocando só a linha afetada (coalescida, sem reler o
disco), "sem conexão" sem virar falha e a retomada ao reabrir. Usam uma API em memória, relógio, agendador e conectividade
controlados e uma thread de interface própria; não tocam a rede, o Credential Manager nem a pasta real do app.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V07*" --minimum-expected-tests 3
```

As classes `V08*` cobrem a tela de transcrição: leitura dos arquivos locais com o serviço indisponível, cópia com e sem
tempos (dublê da porta e o clipboard real do Windows em thread STA, que restaura o texto anterior), nova tentativa
quando a área de transferência está ocupada, exportação com confirmação de sobrescrita (dublê de diálogo), transcrição
vazia e o caminho da notificação até a transcrição. Não tocam a rede, o Credential Manager nem a pasta real do app.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V08*" --minimum-expected-tests 3
```

As classes `V09*` cobrem o player (NAudio com WAV sintético e saída de mentira: tocar, pausar, avançar, voltar, fim do
áudio, erro em texto fixo e liberação do arquivo), a gestão na tela (renomear, apagar com e sem envio em andamento,
apagar tudo esvaziando a pasta e removendo a chave de um alvo de teste do Credential Manager, a trava durante a limpeza e
o arquivo aberto no player liberado antes de apagar) e o diário solto e retomado. Não tocam a rede, os alto-falantes, o
alvo de produção do cofre nem a pasta real do app.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V09*" --minimum-expected-tests 3
```
