using BenchmarkDotNet.Attributes;
using FasTnT.Application.Services.Events;
using FasTnT.Application.Validators;
using FasTnT.Domain.Enumerations;
using FasTnT.Domain.Model;
using FasTnT.Domain.Model.Events;
using FasTnT.PerformanceTests.Config;
using FasTnT.PerformanceTests.Helpers;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class ComponentBenchmarks
{
    private EpcisContext _context = null!;
    private Dictionary<int, Request> _validRequests = new();
    private Dictionary<EventType, Event> _eventsByType = new();

    [Params(100, 500)]
    public int EventCount { get; set; }

    [Params(EventType.ObjectEvent, EventType.AggregationEvent, EventType.TransformationEvent)]
    public EventType EventTypeParam { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = BenchmarkDbContext.CreateInMemoryContext("ComponentBenchmarks");

        // Pre-generate valid requests
        var eventCounts = new[] { 100, 500 };

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
                request.Events.Add(TestDataGenerator.GenerateObjectEvent(50, i));
            }

            _validRequests[events] = request;
        }

        // Pre-generate events by type
        _eventsByType[EventType.ObjectEvent] = TestDataGenerator.GenerateObjectEvent(50);
        _eventsByType[EventType.AggregationEvent] = TestDataGenerator.GenerateAggregationEvent(50);
        _eventsByType[EventType.TransformationEvent] = TestDataGenerator.GenerateTransformationEvent(25, 25);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        BenchmarkDbContext.ClearDataAsync(_context).GetAwaiter().GetResult();
        _context.ChangeTracker.Clear();
    }

    [Benchmark(Baseline = true)]
    public bool ValidateRequest()
    {
        var request = _validRequests[EventCount];
        return RequestValidator.IsValid(request);
    }

    [Benchmark]
    public bool ValidateEvents()
    {
        var evt = _eventsByType[EventTypeParam];
        return EventValidator.IsValid(evt);
    }

    [Benchmark]
    public List<string> ComputeEventHashes()
    {
        var request = _validRequests[EventCount];
        var hashes = new List<string>();

        foreach (var evt in request.Events)
        {
            hashes.Add(EventHash.Compute(evt));
        }

        return hashes;
    }

    [Benchmark]
    public async Task DatabaseInsertOnly()
    {
        var request = TestDataGenerator.DeepCopyAndResetRequest(_validRequests[EventCount]);

        _context.Add(request);
        await _context.SaveChangesAsync();

        _context.Entry(request).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        foreach (var evt in request.Events)
        {
            _context.Entry(evt).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }

    [Benchmark]
    public async Task DatabaseInsertWithTransaction()
    {
        var request = TestDataGenerator.DeepCopyAndResetRequest(_validRequests[EventCount]);

        using var transaction = await _context.Database.BeginTransactionAsync();

        _context.Add(request);
        await _context.SaveChangesAsync();
        request.RecordTime = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await transaction.CommitAsync();

        _context.Entry(request).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        foreach (var evt in request.Events)
        {
            _context.Entry(evt).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }

    [Benchmark]
    public async Task DatabaseBulkInsert()
    {
        const int requestCount = 10;
        int eventsPerRequest = EventCount / requestCount;

        for (int i = 0; i < requestCount; i++)
        {
            var request = new Request
            {
                SchemaVersion = "2.0",
                DocumentTime = DateTime.UtcNow,
                Events = new List<Event>()
            };

            for (int j = 0; j < eventsPerRequest; j++)
            {
                request.Events.Add(TestDataGenerator.GenerateObjectEvent(50, j));
            }

            _context.Add(request);
        }

        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        BenchmarkDbContext.Cleanup(_context);
    }
}
