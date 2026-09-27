using System.Reflection;

using FluentAssertions;

using NetArchTest.Rules;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Tests.Architecture;

/// <summary>
/// Executable guardrails for the layered structure the codebase was refactored into. Each test
/// asserts a dependency-direction or placement invariant against the compiled API assembly, so a
/// future edit that quietly points an arrow the wrong way fails here instead of eroding the design.
///
/// Layers (namespaces under <c>PaymentGateway.Api</c>):
/// Core (<c>Models</c>, <c>Exceptions</c>) ← Ports (<c>Abstractions</c>) / Instrumentation
/// (<c>Metrics</c>) ← Adapters (<c>Services</c>, <c>Persistence</c>, <c>Clients</c>,
/// <c>Validation</c>) and Web (<c>Controllers</c>, <c>Middleware</c>, <c>Filters</c>). The
/// composition root (<c>Configuration</c> + <c>Program</c>) may depend on anything.
///
/// <c>ResideInNamespace</c> matches by prefix, so a layer constant also covers its sub-namespaces
/// (e.g. <c>Models</c> covers <c>Models.Bank</c>/<c>Models.Requests</c>/<c>Models.Responses</c>).
/// </summary>
public class ArchitectureTests
{
    private const string Root = "PaymentGateway.Api";
    private const string Models = Root + ".Models";
    private const string Exceptions = Root + ".Exceptions";
    private const string Abstractions = Root + ".Abstractions";
    private const string Metrics = Root + ".Metrics";
    private const string Services = Root + ".Services";
    private const string Persistence = Root + ".Persistence";
    private const string Clients = Root + ".Clients";
    private const string Validation = Root + ".Validation";
    private const string Controllers = Root + ".Controllers";
    private const string Middleware = Root + ".Middleware";
    private const string Filters = Root + ".Filters";
    private const string Configuration = Root + ".Configuration";

    private static readonly Assembly Api = typeof(IPaymentsService).Assembly;

    private static void Passes(TestResult result)
    {
        var offenders = result.FailingTypes is null
            ? string.Empty
            : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
        result.IsSuccessful.Should().BeTrue("the architecture rule was violated by: {0}", offenders);
    }

    // --- Core is pure --------------------------------------------------------

