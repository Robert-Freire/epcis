# FasTnT Performance Tests

This project contains performance benchmarks for the FasTnT EPCIS implementation using BenchmarkDotNet.

## Purpose

The performance tests are designed to:
- Measure execution time and memory allocation for critical operations
- Identify performance bottlenecks in capture and query endpoints
- Track performance regressions across code changes
- Provide concrete metrics for optimization efforts
- Test system behavior with large EPCIS documents

## Running Benchmarks

### Run All Benchmarks

```bash
dotnet run -c Release --project tests/FasTnT.PerformanceTests
```

**Important**: Always run benchmarks in Release mode for accurate results.

### Run Specific Benchmarks

Use filters to run specific benchmark classes or methods:

```bash
# Run all benchmarks in a specific class
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *CaptureBenchmarks*

# Run a specific benchmark method
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *CaptureBenchmarks.CaptureSmallDocument*
```

### Run with Custom Configuration

```bash
# Run with specific job
dotnet run -c Release --project tests/FasTnT.PerformanceTests -- --job short

# Run with memory profiler
dotnet run -c Release --project tests/FasTnT.PerformanceTests -- --memory
```

## Understanding Results

### Key Metrics

| Metric | Description |
|--------|-------------|
| **Mean** | Arithmetic average of all measurements |
| **Error** | Half of 99.9% confidence interval |
| **StdDev** | Standard deviation of all measurements |
| **Median** | Value separating the higher half from the lower half |
| **Allocated** | Total memory allocated per operation |
| **Gen0/Gen1/Gen2** | Number of garbage collections per 1000 operations |

### Example Output

```
|                Method |     Mean |    Error |   StdDev | Allocated |
|---------------------- |---------:|---------:|---------:|----------:|
| CaptureSmallDocument  | 45.23 ms | 0.892 ms | 0.835 ms |  12.45 MB |
| CaptureLargeDocument  | 523.1 ms | 10.24 ms | 9.576 ms | 145.23 MB |
```

### Interpreting Results

- **Lower is better** for Mean, Error, StdDev, and Allocated
- **Consistency matters**: Lower StdDev indicates more predictable performance
- **Memory allocations**: High allocations may indicate opportunities for optimization
- **Gen2 collections**: Should be minimal for good performance

## Report Locations

After running benchmarks, reports are generated in:

```
tests/FasTnT.PerformanceTests/BenchmarkDotNet.Artifacts/results/
```

Available formats:
- **HTML**: Interactive report with charts (`*-report.html`)
- **Markdown**: GitHub-friendly format (`*-report-github.md`)
- **CSV**: Raw data for analysis (`*-report.csv`)

## Capture Endpoint Benchmarks

The project includes comprehensive benchmarks for the EPCIS capture endpoint:

### XmlParsingBenchmarks
Measures XML document parsing performance across different scales:
- Tests: 100 to 10,000 events with varying EPC counts (10, 50, 100 per event)
- Includes: XML schema validation overhead
- Purpose: Identify XML parsing bottlenecks and memory allocations

### JsonParsingBenchmarks
Measures JSON document parsing performance:
- Tests: Same scale as XML (100-10,000 events)
- Includes: Both full document parsing and individual event parsing
- Purpose: Compare JSON vs XML parsing efficiency

### CaptureBenchmarks (End-to-End)
Measures complete capture pipeline from parsing to database storage:
- `CaptureXmlEndToEnd()`: Full XML capture flow
- `CaptureJsonEndToEnd()`: Full JSON capture flow
- `CapturePreParsedRequest()`: Validation + hashing + database (no parsing)
- `CaptureWithAggregations()`: Parent-child EPC relationship handling
- Purpose: Identify end-to-end bottlenecks and format comparison

### ComponentBenchmarks
Isolates individual component performance:
- `ValidateRequest()`: Request validation overhead
- `ValidateEvents()`: Per-event validation by event type
- `ComputeEventHashes()`: Event hash computation overhead
- `DatabaseInsertOnly()`: Raw EF Core insert performance
- `DatabaseInsertWithTransaction()`: Transaction overhead measurement
- `DatabaseBulkInsert()`: Batch insert vs single large request
- Purpose: Pinpoint specific bottlenecks in the pipeline

### StressTestBenchmarks
Tests system behavior under extreme conditions:
- `CaptureAtMaxLimit()`: Performance at production limit (500 events)
- `CaptureExceedingLimit()`: Validation rejection speed for oversized requests
- `CaptureLargeXmlDocument()`: 5,000-10,000 event XML documents
- `CaptureLargeJsonDocument()`: 5,000-10,000 event JSON documents
- `CaptureRealisticMixedWorkload()`: Mixed event types with varying EPC counts
- Purpose: Identify scalability limits and degradation patterns

### Running Capture Benchmarks

```bash
# Run all capture benchmarks
dotnet run -c Release --project tests/FasTnT.PerformanceTests

# Run specific benchmark classes
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *XmlParsingBenchmarks*
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *JsonParsingBenchmarks*
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *CaptureBenchmarks*
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *ComponentBenchmarks*
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *StressTestBenchmarks*

# Run specific benchmark method
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *CaptureBenchmarks.CapturePreParsedRequest*
```

## Analyzing Capture Performance

### Expected Bottlenecks

Based on code analysis, likely performance issues:

