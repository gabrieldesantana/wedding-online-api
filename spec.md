# Backend C# — Lista de presentes em dinheiro

## 1. Objetivo e escopo

Implementar, em um repositório separado, uma API ASP.NET Core compatível com o frontend React já existente. O casal cadastra presentes simbólicos com valores fixos; convidados e padrinhos escolhem um presente e pagam seu valor por Pix ou cartão pelo Mercado Pago Checkout Pro.

Esta especificação é autossuficiente e pode ser copiada para o repositório do backend. Os contratos abaixo correspondem ao cliente HTTP atual do frontend, conferido em 22/11/2026. Não alterar nomes, formatos ou autenticação sem atualizar e testar os dois projetos.

### Regras de produto

- Uma única lista para um único casal; não implementar multitenancy.
- Conta Mercado Pago recebedora pertencente a um dos noivos, configurada no servidor.
- Preço fixo por presente, em BRL. Vários convidados podem escolher o mesmo presente.
- O valor é destinado ao casal; não há compra em loja, entrega física, estoque ou reserva exclusiva.
- O casal assume o processamento; os juros do parcelamento ficam com o pagador.
- Área privada para duas contas de noivos previamente cadastradas.
- Cadastro, edição e desativação de presentes; histórico e resumo financeiro.
- Nenhum pagamento pode ser confirmado por parâmetros da URL, ação do frontend ou comprovante enviado pelo convidado.

### Fora desta versão

Convites e RSVP, migração do Supabase, valor livre, cotas, metas, uploads, cadastro público, recuperação de senha por e-mail, OAuth de múltiplos recebedores, split, emissão de comprovantes próprios, envio de e-mails e devoluções iniciadas pela API. Devoluções são feitas no Mercado Pago e refletidas no histórico.

## 2. Stack e organização

- .NET 10 LTS, ASP.NET Core Web API e C# da versão correspondente.
- Entity Framework Core e Npgsql com versões compatíveis com .NET/EF escolhidos.
- PostgreSQL gerenciado no Neon.
- ASP.NET Core Identity com cookies e papel `Couple`; sem JWT no contrato desta versão.
- Integração Mercado Pago isolada atrás de `IMercadoPagoClient`, usando `HttpClientFactory`.
- Docker Linux com build multi-stage, usuário sem privilégios e deploy no Render.
- Testes xUnit, `WebApplicationFactory` e PostgreSQL real descartável via Testcontainers.
- OpenAPI gerado a partir dos endpoints e mantido no repositório.

Estrutura sugerida, sem criar microsserviços:

```text
src/Wedding.Api/            # HTTP, autenticação, DI, comandos e entrypoint
src/Wedding.Application/    # casos de uso, DTOs, validação e interfaces
src/Wedding.Domain/         # entidades, estados e regras financeiras
src/Wedding.Infrastructure/ # EF, Identity, Mercado Pago e processamento durável
tests/Wedding.UnitTests/
tests/Wedding.IntegrationTests/
Dockerfile
render.yaml
README.md
```

Servir apenas a API neste repositório. O React pode permanecer em hospedagem independente.

## 3. Convenções obrigatórias

- Prefixo `/api`, JSON camelCase, IDs UUID apresentados como strings.
- Datas `DateTimeOffset`/`timestamptz`, armazenadas em UTC e serializadas em ISO 8601 com fuso.
- Valores internos e públicos em centavos inteiros (`long`/`bigint`); nunca `float` ou `double`.
- Na integração financeira, converter centavos para `decimal` e formato exigido pelo provedor usando cultura invariável. Rejeitar respostas monetárias sem correspondência exata em centavos.
- `currency` sempre `BRL`.
- Campos anuláveis devem aparecer como `null` nas respostas.
- Estados públicos: `pending`, `approved`, `rejected`, `cancelled`, `refunded`, `charged_back`.
- Todos os erros HTTP usam `application/problem+json`; nunca HTML, stack trace ou segredos.
- Respostas de autenticação, contribuições e administração usam `Cache-Control: no-store`.
- O cliente tem timeout de 20 segundos por request. Encerrar chamadas externas em até 10 segundos e responder antes desse limite, sem perder operações em andamento.

