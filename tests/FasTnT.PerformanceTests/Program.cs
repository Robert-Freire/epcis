using BenchmarkDotNet.Running;
using FasTnT.PerformanceTests.Config;

namespace FasTnT.PerformanceTests;

/// <summary>
/// FasTnT EPCIS Performance Test Suite
///
/// Run all benchmarks:
///   dotnet run -c Release
///
/// Run specific benchmark categories:
///   dotnet run -c Release --filter *QueryBenchmarks*
///   dotnet run -c Release --filter *SerializationBenchmarks*
///   dotnet run -c Release --filter *EndToEndQueryBenchmarks*
///   dotnet run -c Release --filter *CaptureBenchmarks*
///
/// Run specific benchmark methods:
///   dotnet run -c Release --filter *QueryByEpc*
///   dotnet run -c Release --filter *SerializeToXml*
///
/// Additional BenchmarkDotNet options:
///   --join                    Join multiple benchmark results
///   --memory                  Enable memory diagnoser
///   --list                    List all available benchmarks
///   --artifacts               Specify artifacts directory
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
