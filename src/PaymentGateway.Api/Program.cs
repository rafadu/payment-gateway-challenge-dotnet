using FluentValidation;

using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Services;
using PaymentGateway.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IPaymentsRepository, PaymentsRepository>();
builder.Services.AddSingleton<IPaymentsService, PaymentsService>();

// Acquiring bank client: typed HttpClient with bounded timeout (ADR-0001). Stage 7 wires it with
// localhost defaults so the IPaymentsService registration above can be constructed; stage 9 reads
// BaseUrl/TimeoutSeconds from the BankSimulator configuration section once appsettings.json is
// finalised (design.md).
builder.Services.AddHttpClient<IAcquiringBankClient, AcquiringBankClient>(client =>
{
    client.BaseAddress = new Uri("http://localhost:8080");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// FluentValidation for POST /api/payments. The currency allow-list is hardcoded here in stage 7;
// stage 9 reads it from the SupportedCurrencies configuration section (design.md) once
// appsettings.json is finalised.
builder.Services.AddSingleton<IValidator<PostPaymentRequest>>(sp =>
    new PostPaymentRequestValidator(
        new[] { "GBP", "USD", "EUR" },
        sp.GetRequiredService<TimeProvider>()));

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