## 4. Modelo de dados

Criar migrations EF próprias. Não executar as migrations históricas do frontend/Supabase.

### Gift

`Id`, `Title`, `Description?`, `ImageUrl?`, `AmountCents`, `Active`, `CreatedAt`, `UpdatedAt`.

- Nome após trim: 2–120 caracteres.
- Descrição: até 500 caracteres; string vazia vira `null`.
- Imagem opcional: URL HTTPS absoluta, sem credenciais, até 2048 caracteres. Não baixar a imagem no backend.
- Valor: 1 a 100000000 centavos, inclusive.
- Desativação preserva o registro e todas as contribuições.

### Contribution

`Id`, `GiftId`, `GiftTitleSnapshot`, `AmountCentsSnapshot`, `Currency`, `GuestName`, `Email`, `Message?`, `Status`, `NetAmountCents?`, `TrackingTokenHash`, `ProtectedTrackingToken`, `CreatedAt`, `UpdatedAt`, `LastProviderSyncAt?`, `Version`.

- Nome após trim: 2–100 caracteres; e-mail válido até 254; mensagem opcional até 500.
- Gerar token de acompanhamento com 32 bytes criptograficamente aleatórios, codificado em base64url.
- Guardar SHA-256 do token para validação com comparação em tempo constante.
- Guardar também o token protegido por `IDataProtector`, com propósito específico e versionado. Isso permite devolver o **mesmo token** em replays idempotentes e montar o retorno do checkout.
- Nunca guardar ou registrar o token em texto puro, nem devolvê-lo em consultas ou listas.
- Nome e valor do presente são snapshots imutáveis. Editar o catálogo não altera o histórico.
- FK para Gift sem exclusão em cascata de histórico financeiro.

### PaymentAttempt

`Id`, `ContributionId`, `Provider`, `ProviderIdempotencyKey`, `ProviderOrderId?`, `CheckoutUrl?`, `CreationState`, `ProviderStatus?`, `ProviderUpdatedAt?`, `CreatedAt`, `UpdatedAt`, `LastErrorCode?`, `NextSyncAt?`.

- Estados internos de criação: `creating`, `ready`, `uncertain`, `failed`.
- Nesta versão, uma única tentativa de pagamento por contribuição, garantida por índice único.
- `ProviderIdempotencyKey` é UUID persistido antes da chamada externa e nunca muda em retries.
- Índice único para o identificador do pedido externo quando preenchido.

### IdempotencyRecord

`Operation`, `KeyHash`, `RequestHash`, `ContributionId`, `CreatedAt`.

- Unicidade por operação e hash da chave. Não armazenar a chave enviada pelo cliente em logs.
- Operações separadas: criar contribuição e obter checkout. Na segunda, incluir o ID da contribuição no escopo.
- Calcular hash do payload validado e normalizado com serialização canônica; mesma chave com payload diferente resulta em 409.
- Resposta de criação é reconstruída pelo ID e pelo token protegido, sem duplicar dados pessoais em um cache de respostas.
- Preservar registros de idempotência enquanto a contribuição existir nesta versão.

### WebhookInbox e sincronização

`Id`, `ProviderEventId`, `Topic`, `ProviderResourceId`, `ReceivedAt`, `ProcessedAt?`, `Attempts`, `NextAttemptAt`, `LeaseUntil?`, `LastErrorCode?`.

- Chave única do evento no contexto da aplicação/ambiente do provedor.
- Quando não houver ID de notificação utilizável, deduplicar com hash dos campos assinados e corpo recebido; não deduplicar apenas pelo ID do pedido, pois ele terá atualizações legítimas.
- Persistir apenas os dados necessários ao processamento. Não registrar indiscriminadamente corpos com dados pessoais.

