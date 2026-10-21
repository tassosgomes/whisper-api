---
status: done
task_kind: vertical
blocked_by: [1.0]
gate: ".venv/bin/pytest -q -k v07_operator_queue"
gate_expect: "ao menos 6 testes de fila e jobs retidos passam, 0 falham"
---

# 7.0 Ver a fila e os jobs retidos sem conteúdo

**Fatia:** V-07 · **Cobre:** RF-05, US-04, Transcrição e Jobs RN-01, RN-02, RN-03, RN-04, RN-06, RN-07, RN-08, Acesso e Contas RN-04 · **Spec:** `techspec.md#v-07-fila-e-jobs-retidos`

## Comportamento

`getQueue` devolve, de todas as contas, `size` igual à quantidade de jobs que não estão `completed` nem `failed`, e `oldestAcceptedAt` igual à aceitação do mais antigo entre eles. Sem job não terminal, devolve `size: 0` e `oldestAcceptedAt: null`. A tela mostra o tamanho e a idade calculada a partir desse horário.

`listOperationalJobs` lista, paginados e de todas as contas, os jobs não terminais e os terminais há até 24 horas, cada um com conta, identificador, estado, horários disponíveis e a referência opaca quando o cliente a enviou. Job terminal há mais de 24 horas não aparece, e `getOperationalJob` dele responde `404 NOT_FOUND`. Nenhuma das duas operações devolve mídia, texto da transcrição, URL de origem nem segredo.

A leitura é projeção de Transcrição e Jobs: não descriptografa a URL de origem, não lê o resultado no armazenamento, não toma lease do worker e não muda estado nem horários de job algum. A consulta do cliente em `/v1/transcriptions` continua limitada aos jobs da própria credencial.

## Fora do escopo desta task

Tentativas de entrega ficam em 8.0. Metas de início e conclusão continuam abertas em CAP-001; a idade é só observação. Saúde de workers está fora do PRD.

## Decisões fechadas

A vista operacional lista os campos do contrato e nada mais; não usa a consulta do cliente nem serializa a linha inteira do job (`techspec.md#riscos-e-preocupações`, linhas de `source_url_for_recovery` e `_public_job`). Retenção de 24 horas herdada do piloto.

## Modificar / Referenciar

- **modificar:** `app/main.py` (rotas `getQueue`, `listOperationalJobs`, `getOperationalJob`)
- **modificar:** a tela `/operator` (resumo da fila e lista de jobs retidos)
- **ref:** `app/jobs/executor.py` (`_public_job` como recorte seguro; `source_url_for_recovery` não é chamado)
- **ref:** `app/jobs/models.py` (campos do job e URL cifrada que não sai)
- **ref:** `app/jobs/security.py` (cifra da URL de origem)
- **ref:** `app/jobs/retention.py` (janela de 24 horas)
- **ref:** `app/api/transcriptions.py` (consulta do cliente inalterada)
- **ref:** `tasks/prd-operacao-do-servico/api-contract.yaml` (`getQueue`, `listOperationalJobs`, `getOperationalJob`)

## Verificações do projeto

| Componente | Comando | Resultado esperado | Fonte |
|---|---|---|---|
| API, domínio e tela Python | `.venv/bin/pytest -q -k v07_operator_queue` | exit 0; ao menos 6 testes; exit 5 se nada for coletado | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/pytest -q` | exit 0; tasks anteriores continuam passando | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff check app tests` | exit 0 | `pyproject.toml`; CI ausente |
| API, domínio e tela Python | `.venv/bin/ruff format --check app tests` | exit 0 | `pyproject.toml`; CI ausente |

## Pronto quando

- [x] Gate focalizado passa (exit 0): `.venv/bin/pytest -q -k v07_operator_queue`
- [x] Fila com jobs de duas contas mostra tamanho e aceitação mais antiga corretos; fila vazia mostra `0` e `null`.
- [x] Job terminal dentro de 24 horas aparece; fora delas não aparece e responde `404`.
- [x] Nenhuma resposta contém URL de origem, texto, mídia ou segredo; estado e lease dos jobs não mudam com a leitura.
- [x] O cliente continua vendo só os jobs da própria credencial.