    [Fact]
    public void Models_depend_on_nothing_else_in_the_application()
    {
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Models)
            .ShouldNot().HaveDependencyOnAny(
                Abstractions, Metrics, Services, Persistence, Clients,
                Validation, Controllers, Middleware, Filters, Configuration)
            .GetResult());
    }

    [Fact]
    public void Exceptions_depend_on_nothing_else_in_the_application()
    {
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Exceptions)
            .ShouldNot().HaveDependencyOnAny(
                Models, Abstractions, Metrics, Services, Persistence, Clients,
                Validation, Controllers, Middleware, Filters, Configuration)
            .GetResult());
    }

    // --- Ports and instrumentation only face Core ----------------------------

    [Fact]
    public void Abstractions_do_not_depend_on_implementations_web_or_configuration()
    {
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Abstractions)
            .ShouldNot().HaveDependencyOnAny(
                Metrics, Services, Persistence, Clients,
                Validation, Controllers, Middleware, Filters, Configuration)
            .GetResult());
    }

    [Fact]
    public void Metrics_only_depend_on_core()
    {
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Metrics)
            .ShouldNot().HaveDependencyOnAny(
                Abstractions, Services, Persistence, Clients,
                Validation, Controllers, Middleware, Filters, Configuration)
            .GetResult());
    }

    // --- Adapters never reach up into Web or the composition root ------------

    [Fact]
    public void Adapters_do_not_depend_on_web_or_configuration()
    {
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Services)
            .Or().ResideInNamespace(Persistence)
            .Or().ResideInNamespace(Clients)
            .Or().ResideInNamespace(Validation)
            .ShouldNot().HaveDependencyOnAny(Controllers, Middleware, Filters, Configuration)
            .GetResult());
    }

    // --- Web depends on ports, not on concrete adapters or each other --------

    [Fact]
    public void Web_components_depend_on_abstractions_not_on_concrete_adapters()
    {
        // R-003: applies to the whole web tier, not just controllers — a future middleware or filter
        // taking a direct dependency on a concrete Services/Persistence/Clients type would violate
        // the same "depend on ports" invariant and must fail here too.
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Controllers)
            .Or().ResideInNamespace(Middleware)
            .Or().ResideInNamespace(Filters)
            .ShouldNot().HaveDependencyOnAny(Services, Persistence, Clients)
            .GetResult());
    }

    [Fact]
    public void Controllers_do_not_depend_on_middleware_or_filters()
    {
        // R-005: the audit-outcome key was moved to Abstractions.AuditConventions so the controller
        // no longer reaches into the middleware; this keeps the web components decoupled.
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Controllers)
            .ShouldNot().HaveDependencyOnAny(Middleware, Filters)
            .GetResult());
    }

    [Fact]
    public void Web_components_do_not_depend_on_configuration()
    {
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Controllers)
            .Or().ResideInNamespace(Middleware)
            .Or().ResideInNamespace(Filters)
            .ShouldNot().HaveDependencyOnAny(Configuration)
            .GetResult());
    }

    // --- The composition root is the only thing that depends on Configuration -

    [Fact]
    public void Only_the_composition_root_depends_on_configuration()
    {
        // Program lives in the global namespace, so ResideInNamespace(Root) excludes it — it is the
        // composition root and may reference Configuration freely.
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Root)
            .And().DoNotResideInNamespace(Configuration)
            .ShouldNot().HaveDependencyOnAny(Configuration)
            .GetResult());
    }

    // --- Placement conventions ----------------------------------------------

    [Fact]
    public void All_interfaces_defined_in_the_api_live_in_abstractions()
    {
        Passes(Types.InAssembly(Api)
            .That().AreInterfaces()
            .Should().ResideInNamespace(Abstractions)
            .GetResult());
    }

    [Fact]
    public void Controllers_are_built_against_the_abstractions()
    {
        // A positive counterpart to the ShouldNot rules: controllers genuinely depend on the ports,
        // which confirms the dependency detection has teeth (a broken detector would fail this).
        Passes(Types.InAssembly(Api)
            .That().ResideInNamespace(Controllers)
            .Should().HaveDependencyOnAny(Abstractions)
            .GetResult());
    }

    // --- PCI safety net (ADR-0008) -------------------------------------------

    [Fact]
    public void Raw_card_fields_exist_only_on_the_designated_request_dtos()
    {
        // Turns "we never persist/log the full PAN or CVV" into a build-enforced invariant: a
        // property named CardNumber/Cvv/Pan may only live on the merchant request DTO or the bank
        // wire DTO — never on a domain model, response, audit record, or anything persisted.
        // (CardNumberLastFour is deliberately not matched — the last four are allowed.)
        var allowed = new[] { typeof(PostPaymentRequest), typeof(BankPaymentRequest) };
        // R-005: exact (case-insensitive) names, widened to differently-spelled PAN carriers — but
        // NOT substring matching, so the allowed CardNumberLastFour is never flagged.
        var sensitiveNames = new[]
        {
            "CardNumber", "Cvv", "Pan", "PrimaryAccountNumber", "AccountNumber", "CardNo", "FullPan"
        };
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // Scan public properties AND public fields (R-005: fields were previously unscanned).
        var offenders = Api.GetTypes()
            .Where(t => !allowed.Contains(t))
            .SelectMany(t => t.GetProperties(flags).Select(m => m.Name)
                .Concat(t.GetFields(flags).Select(m => m.Name))
                .Where(name => sensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(name => $"{t.FullName}.{name}"))
            .ToArray();

        offenders.Should().BeEmpty(
            "raw card fields must not exist outside the designated request DTOs (ADR-0008), but found: {0}",
            string.Join(", ", offenders));
    }

    // --- Guard against vacuous rules -----------------------------------------

    [Theory]
    [InlineData(Models)]
    [InlineData(Exceptions)]
    [InlineData(Abstractions)]
    [InlineData(Metrics)]
    [InlineData(Services)]
    [InlineData(Persistence)]
    [InlineData(Clients)]
    [InlineData(Validation)]
    [InlineData(Controllers)]
    [InlineData(Middleware)]
    [InlineData(Filters)]
    [InlineData(Configuration)]
    public void Every_layer_namespace_actually_contains_types(string layer)
    {
        // If a layer constant were mistyped it would select nothing and every ShouldNot rule over it
        // would pass vacuously. This keeps the rules above honest.
        Types.InAssembly(Api).That().ResideInNamespace(layer).GetTypes()
            .Should().NotBeEmpty("layer '{0}' should contain at least one type", layer);
    }
}