Persistir também usuários/roles Identity, sessões revogáveis e key ring Data Protection. Usar índices em `Contribution(Status, CreatedAt, Id)`, `Contribution(GiftId)` e na fila por próxima execução. Usar controle de concorrência otimista ou locks transacionais explícitos nas atualizações financeiras.

## 5. Autenticação, autorização e CSRF

- Provisionar as duas contas por comando administrativo; sem credenciais padrão ou contas demo em produção.
- Senhas com o hasher do Identity; mínimo 12 caracteres para contas reais.
- Receber senhas de provisionamento por entrada interativa ou secret temporário; não usar argumentos CLI com senha.
- Cookie de sessão `HttpOnly`, `Secure`, host-only; duração absoluta de 8 horas, sem sliding expiration.
- Usar sessão revogável no servidor, por exemplo `ITicketStore` em PostgreSQL. Logout revoga a sessão corrente; redefinir senha revoga todas as sessões do usuário.
- Exigir papel `Couple` nos endpoints administrativos.
- Não redirecionar 401/403 para páginas de login. Retornar `ProblemDetails`.
- `GET /api/auth/csrf` usa `IAntiforgery.GetAndStoreTokens` e devolve o request-token.
- Validar `X-CSRF-Token` em todos os POST/PUT acessados pelo navegador, incluindo login e criação pública de contribuição.
- Após login/logout, o cliente obtém novo request-token. CSRF inválido retorna 403; não realizar a operação.
- Webhook não usa cookies nem CSRF: sua autenticação é a assinatura do Mercado Pago.
- Limitar login por IP e conta; usar erro genérico para usuário inexistente/senha errada.

Persistir as chaves Data Protection em PostgreSQL e protegê-las com certificado X.509 com chave privada fornecido por secret. Todas as réplicas e jobs usam o mesmo `ApplicationName`, key ring e certificado. Não usar o disco efêmero do Render para isso. Rotacionar mantendo capacidade de descriptografar tokens e tickets existentes.

### CORS e implantação separada

- Permitir somente as origens HTTPS configuradas do React; `AllowCredentials`.
- Métodos GET, POST, PUT e OPTIONS.
- Cabeçalhos Content-Type, X-CSRF-Token, X-Contribution-Token e Idempotency-Key.
- Preflight OPTIONS não exige autenticação ou antiforgery.
- Preferir `site.exemplo.com` e `api.exemplo.com`, ou proxy `/api` na origem do frontend. Usar `SameSite=Lax` em implantação same-site.
- Em sites distintos, usar `SameSite=None; Secure`; cookies de terceiros ainda podem ser bloqueados. A homologação precisa verificar o navegador real; CORS não resolve sozinho essa limitação.
- Em desenvolvimento HTTP, permitir cookie não Secure apenas no ambiente Development, de preferência com proxy local na mesma origem.

## 6. Contratos HTTP exatos

Nos exemplos abaixo, `gift-id` e `contribution-id` representam UUIDs reais.

### DTOs de resposta

**GiftDto** — catálogo e administração:

```json
{
  "id": "gift-id", "title": "Um jantar a dois", "description": "Um brinde ao casal.",
  "imageUrl": null, "amountCents": 15000, "active": true
}
```

**ContributionDto** — consulta privada e histórico administrativo:

```json
{
  "id": "contribution-id", "giftId": "gift-id", "giftTitle": "Um jantar a dois",
  "amountCents": 15000, "netAmountCents": null, "currency": "BRL",
  "guestName": "Maria Silva", "email": "maria@example.com", "message": "Felicidades!",
  "status": "pending", "createdAt": "2026-11-22T15:00:00Z"
}
```

**CoupleUserDto**:

```json
{ "email": "noiva@example.com", "name": "Keise" }
```

### Endpoints públicos

