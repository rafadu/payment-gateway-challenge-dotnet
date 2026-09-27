using FluentAssertions;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Tests.Services;

public class InMemoryAuditStoreTests
{
    private static AuditRecord BuildRecord(string merchantId = "merchant-42", string outcome = "Authorized")
    {
        return new AuditRecord
        {
            Id = Guid.NewGuid(),
            Timestamp = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc),
            MerchantId = merchantId,
            Method = "POST",
            Path = "/api/payments",
            StatusCode = 201,
            Outcome = outcome,
            DurationMs = 42,
            RequestSummary = new Dictionary<string, object?>
            {
                { "cardNumberLastFour", "8877" },
                { "currency", "GBP" },
                { "amount", 100 },
                { "expiryMonth", 12 },
                { "expiryYear", 2030 }
            }
        };
    }

    [Fact]
    public async Task WriteAsync_appends_the_record_and_it_is_retrievable_in_order()
    {
        var store = new InMemoryAuditStore();
        var first = BuildRecord(outcome: "Authorized");
        var second = BuildRecord(outcome: "Declined");

        await store.WriteAsync(first);
        await store.WriteAsync(second);

        store.Records.Should().HaveCount(2);
        store.Records[0].Should().BeSameAs(first);
        store.Records[1].Should().BeSameAs(second);
    }

    [Fact]
    public async Task WriteAsync_with_an_unauthenticated_request_keeps_an_empty_merchant_id()
    {
        var store = new InMemoryAuditStore();

        await store.WriteAsync(BuildRecord(merchantId: string.Empty));

        store.Records.Single().MerchantId.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_preserves_the_masked_request_summary_without_cvv()
    {
        var store = new InMemoryAuditStore();

        await store.WriteAsync(BuildRecord());

        var summary = store.Records.Single().RequestSummary;
        summary.Should().NotBeNull();
        summary.Should().NotContainKey("cvv");
        summary.Should().ContainKey("cardNumberLastFour");
        Assert.Equal("8877", summary["cardNumberLastFour"]);
    }
}