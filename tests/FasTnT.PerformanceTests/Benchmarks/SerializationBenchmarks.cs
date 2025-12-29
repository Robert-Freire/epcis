using BenchmarkDotNet.Attributes;
using FasTnT.Host.Communication.Xml.Formatters;
using FasTnT.Host.Communication.Json.Formatters;
using FasTnT.Domain.Model;
using FasTnT.Domain.Model.Queries;
using FasTnT.Host.Endpoints.Interfaces;
using FasTnT.PerformanceTests.Helpers;

namespace FasTnT.PerformanceTests.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class SerializationBenchmarks
{
    [Params(100, 500)]
    public int ResultSize { get; set; }

    private Dictionary<int, QueryResponse> _standardResponses = null!;
    private Dictionary<int, QueryResponse> _customFieldResponses = null!;
    private Dictionary<int, QueryResponse> _sensorDataResponses = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {

        // Pre-generate standard event responses
        _standardResponses = new Dictionary<int, QueryResponse>
        {
            { 100, TestDataGenerator.CreateQueryResponse(TestDataGenerator.GenerateMixedEvents(100)) },
            { 500, TestDataGenerator.CreateQueryResponse(TestDataGenerator.GenerateMixedEvents(500)) }
        };

        // Pre-generate events with custom fields
        _customFieldResponses = new Dictionary<int, QueryResponse>
        {
            { 100, TestDataGenerator.CreateQueryResponse(TestDataGenerator.GenerateEventsWithCustomFields(100)) },
            { 500, TestDataGenerator.CreateQueryResponse(TestDataGenerator.GenerateEventsWithCustomFields(500)) }
        };

        // Pre-generate events with sensor data
        _sensorDataResponses = new Dictionary<int, QueryResponse>
        {
            { 100, TestDataGenerator.CreateQueryResponse(TestDataGenerator.GenerateEventsWithSensorData(100)) },
            { 500, TestDataGenerator.CreateQueryResponse(TestDataGenerator.GenerateEventsWithSensorData(500)) }
        };
    }

    [Benchmark]
    public string SerializeToXml()
    {
        var response = _standardResponses[ResultSize];
        var result = new QueryResult(response);
        return XmlResponseFormatter.Format(result);
    }

    [Benchmark]
    public string SerializeToJson()
    {
        var response = _standardResponses[ResultSize];
        var result = new QueryResult(response);
        return JsonResponseFormatter.Format(result);
    }

    [Benchmark]
    public string SerializeXmlWithCustomFields()
    {
        var response = _customFieldResponses[ResultSize];
        var result = new QueryResult(response);
        return XmlResponseFormatter.Format(result);
    }

    [Benchmark]
    public string SerializeJsonWithCustomFields()
    {
        var response = _customFieldResponses[ResultSize];
        var result = new QueryResult(response);
        return JsonResponseFormatter.Format(result);
    }

    [Benchmark]
    public string SerializeXmlWithSensorData()
    {
        var response = _sensorDataResponses[ResultSize];
        var result = new QueryResult(response);
        return XmlResponseFormatter.Format(result);
    }

    [Benchmark]
    public string SerializeJsonWithSensorData()
    {
        var response = _sensorDataResponses[ResultSize];
        var result = new QueryResult(response);
        return JsonResponseFormatter.Format(result);
    }
}
