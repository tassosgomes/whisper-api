---
status: done
task_kind: vertical
blocked_by: [2.0, 5.0, 7.0]
gate: "dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V06*" --minimum-expected-tests 14"
gate_expect: "ao menos 14 testes do núcleo de envio passam, 0 falham"
---

# 9.0 Enviar uma gravação para transcrição (núcleo)

**Fatia:** V-06 · **Cobre:** RF-05, RF-08, RN-G02, RN-G03, RN-G04 · **Spec:** `techspec.md#v-06` · **ADR:** ADR-001, ADR-005
· **Ambiente:** Linux

## Comportamento

O coordenador de envio, a pedido explícito do usuário e depois da confirmação do que será enviado (título, duração e tamanho aproximado), calcula tamanho e SHA-256 do arquivo, chama `createTranscriptionUpload` com `Idempotency-Key` derivada do identificador da gravação e do número da tentativa, faz o `PUT` na URL devolvida com os cabeçalhos exatamente como vieram e com progresso, chama `completeTranscriptionUpload` e grava o `uploadId` e o `jobId` nos metadados da gravação. A gravação passa de `saved` para `uploading` e depois `processing`. `clientReference` é o identificador da gravação. A URL de envio fica só em memória e nunca é persistida nem registrada.

Falha de rede ou da API em qualquer passo encerra o envio com a gravação intacta e oferece tentar de novo, sem reenvio automático em laço. A nova tentativa manual reaproveita a mesma chave e retoma de onde faz sentido: se o `complete` ficou sem resposta, repetir o pedido devolve o mesmo envio ou o `jobId` e não cria um segundo job, mesmo horas depois. `PUT` com a URL vencida repete o pedido com a mesma chave para obter outra URL. O número da tentativa só aumenta quando o envio foi recusado (`UPLOAD_REJECTED`, `422` do `complete`) ou quando o usuário reenvia uma gravação já transcrita, depois de confirmar que já existe transcrição.

Erros do contrato viram mensagens para o usuário sem perder a gravação: `401` orienta atualizar a chave; `413`, `422 UNSUPPORTED_MEDIA_FORMAT` e `422 MEDIA_DURATION_LIMIT_EXCEEDED` explicam o limite; `429` respeita `Retry-After`. Sem API configurada, o envio leva à configuração e não envia nada. Uma gravação interrompida é enviada como qualquer outra. Nada é enviado sem ação do usuário. Os testes executam contra a API real do repositório com o bucket simulado.

## Fora do escopo desta task

Acompanhamento do job e transcrição local (11.0). A tela de envio e o progresso visual (10.0). Retomada parcial do `PUT` (fora desta versão).

## Decisões fechadas

Contrato 1.1.0. O `PUT` repete o arquivo inteiro. A URL de envio é segredo temporário. Nenhum envio é automático ao terminar de gravar (RN-G02, non-goal do PRD).

## Modificar / Referenciar

- **modificar:** `clients/windows-recorder` (núcleo, apresentação e testes; sem arquivo fora dele)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml` e `contracts.md` (cenários de verificação)
- **ref:** `tasks/prd-gravador-de-reunioes-desktop/prd.md` (RF-05, RN-G02 a RN-G04)
- **ref:** API de envio implementada em 1.0 e 2.0

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Cliente .NET (Linux) | `dotnet build clients/windows-recorder -warnaserror` | exit 0; 0 warnings | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet format clients/windows-recorder --verify-no-changes` | exit 0 | EN-01 (4.0); CI ausente |
| Cliente .NET (Linux) | `dotnet test --project clients/windows-recorder/tests/Core.Tests --minimum-expected-tests 1` | exit 0; toda a suíte do núcleo passa | EN-01 (4.0); CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `dotnet test --project clients/windows-recorder/tests/Core.Tests --filter-class "*V06*" --minimum-expected-tests 14`
- [x] Envio completo contra a API real do repositório termina em `processing` com o `jobId` nos metadados.
- [x] Queda entre o `PUT` e o `complete`, e entre o `complete` e a resposta, seguida de nova tentativa, resulta em um só job.
- [x] Nova tentativa horas depois (relógio avançado) não cria segundo job; URL vencida é renovada com a mesma chave.
- [x] Checksum divergente recusa o envio e a tentativa seguinte usa chave nova; `401` e `429` produzem as mensagens e o comportamento esperados.
- [x] Nenhum teste encontra a URL de envio ou a API Key em arquivo ou diário.
- [x] Sem API configurada nada é enviado.
