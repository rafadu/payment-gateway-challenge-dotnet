using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers(options =>
    options.Filters.AddService<IdempotencyResourceFilter>());
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
// Swagger is a Development affordance; docker-compose (production) flips `Swagger__Enabled`
// to false via the environment so the containerised API doesn't expose the schema browser.
if (builder.Configuration.GetValue<bool>("Swagger:Enabled", true))
{
    builder.Services.AddSwaggerGen();
}

builder.Services.AddSingleton<IPaymentsRepository, MongoPaymentsRepository>();
builder.Services.AddSingleton<IPaymentsService, PaymentsService>();

// In-memory idempotency-key store (ADR-0003). Singleton — it owns the dictionary of claims, no
// per-request state.
builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddScoped<IdempotencyResourceFilter>();

// Acquiring bank client: typed HttpClient with bounded timeout (ADR-0001). BaseUrl /
// TimeoutSeconds are read from the BankSimulator configuration section (design.md).
builder.Services.AddBankClient(builder.Configuration);

// FluentValidation for POST /api/payments. Currency allow-list is read from the
// SupportedCurrencies configuration section (design.md).
builder.Services.AddPaymentValidation(builder.Configuration);

// MongoDB-backed merchant credentials behind an in-memory read-through cache (ADR-0010).
builder.Services.AddMongoDb(builder.Configuration);
builder.Services.AddCredentialCache(builder.Configuration);

// MongoDB-backed audit trail (ADR-0004, audit half — Stage 11).
builder.Services.AddAudit(builder.Configuration);

// Merchant login: JWT issuance for POST /api/auth/token (ADR-0010).
builder.Services.AddTokenIssuance(builder.Configuration);

// JWT Bearer authentication gating POST/GET /api/payments (ADR-0010).
builder.Services.AddJwtAuthentication(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Audit trail (ADR-0004) — middleware-level so it captures 401s emitted by UseAuthorization.
// Placed before UseAuthorization/MapControllers so the middleware sees requests rejected at
// the authorization-middleware layer too. User may be anonymous at this point for unauth requests;
// that's expected and the audit record correctly records MerchantId="" for those.
app.UseMiddleware<AuditMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposed so test projects can use WebApplicationFactory<Program>. Top-level statements compile to
// an internal Program class by default; the partial declaration below promotes it to public.
public partial class Program;
