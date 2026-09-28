---
tsg_artifact: architecture-baseline
product: whisper
version: 0.1
status: approved
updated: 2026-09-28
sources: vision.md@0.1, context/domain-map.md@0.1
---

# Baseline Arquitetural

Este baseline estabelece regras estruturais para o Whisper com base na visão v0.1, no Domain Map v0.1, no PRD aprovado v1.0 e na prova de conceito local. A visão e o Domain Map foram formalizados nesta passada e permanecem em revisão; este baseline foi aprovado pelo usuário em 2026-09-28.

## Estilo Arquitetural

Adotar um **monólito modular como fronteira inicial do produto**, organizado pelos domínios Acesso e Contas, Transcrição e Jobs e Entrega de Notificações. A API e os papéis de ingestão, transcrição e entrega podem executar como processos separados e escalar independentemente, mas continuam sob a mesma propriedade de produto e de dados do Whisper. Essa composição mantém operação simples para o volume inicial e isola trabalho intensivo de CPU e rede da API.

O grupo inicial não cria um serviço por domínio. Cada domínio deve ter módulo e interfaces próprios para que a separação interna sobreviva a mudanças de processo. Um módulo só deve ser extraído depois quando medições ou responsabilidades mostrarem uma necessidade de escala independente, isolamento de falha ou segurança, cadência de publicação incompatível, dependência externa própria ou manutenção por equipe distinta. A extração exige ADR com contexto, evidência e impacto operacional.

A prova de conceito atual usa Python, FastAPI, `faster-whisper`, estado de jobs em memória e arquivos locais. Isso é evidência da implementação existente, não uma decisão de plataforma para produção. O adaptador de transcrição deve permanecer substituível sem vazar tipos do motor para contratos públicos.

As decisões de runtime, provedor de banco e backend de armazenamento temporário permanecem abertas. O baseline do code-for-coders e suas ADRs foram consultados como contexto do consumidor, mas não são decisões herdadas pelo Whisper: os produtos têm propriedade de dados e necessidades operacionais distintas.

## Princípios de Interação entre Domínios

- Cada domínio mantém uma interface explícita e é o único autorizado a aplicar suas regras e alterar seus dados.
- Dentro do mesmo processo, um domínio chama outro por uma interface de aplicação estável; não importa entidades, repositórios ou detalhes internos do módulo vizinho.
- Fatos que iniciam trabalho ou notificações são comunicados de forma assíncrona e durável. Consumidores toleram repetição e operações com efeito externo são idempotentes.
- Consultas síncronas ficam para validação, autorização e leitura que precisam responder ao cliente. Transcrição e entrega de notificações não mantêm a chamada original aberta durante processamento prolongado.
- Não compartilhar o banco, filas internas ou credenciais do cliente code-for-coders. A integração entre produtos usa contratos externos explícitos.
- Falha de Entrega de Notificações não altera o estado terminal nem o resultado do job.
- Dependências externas — origem de mídia, motor de transcrição e destino de notificação — ficam atrás de adaptadores com vocabulário do Whisper. Tipos, erros e credenciais do provedor não atravessam a fronteira do domínio.

## Regras de Propriedade dos Dados

- Whisper é dono das contas e credenciais provisionadas no próprio serviço, dos jobs e de seus estados, dos resultados temporários e das configurações e tentativas de notificação.
- O cliente é dono da mídia original e de qualquer cópia permanente do resultado que decidir guardar. Whisper recebe apenas a autorização de acesso necessária à origem e não precisa de credenciais do armazenamento do cliente.
- Cada conjunto de dados tem um domínio proprietário. Módulos podem compartilhar uma instância de banco no grupo inicial, mas um módulo não lê nem altera tabelas do outro diretamente. Compartilhamento ocorre por interface, evento ou projeção explicitamente descartável.
- O estado durável do job é a fonte de verdade para seu ciclo de vida. O agendamento do trabalho não pode depender apenas de memória volátil. Uma tabela de trabalho no mesmo banco é adequada inicialmente se permitir gravação transacional, reivindicação exclusiva, lease, tentativas e recuperação após reinício; introduzir broker próprio exige necessidade medida de vazão, fan-out ou isolamento operacional.
- Mídia e resultado ficam em armazenamento privado sob responsabilidade do Whisper, separado do banco de metadados. A mídia pode ser removida assim que o processamento terminar e deve ser expurgada até o limite de retenção terminal definido no PRD; resultado e metadados ficam disponíveis por até 24 horas após `completed` ou `failed`.
- O backend físico do armazenamento temporário e as garantias de backup/expurgo precisam ser decididos antes do desenho operacional. O bucket do consumidor não é reutilizado por inferência.

## Padrões de Comunicação

- A borda pública usa HTTP REST com JSON. O prefixo principal segue versionamento maior em caminho, atualmente `/v1`; mudanças aditivas preservam compatibilidade e mudanças incompatíveis introduzem nova versão maior.
- Erros HTTP usam Problem Details (RFC 9457), sem expor stack trace, URL assinada, segredo ou detalhe interno. A API mantém códigos estáveis para que o cliente diferencie erro de entrada, autorização e falha operacional.
- A aceitação de um job só pode ocorrer após registrar estado durável suficiente para recuperar o trabalho. Download, transcrição, expurgo e envio de notificações são etapas assíncronas e retomáveis, não trabalho mantido dentro da conexão do cliente.
- A entrega de notificações tem semântica ao menos uma vez, retentativas limitadas e identificador estável para deduplicação. Não prometer entrega exatamente uma vez. O estado das tentativas pertence a Entrega de Notificações.
- O resultado da transcrição e os avisos públicos carregam versão de schema própria, separada da versão da API e do motor. O campo `schemaVersion` já previsto para o resultado deve evoluir de modo explícito quando houver mudança incompatível.
- Toda escrita repetível por timeout ou retentativa deve declarar sua janela e regra de idempotência. Retentativas internas distinguem falha transitória de falha permanente e não podem duplicar o job ou notificação lógica.

