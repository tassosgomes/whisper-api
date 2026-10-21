---
status: pending
task_kind: vertical
blocked_by: [5.0]
gate: "dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V04*" --minimum-expected-tests 6"
gate_expect: "ao menos 6 testes do host da gravação passam no Windows, 0 falham"
---

# 6.0 Gravar a reunião e guardar a gravação no PC (host Windows)

**Fatia:** V-04 · **Cobre:** RF-02, RF-03, RF-04, RN-G01 · **Spec:** `techspec.md#v-04` · **ADR:** ADR-005
· **Ambiente:** Windows (sessão do Claude no Windows 11, com microfone e saída de áudio)

## Comportamento

O host implementa a porta de captura com WASAPI: o microfone e o áudio do sistema (loopback do dispositivo de saída padrão), com listagem e troca de dispositivos, nível de cada fonte, eventos de perda e volta de dispositivo e entrega de blocos PCM com marcação de tempo ao núcleo (5.0). Não há driver nem cabo virtual. A tela de gravação mostra as fontes e o nível de cada uma antes de gravar, o contador, o aviso de gravação de terceiros a cada início, e os controles de iniciar, pausar, retomar e parar. Quando o Windows nega o acesso ao microfone, a tela explica como liberar e permite gravar só o áudio do sistema se houver saída.

Enquanto grava, o app deixa isso evidente com a janela minimizada: indicador de gravação e tempo na barra de tarefas e na área de notificação, e atalho global para iniciar e parar. Fechar o app durante a gravação pergunta se quer parar e salvar ou continuar, e nunca descarta sem confirmação. A lista de gravações mostra título, data, duração e situação, com tema Fluent do WPF-UI, operação por teclado e estados que não dependem só de cor.

**Validação manual nesta sessão Windows:** gravar 5 minutos de uma reunião real, falando e com áudio tocando no PC, e conferir as duas vozes na reprodução; desconectar o fone no meio e confirmar que a gravação continua; encerrar o app à força e confirmar a recuperação como interrompida.

## Fora do escopo desta task

Reprodução com avançar e voltar, renomear e apagar (16.0). Envio, configurações da API e notificação do job (8.0, 10.0, 12.0). Instalador (17.0).

## Decisões fechadas

ADR-005 (WASAPI, sem driver de terceiros). A captura só entrega dados enquanto algo toca; o núcleo preenche o silêncio, o host não. O host implementa as portas do núcleo e não duplica regra de gravação.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (host, testes do host e ViewModels de gravação; sem arquivo fora dele)
- **ref:** O núcleo de gravação (5.0) e a porta de captura (4.0)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/techspec.md` (Captura, Tema)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-02, RF-03, RF-04, Experiência do Usuário)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Windows) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings; compila o host de verdade | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o núcleo continua passando no Windows | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do host passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0) no Windows: `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V04*" --minimum-expected-tests 6`
- [ ] Os testes do host exercitam a mixagem do host com fontes sintéticas do WASAPI substituíveis, o evento de perda de dispositivo e o indicador de gravação.
- [ ] A validação manual de 5 minutos com as duas vozes, a desconexão do fone e o encerramento à força foi feita e registrada na task.
- [ ] A gravação em segundo plano mostra o indicador na barra de tarefas e na bandeja, e o atalho global inicia e para.
