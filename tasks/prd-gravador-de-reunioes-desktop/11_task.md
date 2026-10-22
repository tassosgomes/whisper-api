---
status: done
task_kind: vertical
blocked_by: [9.0]
gate: "dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V07*" --minimum-expected-tests 12"
gate_expect: "ao menos 12 testes do núcleo de acompanhamento passam, 0 falham"
---

# 11.0 Acompanhar o job e guardar a transcrição (núcleo)

**Fatia:** V-07 · **Cobre:** RF-06, RF-07, RN-G05, RN-G06, RN-G08 · **Spec:** `techspec.md#v-07` · **ADR:** ADR-005
· **Ambiente:** Linux

## Comportamento

Para toda gravação com envio em andamento, o acompanhador consulta `getTranscription` em intervalos crescentes até um estado terminal, com recuo diante de erro temporário e sem consultar enquanto não há conexão, quando mostra "sem conexão" sem marcar falha. Ao abrir o app, retoma as gravações em `uploading` ou `processing`. O estado do job se reflete na situação da gravação sem recarregar a lista.

Em `completed`, busca `getTranscriptionResult` e grava ao lado do áudio o JSON original (`result-v1.json`) e o arquivo `.txt`, com um trecho por linha e o tempo inicial relativo à gravação, e marca a gravação como `transcribed`. Em `failed`, guarda o código e marca `failed`, com mensagem em linguagem simples e a opção de novo envio. Se o job responde `404` depois de um envio aceito (resultado expirado antes de o app coletá-lo), marca `result-expired` e informa que é preciso enviar de novo, sem afirmar que a gravação se perdeu. Resultado com `schemaVersion` desconhecida guarda o JSON bruto, sinaliza que o app precisa ser atualizado e não descarta a gravação nem o resultado. Transcrição vazia é guardada e sinalizada como "nenhuma fala reconhecida". O acompanhador só usa o estado público do job e não depende de webhook.

O núcleo dispara a notificação pela porta do notificador ao concluir ou falhar. Os testes usam a API real do repositório em processo e um relógio controlado.

## Fora do escopo desta task

A notificação real do Windows e a lista na tela (12.0). A leitura, a cópia e a exportação (13.0, 14.0).

## Decisões fechadas

O acompanhamento é por consulta (RN-G08). A cópia local pertence ao usuário e independe da retenção do serviço (RN-G05). Intervalos crescentes com teto, definidos e testados aqui; sem hammering da API (RN-G06).

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (núcleo, apresentação e testes; sem arquivo fora dele)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml` (`getTranscription`, `getTranscriptionResult`)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-06, RF-07, RN-G05, RN-G06, RN-G08)
- **ref:** O coordenador de envio (9.0) e a porta do notificador (4.0)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Linux) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do núcleo passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V07*" --minimum-expected-tests 12`
- [x] Ciclo completo contra a API real do repositório: envio, `processing`, `completed`, `.txt` e `result-v1.json` gravados e gravação `transcribed`.
- [x] Reabrir o app no meio do `processing` retoma o acompanhamento; backoff após erro temporário; sem conexão não marca falha.
- [x] `404` após envio aceito vira `result-expired`; `failed` guarda o código; `schemaVersion` desconhecida preserva o JSON e a gravação.
- [x] Transcrição vazia é guardada e sinalizada; o `.txt` tem o tempo de cada trecho.
