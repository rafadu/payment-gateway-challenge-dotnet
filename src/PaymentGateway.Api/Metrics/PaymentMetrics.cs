using System.Diagnostics.Metrics;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Metrics;

/// <summary>
/// Custom business and bank-latency instrumentation (ADR-0007), built on the vendor-neutral
/// <see cref="System.Diagnostics.Metrics"/> API so the observability backend (Prometheus, Grafana,
/// Application Insights, Datadog) is an exporter-configuration choice rather than an
/// instrumentation-code choice. All tags are aggregate/statistical (status, currency, acquirer,
/// outcome, reason) — never request content — so no card data or PII is emitted by construction.
/// Registered as a singleton; the underlying <see cref="Meter"/> is created via
/// <see cref="IMeterFactory"/> so tests can observe it with a scoped <c>MetricCollector</c>.
/// </summary>
public sealed class PaymentMetrics
{
    /// <summary>Meter name the OpenTelemetry pipeline subscribes to and tests collect from.</summary>
    public const string MeterName = "PaymentGateway.Payments";

    private const string RejectedStatus = "Rejected";

    private readonly Counter<long> _processed;
    private readonly Histogram<double> _bankCallDuration;
    private readonly Counter<long> _idempotencyReplays;
    private readonly Counter<long> _rejectedReasons;

    public PaymentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _processed = meter.CreateCounter<long>(
            "payments.processed.count",
            unit: "{payment}",
            description: "Number of payments processed, tagged by bank-adjudicated status and currency.");

        _bankCallDuration = meter.CreateHistogram<double>(
            "payments.bank.call.duration",
            unit: "ms",
            description: "Duration of an acquiring-bank call, tagged by acquirer and outcome (success/timeout/error).");

        _idempotencyReplays = meter.CreateCounter<long>(
            "payments.idempotency.replay.count",
            unit: "{replay}",
            description: "Number of times a cached idempotent response was replayed instead of processed afresh (ADR-0003).");

        _rejectedReasons = meter.CreateCounter<long>(
            "payments.rejected.reason.count",
            unit: "{rejection}",
            description: "Number of validation rejections, tagged by the validation rule that failed — distinguishes a merchant integration bug from organic invalid input or probing traffic.");
    }

    /// <summary>
    /// Records one processed payment, tagged by its outcome <paramref name="status"/> and
    /// <paramref name="currency"/>.
    /// </summary>
    public void RecordProcessed(PaymentStatus status, string currency) =>
        _processed.Add(1,
            new KeyValuePair<string, object?>("status", status.ToString()),
            new KeyValuePair<string, object?>("currency", currency));

    /// <summary>
    /// Records the wall-clock <paramref name="durationMs"/> of a single acquiring-bank call,
    /// tagged by <paramref name="acquirer"/> and its <paramref name="outcome"/>. This is the
    /// p95/p99 latency and failure-rate signal ADR-0001 needs before a circuit breaker's
    /// thresholds could be sized.
    /// </summary>
    public void RecordBankCall(double durationMs, string acquirer, BankCallOutcome outcome) =>
        _bankCallDuration.Record(durationMs,
            new KeyValuePair<string, object?>("acquirer", acquirer),
            new KeyValuePair<string, object?>("outcome", outcome.ToString().ToLowerInvariant()));

    /// <summary>
    /// Records one served idempotent replay — a cached response returned instead of fresh
    /// processing (ADR-0003), a signal of how often merchants actually retry.
    /// </summary>
    public void RecordIdempotencyReplay() => _idempotencyReplays.Add(1);

    /// <summary>
    /// Records a validation rejection: one <c>payments.processed.count</c> tagged
    /// <c>Rejected</c> (with <paramref name="currency"/>), plus one
    /// <c>payments.rejected.reason.count</c> per failed rule in <paramref name="failedRules"/>.
    /// A payment can fail several rules at once, so the reason counter is incremented per rule
    /// while the payment itself counts once. Rule names are a bounded, gateway-defined set (the
    /// validated property names), never request content — so this stays cardinality-safe.
    /// </summary>
    public void RecordRejection(string currency, IEnumerable<string> failedRules)
    {
        _processed.Add(1,
            new KeyValuePair<string, object?>("status", RejectedStatus),
            new KeyValuePair<string, object?>("currency", currency));

        foreach (var rule in failedRules)
        {
            _rejectedReasons.Add(1, new KeyValuePair<string, object?>("reason", rule));
        }
    }
}
