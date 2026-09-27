using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the MongoDB-backed audit store (ADR-0004, audit half — long marked "documented,
/// not built"; built in Stage 11) and the <see cref="AuditMiddleware"/> that captures every
/// request through the pipeline. The middleware is wired in <c>Program.cs</c> before
/// <c>UseAuthentication</c>/<c>UseAuthorization</c>, so it captures requests even when they are
/// rejected at the authorization-middleware layer (i.e. <c>401</c>s). Requires <c>AddMongoDb</c>
/// to have registered the <see cref="MongoDB.Driver.IMongoDatabase"/> this collection writes to.
/// </summary>
public static class AuditServiceCollectionExtensions
{
    public static IServiceCollection AddAudit(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditStore, MongoAuditStore>();
        return services;
    }
}