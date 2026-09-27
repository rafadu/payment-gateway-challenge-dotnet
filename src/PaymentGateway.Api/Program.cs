using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IPaymentsRepository, PaymentsRepository>();

// MongoDB-backed merchant credentials behind an in-memory read-through cache (ADR-0010).
builder.Services.AddMongoDb(builder.Configuration);
builder.Services.AddCredentialCache(builder.Configuration);

// Merchant login: JWT issuance for POST /api/auth/token (ADR-0010).
builder.Services.AddTokenIssuance(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
