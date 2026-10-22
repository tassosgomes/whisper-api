---
status: done
task_kind: vertical
blocked_by: [6.0, 7.0]
gate: "dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V05*" --minimum-expected-tests 4"
gate_expect: "ao menos 4 testes do host de configuração da API passam no Windows, 0 falham"
---

# 8.0 Configurar o acesso à API (host Windows)

**Fatia:** V-05 · **Cobre:** RF-01, RN-G07 · **Spec:** `techspec.md#v-05` · **ADR:** ADR-005
· **Ambiente:** Windows

## Comportamento

O host implementa a porta do cofre de credenciais com o Credential Manager do Windows, e a tela de configurações coleta endereço e API Key, mostra o resultado da validação (acesso confirmado, chave não aceita, não foi possível verificar) em linguagem simples, a chave mascarada e os comandos de substituir e remover. O app permite gravar sem configurar nada, e a configuração é pedida na hora do primeiro envio.

A chave sobrevive ao reinício do app, não aparece em arquivo, em log nem na tela, e a remoção apaga do cofre. Os testes do host usam o Credential Manager real do usuário de teste, com um nome de alvo próprio para teste que é limpo ao fim.

## Fora do escopo desta task

O envio (10.0). A validação e as regras de configuração (7.0).

## Decisões fechadas

O alvo do Credential Manager é próprio do app e distinto do alvo de teste. O host não reimplementa regra do núcleo.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (host e testes do host; sem arquivo fora dele)
- **ref:** O núcleo de configuração (7.0) e a porta do cofre (4.0)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-01)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Windows) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings; compila o host de verdade | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o núcleo continua passando no Windows | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do host passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0) no Windows: `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V05*" --minimum-expected-tests 4`
- [x] Salvar, reiniciar o app e ler a chave funciona; remover apaga do Credential Manager.
- [x] Validação manual na sessão Windows: chave válida, chave inválida e API desligada mostram as três mensagens corretas.
- [x] A chave não aparece em arquivo de configuração nem em log.
