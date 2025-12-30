using BenchmarkDotNet.Attributes;
using FasTnT.Application.Handlers;
using FasTnT.Domain.Model;
using FasTnT.Host.Communication.Json.Parsers;
using FasTnT.Host.Communication.Xml.Parsers;
using FasTnT.PerformanceTests.Config;
using FasTnT.PerformanceTests.Helpers;
using System.Text;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class CaptureBenchmarks
{
    private EpcisContext _context = null!;
    private CaptureHandler _captureHandler = null!;
    private Dictionary<int, Request> _templateRequests = new();
    private Dictionary<int, string> _xmlDocuments = new();
    private Dictionary<int, string> _jsonDocuments = new();
    private Dictionary<int, Request> _aggregationTemplateRequests = new();
    private Dictionary<int, Request> _preParsedRequests = new();
    private Dictionary<int, Request> _aggregationRequests = new();
    private Namespaces _namespaces = null!;

    [Params(100, 500)]
    public int EventCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = BenchmarkDbContext.CreateInMemoryContext("CaptureBenchmarks");
        _captureHandler = new CaptureHandler(
            _context,
            new TestCurrentUser(),
            new NoOpEventNotifier(),
            BenchmarkConstants.Create()
        );

        _namespaces = new Namespaces(new Dictionary<string, string>());

        // Pre-generate template requests for all event counts
        var eventCounts = new[] { 100, 500 };

        foreach (var events in eventCounts)
        {
            // Generate mixed events (70% Object, 20% Aggregation, 10% Transformation)
            var request = new Request
            {
                SchemaVersion = "2.0",
                DocumentTime = DateTime.UtcNow,
                Events = new List<Event>()
            };

            for (int i = 0; i < events; i++)
            {
                Event evt;
                var ratio = (double)i / events;

                if (ratio < 0.7)
                {
                    evt = TestDataGenerator.GenerateObjectEvent(50, i);
                }
                else if (ratio < 0.9)
                {
                    evt = TestDataGenerator.GenerateAggregationEvent(50, i);
                }
                else
                {
                    evt = TestDataGenerator.GenerateTransformationEvent(25, 25, i);
                }

                request.Events.Add(evt);
            }

            _templateRequests[events] = request;

            // Generate XML and JSON documents
            _xmlDocuments[events] = TestDataGenerator.GenerateXmlRequest(events, 50);
            _jsonDocuments[events] = TestDataGenerator.GenerateJsonRequest(events, 50);

            // Generate aggregation-heavy requests
            var aggRequest = new Request
            {
                SchemaVersion = "2.0",
                DocumentTime = DateTime.UtcNow,
                Events = new List<Event>()
            };

            for (int i = 0; i < events; i++)
            {
                aggRequest.Events.Add(TestDataGenerator.GenerateAggregationEvent(50, i));
            }

            _aggregationTemplateRequests[events] = aggRequest;
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        BenchmarkDbContext.ClearDataAsync(_context).GetAwaiter().GetResult();
        _context.ChangeTracker.Clear();

        // Deep copy and reset requests to ensure EventId hashing and inserts execute every run
        _preParsedRequests[EventCount] = TestDataGenerator.DeepCopyAndResetRequest(_templateRequests[EventCount]);
        _aggregationRequests[EventCount] = TestDataGenerator.DeepCopyAndResetRequest(_aggregationTemplateRequests[EventCount]);
    }

    [Benchmark]
    public async Task<Request> CaptureXmlEndToEnd()
    {
        var xml = _xmlDocuments[EventCount];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var request = await XmlCaptureRequestParser.ParseAsync(stream, CancellationToken.None);

        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> CaptureJsonEndToEnd()
    {
        var json = _jsonDocuments[EventCount];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var request = await JsonCaptureRequestParser.ParseDocumentAsync(stream, _namespaces, CancellationToken.None);

        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark(Baseline = true)]
    public async Task<Request> CapturePreParsedRequest()
    {
        var request = _preParsedRequests[EventCount];
        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> CaptureWithAggregations()
    {
        var request = _aggregationRequests[EventCount];
        return await _captureHandler.StoreAsync(request, CancellationToken.None);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        BenchmarkDbContext.Cleanup(_context);
    }
}
