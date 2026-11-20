# CatCar Billing Service

[![CI](https://github.com/RiseOn-CatCar/catcar-billing-service/actions/workflows/ci.yml/badge.svg)](https://github.com/RiseOn-CatCar/catcar-billing-service/actions/workflows/ci.yml)
[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=riseon_catcar-billing-service&metric=alert_status)](https://sonarcloud.io/project/overview?id=riseon_catcar-billing-service)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=riseon_catcar-billing-service&metric=coverage)](https://sonarcloud.io/project/overview?id=riseon_catcar-billing-service)

Microsserviço de **Orçamento e Pagamento** da plataforma CatCar (Tech Challenge FIAP SOAT, Fase 4). Gera e envia orçamentos para aprovação do cliente, registra e verifica pagamentos no Mercado Pago e informa o resultado ao OS Service por eventos. É o banco **não relacional (NoSQL)** da solução: usa **Marten**, um banco de documentos sobre PostgreSQL (JSONB).

A topologia completa, os contratos de mensagens e o AppHost do Aspire ficam no meta-repositório [`catcar-platform`](https://github.com/RiseOn-CatCar/catcar-platform) (`docs/architecture/messaging-contracts.md` e `aspire/CatCar.AppHost`).

## Sumário

- [Contexto delimitado](#contexto-delimitado)
- [Arquitetura do serviço](#arquitetura-do-serviço)
- [Papel na Saga](#papel-na-saga)
- [Mensagens](#mensagens)
- [Endpoints REST](#endpoints-rest)
- [Configuração](#configuração)
- [Execução local](#execução-local)
- [Testes](#testes)
- [CI/CD](#cicd)
- [Manifestos Kubernetes](#manifestos-kubernetes)
- [Evidências de qualidade](#evidências-de-qualidade)

## Contexto delimitado

| Contexto (`src/Contexts`) | Responsabilidade |
|---|---|
| `ServiceOperations` (domínio `Budgets`) | Agregado `Budget`: linhas de serviço e peça com preço congelado, total e ciclo de vida do orçamento. |
| `Communication` | Link de aprovação de uso único (`ExternalAccessToken`, com apenas o hash armazenado e TTL de 7 dias) e envio de e-mails (orçamento, pagamento e situação da ordem). |
| Pagamentos (`src/Api/Payments`) | Integração com o Mercado Pago: Checkout Pro, webhook com `x-signature`, reconciliação por polling e reembolso. |

O que **não** pertence a este serviço: OS, clientes e veículos (`catcar-os-service`); catálogo, estoque e execução (`catcar-execution-service`). O Billing não acessa o banco de nenhum outro serviço: os dados do cliente necessários para o e-mail chegam dentro das mensagens.

## Arquitetura do serviço

```text
src/
  Api/                         Program.cs (composição, rotas), Budgets.cs (handlers do orçamento),
                               Messages.cs (contratos), ApprovalDecision.cs
    Payments/                  MercadoPagoGateway, MercadoPagoWebhookSignature, PaymentWorkflow
                               (handlers, Payment, reconciliação em background)
  Contexts/
    ServiceOperations/Domain/Budgets/   Budget, BudgetLine, BudgetStatus
    Communication/                      Domain/ExternalAccessTokens, Notifications (e-mail)
  Host/CatCar.ServiceDefaults/ OpenTelemetry, health, resiliência HTTP
  SharedKernel/                Entity, ValueObject, exceções de regra de negócio
tests/                         CatCar.Billing.Tests, SharedKernel.Tests
k8s/                           base e overlays/ci
```

### Banco de dados: Marten (documentos JSONB) no banco `catcar_billing`

| Documento | Conteúdo | Identidade |
|---|---|---|
| `Budget` | Ordem de serviço, linhas (tipo, referência, descrição, preço unitário, quantidade), status, datas e motivo de rejeição. O orçamento inteiro, com as linhas embutidas, é um documento único. | `Id` do orçamento |
| `Payment` | Valor, status (`Pending`, `Confirmed`, `Failed`, `Refunded`), id externo, URL de checkout e correlação. | `BudgetId` (um pagamento por orçamento) |
| `ExternalAccessToken` | Hash do token do link, status, emissão, expiração e consumo. | `Id` |
| `BudgetCustomer` | Nome, e-mail e `CorrelationId` copiados do pedido de orçamento. | `BudgetId` |

Os documentos usam concorrência otimista (`UseOptimisticConcurrency`) e serialização `System.Text.Json`. O esquema é criado na subida do serviço ou pelo modo `--migrate`.

**Por que um banco de documentos aqui.** O orçamento é um agregado que nasce, é lido e muda de estado como um todo: documento único com as linhas embutidas dispensa junções e migrações a cada evolução do formato. O Marten também é a integração oficial do Wolverine para **inbox, outbox transacional e Saga** (`IntegrateWithWolverine`), de modo que gravar o documento e publicar o evento acontecem na mesma transação; para o MongoDB só existe pacote comunitário.

**Tradeoff assumido (risco aceito).** O motor por baixo é o PostgreSQL (JSONB), então não é um servidor NoSQL separado, com escala horizontal e consistência eventual próprias de um MongoDB ou Cosmos DB. Em troca, a solução mantém uma única tecnologia operacional, transações ACID entre documentos e fila de mensagens, e dispensa um servidor de banco adicional. O modelo é de fato de documentos (sem tabelas por entidade, sem esquema relacional, consultas por LINQ sobre JSON), em um banco **separado** (`catcar_billing`) que nenhum outro serviço acessa.

### Outras decisões

- **Mensageria:** RabbitMQ com `WolverineFx.RabbitMQ`, roteamento convencional, inbox e outbox duráveis.
- **E-mail:** porta `IEmailSender` com três transportes: Mailpit (local e CI), Azure Communication Services (nuvem) e log (somente `Development`).
- **Autenticação:** as rotas administrativas validam o mesmo JWT administrativo emitido pelo OS Service (`Jwt:*`). Os links de aprovação do cliente são anônimos e protegidos pelo token de uso único.
- **Observabilidade:** OpenTelemetry pelo `ServiceDefaults` (inclui a fonte `Wolverine`, então os traces atravessam o RabbitMQ).

## Papel na Saga

A Saga é **orquestrada pelo OS Service** (`WorkOrderSaga`; ver o [README do OS Service](https://github.com/RiseOn-CatCar/catcar-os-service#saga-de-ordem-de-serviço) para a escolha, a tabela de estados, as compensações e os timeouts). O Billing é um participante: recebe comandos e responde com eventos, sem conhecer o fluxo.

| Passo da Saga | Comando recebido | O que o Billing faz | Evento publicado |
|---|---|---|---|
| Orçamento | `RequestBudget` | Cria o `Budget` (ativo), emite o link de aprovação e envia o e-mail ao cliente. Se já há orçamento ativo para a OS, ignora (idempotente). | `BudgetIssued` |
| Aprovação | (POST no link do cliente) | Aprova ou rejeita o orçamento e consome o token. | `BudgetApproved` ou `BudgetRejected` |
| Pagamento | `RequestPayment` | Cria o `Payment`, a preferência do Checkout Pro e envia o link ao cliente. Sem credencial do Mercado Pago, falha rápido. | (webhook/polling) `PaymentConfirmed` ou `PaymentFailed`; `PaymentFailed` imediato com `payment-provider-not-configured` |
| Compensação: orçamento | `CancelBudget` | Marca o orçamento como `Cancelled` e expira os tokens. | — |
| Compensação: pagamento | `RefundPayment` | Reembolsa no Mercado Pago o pagamento `Confirmed` e marca `Refunded`. | — |
| Notificação | `WorkOrderStatusChanged` | Envia o e-mail de situação da ordem ao cliente. | — |

Os pagamentos são confirmados pelo webhook (`x-signature` validada) ou, quando o webhook não é alcançável, pela **reconciliação por polling** (a cada 2 minutos, para pagamentos `Pending`). A aplicação do resultado só ocorre se o valor remoto for igual ao valor do `Payment`.

Ciclo de vida do `Budget`: `Active` -> `Approved` ou `Rejected` -> `Replaced`; `Cancelled` é definido pela compensação `CancelBudget`.

## Mensagens

Cada serviço mantém a própria cópia dos tipos (sem biblioteca compartilhada). A compatibilidade vem da identidade estável `[MessageIdentity]`, dos nomes dos campos em JSON e de testes de contrato (`MessageContractTests`). Todo consumidor tolera reentrega.

**Consumidas pelo Billing**

| Identidade | Origem |
|---|---|
| `catcar.os.request-budget.v1` | OS Service |
| `catcar.os.cancel-budget.v1` | OS Service (compensação) |
| `catcar.os.request-payment.v1` | OS Service |
| `catcar.os.refund-payment.v1` | OS Service (compensação) |
| `catcar.os.work-order-status-changed.v1` | OS Service |

**Publicadas pelo Billing** (consumidas pela Saga do OS Service)

| Identidade |
|---|
| `catcar.billing.budget-issued.v1` |
| `catcar.billing.budget-approved.v1` |
| `catcar.billing.budget-rejected.v1` |
| `catcar.billing.payment-confirmed.v1` |
| `catcar.billing.payment-failed.v1` |

Campos de cada mensagem: [`messaging-contracts.md`](https://github.com/RiseOn-CatCar/catcar-platform/blob/main/docs/architecture/messaging-contracts.md).

## Endpoints REST

Prefixo `/api/v1/billing`.

| Método e rota | Autenticação | Descrição |
|---|---|---|
| `GET /budgets/{id}` | JWT administrativo | Consulta um orçamento. |
| `GET /work-orders/{id}/budgets` | JWT administrativo | Orçamentos de uma OS. |
| `GET /work-orders/{id}/payments` | JWT administrativo | Pagamentos de uma OS. |
| `GET /approval-links/{raw}` | anônimo (token no link) | Exibe o orçamento do link de aprovação. |
| `POST /approval-links/{raw}` | anônimo (token no link) | Decisão do cliente: `{"approved": true\|false, "reason": "..."}`. Uso único. |
| `POST /webhooks/mercadopago` | assinatura `x-signature` | Notificação do Mercado Pago (`data.id` na query). |

Operacionais: `/health/live` e `/health/ready`. Coleção Postman atualizada: [`docs/postman`](https://github.com/RiseOn-CatCar/catcar-platform/tree/main/docs/postman) no `catcar-platform`.

## Configuração

- `ConnectionStrings:billing`: PostgreSQL do Marten (`catcar_billing`).
- `ConnectionStrings:messaging`: URI AMQP do RabbitMQ.
- `Jwt:Secret`, `Jwt:Issuer`, `Jwt:Audience`: JWT administrativo (o mesmo do OS Service).
- `MercadoPago:AccessToken`, `MercadoPago:WebhookSecret`, `MercadoPago:NotificationUrl`: credenciais e URL pública do webhook. **Opcionais localmente**: sem `AccessToken`, `RequestPayment` responde `PaymentFailed` e a Saga compensa.
- `Communication:EmailTransport` (`Mailpit`, `AzureCommunicationServices` ou `Logging` em `Development`), `Communication:MailpitBaseUrl`, `Communication:AzureCommunicationServicesConnectionString`, `Communication:SenderAddress` e `Communication:ExternalBaseUrl` (base pública usada no link de aprovação).

Nunca versione credenciais reais. O esquema do Marten é aplicado com `dotnet run --project src/Api/CatCar.Billing.Api.csproj -- --migrate`; no Kubernetes, pelo Job `billing-service-migration`.

## Execução local

**Topologia completa (recomendado):** pelo AppHost do Aspire no `catcar-platform`, que sobe PostgreSQL (`catcar_os`, `catcar_billing`, `catcar_execution`), RabbitMQ, Mailpit, os três serviços e a Function de autenticação. Os e-mails de aprovação aparecem no Mailpit.

```bash
git clone --recurse-submodules https://github.com/RiseOn-CatCar/catcar-platform.git
cd catcar-platform
make dev    # aspire start --apphost aspire/CatCar.AppHost/CatCar.AppHost.csproj
```

Para habilitar o pagamento real em sandbox, informe os parâmetros `mercadopago-access-token`, `mercadopago-webhook-secret` e `mercadopago-notification-url` (user-secrets ou variáveis de ambiente) antes de iniciar o AppHost. SDK .NET conforme `global.json` (11.0.100).

## Testes

```bash
dotnet restore CatCar.Billing.slnx
dotnet build CatCar.Billing.slnx -c Release
dotnet test CatCar.Billing.slnx -c Release
dotnet format whitespace CatCar.Billing.slnx --verify-no-changes
```

xUnit, FluentAssertions, NSubstitute e Testcontainers (PostgreSQL para o Marten):

- domínio do orçamento e do token de aprovação (`BudgetDomainTests`);
- handlers de pagamento, reembolso e reconciliação (`PaymentWorkflowTests`);
- gateway do Mercado Pago com `HttpMessageHandler` simulado e validação de `x-signature` (`MercadoPagoGatewayTests`, `GatewayEdgeTests`);
- transportes de e-mail (`EmailTransportTests`);
- contratos de mensagens e serialização (`MessageContractTests`, `SerializationTests`);
- integração da API com PostgreSQL real (`BillingIntegrationTests`).

O fluxo completo da Saga, com BDD (Reqnroll), é testado no OS Service.

## CI/CD

Workflow [`.github/workflows/ci.yml`](.github/workflows/ci.yml) em todo pull request e push na `main` ([execuções](https://github.com/RiseOn-CatCar/catcar-billing-service/actions/workflows/ci.yml)):

| Job | O que faz |
|---|---|
| `build-test` | restore, `dotnet format whitespace --verify-no-changes`, build Release, testes com coverlet (OpenCover) e **gate de 80%** de cobertura de linhas do código do serviço (exclui migrations, `Program` e código gerado); publica o relatório. |
| `sonar` | SonarCloud (organização `riseon`, projeto `riseon_catcar-billing-service`) com cobertura e **quality gate** bloqueante. |
| `security` | Opengrep, `dotnet package list --vulnerable` (falha em Critical/High) e Trivy na imagem (falha em CRITICAL/HIGH). |
| `deploy-kind` | Cria um cluster kind no runner, constrói e carrega a imagem, aplica `k8s/overlays/ci` (serviço, PostgreSQL, RabbitMQ e Mailpit), espera o Job de migração e o rollout e faz smoke em `/health/ready` e em um endpoint público (`/api/v1/billing/approval-links/{token}`, resposta abaixo de 500). |

A branch `main` é protegida: exige pull request e os checks `build-test`, `security`, `sonar` e `deploy-kind`.

## Manifestos Kubernetes

- `k8s/base`: `Deployment` (não-root, probes `/health/live` e `/health/ready`), `Service`, `ConfigMap`, `HorizontalPodAutoscaler` (1 a 3 réplicas, 70% de CPU), `Job` de migração (`--migrate`) e referência ao Secret `billing-service-secrets`.
- `k8s/overlays/ci`: base mais PostgreSQL, RabbitMQ e Mailpit efêmeros e o Secret de teste. Uso somente em teste: `kubectl apply -k k8s/overlays/ci`.
- `Dockerfile`: build multi-stage (SDK 11.0.100) e runtime `aspnet:11.0.0-azurelinux3.0`, usuário não-root, porta 8080.

A publicação deste serviço no AKS está fora do escopo da Fase 4; o deploy automatizado em Kubernetes é provado pelo job `deploy-kind`.

## Evidências de qualidade

| Indicador | Valor |
|---|---|
| Testes | 172 |
| Cobertura de linhas (código de `src`, sem Migrations/Program/gerado) | **91,79%** (gate mínimo: 80%) |
| SonarCloud | [painel do projeto](https://sonarcloud.io/project/overview?id=riseon_catcar-billing-service) |
| CI (PR #1) | [pull request #1](https://github.com/RiseOn-CatCar/catcar-billing-service/pull/1): `build-test`, `security`, `sonar` e `deploy-kind` verdes |
| Execuções do CI | [workflow CI](https://github.com/RiseOn-CatCar/catcar-billing-service/actions/workflows/ci.yml) |

**Pendência:** a validação do Mercado Pago ao vivo (checkout, webhook e reembolso em sandbox) depende das credenciais de teste e ainda não foi executada; o adaptador é coberto por testes com HTTP simulado.
