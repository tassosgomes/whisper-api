---
status: done
task_kind: vertical
blocked_by: [11.0]
gate: "dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V08*" --minimum-expected-tests 8"
gate_expect: "ao menos 8 testes de leitura e exportação passam, 0 falham"
---

# 13.0 Ler, copiar e exportar a transcrição (núcleo e apresentação)

**Fatia:** V-08 · **Cobre:** RF-07, RN-G05 · **Spec:** `techspec.md#v-08` · **ADR:** ADR-005
· **Ambiente:** Linux

## Comportamento

A ViewModel de transcrição lê o `.txt` e o JSON locais, sem consultar o serviço, e expõe o texto por trechos em ordem, com o tempo de cada um. Copiar entrega o texto completo, com ou sem os tempos, conforme a escolha. Exportar grava o texto no caminho escolhido e, se o arquivo já existe, só sobrescreve depois de confirmação. Transcrição vazia mostra "nenhuma fala foi reconhecida". Transcrição de um resultado expirado no serviço abre normalmente. Resultado com `schemaVersion` desconhecida informa que o app precisa ser atualizado e não esconde a gravação.

## Fora do escopo desta task

A tela e a área de transferência reais (14.0).

## Decisões fechadas

Leitura sempre do disco local (RN-G05). Sem edição de texto (feature futura).

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (núcleo, apresentação e testes; sem arquivo fora dele)
- **ref:** O formato do `.txt` e do JSON gravados em 11.0
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-07)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Linux) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do núcleo passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V08*" --minimum-expected-tests 8`
- [x] Cópia com e sem tempos; exportação com e sem arquivo existente; transcrição vazia; `schemaVersion` desconhecida.
- [x] Nenhum teste faz requisição de rede.