## Princípios de Segurança

- Toda credencial de API pertence a uma conta, tem escopo mínimo, pode ser revogada e rotacionada. O segredo em claro é apresentado apenas na criação; o serviço armazena somente um verificador resistente a vazamento do banco.
- Toda consulta de job ou resultado valida a conta proprietária no servidor. Identificadores opacos não substituem autorização; respostas não revelam se um job de outra conta existe.
- Autenticação de cliente e verificação de webhook são credenciais distintas. Segredos usados para assinatura de webhook ficam protegidos em armazenamento de segredos, e a validação deve permitir detectar replays por timestamp e identificador de evento.
- URLs de origem são entrada não confiável. Validar esquema, destino, DNS e redirecionamentos a cada acesso; bloquear loopback, redes privadas, link-local e destinos de metadados; limitar bytes, duração e tempo de conexão; restringir a saída de rede do worker. O limite de bytes permanece pendente no PRD.
- Conteúdo de mídia, transcrições, API Keys, material de assinatura e URLs assinadas não aparecem em logs, traces, métricas, mensagens de erro ou eventos de integração. Aplicar criptografia em trânsito e em repouso aos dados temporários.
- O processamento permanece sob controle do Whisper e não envia conteúdo a um motor externo sem uma decisão explícita de produto, segurança e privacidade. Apagar mídia e resultados ao fim da retenção; detectar e remover temporários órfãos.
- A autorização entre contas deve ser testada em toda operação de leitura, escrita, callback e expurgo. Privilégios operacionais também seguem menor privilégio e deixam trilha de auditoria sem conteúdo sensível.

## Padrões de Observabilidade

- Usar logs estruturados com identificadores de correlação do job e da requisição; identificar conta apenas por valor pseudonimizado quando necessário. Não registrar conteúdo ou segredos.
- Medir volume e falhas por etapa, idade e tamanho da fila, tempo entre aceite e início da ingestão, tempo de espera, fator de tempo real (RTF), duração de processamento, uso de CPU/memória, expurgo e tentativas/atraso/falha de notificação.
- Propagar contexto de rastreamento entre a chamada HTTP e o trabalho assíncrono. Persistir somente o contexto mínimo necessário e impedir que atributos de alta cardinalidade ou conteúdo do cliente virem métricas.
- Correlacionar falhas externas com um erro categorizado e seguro. Alertas operacionais devem apontar para atraso crescente, worker indisponível, expurgo atrasado e falha de entrega, sem incluir a mídia ou o resultado.
- A retenção dos próprios logs e traces é definida separadamente da retenção dos dados do job e nunca amplia a retenção do conteúdo.

## Premissas de Escalabilidade

- O ponto de partida é a premissa de até dez jobs por dia e mídia de até duas horas, não uma garantia de capacidade. Isso pode representar até vinte horas de mídia por dia; RTF, tamanho em bytes, pico simultâneo e metas de início/conclusão precisam ser medidos antes de assumir compromissos de serviço.
- API, obtenção da mídia e inferência têm perfis de recurso diferentes. Manter capacidade e limites de concorrência separados para que transcrições longas não impeçam a admissão de novos downloads e consultas.
- Dimensionar workers pelo benchmark do motor, memória e CPU disponíveis, carregando o modelo por processo de worker em vez de por job. Restringir a fila e aplicar backpressure e limites por conta quando necessário.
- Aumentar a quantidade de réplicas ou introduzir infraestrutura distribuída somente quando métricas mostrarem que o grupo inicial não atende os limites acordados. Não fixar capacidade por suposição nem replicar estado volátil como estratégia de escala.
- Os componentes devem ser stateless quando apropriado; estado de job, leases, idempotência e notificações permanecem duráveis e compartilhados por interfaces de propriedade.

## Guardrails Arquiteturais

- Fronteiras de Acesso e Contas, Transcrição e Jobs e Entrega de Notificações seguem o Domain Map. Não criar um serviço ou domínio por operação técnica sem uma regra de negócio independente.
- Nenhum domínio acessa diretamente dados privados de outro, mesmo quando os módulos estão no mesmo processo ou banco.
- O banco do consumidor, suas credenciais e seu bucket não são dependências internas do Whisper. O cliente fornece uma referência temporária autorizada e persiste o que quiser conservar.
- Não executar obtenção de mídia ou inferência longa na thread de requisição HTTP. O estado durável deve permitir retomar trabalho após reinício ou falha do processo.
- Não expor o resultado completo em notificações; notificar com referência e permitir recuperação autenticada enquanto houver retenção.
- Contratos públicos são versionados e independentes do motor de transcrição. Integração externa sempre passa por adaptador e nunca propaga identificadores, erros ou tipos de fornecedores diretamente.
- Extração de um domínio para unidade de deploy própria requer evidência de necessidade e ADR aprovada; mudança de escala prevista não basta sem medição ou requisito operacional concreto.
- O índice de ADR deste repositório é compartilhado entre features. A ADR-001 e a ADR-002 aceitas registram decisões específicas da API assíncrona de transcrição e não substituem este baseline.
- O primeiro consumidor ainda depende de uma forma autorizada de obter URL de leitura da mídia; o contrato consultado do code-for-coders documenta URLs de escrita. Resolver essa integração antes de fechar a TechSpec ponta a ponta.
