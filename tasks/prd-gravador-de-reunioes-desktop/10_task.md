---
status: pending
task_kind: vertical
blocked_by: [8.0, 9.0]
gate: "dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V06*" --minimum-expected-tests 3"
gate_expect: "ao menos 3 testes do host de envio passam no Windows, 0 falham"
---

# 10.0 Enviar uma gravação para transcrição (host Windows)

**Fatia:** V-06 · **Cobre:** RF-05, RF-08 · **Spec:** `techspec.md#v-06` · **ADR:** ADR-005
· **Ambiente:** Windows

## Comportamento

A tela da gravação oferece Transcrever, mostra o que será enviado e o aviso de que o áudio será processado pelo serviço configurado, e pede a confirmação. Durante o envio mostra o progresso, o botão de tentar de novo nas falhas e as mensagens do núcleo em português. O arquivo é lido em fluxo, sem carregar a gravação inteira na memória, e o `PUT` aceita o certificado do armazenamento pela validação normal do Windows.

**Validação manual nesta sessão Windows:** enviar uma gravação de 5 minutos para a API do ambiente (compose ou o armazenamento do piloto) e ver o job em `queued` e depois `processing`; derrubar a rede no meio do envio e tentar de novo.

## Fora do escopo desta task

Acompanhamento e notificação (12.0). Retomada parcial do `PUT`.

## Decisões fechadas

O arquivo não é carregado inteiro na memória. A validação do certificado é a do Windows, sem exceção para certificado inválido.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (host, telas e testes do host; sem arquivo fora dele)
- **ref:** O coordenador de envio (9.0) e a configuração de acesso (8.0)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-05)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Windows) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings; compila o host de verdade | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o núcleo continua passando no Windows | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do host passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0) no Windows: `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V06*" --minimum-expected-tests 3`
- [ ] O envio de um arquivo grande sintético mantém o consumo de memória estável.
- [ ] A validação manual do envio de 5 minutos e da queda de rede foi feita e registrada.