1. **XML Schema Validation**: XSD validation in XML parsing
2. **JSON Schema Validation**: JSON schema validation overhead
3. **Database Transaction**: Single transaction for entire capture
4. **Double SaveChanges**: Called twice per capture (insert + record time update)
5. **Event Hash Computation**: Computed for all events without IDs

### Interpreting Results

**Parsing Performance:**
- Compare XML vs JSON parsing times
- Higher memory allocations in XML indicate schema validation overhead
- JSON should generally be faster due to simpler validation

**End-to-End Performance:**
- If `CapturePreParsedRequest` is fast but `CaptureXmlEndToEnd` is slow, parsing is the bottleneck
- If both are slow, focus on validation or database operations
- Compare transaction overhead between `DatabaseInsertOnly` and `DatabaseInsertWithTransaction`

**Common Bottlenecks:**
- **Parsing > 50% of total time**: Optimize schema validation or consider async parsing
- **High Gen2 collections**: Reduce memory allocations in hot paths
- **Transaction overhead > 20%**: Consider batching strategies
- **Validation > 30% of total time**: Cache validation results or optimize validators

### Optimization Strategies

Based on benchmark results:

| Observation | Likely Cause | Optimization Strategy |
|-------------|--------------|----------------------|
| XML parsing > 2x JSON parsing | XML schema validation | Consider JSON-first approach or lazy validation |
| High memory allocations | String concatenation, temporary objects | Use StringBuilder, object pooling |
| Linear degradation with event count | O(n) operations in hot path | Consider parallel processing |
| Transaction commit time dominates | Database I/O | Batch multiple requests, optimize indexes |
| Validation time proportional to EPCs | Per-EPC validation overhead | Batch validate EPCs, use HashSet lookups |

## Benchmark Results

Baseline results (to be populated after first run):

### Parsing Performance

| Benchmark | Event Count | EPCs/Event | Mean | Allocated |
|-----------|-------------|------------|------|-----------|
| XmlParsing | 100 | 10 | TBD | TBD |
| XmlParsing | 1000 | 50 | TBD | TBD |
| JsonParsing | 100 | 10 | TBD | TBD |
| JsonParsing | 1000 | 50 | TBD | TBD |

### End-to-End Capture

| Benchmark | Event Count | Mean | Allocated |
|-----------|-------------|------|-----------|
| CaptureXmlEndToEnd | 100 | TBD | TBD |
| CaptureJsonEndToEnd | 100 | TBD | TBD |
| CapturePreParsedRequest | 100 | TBD | TBD |

### Component Performance

| Benchmark | Event Count | Mean | Allocated |
|-----------|-------------|------|-----------|
| ValidateRequest | 1000 | TBD | TBD |
| ComputeEventHashes | 1000 | TBD | TBD |
| DatabaseInsertWithTransaction | 1000 | TBD | TBD |

**Instructions for updating results:**
1. Run benchmarks in Release mode on a clean system
2. Record Mean and Allocated metrics for key configurations
3. Update this table with baseline values
4. Re-run after optimizations to track improvements

## Test Data Configurations

The `TestDataGenerator` provides preset configurations:

| Size | Events | EPCs/Event | Total EPCs | Use Case |
|------|--------|------------|------------|----------|
| Small | 100 | 10 | 1,000 | Quick tests |
| Medium | 500 | 20 | 10,000 | Standard documents |
| Large | 1,000 | 50 | 50,000 | Large batches |
| XLarge | 5,000 | 100 | 500,000 | Stress testing |

## Adding New Benchmarks

1. Create a new class in the project
2. Add the `[Config(typeof(BenchmarkConfig))]` attribute
3. Create benchmark methods with `[Benchmark]` attribute
4. Use `[Params]` to test different input sizes
5. Implement `[GlobalSetup]` and `[GlobalCleanup]` for initialization

Example:

```csharp
[Config(typeof(BenchmarkConfig))]
public class MyBenchmarks
{
    private EpcisContext _context;

    [Params(100, 500, 1000)]
    public int EventCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = BenchmarkDbContext.CreateInMemoryContext("MyBenchmark");
    }

    [Benchmark]
    public async Task MyOperation()
    {
        // Benchmark code here
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkDbContext.Cleanup(_context);
    }
}
```

## Best Practices

1. **Always use Release mode**: Debug builds include overhead that skews results
2. **Close unnecessary applications**: Reduce system noise during benchmarking
3. **Run multiple times**: Verify consistency across runs
4. **Use realistic data**: Generate test data that matches production scenarios
5. **Baseline comparisons**: Use `[Baseline]` attribute to compare implementations
6. **Avoid I/O in hot path**: Database operations should be part of setup when measuring pure logic
7. **Clean state**: Use `[IterationSetup]` if each iteration needs a fresh state

## Troubleshooting

### Benchmarks Running Slowly

- Check if running in Debug mode (should be Release)
- Verify no debugger is attached
- Close resource-intensive applications

### High Memory Usage

- Use in-memory database for faster tests
- Implement proper cleanup in `[GlobalCleanup]`
- Check for memory leaks with memory profiler

### Inconsistent Results

- Reduce system load during benchmarking
- Increase iteration count for more stable results
- Check for background processes interfering

## References

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [BenchmarkDotNet Best Practices](https://benchmarkdotnet.org/articles/guides/good-practices.html)
- [EPCIS 2.0 Standard](https://www.gs1.org/standards/epcis)
