using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Validators;

namespace FasTnT.PerformanceTests.Config;

public class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        // Add memory diagnostics to track allocations
        AddDiagnoser(MemoryDiagnoser.Default);

        // Add standard columns
        AddColumn(StatisticColumn.Mean);
        AddColumn(StatisticColumn.StdDev);
        AddColumn(StatisticColumn.Median);
        AddColumn(StatisticColumn.OperationsPerSecond);

        // Configure exporters for report generation
        AddExporter(HtmlExporter.Default);
        AddExporter(MarkdownExporter.GitHub);
        AddExporter(CsvExporter.Default);

        // Configure job settings for reliable benchmarking
        AddJob(Job.Default
            .WithId("PerformanceTests")
            .WithPlatform(BenchmarkDotNet.Environments.Platform.X64)
            .WithJit(BenchmarkDotNet.Environments.Jit.RyuJit)
            .WithMinIterationCount(5)
            .WithMaxIterationCount(20)
        );

        // Add baseline validation
        AddValidator(JitOptimizationsValidator.FailOnError);

        // Configure summary style
        WithSummaryStyle(BenchmarkDotNet.Reports.SummaryStyle.Default.WithRatioStyle(RatioStyle.Trend));
    }
}
