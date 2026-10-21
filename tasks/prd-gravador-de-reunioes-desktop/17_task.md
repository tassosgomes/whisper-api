---
status: pending
task_kind: vertical
blocked_by: [12.0, 16.0]
gate: "dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V10*" --minimum-expected-tests 3"
gate_expect: "ao menos 3 testes do instalador passam no Windows, 0 falham"
---

# 17.0 Instalar e desinstalar o app

**Fatia:** V-10 · **Cobre:** RF-09 · **Spec:** `techspec.md#v-10` · **ADR:** ADR-005
· **Ambiente:** Windows (Windows 11 limpo, máquina virtual ou outro PC)

## Comportamento

O usuário instala o app em um Windows 11 sem instalar driver, cabo virtual nem software de terceiros para capturar o áudio do sistema, e o app abre e grava. A desinstalação pergunta se as gravações e transcrições devem ser mantidas; ao reinstalar com os dados mantidos, as gravações voltam a aparecer. A instalação não exige direitos de administrador quando possível.

**Essa task é Should Have e pertence à Fase 2 do PRD; pode ser adiada sem bloquear as demais.** O formato do pacote e a ferramenta de instalação são decisões desta task, dentro da ADR-005.

**Validação manual:** instalar e gravar num Windows 11 limpo; desinstalar mantendo e apagando os dados.

## Fora do escopo desta task

Atualização automática e outros sistemas operacionais (non-goals).

## Decisões fechadas

Só Windows 11. A decisão da ferramenta de instalação fica registrada na task, sem exigir ADR nova a menos que mude a plataforma.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (host e projeto de instalador; sem arquivo fora dele)
- **modificar:** `README.md` (como gerar e instalar o pacote)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-09)
- **ref:** `docs/adr/adr-005.md`

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Windows) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings; compila o host de verdade | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o núcleo continua passando no Windows | EN-01 (4.0); CI ausente |
| Cliente .NET (Windows) | `dotnet test --project clients/windows-recorder/tests/Host.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do host passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [ ] Gate focalizado passa (exit 0) no Windows: `dotnet test --project clients/windows-recorder/tests/Host.Tests --filter-class "*V10*" --minimum-expected-tests 3`
- [ ] Instalação num Windows 11 limpo, gravação sem componentes extras, desinstalação com e sem manter os dados, e reinstalação foram validadas e registradas.
