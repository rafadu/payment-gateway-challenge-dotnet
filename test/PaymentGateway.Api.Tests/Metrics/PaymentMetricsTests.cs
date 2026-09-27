using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Tests.Metrics;

public class PaymentMetricsTests
{
    private readonly IMeterFactory _meterFactory;
    private readonly PaymentMetrics _metrics;

    public PaymentMetricsTests()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        _meterFactory = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
        _metrics = new PaymentMetrics(_meterFactory);
    }

    [Fact]
    public void RecordProcessed_emits_processed_count_tagged_by_status_and_currency()
    {
        using var collector = new MetricCollector<long>(
            _meterFactory, PaymentMetrics.MeterName, "payments.processed.count");

        _metrics.RecordProcessed(PaymentStatus.Authorized, "GBP");

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().Be(1);
        snapshot[0].Tags["status"].Should().Be("Authorized");
        snapshot[0].Tags["currency"].Should().Be("GBP");
    }

    [Fact]
    public void RecordBankCall_emits_bank_call_duration_tagged_by_acquirer_and_outcome()
    {
        using var collector = new MetricCollector<double>(
            _meterFactory, PaymentMetrics.MeterName, "payments.bank.call.duration");

        _metrics.RecordBankCall(42.5, acquirer: "simulator", outcome: BankCallOutcome.Success);

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().Be(42.5);
        snapshot[0].Tags["acquirer"].Should().Be("simulator");
        snapshot[0].Tags["outcome"].Should().Be("success");
    }

    [Fact]
    public void RecordIdempotencyReplay_increments_the_replay_count()
    {
        using var collector = new MetricCollector<long>(
            _meterFactory, PaymentMetrics.MeterName, "payments.idempotency.replay.count");

        _metrics.RecordIdempotencyReplay();

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Value.Should().Be(1);
    }

    [Fact]
    public void RecordRejection_counts_the_rejection_once_and_each_failed_reason_separately()
    {
        using var processed = new MetricCollector<long>(
            _meterFactory, PaymentMetrics.MeterName, "payments.processed.count");
        using var reasons = new MetricCollector<long>(
            _meterFactory, PaymentMetrics.MeterName, "payments.rejected.reason.count");

        _metrics.RecordRejection("GBP", new[] { "CardNumber", "Cvv" });

        // The rejection is one processed payment, tagged Rejected.
        var processedSnapshot = processed.GetMeasurementSnapshot();
        processedSnapshot.Should().ContainSingle();
        processedSnapshot[0].Value.Should().Be(1);
        processedSnapshot[0].Tags["status"].Should().Be("Rejected");
        processedSnapshot[0].Tags["currency"].Should().Be("GBP");

        // Each failed rule is counted independently, tagged by the rule name.
        var reasonSnapshot = reasons.GetMeasurementSnapshot();
        reasonSnapshot.Should().HaveCount(2);
        reasonSnapshot.Select(m => m.Tags["reason"]).Should().BeEquivalentTo(new[] { "CardNumber", "Cvv" });
        reasonSnapshot.Sum(m => m.Value).Should().Be(2);
    }
}
