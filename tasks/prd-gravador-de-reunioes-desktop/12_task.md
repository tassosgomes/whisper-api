---
status: pending
task_kind: vertical
blocked_by: [10.0, 11.0]
gate: "dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V07*" --minimum-expected-tests 3"
gate_expect: "ao menos 3 testes do host de acompanhamento passam no Windows, 0 falham"
---

# 12.0 Acompanhar o job e guardar a transcrição (host Windows)

**Fatia:** V-07 · **Cobre:** RF-06 · **Spec:** `techspec.md#v-07` · **ADR:** ADR-005
· **Ambiente:** Windows

## Comportamento

O host implementa o notificador com notificação do Windows ao concluir ou falhar, mesmo com o app minimizado, e a lista de gravações mostra a situação do job, o aviso "sem conexão" e as mensagens de falha e de resultado expirado. Ao tocar na notificação, o app abre a gravação correspondente.

**Validação manual nesta sessão Windows:** enviar uma gravação, minimizar o app e confirmar a notificação ao concluir; fechar o app durante o `processing`, reabrir e ver a gravação concluída.

## Fora do escopo desta task

Leitura, cópia e exportação (13.0, 14.0).

## Decisões fechadas

A notificação usa a porta do notificador do núcleo; o host não decide quando notificar.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (host e testes do host; sem arquivo fora dele)
- **ref:** O acompanhador do núcleo (11.0) e a porta do notificador (4.0)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-06)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Windows) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings; compila o host de verdade | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o núcleo continua passando no Windows | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do host passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0) no Windows: `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V07*" --minimum-expected-tests 3`
- [ ] A notificação aparece com o app minimizado e abre a gravação ao ser tocada.
- [ ] A validação manual do ciclo completo foi feita e registrada.
