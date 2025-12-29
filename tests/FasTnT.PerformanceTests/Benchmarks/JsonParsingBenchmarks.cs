using BenchmarkDotNet.Attributes;
using FasTnT.Domain.Model;
using FasTnT.Host.Communication.Json.Parsers;
using FasTnT.PerformanceTests.Config;
using FasTnT.PerformanceTests.Helpers;
using System.Text;
using System.Text.Json;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class JsonParsingBenchmarks
{
    private Dictionary<(int events, int epcs), string> _jsonData = new();
    private Dictionary<(int events, int epcs), string> _jsonMixedData = new();
    private Namespaces _namespaces = null!;

    [Params(100, 500, 1000, 5000, 10000)]
    public int EventCount { get; set; }

    [Params(10, 50, 100)]
    public int EpcsPerEvent { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _namespaces = new Namespaces(new Dictionary<string, string>());

        // Generate JSON strings for all parameter combinations
        var eventCounts = new[] { 100, 500, 1000, 5000, 10000 };
        var epcCounts = new[] { 10, 50, 100 };

        foreach (var events in eventCounts)
        {
            foreach (var epcs in epcCounts)
            {
                var json = TestDataGenerator.GenerateJsonRequest(events, epcs);
                _jsonData[(events, epcs)] = json;

                var jsonMixed = TestDataGenerator.GenerateJsonMixedRequest(events, epcs);
                _jsonMixedData[(events, epcs)] = jsonMixed;
            }
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<Request> ParseJsonDocument()
    {
        var json = _jsonData[(EventCount, EpcsPerEvent)];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return await JsonCaptureRequestParser.ParseDocumentAsync(stream, _namespaces, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> ParseJsonMixedDocument()
    {
        var json = _jsonMixedData[(EventCount, EpcsPerEvent)];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        return await JsonCaptureRequestParser.ParseDocumentAsync(stream, _namespaces, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> ParseJsonEvent()
    {
        var json = _jsonData[(EventCount, EpcsPerEvent)];
        using var document = JsonDocument.Parse(json);
        var eventList = document.RootElement.GetProperty("epcisBody").GetProperty("eventList");
        var firstEvent = eventList.EnumerateArray().First();

        // Serialize the first event back to JSON and create a stream
        var eventJson = JsonSerializer.Serialize(firstEvent);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(eventJson));

        return await JsonCaptureRequestParser.ParseEventAsync(stream, _namespaces, CancellationToken.None);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _jsonData.Clear();
        _jsonMixedData.Clear();
    }
}
