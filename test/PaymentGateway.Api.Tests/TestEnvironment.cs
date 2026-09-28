using System;
using System.Runtime.CompilerServices;

namespace PaymentGateway.Api.Tests;

/// <summary>
/// Assembly-wide test bootstrap. Runs once, before any test or <c>WebApplicationFactory</c> is
/// constructed, so every host booted in this assembly picks up the settings below.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Disable the bank-intents hosted services (reconciler + cleanup) for the whole test
        // assembly. Booting the app under WebApplicationFactory otherwise starts the reconciler's
        // poll loop, which — with no Mongo running — blocks on the driver's server-selection
        // timeout and orphans the test host (this is what broke the "Unit + Architectural" CI job).
        // The default host configuration reads environment variables (with "__" mapping to ":"),
        // so this feeds BankIntents:RunBackgroundServices=false into AddBankIntents. Integration
        // tests drive IntentReconciliationLogic directly and never need the hosted timers, so
        // switching them off globally is safe.
        Environment.SetEnvironmentVariable("BankIntents__RunBackgroundServices", "false");
    }
}
