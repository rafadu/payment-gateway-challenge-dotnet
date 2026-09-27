using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers(options =>
    options.Filters.AddService<IdempotencyResourceFilter>());
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IPaymentsRepository, PaymentsRepository>();
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

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
