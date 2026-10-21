---
status: done
task_kind: vertical
blocked_by: [4.0]
gate: "dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V04*" --minimum-expected-tests 12"
gate_expect: "ao menos 12 testes do núcleo da gravação passam, 0 falham"
---

# 5.0 Gravar a reunião e guardar a gravação no PC (núcleo)

**Fatia:** V-04 · **Cobre:** RF-02, RF-03, RF-04, RN-G01, RN-G02, RN-G03 · **Spec:** `techspec.md#v-04` · **ADR:** ADR-005
· **Ambiente:** Linux

## Comportamento

O núcleo recebe da porta de captura os blocos de cada fonte (microfone e áudio do sistema), com marcação de tempo, e produz uma gravação íntegra no diretório do app do usuário. O mixer é dirigido pelo relógio de parede: quando uma fonte não entrega dados por um intervalo, insere silêncio nela, de modo que a duração do arquivo bate com o relógio mesmo quando nada toca. Cada fonte é atenuada antes da soma, sem saturação audível, e o resultado é WAV PCM 16 kHz mono 16 bits. Com uma só fonte ligada, grava só ela; com as duas desligadas, não inicia e informa o motivo.

O WAV é escrito em blocos pequenos e o cabeçalho é regravado em intervalos curtos. Os metadados da gravação (identificador, título padrão por data e hora, estado, duração, fontes usadas, avisos) são gravados de forma atômica. Estados: `recording`, `saved`, `interrupted`. Na abertura, qualquer gravação em `recording` vira `interrupted` e o cabeçalho é recalculado pelo tamanho do arquivo; matar o processo no meio da escrita e reabrir produz um arquivo legível com o áudio até o último momento persistido. Pausa e retomada não acrescentam o tempo parado à duração nem ao áudio. Parar salva a gravação como `saved` e ela aparece na lista, ordenada da mais recente para a mais antiga, com título, data, duração e situação.

Perda de dispositivo no meio da gravação (evento da porta): o núcleo registra o aviso, continua com a fonte que sobrou e preserva o que já escreveu. Aos 5 min do limite de 2 h emite o aviso; ao atingir 2 h, salva e encerra com a informação de que o limite do serviço foi atingido. O núcleo avisa quando o espaço em disco está baixo e, se o espaço acabar, salva o que já capturou. A ViewModel de gravação expõe níveis, contador, estado das fontes, avisos e o aviso de gravação de terceiros, que aparece em todo início sem pedir confirmação. Nenhum dado sai do PC nesta task.

## Fora do escopo desta task

A captura real, as telas, o indicador na barra e na bandeja e o atalho global (6.0). Envio (9.0). Reprodução, renomear e apagar (15.0).

## Decisões fechadas

WAV PCM 16 kHz mono 16 bits (TechSpec, Decisões Técnicas). Duração máxima de 2 h por gravação (RN-G03). O aviso de terceiros aparece em toda gravação e não adia o início. O núcleo é testado com fontes sintéticas e um relógio controlado.

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (núcleo, apresentação e testes do núcleo criados em 4.0; não há arquivo a modificar fora dele)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-02, RF-03, RF-04, RN-G01 a RN-G03)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/techspec.md` (Captura, Persistência contínua, Dados locais)
- **ref:** A porta de captura definida em 4.0

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Linux) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do núcleo passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V04*" --minimum-expected-tests 12`
- [x] Mixagem de duas fontes sintéticas: a duração do arquivo é igual à do relógio, também com uma das fontes em silêncio por vários minutos.
- [x] Interromper a escrita no meio e reabrir deixa a gravação `interrupted`, legível e com o cabeçalho correto.
- [x] Pausa não entra na duração; as duas fontes desligadas não iniciam; o limite de 2 h salva e encerra; o disco cheio salva o que existe.
- [x] Perda de uma fonte durante a gravação mantém a gravação e registra o aviso.
- [x] O aviso de terceiros é exposto em todo início, sem exigir confirmação.
- [x] Nenhum teste faz requisição de rede.
