using BenchmarkDotNet.Attributes;
using FasTnT.Application.Handlers;
using FasTnT.Domain.Exceptions;
using FasTnT.Domain.Model;
using FasTnT.Host.Communication.Json.Parsers;
using FasTnT.Host.Communication.Xml.Parsers;
using FasTnT.PerformanceTests.Config;
using FasTnT.PerformanceTests.Helpers;
using System.Text;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class StressTestBenchmarks
{
    private EpcisContext _context = null!;
    private CaptureHandler _captureHandler = null!;
    private CaptureHandler _limitedCaptureHandler = null!;
    private Dictionary<int, string> _largeXmlDocuments = new();
    private Dictionary<int, string> _largeJsonDocuments = new();
    private Request _mixedWorkloadTemplateRequest = null!;
    private Request _mixedWorkloadRequest = null!;
    private Dictionary<int, Request> _limitTestTemplateRequests = new();
    private Dictionary<int, Request> _limitTestRequests = new();
    private Random _random = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = BenchmarkDbContext.CreateInMemoryContext("StressTestBenchmarks");
        _random = new Random(42);

        // Handler with high limit for stress testing
        _captureHandler = new CaptureHandler(
            _context,
            new TestCurrentUser(),
            new NoOpEventNotifier(),
            BenchmarkConstants.Create(maxEventsPerCall: 10000)
        );

        // Handler with production limit for limit testing
        _limitedCaptureHandler = new CaptureHandler(
            _context,
            new TestCurrentUser(),
            new NoOpEventNotifier(),
            BenchmarkConstants.Create(maxEventsPerCall: 500)
        );

        // Generate large XML documents
        _largeXmlDocuments[5000] = TestDataGenerator.GenerateXmlRequest(5000, 100);
        _largeXmlDocuments[10000] = TestDataGenerator.GenerateXmlRequest(10000, 100);

        // Generate large JSON documents
        _largeJsonDocuments[5000] = TestDataGenerator.GenerateJsonRequest(5000, 100);
        _largeJsonDocuments[10000] = TestDataGenerator.GenerateJsonRequest(10000, 100);

        // Generate realistic mixed workload template (70% Object, 20% Aggregation, 10% Transformation)
        _mixedWorkloadTemplateRequest = new Request
        {
            SchemaVersion = "2.0",
            DocumentTime = DateTime.UtcNow,
            Events = new List<Event>()
        };

        for (int i = 0; i < 1000; i++)
        {
            Event evt;
            var ratio = (double)i / 1000;
            var epcCount = _random.Next(10, 101); // 10-100 EPCs per event

            if (ratio < 0.7)
            {
                evt = TestDataGenerator.GenerateObjectEvent(epcCount, i);
            }
            else if (ratio < 0.9)
            {
                evt = TestDataGenerator.GenerateAggregationEvent(epcCount, i);
            }
            else
            {
                evt = TestDataGenerator.GenerateTransformationEvent(epcCount / 2, epcCount / 2, i);
            }

            _mixedWorkloadTemplateRequest.Events.Add(evt);
        }

        // Pre-generate template requests for limit testing
        var limitTestEventCounts = new[] { 500, 501, 1000 };

        foreach (var events in limitTestEventCounts)
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

            _limitTestTemplateRequests[events] = request;
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        BenchmarkDbContext.ClearDataAsync(_context).GetAwaiter().GetResult();
        _context.ChangeTracker.Clear();

        // Deep copy and reset requests to ensure EventId hashing and inserts execute every run
        _mixedWorkloadRequest = TestDataGenerator.DeepCopyAndResetRequest(_mixedWorkloadTemplateRequest);

        // Deep copy and reset limit test requests
        foreach (var eventCount in new[] { 500, 501, 1000 })
        {
            _limitTestRequests[eventCount] = TestDataGenerator.DeepCopyAndResetRequest(_limitTestTemplateRequests[eventCount]);
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<Request> CaptureLargeXmlDocument()
    {
        var xml = _largeXmlDocuments[5000];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var request = await XmlCaptureRequestParser.ParseAsync(stream, CancellationToken.None);
        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> CaptureLargeJsonDocument()
    {
        var json = _largeJsonDocuments[5000];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var namespaces = new Namespaces(new Dictionary<string, string>());
        var request = await JsonCaptureRequestParser.ParseDocumentAsync(stream, namespaces, CancellationToken.None);
        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> CaptureVeryLargeXmlDocument()
    {
        var xml = _largeXmlDocuments[10000];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var request = await XmlCaptureRequestParser.ParseAsync(stream, CancellationToken.None);
        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> CaptureVeryLargeJsonDocument()
    {
        var json = _largeJsonDocuments[10000];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var namespaces = new Namespaces(new Dictionary<string, string>());
        var request = await JsonCaptureRequestParser.ParseDocumentAsync(stream, namespaces, CancellationToken.None);
        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> CaptureRealisticMixedWorkload()
    {
        return await _captureHandler.StoreAsync(_mixedWorkloadRequest, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> CaptureAtConfiguredLimit()
    {
        var request = _limitTestRequests[500];
        return await _limitedCaptureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task CaptureExceedingLimitBy1()
    {
        var request = _limitTestRequests[501];
        var exceptionThrown = false;

        try
        {
            await _limitedCaptureHandler.StoreAsync(request, CancellationToken.None);
        }
        catch (EpcisException ex) when (ex.ExceptionType == ExceptionType.CaptureLimitExceededException)
        {
            exceptionThrown = true;
        }

        if (!exceptionThrown)
        {
            throw new InvalidOperationException($"Expected CaptureLimitExceededException for 501 events (limit: 500), but no exception was thrown.");
        }
    }

    [Benchmark]
    public async Task CaptureExceedingLimitBy500()
    {
        var request = _limitTestRequests[1000];
        var exceptionThrown = false;

        try
        {
            await _limitedCaptureHandler.StoreAsync(request, CancellationToken.None);
        }
        catch (EpcisException ex) when (ex.ExceptionType == ExceptionType.CaptureLimitExceededException)
        {
            exceptionThrown = true;
        }

        if (!exceptionThrown)
        {
            throw new InvalidOperationException($"Expected CaptureLimitExceededException for 1000 events (limit: 500), but no exception was thrown.");
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        BenchmarkDbContext.Cleanup(_context);
    }
}
