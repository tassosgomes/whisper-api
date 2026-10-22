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
