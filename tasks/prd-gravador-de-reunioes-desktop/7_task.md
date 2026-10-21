---
status: pending
task_kind: vertical
blocked_by: [4.0]
gate: "dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V05*" --minimum-expected-tests 8"
gate_expect: "ao menos 8 testes do núcleo de configuração da API passam, 0 falham"
---

# 7.0 Configurar o acesso à API (núcleo)

**Fatia:** V-05 · **Cobre:** RF-01, RN-G07 · **Spec:** `techspec.md#v-05` · **ADR:** ADR-005
· **Ambiente:** Linux

## Comportamento

O núcleo valida o acesso informando endereço e API Key e consultando `getTranscription` com um identificador que não existe: `401` significa chave não aceita; `404` significa chave aceita; erro de rede, tempo esgotado ou `5xx` significam que não foi possível verificar, nunca chave inválida. Chave revogada ou de conta desativada responde como `401`. Só uma chave aceita é salva: a chave vai à porta do cofre de credenciais e o endereço vai à configuração comum, que nunca contém a chave. Substituir e remover a chave são operações do núcleo; a remoção apaga do cofre.

O estado de acesso é exposto como confirmado, recusado, não verificável ou ausente, e a ViewModel mostra apenas os últimos caracteres da chave. O estado de acesso ausente permite gravar e só bloqueia o envio, levando o usuário à configuração. A chave nunca aparece em log, mensagem de erro nem arquivo de configuração. Os testes usam a API real do repositório em processo, com a fronteira de rede controlada para os casos de indisponibilidade, e um dublê do cofre.

## Fora do escopo desta task

O cofre real do Windows e a tela de configurações (8.0). O envio de áudio (9.0).

## Decisões fechadas

A validação usa `getTranscription` com identificador inexistente (TechSpec, Decisões Técnicas); o contrato não ganha operação nova. Uma chave por instalação. A API Key é segredo do usuário (RN-G07; baseline, seção Clientes do Whisper).

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (núcleo, apresentação e testes; sem arquivo fora dele)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml` (`getTranscription`, `Unauthorized`, `NotFound`)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-01, RN-G07)
- **ref:** A porta do cofre de credenciais definida em 4.0

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Linux) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do núcleo passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0): `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V05*" --minimum-expected-tests 8`
- [ ] Chave válida, revogada, de conta desativada e API fora do ar produzem os quatro resultados distintos, e a API fora do ar não é tratada como chave inválida.
- [ ] Após salvar, o arquivo de configuração e o diário do teste não contêm a chave; a ViewModel mostra só os últimos caracteres.
- [ ] Substituir e remover a chave refletem no estado de acesso e no dublê do cofre.
- [ ] O adaptador de rede real é exercitado contra a API real do repositório, não só com dublê.
