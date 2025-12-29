using BenchmarkDotNet.Attributes;
using FasTnT.Domain.Model;
using FasTnT.Host.Communication.Xml.Parsers;
using FasTnT.PerformanceTests.Config;
using FasTnT.PerformanceTests.Helpers;
using System.Text;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class XmlParsingBenchmarks
{
    private Dictionary<(int events, int epcs), string> _xmlData = new();
    private Dictionary<(int events, int epcs), string> _xmlMixedData = new();

    [Params(100, 500)]
    public int EventCount { get; set; }

    [Params(10, 50, 100)]
    public int EpcsPerEvent { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Generate XML strings for all parameter combinations
        var eventCounts = new[] { 100, 500 };
        var epcCounts = new[] { 10, 50, 100 };

        foreach (var events in eventCounts)
        {
            foreach (var epcs in epcCounts)
            {
                var xml = TestDataGenerator.GenerateXmlRequest(events, epcs);
                _xmlData[(events, epcs)] = xml;

                var xmlMixed = TestDataGenerator.GenerateXmlMixedRequest(events, epcs);
                _xmlMixedData[(events, epcs)] = xmlMixed;
            }
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<Request> ParseXmlDocument()
    {
        var xml = _xmlData[(EventCount, EpcsPerEvent)];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        return await XmlCaptureRequestParser.ParseAsync(stream, CancellationToken.None);
    }

    [Benchmark]
    public async Task<Request> ParseXmlMixedDocument()
    {
        var xml = _xmlMixedData[(EventCount, EpcsPerEvent)];
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        return await XmlCaptureRequestParser.ParseAsync(stream, CancellationToken.None);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _xmlData.Clear();
        _xmlMixedData.Clear();
    }
}
