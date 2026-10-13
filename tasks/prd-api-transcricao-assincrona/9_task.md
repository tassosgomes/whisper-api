---
status: done
task_kind: enabling
blocked_by: []
gate: "rtk rg -q 'TRUSTED_PRIVATE_SOURCES=http://192.168.0.5:9000' README.md"
gate_expect: "runbook registra a allowlist do MinIO remoto, exit 0"
---

# 9.0 Configurar a allowlist de destinos privados no deploy

**Fatia:** EN-04 · **Cobre:** RF-02 · **Spec:** `techspec.md#v-02` · **ADR:** ADR-003

## Comportamento

Sem a allowlist, o Whisper recusa a URL do MinIO interno e o webhook do media, porque os dois estão em IP privado. A ADR-003 já define as variáveis; o que falta é aplicá-las na stack do Coolify em 192.168.0.11, nos dois serviços, api e worker. O compose desse deploy não está no repositório. O `docker-compose.yml` local só repassa `TRUSTED_PRIVATE_SOURCES` e `TRUSTED_PRIVATE_WEBHOOKS` para os serviços `transcriber` e `worker`.

Definir:

- `TRUSTED_PRIVATE_SOURCES=http://192.168.0.5:9000`, o MinIO do ambiente remoto do code-for-coders.
- `TRUSTED_PRIVATE_WEBHOOKS` vazia, até o receptor do media existir. O host e a porta vêm da CAP-032. O caminho do receptor é `/webhooks/v1/transcription-events`.

Em produção o media usa AWS S3 público, que não entra na allowlist.

O README passa a registrar esses dois valores e o fato de a lista de webhooks permanecer vazia até a CAP-032. Esse registro é o gate do plano. A evidência de que a stack remota ficou certa é outra: depois de reiniciar a api e o worker, um POST com `sourceUrl` pré-assinada do MinIO `http://192.168.0.5:9000` devolve 202 e o job chega a `completed`.

É habilitador porque a mudança está na configuração dos processos já implantados, não numa fatia nova de comportamento do código. Desbloqueia o smoke remoto contra o MinIO e a task 10.0, que não pode gravar a lista de webhooks e depois ser sobrescrita por este valor vazio.

## Fora do escopo desta task

Cadastrar o destino de webhook da conta code-for-coders, alterar o schema público da `sourceUrl` e mudar a regra de SSRF no código.

## Decisões fechadas

Seguir a ADR-003. Uma lista não autoriza a outra. Lista vazia mantém a recusa de destino privado. Não abrir rede privada genérica nem aceitar `http` fora da allowlist.

## Modificar / Referenciar

- **modificar:** `README.md` (valores da stack remota e webhook vazio até a CAP-032)
- **ref:** `docker-compose.yml` (repassa as duas variáveis a `transcriber` e `worker`)
- **ref:** `docs/adr/adr-003.md` (forma das listas e o que cada uma autoriza)
- **ref:** `.env.example` (nomes das variáveis, sem valores de ambiente)

## Verificações do projeto

Não há workflow de CI. O Coolify não é alcançado por check deste repositório.

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| Runbook | `rtk rg -q 'TRUSTED_PRIVATE_SOURCES=http://192.168.0.5:9000' README.md` | exit 0 | README, seção de allowlist |
| Compose local | `rtk docker compose config -q` | exit 0 | `docker-compose.yml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `rtk rg -q 'TRUSTED_PRIVATE_SOURCES=http://192.168.0.5:9000' README.md`
- [ ] Api e worker em 192.168.0.11 foram reiniciados com `TRUSTED_PRIVATE_SOURCES=http://192.168.0.5:9000` e `TRUSTED_PRIVATE_WEBHOOKS` vazia
- [ ] Um POST com `sourceUrl` pré-assinada de `http://192.168.0.5:9000` devolve 202 e o job chega a `completed`
- [ ] Origem pública que não está na lista, e destino privado não listado, continuam recusados