| Método e rota | Entrada | Resposta |
| --- | --- | --- |
| GET `/api/gifts` | Nenhuma | 200, array de GiftDto ativos, ordenado por criação/ID |
| POST `/api/contributions` | Corpo abaixo; CSRF e Idempotency-Key | 201 na criação; 200 no replay, com `{ "id": "contribution-id", "trackingToken": "segredo" }` |
| POST `/api/contributions/{id}/checkout` | `{}`; CSRF, X-Contribution-Token e Idempotency-Key | 200, `{ "checkoutUrl": "https://www.mercadopago.com.br/..." }` |
| GET `/api/contributions/{id}` | X-Contribution-Token | 200, ContributionDto |

Corpo de criação:

```json
{ "giftId": "gift-id", "guestName": "Maria Silva", "email": "maria@example.com", "message": "Felicidades!" }
```

Não aceitar preço, estado de pagamento ou valor líquido enviados pelo navegador. Presente inativo/inexistente retorna 404 na criação. Criação inválida retorna 400/422 sem persistir contribuição ou tentativa. Validar token antes de revelar estado do checkout; ID inexistente ou token inválido retornam o mesmo 404 genérico.

O catálogo nunca expõe dados de convidados, tokens ou arrecadação. A consulta privada devolve exclusivamente a contribuição autorizada pelo token. Não aceitar o token em query string.

### Sessão

| Método e rota | Entrada | Resposta |
| --- | --- | --- |
| GET `/api/auth/csrf` | Sem sessão obrigatória | 200, `{ "token": "request-token" }` e cookie antiforgery |
| POST `/api/auth/login` | `{ "email": "noiva@example.com", "password": "senha" }`; CSRF | 200, CoupleUserDto e cookie de sessão; erro 401 |
| GET `/api/auth/me` | Cookie de sessão | 200, CoupleUserDto; sem sessão 401 |
| POST `/api/auth/logout` | `{}`; sessão e CSRF | 204, sessão revogada e cookie removido |

### Administração

Todos exigem sessão e papel Couple. POST/PUT também exigem CSRF.

| Método e rota | Resposta |
| --- | --- |
| GET `/api/admin/gifts` | 200, array de GiftDto ativos e desativados |
| POST `/api/admin/gifts` | 201, GiftDto criado |
| PUT `/api/admin/gifts/{id}` | 200, GiftDto atualizado; 404 se inexistente |
| GET `/api/admin/contributions?page=1&pageSize=10&status=approved` | 200, página de ContributionDto |
| GET `/api/admin/summary` | 200, resumo financeiro |

Corpo completo de POST/PUT de presente:

```json
{ "title": "Um jantar a dois", "description": "", "imageUrl": null, "amountCents": 15000, "active": true }
```

PUT é atualização completa. Desativar usa `active=false`. Não criar endpoint DELETE nesta versão.

Histórico: `page` default 1, `pageSize` default 10 e máximo 100; valores inválidos retornam 400. `status` opcional deve ser um dos seis estados públicos. Ordenar por `createdAt DESC, id DESC`. Página além do final retorna items vazio e mantém o total filtrado.

```json
{ "items": [], "page": 1, "pageSize": 10, "total": 0 }
```

Resumo, independente do filtro/página do histórico:

```json
{ "approvedCount": 2, "grossAmountCents": 45000, "netAmountCents": 44555 }
```

- Contar/somar apenas contribuições atualmente `approved`.
- Líquido é informado pelo provedor, sem taxa fixa estimada no código.
- Se algum aprovado ainda não tiver líquido conhecido, total líquido é `null`.
- Sem aprovados: contagem e valores zero.
- Devolvidos e contestados não entram nos totais confirmados; resumo não equivale a saldo para saque.

### Erros e limites

```json
{
  "type": "about:blank", "title": "Dados inválidos", "status": 422,
  "detail": "Confira os campos informados.",
  "errors": { "email": ["Informe um e-mail válido."] }
}
```

Usar 400 para JSON/cabeçalhos/parâmetros malformados; 422 para validação de campos; 401 sessão/credenciais; 403 CSRF/permissão; 404 recurso indisponível; 409 idempotência/estado incompatível; 429 rate limit; 502 resposta inválida do provedor; 503 timeout/indisponibilidade temporária. Respostas 429/503 podem informar Retry-After.

