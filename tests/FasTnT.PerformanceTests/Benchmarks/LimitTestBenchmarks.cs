using BenchmarkDotNet.Attributes;
using FasTnT.Application.Handlers;
using FasTnT.Domain.Exceptions;
using FasTnT.Domain.Model;
using FasTnT.PerformanceTests.Config;
using FasTnT.PerformanceTests.Helpers;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class LimitTestBenchmarks
{
    private EpcisContext _context = null!;
    private CaptureHandler _limitedCaptureHandler = null!;
    private Dictionary<int, Request> _templateRequests = new();
    private Dictionary<int, Request> _requests = new();

    [Params(500, 501, 1000, 5000)]
    public int EventCountForLimitTest { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = BenchmarkDbContext.CreateInMemoryContext("LimitTestBenchmarks");

        // Handler with production limit
        _limitedCaptureHandler = new CaptureHandler(
            _context,
            new TestCurrentUser(),
            new NoOpEventNotifier(),
            BenchmarkConstants.Create(maxEventsPerCall: 500)
        );

        // Pre-generate template requests for limit testing
        var eventCounts = new[] { 500, 501, 1000, 5000 };

        foreach (var events in eventCounts)
        {
            var request = new Request
            {
                SchemaVersion = "2.0",
                DocumentTime = DateTime.UtcNow,
                Events = new List<Event>()
            };

            for (int i = 0; i < events; i++)
            {
                request.Events.Add(TestDataGenerator.GenerateObjectEvent(75, i));
            }

            _templateRequests[events] = request;
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        BenchmarkDbContext.ClearDataAsync(_context).GetAwaiter().GetResult();
        _context.ChangeTracker.Clear();

        // Deep copy and reset requests to ensure EventId hashing and inserts execute every run
        _requests[500] = TestDataGenerator.DeepCopyAndResetRequest(_templateRequests[500]);
        _requests[EventCountForLimitTest] = TestDataGenerator.DeepCopyAndResetRequest(_templateRequests[EventCountForLimitTest]);
    }

    [Benchmark(Baseline = true)]
    public async Task<Request> CaptureAtMaxLimit()
    {
        var request = _requests[499];
        return await _limitedCaptureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task CaptureExceedingLimit()
    {
        var request = _requests[EventCountForLimitTest];
        var exceptionThrown = false;

        try
        {
            await _limitedCaptureHandler.StoreAsync(request, CancellationToken.None);
        }
        catch (EpcisException ex) when (ex.ExceptionType == ExceptionType.CaptureLimitExceededException)
        {
            exceptionThrown = true;
        }

        // Assert that exception was thrown for counts > 500
        if (EventCountForLimitTest > 500 && !exceptionThrown)
        {
            throw new InvalidOperationException($"Expected CaptureLimitExceededException for {EventCountForLimitTest} events (limit: 500), but no exception was thrown.");
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        BenchmarkDbContext.Cleanup(_context);
    }
}
