---
status: pending
task_kind: vertical
blocked_by: [14.0, 15.0]
gate: "dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V09*" --minimum-expected-tests 3"
gate_expect: "ao menos 3 testes do host de gestão de gravações passam no Windows, 0 falham"
---

# 16.0 Gerenciar gravações e apagar os dados do app (host Windows)

**Fatia:** V-09 · **Cobre:** RF-04, RF-08 · **Spec:** `techspec.md#v-09` · **ADR:** ADR-005
· **Ambiente:** Windows

## Comportamento

A lista mostra os comandos de renomear e apagar com os diálogos de confirmação do núcleo, o player reproduz o WAV com controle de avançar e voltar, e as configurações oferecem "apagar todos os dados do app". O app informa onde os dados ficam no PC.

**Validação manual nesta sessão Windows:** reproduzir uma gravação, avançar e voltar; apagar uma gravação; apagar tudo e conferir que a chave sumiu do Credential Manager.

## Fora do escopo desta task

Instalador (17.0).

## Decisões fechadas

O player implementa a porta do reprodutor do núcleo.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (host e testes do host; sem arquivo fora dele)
- **ref:** A gestão de gravações do núcleo (15.0) e a porta do reprodutor (4.0)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-04, RF-08)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Windows) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings; compila o host de verdade | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o núcleo continua passando no Windows | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do host passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0) no Windows: `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V09*" --minimum-expected-tests 3`
- [ ] Reprodução com avançar e voltar, apagar e apagar tudo foram validados e registrados, incluindo a remoção da chave no cofre.