Aplicar limite de corpo de 16 KiB nos endpoints próprios e 256 KiB no webhook. Valores iniciais configuráveis de rate limit: login 5/minuto por IP e lockout Identity após 5 falhas por conta por 15 minutos; criação 10/minuto por IP; checkout 10/minuto por IP/contribuição; consulta privada 30/minuto por IP/contribuição. Considerar proxy confiável antes de derivar o IP. Não aplicar a política de convidados ao webhook.

## 7. Idempotência e criação do checkout

### Criar contribuição

1. Validar CSRF, payload e chave UUID no cabeçalho `Idempotency-Key`.
2. Normalizar campos e calcular o fingerprint.
3. Consultar o registro idempotente antes de revalidar o estado atual do catálogo: um replay deve continuar funcionando após desativação do presente.
4. Se houver registro compatível, recuperar a contribuição e descriptografar seu token; devolver a mesma resposta.
5. Sem registro, ler o presente ativo, gerar token, salvar contribuição `pending`, snapshots e registro idempotente em uma transação.
6. Resolver colisões concorrentes pela constraint única e reler o registro vencedor; nunca criar duas contribuições para a mesma chave.

### Obter checkout

1. Validar CSRF e token privado antes de consultar/devolver dados da contribuição.
2. Rejeitar com 409 contribuições aprovadas ou encerradas. Não criar outro pagamento para a mesma contribuição nesta versão.
3. Se já houver checkout válido em estado pendente, devolvê-lo, mesmo que o cliente envie uma nova chave. O acompanhamento usa uma nova chave ao retomar o pagamento.
4. Em transação curta, criar ou adquirir uma lease da tentativa única e persistir a chave de idempotência do provedor e o payload necessário antes de chamar a API externa.
5. Não manter transação/lock do PostgreSQL aberto durante a chamada HTTP.
6. Criar o pedido usando a mesma chave persistida em todas as repetições. Salvar ID e URL; verificar conflitos de versão antes de gravar.
7. Se outra execução estiver criando o checkout, retornar 503 temporário; não disparar uma segunda criação.
8. Timeout, desconexão ou queda do processo deixam a tentativa `uncertain`, não `failed`. Retomar pela mesma chave/recurso, sem gerar uma chave nova.
9. Se a janela de idempotência do provedor expirar sem ser possível determinar o resultado, bloquear nova criação, registrar alerta e exigir conciliação operacional. Nunca assumir que a cobrança não existiu.

Erros definitivos de criação ficam registrados. Para um novo pagamento após rejeição, cancelamento ou expiração confirmados, o convidado pode iniciar outra contribuição pelo catálogo. Esta versão não exige que o frontend ofereça retry de pagamento encerrado no mesmo ID.

## 8. Mercado Pago

Usar Checkout Pro pela API de Orders, com o fluxo público atual de `POST /v1/orders` e retorno `checkout_url`. Não misturar IDs, assinaturas ou eventos da API de Preferências com Orders. Manter toda adaptação em um único módulo e fixar fixtures de testes baseadas na documentação oficial.

- Autenticar chamadas com Access Token do ambiente, exclusivamente no servidor.
- Enviar `X-Idempotency-Key` do provedor, distinto do cabeçalho próprio `Idempotency-Key` do frontend.
- `external_reference` deve identificar a tentativa persistida; relacioná-la inequivocamente à contribuição.
- Produto simbólico, descrição e preço vêm do snapshot, quantidade 1 e moeda BRL.
- Habilitar Pix e crédito; não habilitar boleto nesta versão. Configurar o parcelamento com juros para o pagador, sem o casal subsidiar parcelas.
- Usar valores oficiais da conta/provedor; não codificar percentuais de tarifas.
- Aceitar somente `checkout_url` HTTPS em `mercadopago.com.br` ou subdomínio legítimo, sem credenciais na URL.
- Construir URLs de retorno exclusivamente a partir de `Frontend__PublicOrigin` e `/presentes/acompanhar/{id}#token={trackingToken}`. Nunca aceitar URL de retorno arbitrária do cliente.
- O frontend remove o fragmento e mantém o token em sessionStorage. Ele não interpreta status vindo do redirect.
- Configurar a URL de notificação HTTPS para o endpoint abaixo.

