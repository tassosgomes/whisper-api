---
status: pending
task_kind: vertical
blocked_by: [12.0, 13.0]
gate: "dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V08*" --minimum-expected-tests 3"
gate_expect: "ao menos 3 testes do host de transcrição passam no Windows, 0 falham"
---

# 14.0 Ler, copiar e exportar a transcrição (host Windows)

**Fatia:** V-08 · **Cobre:** RF-07 · **Spec:** `techspec.md#v-08` · **ADR:** ADR-005
· **Ambiente:** Windows

## Comportamento

A tela de transcrição mostra o texto por trechos com o tempo, os comandos de copiar (com e sem tempos) usando a área de transferência do Windows e de exportar com o seletor de arquivo do sistema e a confirmação de sobrescrita. Abre com o serviço desligado.

**Validação manual nesta sessão Windows:** abrir uma transcrição com a API desligada, copiar e colar em outro programa, exportar para uma pasta existente com arquivo repetido.

## Fora do escopo desta task

Edição da transcrição e player sincronizado (non-goals).

## Decisões fechadas

A tela não consulta o serviço.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (host e testes do host; sem arquivo fora dele)
- **ref:** A ViewModel de transcrição (13.0)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-07)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Windows) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings; compila o host de verdade | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o núcleo continua passando no Windows | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do host passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0) no Windows: `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V08*" --minimum-expected-tests 3`
- [ ] Copiar e colar em outro programa e exportar com confirmação de sobrescrita foram validados e registrados.
