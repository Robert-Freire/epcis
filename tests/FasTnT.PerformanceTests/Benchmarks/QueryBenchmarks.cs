using BenchmarkDotNet.Attributes;
using FasTnT.Application.Database;
using FasTnT.Application.Handlers;
using FasTnT.Domain.Model;
using FasTnT.Domain.Model.Queries;
using FasTnT.PerformanceTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class QueryBenchmarks
{
    [Params(1000)]
    public int DatabaseSize { get; set; }

    private EpcisContext _context1K = null!;
    private EpcisContext _context10K = null!;
    private EpcisContext _context50K = null!;
    private DataRetrieverHandler _handler1K = null!;
    private DataRetrieverHandler _handler10K = null!;
    private DataRetrieverHandler _handler50K = null!;

    private string _testEpc = null!;
    private string _testBizStep = null!;
    private string _testDisposition = null!;
    private string _testReadPoint = null!;
    private string _testBizLocation = null!;
    private DateTime _startDate;
    private DateTime _endDate;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Create and populate 1K database
        _context1K = BenchmarkDbContext.CreateInMemoryContext("QueryBenchmarks_1K");
        TestDataGenerator.PopulateDatabaseWithEvents(_context1K, 1000);

        // Create and populate 10K database
        _context10K = BenchmarkDbContext.CreateInMemoryContext("QueryBenchmarks_10K");
        TestDataGenerator.PopulateDatabaseWithEvents(_context10K, 10000);

        // Create and populate 50K database
        _context50K = BenchmarkDbContext.CreateInMemoryContext("QueryBenchmarks_50K");
        TestDataGenerator.PopulateDatabaseWithEvents(_context50K, 50000);

        // Initialize handlers per context
        var constants = BenchmarkConstants.CreateForQueries(maxEventsReturned: 60000);
        _handler1K = new DataRetrieverHandler(_context1K, new TestCurrentUser(), constants);
        _handler10K = new DataRetrieverHandler(_context10K, new TestCurrentUser(), constants);
        _handler50K = new DataRetrieverHandler(_context50K, new TestCurrentUser(), constants);

        // Pre-generate test values (using values from the generated data)
        _testEpc = TestDataGenerator.GenerateRandomEpc(0, 0);
        _testBizStep = "urn:epcglobal:cbv:bizstep:shipping";
        _testDisposition = "urn:epcglobal:cbv:disp:in_transit";
        _testReadPoint = "urn:epc:id:sgln:0614141.07346.0";
        _testBizLocation = "urn:epc:id:sgln:0614141.07346.0";
        _startDate = DateTime.UtcNow.AddDays(-30);
        _endDate = DateTime.UtcNow;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Clear change tracker for clean state
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

    [Benchmark]
    public async Task<List<Event>> QueryByEpc()
    {
        var parameters = new[] { QueryParameter.Create("MATCH_anyEPC", _testEpc) };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryByEventType()
    {
        var parameters = new[]
        {
            QueryParameter.Create("eventType", "ObjectEvent"),
            QueryParameter.Create("maxEventCount", "60000")
        };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryByBizStep()
    {
        var parameters = new[] { QueryParameter.Create("EQ_bizStep", _testBizStep) };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryByDisposition()
    {
        var parameters = new[] { QueryParameter.Create("EQ_disposition", _testDisposition) };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryByReadPoint()
    {
        var parameters = new[] { QueryParameter.Create("EQ_readPoint", _testReadPoint) };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryByBizLocation()
    {
        var parameters = new[] { QueryParameter.Create("EQ_bizLocation", _testBizLocation) };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryByDateRange()
    {
        var parameters = new[]
        {
            QueryParameter.Create("GE_eventTime", _startDate.ToString("o")),
            QueryParameter.Create("LT_eventTime", _endDate.ToString("o")),
            QueryParameter.Create("maxEventCount", "60000"),
            QueryParameter.Create("perPage", "60000")
        };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryComplex()
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
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Event>> QueryWithPagination()
    {
        var parameters = new[]
        {
            QueryParameter.Create("eventType", "ObjectEvent"),
            QueryParameter.Create("perPage", "100")
        };
        var handler = GetCurrentHandler();
        return await handler.QueryEventsAsync(parameters, CancellationToken.None);
    }
}
