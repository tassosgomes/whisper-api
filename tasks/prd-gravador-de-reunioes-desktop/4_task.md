---
status: done
task_kind: enabling
blocked_by: []
gate: "dotnet build clients/windows-recorder -warnaserror"
gate_expect: "build sem erros e 0 warnings no Linux; o projeto de testes do núcleo executa 1 teste de fumaça com o runner Microsoft.Testing.Platform"
---

# 4.0 Preparar a solução do cliente e a execução dos testes no Linux

**Fatia:** EN-01 · **Cobre:** RF-09 (base do instalador), RN-G07 (base do registro sem segredo) · **Spec:** `techspec.md#habilitadores-inevitáveis` · **ADR:** ADR-005
· **Ambiente:** Linux

## Comportamento

**Habilitador.** Não cabe numa fatia porque as fatias V-04 a V-10 compartilham a estrutura da solução, o cliente da API e a execução dos testes no Linux. Desbloqueia V-04 (5.0) e V-05 (7.0).

Entrega uma solução em `clients/windows-recorder/` com os projetos descritos na TechSpec: núcleo (`net10.0`), apresentação (`net10.0`), host (`net10.0-windows`, WPF) e projetos de teste do núcleo e do host. O núcleo define as portas que o host implementa (captura de áudio, cofre de credenciais, notificador, reprodutor, relógio e agendador). O cliente da API é gerado de `api-contract.yaml`. O runner de testes é o Microsoft.Testing.Platform, configurado no repositório para que `--minimum-expected-tests` falhe quando um filtro não selecionar nada; esta task fixa e documenta o comando exato de filtro por classe que as demais tasks usam. Há um registro de diagnóstico sem conteúdo sensível (sem áudio, texto, URL de envio nem API Key).

No Linux, `dotnet build` compila o núcleo e a apresentação. O host é compilado com alvo Windows habilitado (`EnableWindowsTargeting`) quando isso funcionar; se a compilação de XAML falhar fora do Windows, esta task registra isso e o build do host passa a ser exclusivo do Windows, sem mudar as fatias. Um fluxo de CI no repositório executa build, formatação e testes do núcleo no Linux.

## Fora do escopo desta task

Qualquer comportamento do cliente. O projeto de testes tem só um teste de fumaça, para provar o runner e o filtro.

## Decisões fechadas

ADR-005: .NET e WPF, código em `clients/windows-recorder/`. `net10.0` e `net10.0-windows`. ViewModels no projeto de apresentação, sem tipos de WPF. WPF-UI como tema (a licença não foi verificada; registre-a). O cliente consome só o contrato público e não importa código do serviço Python.

## Modificar / Referenciar

- **modificar:** `README.md` (como compilar e testar o cliente no Linux e no Windows)
- **modificar:** `.gitignore` (artefatos de build do .NET)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml` (fonte do cliente da API)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/techspec.md` (blocos Frontend, Contratos e Fronteiras, Interfaces entre fatias)
- **ref:** `docs/adr/adr-005.md`
- **ref:** `context/architecture-baseline.md` (seção Clientes do Whisper)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Linux) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings | esta task; CI ausente |
| Cliente .NET (Linux) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | esta task; CI ausente |
| Cliente .NET (Linux) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; o filtro e o mínimo esperado falham quando não selecionam nada | esta task; CI ausente |
| API Python | `.venv/bin/pytest -q` | exit 0; a base não é afetada | `pyproject.toml` |

## Pronto quando

- [x] Gate de build passa (exit 0): `dotnet build clients/windows-recorder -warnaserror`
- [x] O teste de fumaça roda com `--minimum-expected-tests 1`, e um filtro que não casa com nada reprova com exit diferente de zero.
- [x] A geração do cliente da API a partir de `api-contract.yaml` é reproduzível por um comando documentado, e o código gerado compila.
- [x] As portas do host estão definidas no núcleo e têm dublês para os testes.
- [x] Está registrado se o host compila no Linux com alvo Windows, e o comando de filtro por classe que as demais tasks usam.
- [x] O fluxo de CI executa build, formatação e testes do núcleo.