Validar em homologação os campos atuais para seleção de meios, parcelamento, retorno e notificações. Se a conta não disponibilizar o fluxo Orders esperado, registrar o bloqueio de integração; não mudar silenciosamente o contrato ou simular pagamentos reais.

### Webhook: `POST /api/webhooks/mercadopago`

1. Validar assinatura `x-signature` pelo algoritmo/canonicalização documentados para o tópico Orders, com os identificadores e `x-request-id` exigidos. Não inventar uma comparação direta entre o header e o segredo.
2. Limitar corpo e validar campos mínimos; assinatura inválida retorna 401, payload inválido 400.
3. Persistir o evento válido na inbox antes de responder 200. Evento duplicado já persistido também recebe 200. Falha de banco recebe 503 para permitir retry do provedor.
4. Processar o evento consultando o pedido/pagamento autoritativo na API do Mercado Pago; não confiar no status do corpo da notificação.
5. Verificar conta recebedora configurada, ambiente teste/produção, pedido, referência, valor e BRL antes de aplicar qualquer atualização.
6. Divergência não confirma pagamento: registrar código de erro e alerta operacional sem expor dados ao público.
7. Aplicar atualização transacional, idempotente e com controle de concorrência.

Não usar IPN sem assinatura validável como fonte de confirmação financeira. Eventos não suportados, mas autenticados, podem ser registrados como ignorados. Caso a documentação exija tópicos adicionais para estornos/contestações, validá-los separadamente e encaminhar ao mesmo processo de consulta autoritativa.

### Estados e valor líquido

- Pedido criado/aguardando ação/em análise: `pending`.
- Pagamento efetivamente aprovado/capturado: `approved`.
- Recusa definitiva: `rejected`.
- Cancelamento/expiração confirmados pelo provedor sem pagamento: `cancelled`.
- Devolução confirmada: `refunded`.
- Contestação/chargeback informado pelo provedor: `charged_back`.
- Estados desconhecidos não aprovam a contribuição; preservar estado seguro e alertar para atualização do mapeamento.
- A ordem dos eventos recebidos não define a ordem dos estados. Consultar o recurso atual e impedir que uma leitura antiga sobrescreva uma atualização mais recente; usar versão/lock por contribuição e timestamp autoritativo quando disponível.
- O contrato não tem estado de devolução parcial. Nesta versão, qualquer devolução financeira positiva confirmada classifica a contribuição como `refunded`, excluindo-a dos totais confirmados; guardar os valores parciais internamente para auditoria.
- Persistir bruto, taxas, devoluções e líquido disponíveis no recurso financeiro. Se o recurso Orders não contiver líquido suficiente, consultar o recurso de pagamento associado; permanecer `null` enquanto desconhecido.

## 9. Processamento durável e reconciliação

- A inbox reside no PostgreSQL; não depender de fila apenas em memória ou do tempo de vida de um request.
- Um `BackgroundService` processa eventos disponíveis, com polling curto e leases transacionais. Usar `FOR UPDATE SKIP LOCKED` e lease com expiração para permitir múltiplas instâncias e recuperação após crash.
- Não usar advisory lock de sessão em conexão pooled do Neon; preferir locks de linha em transações curtas.
- Retry com backoff para falhas transitórias, até 10 tentativas, depois estado de intervenção manual e alerta. Nunca descartar eventos silenciosamente.
- Criar comando `jobs reconcile` para execução a cada 5 minutos em Render Cron Job. Ele retoma tentativas incertas, pagamentos pendentes e notificações não processadas elegíveis.
- Conciliar também aprovados dos últimos 30 dias e registros contestados/devolvidos ainda com atualização financeira pendente; o recebimento de webhooks continua habilitado para eventos posteriores.
- Oferecer comando operacional de reconciliação por ID/período e reprocessamento de eventos, sem endpoints públicos de manutenção.
- Guardar `LastProviderSyncAt`, tentativas e próxima execução. Sobreposição entre web service e cron deve ser segura por leases/constraints no banco.
- Banco indisponível nunca resulta em confirmação financeira ou resposta de sucesso de escrita.

