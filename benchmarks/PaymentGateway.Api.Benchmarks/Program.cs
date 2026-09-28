using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;

// Single entrypoint: BenchmarkRunner discovers every [Benchmark] method in the assembly and
// runs each through BenchmarkDotNet's standard protocol (warmup + several measurement
// iterations + outlier analysis). Reports land in `BenchmarkDotNet.Artifacts/results/`.
//
// Without an explicit logger, BenchmarkDotNet emits "No loggers defined, you will not see any
// progress!" — the ConsoleLogger below restores the per-benchmark progress bars and summary
// header so the run is observable in the terminal.
var config = ManualConfig.CreateEmpty()
    .WithOptions(ConfigOptions.JoinSummary)
    .WithOptions(ConfigOptions.DisableLogFile)
    .AddLogger(ConsoleLogger.Default)
    .AddDiagnoser(MemoryDiagnoser.Default);

BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .RunAll(config);
