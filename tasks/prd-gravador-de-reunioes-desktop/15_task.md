---
status: done
task_kind: vertical
blocked_by: [5.0]
gate: "dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V09*" --minimum-expected-tests 8"
gate_expect: "ao menos 8 testes do núcleo de gestão de gravações passam, 0 falham"
---

# 15.0 Gerenciar gravações e apagar os dados do app (núcleo)

**Fatia:** V-09 · **Cobre:** RF-04, RF-08, RN-G05 · **Spec:** `techspec.md#v-09` · **ADR:** ADR-005
· **Ambiente:** Linux

## Comportamento

Renomear altera o título da gravação nos metadados, e o título é opcional (o padrão é data e hora). Apagar uma gravação remove áudio, transcrição, JSON do resultado e metadados do disco e informa que não há como desfazer; com envio em andamento, exige uma confirmação específica avisando que o job no serviço não é cancelado, e não toca o serviço. "Apagar todos os dados do app" remove todas as gravações e transcrições locais, a configuração e a API Key do cofre (pela porta), depois de confirmação, e deixa o diretório do app vazio. A transcrição local de uma gravação não é afetada por nada que aconteça no serviço, e apagar a cópia local não afeta o serviço.

## Fora do escopo desta task

Player, lista e diálogos reais (16.0).

## Decisões fechadas

Apagar é definitivo e local (RN-G05). Nunca chama o serviço.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (núcleo, apresentação e testes; sem arquivo fora dele)
- **ref:** O repositório local da gravação (5.0)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-04, RF-08)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Linux) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do núcleo passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V09*" --minimum-expected-tests 8`
- [x] Apagar com envio em andamento exige a confirmação e não gera requisição; apagar tudo deixa o diretório vazio e remove a chave do dublê do cofre.
- [x] Renomear persiste e o título padrão aparece quando não há título.