## 10. Configuração e operação

Usar Options com validação na inicialização. Exemplo sem segredos reais:

```dotenv
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Default=Host=...;Database=...;Username=...;Password=...;SSL Mode=VerifyFull
ConnectionStrings__Migrations=Host=...;Database=...;Username=...;Password=...;SSL Mode=VerifyFull
Frontend__PublicOrigin=https://site.exemplo.com
Cors__AllowedOrigins__0=https://site.exemplo.com
Authentication__CookieSameSite=Lax
MercadoPago__Environment=production
MercadoPago__AccessToken=<secret>
MercadoPago__WebhookSecret=<secret>
MercadoPago__ReceiverId=<id-da-conta>
MercadoPago__NotificationUrl=https://api.exemplo.com/api/webhooks/mercadopago
DataProtection__ApplicationName=WeddingGifts
DataProtection__CertificateBase64=<secret-pfx>
DataProtection__CertificatePassword=<secret>
```

- Aplicação usa conexão Neon pooled quando apropriado, Npgsql pooling e limite inicial de 10 conexões por instância, configurável conforme o plano do banco.
- Migrations usam conexão direta do Neon e comando separado `db migrate`. Não executar migrations concorrentes em cada réplica.
- Exigir TLS e validação do certificado do banco em produção; não usar `Trust Server Certificate=true`.
- Docker deve escutar `0.0.0.0` na porta fornecida pelo Render via `PORT`; configurar forwarded headers somente para proxies confiáveis.
- `GET /health/live`: 200 se processo ativo. `GET /health/ready`: verifica banco e configuração local; sem chamadas financeiras ao Mercado Pago. Respostas sem detalhes internos.
- Usar web service sem suspensão automática para produção financeira. Jobs e migrations usam a mesma versão de imagem e segredos compatíveis.
- HTTPS obrigatório; logs estruturados com correlation ID, IDs internos e códigos de falha, sem senha, cookie, CSRF, token de acompanhamento, Idempotency-Key, Authorization ou payload completo de convidados.
- Métricas/alertas mínimos: tentativas incertas, atraso da inbox, falhas recorrentes de reconciliação, divergências financeiras e erros do provedor.
- Não apagar contribuições/inbox/histórico automaticamente nesta versão. Proteger backups e restringir o acesso aos dados pessoais.

Comandos a entregar: `db migrate`, `users create`, `users reset-password`, `jobs reconcile` e `jobs reprocess`. Execução administrativa ocorre via ambiente autorizado do Render ou terminal seguro.

## 11. Plano de testes e critérios de aceite

### Unitários

- Validação de campos, normalização, centavos, limites e enumeração de estados.
- Snapshot imutável após edição/desativação do catálogo.
- Tokens imprevisíveis, hash/comparação e recuperação pelo token protegido.
- Mapeamento dos estados do provedor e resumo com líquido desconhecido/devolução parcial.
- Validação de URL de checkout/retorno, assinatura com fixtures oficiais e deduplicação.

### Integração HTTP e PostgreSQL

