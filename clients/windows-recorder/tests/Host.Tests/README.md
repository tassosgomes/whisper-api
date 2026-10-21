# Testes do host

Testes do host Windows (`net10.0-windows`), executados somente no Windows. As classes `V04*` cobrem a
captura WASAPI com fontes sintéticas, os indicadores, o atalho global e a confirmação ao fechar.
Não dependem de hardware nem de interação; o que usa WPF roda em thread STA explícita.

```powershell
dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1
dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V04*" --minimum-expected-tests 6
```
