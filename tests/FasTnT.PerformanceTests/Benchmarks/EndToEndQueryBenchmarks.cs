using BenchmarkDotNet.Attributes;
using FasTnT.Application.Database;
using FasTnT.Host.Communication.Xml.Formatters;
using FasTnT.Host.Communication.Json.Formatters;
using FasTnT.Application.Handlers;
using FasTnT.Domain.Model.Queries;
using FasTnT.Host.Endpoints.Interfaces;
using FasTnT.PerformanceTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class EndToEndQueryBenchmarks
{
    [Params(1000, 10000, 50000)]
    public int DatabaseSize { get; set; }

    [Params("Xml", "Json")]
    public string Format { get; set; } = null!;

    private EpcisContext _context1K = null!;
    private EpcisContext _context10K = null!;
    private EpcisContext _context50K = null!;
    private DataRetrieverHandler _handler1K = null!;
    private DataRetrieverHandler _handler10K = null!;
    private DataRetrieverHandler _handler50K = null!;

    private string _testEpc = null!;
    private string _testBizStep = null!;
    private string _testDisposition = null!;
    private DateTime _startDate;
    private DateTime _endDate;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Create and populate databases
        _context1K = BenchmarkDbContext.CreateInMemoryContext("EndToEndBenchmarks_1K");
        TestDataGenerator.PopulateDatabaseWithEvents(_context1K, 1000);

        _context10K = BenchmarkDbContext.CreateInMemoryContext("EndToEndBenchmarks_10K");
        TestDataGenerator.PopulateDatabaseWithEvents(_context10K, 10000);

        _context50K = BenchmarkDbContext.CreateInMemoryContext("EndToEndBenchmarks_50K");
        TestDataGenerator.PopulateDatabaseWithEvents(_context50K, 50000);

        // Initialize handlers
        var constants = BenchmarkConstants.CreateForQueries(maxEventsReturned: 60000);
        _handler1K = new DataRetrieverHandler(_context1K, new TestCurrentUser(), constants);
        _handler10K = new DataRetrieverHandler(_context10K, new TestCurrentUser(), constants);
        _handler50K = new DataRetrieverHandler(_context50K, new TestCurrentUser(), constants);

        // Pre-generate test values
        _testEpc = TestDataGenerator.GenerateRandomEpc(0, 0);
        _testBizStep = "urn:epcglobal:cbv:bizstep:shipping";
        _testDisposition = "urn:epcglobal:cbv:disp:in_transit";
        _startDate = DateTime.UtcNow.AddDays(-30);
        _endDate = DateTime.UtcNow;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _context1K.ChangeTracker.Clear();
        _context10K.ChangeTracker.Clear();
        _context50K.ChangeTracker.Clear();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        BenchmarkDbContext.Cleanup(_context1K);
        BenchmarkDbContext.Cleanup(_context10K);
        BenchmarkDbContext.Cleanup(_context50K);
    }

    private EpcisContext GetCurrentContext()
    {
        return DatabaseSize switch
        {
            1000 => _context1K,
            10000 => _context10K,
            50000 => _context50K,
            _ => throw new InvalidOperationException($"Invalid DatabaseSize: {DatabaseSize}")
        };
    }

    private DataRetrieverHandler GetCurrentHandler()
    {
        return DatabaseSize switch
        {
            1000 => _handler1K,
            10000 => _handler10K,
            50000 => _handler50K,
            _ => throw new InvalidOperationException($"Invalid DatabaseSize: {DatabaseSize}")
        };
    }

    private string FormatResponse(QueryResponse response)
    {
        var result = new QueryResult(response);
        return Format switch
        {
            "Xml" => XmlResponseFormatter.Format(result),
            "Json" => JsonResponseFormatter.Format(result),
            _ => throw new InvalidOperationException($"Invalid Format: {Format}")
        };
    }

    [Benchmark]
    public async Task<string> EndToEndSimpleQuery()
    {
        var parameters = new[] { QueryParameter.Create("MATCH_anyEPC", _testEpc) };
        var handler = GetCurrentHandler();
        var events = await handler.QueryEventsAsync(parameters, CancellationToken.None);
        var response = TestDataGenerator.CreateQueryResponse(events);
        return FormatResponse(response);
    }

    [Benchmark]
    public async Task<string> EndToEndComplexQuery()
    {
        var parameters = new[]
        {
            QueryParameter.Create("MATCH_anyEPC", _testEpc),
            QueryParameter.Create("GE_eventTime", _startDate.ToString("o")),
            QueryParameter.Create("LT_eventTime", _endDate.ToString("o")),
            QueryParameter.Create("EQ_bizStep", _testBizStep),
            QueryParameter.Create("EQ_disposition", _testDisposition)
        };
        var handler = GetCurrentHandler();
        var events = await handler.QueryEventsAsync(parameters, CancellationToken.None);
        var response = TestDataGenerator.CreateQueryResponse(events);
        return FormatResponse(response);
    }

    [Benchmark]
    public async Task<string> EndToEndLargeResultSet()
    {
        var parameters = new[]
        {
            QueryParameter.Create("eventType", "ObjectEvent"),
            QueryParameter.Create("maxEventCount", "60000"),
            QueryParameter.Create("perPage", "60000")
        };
        var handler = GetCurrentHandler();
        var events = await handler.QueryEventsAsync(parameters, CancellationToken.None);
        var response = TestDataGenerator.CreateQueryResponse(events);
        return FormatResponse(response);
    }

    [Benchmark]
    public async Task<string> EndToEndWithPagination()
    {
        var parameters = new[]
        {
            QueryParameter.Create("eventType", "ObjectEvent"),
            QueryParameter.Create("perPage", "100")
        };
        var handler = GetCurrentHandler();
        var events = await handler.QueryEventsAsync(parameters, CancellationToken.None);
        var response = TestDataGenerator.CreateQueryResponse(events);
        return FormatResponse(response);
    }
}