- Exatos JSON/status/cabeçalhos desta spec, incluindo nulls e `ProblemDetails`.
- Cookie/CSRF antes e depois do login; 401/403 sem HTML; preflight CORS.
- Duas contas válidas; usuário anônimo sem acesso administrativo; logout/reset de senha revogando sessões.
- Catálogo sem dados pessoais; token de contribuição A não acessa B; token inválido e ID inexistente com o mesmo 404.
- Payload sem preço; adulteração de preço/status não altera o valor financeiro.
- Criações concorrentes com mesma chave produzem uma contribuição e o mesmo token; payload divergente retorna 409.
- Replay após desativar presente continua retornando o recibo original; criação nova para presente inativo falha.
- Checkout concorrente com chaves iguais/diferentes produz uma tentativa única; timeout/crash reutiliza a chave persistida do provedor.
- Reiniciar aplicação mantém tokens protegidos, sessões e operações recuperáveis.
- Webhook repetido, fora de ordem, inválido, recebido durante queda do provedor e recebido antes da resposta de criação do checkout.
- Mismatch de valor, conta, ambiente ou referência nunca aprova contribuição.
- Paginação estável, filtros e totais independentes da página; devolução/contestação retiradas dos confirmados.
- Worker/cron sobrepostos não processam o mesmo item simultaneamente nem perdem eventos após lease expirar.

### Homologação com Mercado Pago e frontend

1. Usar banco e credenciais separados de produção.
2. Conectar React com `VITE_GIFTS_MODE=api` e `VITE_API_BASE_URL` apontando para esta API.
3. Testar login real com cookies no domínio de destino e fluxo CSRF completo.
4. Cadastrar presente, abrir checkout Pix e cartão e conferir juros do parcelamento para o pagador.
5. Validar retorno pendente e posterior aprovação autoritativa, com acompanhamento atualizando sem recarregar manualmente.
6. Validar nova aba/link privado, falha de rede, retry sem cobrança duplicada, desativação do presente e preservação de snapshots.
7. Simular/testar estorno e contestação pelos recursos permitidos do ambiente do provedor.
8. Executar build, testes e smoke test HTTPS após deploy.

**Aceite:** o frontend existente funciona em modo API sem alterar seus contratos; convidados pagam pelo provedor; só verificações autoritativas confirmam valores; o casal acompanha o histórico com login; concorrência, notificações repetidas e reinicializações não perdem nem duplicam registros financeiros.

## 12. Entregáveis e ordem de implementação

1. Solution, Docker, configuração validada, EF migrations e endpoints de saúde.
2. Identity, sessões, Data Protection persistente, comandos de usuários, CORS e CSRF.
3. Catálogo, contribuições, snapshots, tokens privados e idempotência transacional.
4. Adapter Mercado Pago e criação/retomada segura de checkout.
5. Inbox, processamento de webhooks, reconciliação e métricas.
6. Histórico, resumo e testes de integração completos.
7. OpenAPI, `render.yaml`, README com deploy/rollback, operação e checklist de homologação.

O README deve explicar provisão das contas, certificados/segredos, migrations, conexões Neon, domínio/cookies, cadastro do webhook, jobs e recuperação de uma tentativa incerta. Não incluir credenciais demo ou exemplos com segredos reais.

## 13. Referências oficiais

Os contratos HTTP desta spec são próprios da aplicação. Os detalhes de payload/assinatura do provedor devem ser conferidos na documentação vigente e cobertos por testes do adapter:

- [Checkout Pro Orders — criar pedido e chave de idempotência](https://www.mercadopago.com.br/developers/en/docs/checkout-pro-orders/create-order?scope=prod).
- [Referência da criação de Orders](https://www.mercadopago.com.br/developers/en/reference/online-payments/checkout-pro/create-order/post).
- [Notificações Checkout Pro Orders](https://www.mercadopago.com.br/developers/pt/docs/checkout-pro-orders/notifications?scope=prod).
- [Entrada em produção](https://www.mercadopago.com.br/developers/pt/docs/checkout-pro-orders/go-to-production?scope=prod).
- [Render — Docker](https://render.com/docs/docker) e [Cron Jobs](https://render.com/docs/cronjobs).
- [Neon — connection pooling](https://neon.com/docs/connect/connection-pooling).
- [ASP.NET Core — Data Protection com EF Core](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers#entity-framework-core).
