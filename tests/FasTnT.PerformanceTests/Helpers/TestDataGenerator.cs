using System.Text;
using System.Text.Json;
using FasTnT.Application.Database;
using FasTnT.Domain.Enumerations;
using FasTnT.Domain.Model;
using FasTnT.Domain.Model.Events;
using FasTnT.Domain.Model.Masterdata;
using FasTnT.Domain.Model.Queries;
using FasTnT.Domain.Model.Subscriptions;

namespace FasTnT.PerformanceTests.Helpers;

public static class TestDataGenerator
{
    private static readonly Random Random = new(42); // Fixed seed for reproducibility

    public enum DataSize
    {
        Small,    // 100 events, 10 EPCs each
        Medium,   // 500 events, 20 EPCs each
        Large,    // 1000 events, 50 EPCs each
        XLarge    // 5000 events, 100 EPCs each
    }

    public static (int eventCount, int epcsPerEvent) GetPresetConfiguration(DataSize size)
    {
        return size switch
        {
            DataSize.Small => (100, 10),
            DataSize.Medium => (500, 20),
            DataSize.Large => (1000, 50),
            DataSize.XLarge => (5000, 100),
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };
    }

    /// <summary>
    /// Generates an EPCIS 2.0 XML capture request with specified number of events.
    /// </summary>
    public static string GenerateXmlRequest(int eventCount, int epcsPerEvent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<epcis:EPCISDocument xmlns:epcis=\"urn:epcglobal:epcis:xsd:2\" schemaVersion=\"2.0\" creationDate=\"2025-01-15T10:30:00Z\">");
        sb.AppendLine("  <EPCISHeader/>");
        sb.AppendLine("  <EPCISBody>");
        sb.AppendLine("    <EventList>");

        for (int i = 0; i < eventCount; i++)
        {
            sb.AppendLine(GenerateXmlObjectEvent(epcsPerEvent, i));
        }

        sb.AppendLine("    </EventList>");
        sb.AppendLine("  </EPCISBody>");
        sb.AppendLine("</epcis:EPCISDocument>");

        return sb.ToString();
    }

    /// <summary>
    /// Generates an EPCIS 2.0 JSON capture request with specified number of events.
    /// </summary>
    public static string GenerateJsonRequest(int eventCount, int epcsPerEvent)
    {
        var events = new List<object>();

        for (int i = 0; i < eventCount; i++)
        {
            events.Add(GenerateJsonObjectEvent(epcsPerEvent, i));
        }

        var document = new
        {
            type = "EPCISDocument",
            schemaVersion = "2.0",
            creationDate = "2025-01-15T10:30:00Z",
            epcisHeader = new { },
            epcisBody = new
            {
                eventList = events
            }
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    /// <summary>
    /// Generates an EPCIS 2.0 XML capture request with mixed event types (ObjectEvent, AggregationEvent, TransformationEvent).
    /// Event distribution: 70% ObjectEvents, 20% AggregationEvents, 10% TransformationEvents.
    /// </summary>
    public static string GenerateXmlMixedRequest(int eventCount, int epcsPerEvent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<epcis:EPCISDocument xmlns:epcis=\"urn:epcglobal:epcis:xsd:2\" schemaVersion=\"2.0\" creationDate=\"2025-01-15T10:30:00Z\">");
        sb.AppendLine("  <EPCISHeader/>");
        sb.AppendLine("  <EPCISBody>");
        sb.AppendLine("    <EventList>");

        var objectEventCount = (int)(eventCount * 0.7);  // 70% ObjectEvents
        var aggregationEventCount = (int)(eventCount * 0.2);  // 20% AggregationEvents
        var transformationEventCount = eventCount - objectEventCount - aggregationEventCount;  // 10% TransformationEvents

        int currentIndex = 0;

        // Generate ObjectEvents
        for (int i = 0; i < objectEventCount; i++)
        {
            sb.AppendLine(GenerateXmlObjectEvent(epcsPerEvent, currentIndex++));
        }

        // Generate AggregationEvents
        for (int i = 0; i < aggregationEventCount; i++)
        {
            sb.AppendLine(GenerateXmlAggregationEvent(epcsPerEvent, currentIndex++));
        }

        // Generate TransformationEvents
        for (int i = 0; i < transformationEventCount; i++)
        {
            sb.AppendLine(GenerateXmlTransformationEvent(epcsPerEvent / 2, epcsPerEvent / 2, currentIndex++));
        }

        sb.AppendLine("    </EventList>");
        sb.AppendLine("  </EPCISBody>");
        sb.AppendLine("</epcis:EPCISDocument>");

        return sb.ToString();
    }

    /// <summary>
    /// Generates an EPCIS 2.0 JSON capture request with mixed event types (ObjectEvent, AggregationEvent, TransformationEvent).
    /// Event distribution: 70% ObjectEvents, 20% AggregationEvents, 10% TransformationEvents.
    /// </summary>
    public static string GenerateJsonMixedRequest(int eventCount, int epcsPerEvent)
    {
        var events = new List<object>();

        var objectEventCount = (int)(eventCount * 0.7);  // 70% ObjectEvents
        var aggregationEventCount = (int)(eventCount * 0.2);  // 20% AggregationEvents
        var transformationEventCount = eventCount - objectEventCount - aggregationEventCount;  // 10% TransformationEvents

        int currentIndex = 0;

        // Generate ObjectEvents
        for (int i = 0; i < objectEventCount; i++)
        {
            events.Add(GenerateJsonObjectEvent(epcsPerEvent, currentIndex++));
        }

        // Generate AggregationEvents
        for (int i = 0; i < aggregationEventCount; i++)
        {
            events.Add(GenerateJsonAggregationEvent(epcsPerEvent, currentIndex++));
        }

        // Generate TransformationEvents
        for (int i = 0; i < transformationEventCount; i++)
        {
            events.Add(GenerateJsonTransformationEvent(epcsPerEvent / 2, epcsPerEvent / 2, currentIndex++));
        }

        var document = new
        {
            type = "EPCISDocument",
            schemaVersion = "2.0",
            creationDate = "2025-01-15T10:30:00Z",
            epcisHeader = new { },
            epcisBody = new
            {
                eventList = events
            }
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    /// <summary>
    /// Generates a single XML ObjectEvent.
    /// </summary>
    private static string GenerateXmlObjectEvent(int epcCount, int eventIndex)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var sb = new StringBuilder();

        sb.AppendLine("      <ObjectEvent>");
        sb.AppendLine($"        <eventTime>{eventTime:yyyy-MM-ddTHH:mm:ss}Z</eventTime>");
        sb.AppendLine("        <eventTimeZoneOffset>+00:00</eventTimeZoneOffset>");
        sb.AppendLine("        <epcList>");

        for (int i = 0; i < epcCount; i++)
        {
            sb.AppendLine($"          <epc>{GenerateRandomEpc(eventIndex, i)}</epc>");
        }

        sb.AppendLine("        </epcList>");
        sb.AppendLine("        <action>OBSERVE</action>");
        sb.AppendLine("        <bizStep>urn:epcglobal:cbv:bizstep:receiving</bizStep>");
        sb.AppendLine("        <disposition>urn:epcglobal:cbv:disp:in_progress</disposition>");
        sb.AppendLine($"        <readPoint><id>urn:epc:id:sgln:8901213.00001.{eventIndex % 100}</id></readPoint>");
        sb.AppendLine($"        <bizLocation><id>urn:epc:id:sgln:8901213.00002.{eventIndex % 50}</id></bizLocation>");
        sb.AppendLine("      </ObjectEvent>");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a single JSON ObjectEvent.
    /// </summary>
    private static object GenerateJsonObjectEvent(int epcCount, int eventIndex)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var epcs = new List<string>();

        for (int i = 0; i < epcCount; i++)
        {
            epcs.Add(GenerateRandomEpc(eventIndex, i));
        }

        return new
        {
            type = "ObjectEvent",
            eventTime = eventTime.ToString("yyyy-MM-ddTHH:mm:ss") + "Z",
            eventTimeZoneOffset = "+00:00",
            epcList = epcs,
            action = "OBSERVE",
            bizStep = "urn:epcglobal:cbv:bizstep:receiving",
            disposition = "urn:epcglobal:cbv:disp:in_progress",
            readPoint = new { id = $"urn:epc:id:sgln:8901213.00001.{eventIndex % 100}" },
            bizLocation = new { id = $"urn:epc:id:sgln:8901213.00002.{eventIndex % 50}" }
        };
    }

    /// <summary>
    /// Generates a single XML AggregationEvent.
    /// </summary>
    private static string GenerateXmlAggregationEvent(int childEpcCount, int eventIndex)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var parentId = $"urn:epc:id:sscc:8901213.0000{eventIndex % 1000:D6}";
        var sb = new StringBuilder();

        sb.AppendLine("      <AggregationEvent>");
        sb.AppendLine($"        <eventTime>{eventTime:yyyy-MM-ddTHH:mm:ss}Z</eventTime>");
        sb.AppendLine("        <eventTimeZoneOffset>+00:00</eventTimeZoneOffset>");
        sb.AppendLine($"        <parentID>{parentId}</parentID>");
        sb.AppendLine("        <childEPCs>");

        for (int i = 0; i < childEpcCount; i++)
        {
            sb.AppendLine($"          <epc>{GenerateRandomEpc(eventIndex, i)}</epc>");
        }

        sb.AppendLine("        </childEPCs>");
        sb.AppendLine("        <action>ADD</action>");
        sb.AppendLine("        <bizStep>urn:epcglobal:cbv:bizstep:packing</bizStep>");
        sb.AppendLine("        <disposition>urn:epcglobal:cbv:disp:in_progress</disposition>");
        sb.AppendLine($"        <readPoint><id>urn:epc:id:sgln:8901213.00001.{eventIndex % 100}</id></readPoint>");
        sb.AppendLine($"        <bizLocation><id>urn:epc:id:sgln:8901213.00002.{eventIndex % 50}</id></bizLocation>");
        sb.AppendLine("      </AggregationEvent>");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a single JSON AggregationEvent.
    /// </summary>
    private static object GenerateJsonAggregationEvent(int childEpcCount, int eventIndex)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var parentId = $"urn:epc:id:sscc:8901213.0000{eventIndex % 1000:D6}";
        var childEpcs = new List<string>();

        for (int i = 0; i < childEpcCount; i++)
        {
            childEpcs.Add(GenerateRandomEpc(eventIndex, i));
        }

        return new
        {
            type = "AggregationEvent",
            eventTime = eventTime.ToString("yyyy-MM-ddTHH:mm:ss") + "Z",
            eventTimeZoneOffset = "+00:00",
            parentID = parentId,
            childEPCs = childEpcs,
            action = "ADD",
            bizStep = "urn:epcglobal:cbv:bizstep:packing",
            disposition = "urn:epcglobal:cbv:disp:in_progress",
            readPoint = new { id = $"urn:epc:id:sgln:8901213.00001.{eventIndex % 100}" },
            bizLocation = new { id = $"urn:epc:id:sgln:8901213.00002.{eventIndex % 50}" }
        };
    }

    /// <summary>
    /// Generates a single XML TransformationEvent.
    /// </summary>
    private static string GenerateXmlTransformationEvent(int inputEpcCount, int outputEpcCount, int eventIndex)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var sb = new StringBuilder();

        sb.AppendLine("      <TransformationEvent>");
        sb.AppendLine($"        <eventTime>{eventTime:yyyy-MM-ddTHH:mm:ss}Z</eventTime>");
        sb.AppendLine("        <eventTimeZoneOffset>+00:00</eventTimeZoneOffset>");
        sb.AppendLine("        <inputEPCList>");

        for (int i = 0; i < inputEpcCount; i++)
        {
            sb.AppendLine($"          <epc>{GenerateRandomEpc(eventIndex, i)}</epc>");
        }

        sb.AppendLine("        </inputEPCList>");
        sb.AppendLine("        <outputEPCList>");

        for (int i = 0; i < outputEpcCount; i++)
        {
            sb.AppendLine($"          <epc>{GenerateRandomEpc(eventIndex, inputEpcCount + i)}</epc>");
        }

        sb.AppendLine("        </outputEPCList>");
        sb.AppendLine("        <bizStep>urn:epcglobal:cbv:bizstep:commissioning</bizStep>");
        sb.AppendLine("        <disposition>urn:epcglobal:cbv:disp:in_progress</disposition>");
        sb.AppendLine($"        <readPoint><id>urn:epc:id:sgln:8901213.00001.{eventIndex % 100}</id></readPoint>");
        sb.AppendLine($"        <bizLocation><id>urn:epc:id:sgln:8901213.00002.{eventIndex % 50}</id></bizLocation>");
        sb.AppendLine("      </TransformationEvent>");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a single JSON TransformationEvent.
    /// </summary>
    private static object GenerateJsonTransformationEvent(int inputEpcCount, int outputEpcCount, int eventIndex)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var inputEpcs = new List<string>();
        var outputEpcs = new List<string>();

        for (int i = 0; i < inputEpcCount; i++)
        {
            inputEpcs.Add(GenerateRandomEpc(eventIndex, i));
        }

        for (int i = 0; i < outputEpcCount; i++)
        {
            outputEpcs.Add(GenerateRandomEpc(eventIndex, inputEpcCount + i));
        }

        return new
        {
            type = "TransformationEvent",
            eventTime = eventTime.ToString("yyyy-MM-ddTHH:mm:ss") + "Z",
            eventTimeZoneOffset = "+00:00",
            inputEPCList = inputEpcs,
            outputEPCList = outputEpcs,
            bizStep = "urn:epcglobal:cbv:bizstep:commissioning",
            disposition = "urn:epcglobal:cbv:disp:in_progress",
            readPoint = new { id = $"urn:epc:id:sgln:8901213.00001.{eventIndex % 100}" },
            bizLocation = new { id = $"urn:epc:id:sgln:8901213.00002.{eventIndex % 50}" }
        };
    }

    /// <summary>
    /// Generates a valid SGTIN EPC URN.
    /// </summary>
    public static string GenerateRandomEpc(int eventIndex = 0, int epcIndex = 0)
    {
        var companyPrefix = "8901213";
        var itemReference = $"{105919 + (eventIndex % 1000):D6}";
        var serialNumber = $"{epcIndex:D6}";

        return $"urn:epc:id:sgtin:{companyPrefix}.{itemReference}.{serialNumber}";
    }

    /// <summary>
    /// Generates an ObjectEvent domain model.
    /// </summary>
    public static Event GenerateObjectEvent(int epcCount, int eventIndex = 0)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var epcs = new List<Epc>();

        for (int i = 0; i < epcCount; i++)
        {
            epcs.Add(new Epc
            {
                Type = EpcType.List,
                Id = GenerateRandomEpc(eventIndex, i)
            });
        }

        return new Event
        {
            Type = EventType.ObjectEvent,
            EventTime = eventTime,
            EventTimeZoneOffset = "+00:00",
            Action = EventAction.Observe,
            BusinessStep = "urn:epcglobal:cbv:bizstep:receiving",
            Disposition = "urn:epcglobal:cbv:disp:in_progress",
            ReadPoint = $"urn:epc:id:sgln:8901213.00001.{eventIndex % 100}",
            BusinessLocation = $"urn:epc:id:sgln:8901213.00002.{eventIndex % 50}",
            Epcs = epcs
        };
    }

    /// <summary>
    /// Generates an AggregationEvent domain model.
    /// </summary>
    public static Event GenerateAggregationEvent(int childEpcCount, int eventIndex = 0)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var childEpcs = new List<Epc>();

        for (int i = 0; i < childEpcCount; i++)
        {
            childEpcs.Add(new Epc
            {
                Type = EpcType.ChildEpc,
                Id = GenerateRandomEpc(eventIndex, i)
            });
        }

        var parentId = $"urn:epc:id:sscc:8901213.0000{eventIndex % 1000:D6}";

        childEpcs.Add(new Epc
        {
            Type = EpcType.ParentId,
            Id = parentId
        });

        return new Event
        {
            Type = EventType.AggregationEvent,
            EventTime = eventTime,
            EventTimeZoneOffset = "+00:00",
            Action = EventAction.Add,
            BusinessStep = "urn:epcglobal:cbv:bizstep:packing",
            Disposition = "urn:epcglobal:cbv:disp:in_progress",
            ReadPoint = $"urn:epc:id:sgln:8901213.00001.{eventIndex % 100}",
            BusinessLocation = $"urn:epc:id:sgln:8901213.00002.{eventIndex % 50}",
            Epcs = childEpcs
        };
    }

    /// <summary>
    /// Generates a TransformationEvent domain model.
    /// </summary>
    public static Event GenerateTransformationEvent(int inputEpcCount, int outputEpcCount, int eventIndex = 0)
    {
        var eventTime = DateTime.UtcNow.AddMinutes(-eventIndex);
        var inputEpcs = new List<Epc>();
        var outputEpcs = new List<Epc>();

        for (int i = 0; i < inputEpcCount; i++)
        {
            inputEpcs.Add(new Epc
            {
                Type = EpcType.InputEpc,
                Id = GenerateRandomEpc(eventIndex, i)
            });
        }

        for (int i = 0; i < outputEpcCount; i++)
        {
            outputEpcs.Add(new Epc
            {
                Type = EpcType.OutputEpc,
                Id = GenerateRandomEpc(eventIndex, inputEpcCount + i)
            });
        }

        return new Event
        {
            Type = EventType.TransformationEvent,
            EventTime = eventTime,
            EventTimeZoneOffset = "+00:00",
            BusinessStep = "urn:epcglobal:cbv:bizstep:commissioning",
            Disposition = "urn:epcglobal:cbv:disp:in_progress",
            ReadPoint = $"urn:epc:id:sgln:8901213.00001.{eventIndex % 100}",
            BusinessLocation = $"urn:epc:id:sgln:8901213.00002.{eventIndex % 50}",
            Epcs = inputEpcs.Concat(outputEpcs).ToList()
        };
    }

    /// <summary>
    /// Creates a Request object from a list of events.
    /// </summary>
    public static Request CreateRequestFromEvents(List<Event> events)
    {
        return new Request
        {
            DocumentTime = DateTime.UtcNow,
            RecordTime = DateTime.UtcNow,
            SchemaVersion = "2.0",
            Events = events
        };
    }

    /// <summary>
    /// Generates a list of mixed event types.
    /// </summary>
    public static List<Event> GenerateMixedEvents(int totalCount, int epcsPerEvent = 10)
    {
        var events = new List<Event>();
        var objectEventCount = (int)(totalCount * 0.7);  // 70% ObjectEvents
        var aggregationEventCount = (int)(totalCount * 0.2);  // 20% AggregationEvents
        var transformationEventCount = totalCount - objectEventCount - aggregationEventCount;  // 10% TransformationEvents

        for (int i = 0; i < objectEventCount; i++)
        {
            events.Add(GenerateObjectEvent(epcsPerEvent, i));
        }

        for (int i = 0; i < aggregationEventCount; i++)
        {
            events.Add(GenerateAggregationEvent(epcsPerEvent, objectEventCount + i));
        }

        for (int i = 0; i < transformationEventCount; i++)
        {
            events.Add(GenerateTransformationEvent(epcsPerEvent / 2, epcsPerEvent / 2, objectEventCount + aggregationEventCount + i));
        }

        return events;
    }

    /// <summary>
    /// Deep copies a Request and resets its Id, RecordTime, and EventId values to ensure
    /// hashing and inserts execute every run, avoiding benchmark skew from reused instances.
    /// </summary>
    public static Request DeepCopyAndResetRequest(Request source)
    {
        var newRequest = new Request
        {
            Id = 0, // Reset Id for EF Core
            CaptureId = source.CaptureId,
            RecordTime = default, // Reset RecordTime
            UserId = source.UserId,
            StandardBusinessHeader = source.StandardBusinessHeader,
            DocumentTime = source.DocumentTime == default ? DateTime.UtcNow : source.DocumentTime,
            SchemaVersion = string.IsNullOrEmpty(source.SchemaVersion) ? "2.0" : source.SchemaVersion,
            SubscriptionCallback = source.SubscriptionCallback,
            Events = new List<Event>(),
            Masterdata = new List<MasterData>()
        };

        foreach (var evt in source.Events)
        {
            var newEvent = new Event
            {
                Id = 0, // Reset Id for EF Core
                EventTime = evt.EventTime,
                EventTimeZoneOffset = evt.EventTimeZoneOffset,
                Type = evt.Type,
                Action = evt.Action,
                EventId = null, // Reset EventId to force rehashing
                CertificationInfo = evt.CertificationInfo,
                ReadPoint = evt.ReadPoint,
                BusinessLocation = evt.BusinessLocation,
                BusinessStep = evt.BusinessStep,
                Disposition = evt.Disposition,
                TransformationId = evt.TransformationId,
                CorrectiveDeclarationTime = evt.CorrectiveDeclarationTime,
                CorrectiveReason = evt.CorrectiveReason,
                CorrectiveEventIds = evt.CorrectiveEventIds.Select(cei => new CorrectiveEventId
                {
                    CorrectiveId = cei.CorrectiveId
                }).ToList(),
                Epcs = evt.Epcs.Select(epc => new Epc
                {
                    Type = epc.Type,
                    Id = epc.Id,
                    Quantity = epc.Quantity,
                    UnitOfMeasure = epc.UnitOfMeasure
                }).ToList(),
                Transactions = evt.Transactions.Select(t => new BusinessTransaction
                {
                    Type = t.Type,
                    Id = t.Id
                }).ToList(),
                Sources = evt.Sources.Select(s => new Source
                {
                    Type = s.Type,
                    Id = s.Id
                }).ToList(),
                Destinations = evt.Destinations.Select(d => new Destination
                {
                    Type = d.Type,
                    Id = d.Id
                }).ToList(),
                SensorElements = evt.SensorElements.Select(se => new SensorElement
                {
                    Index = se.Index,
                    Time = se.Time,
                    DeviceId = se.DeviceId,
                    DeviceMetadata = se.DeviceMetadata,
                    RawData = se.RawData,
                    StartTime = se.StartTime,
                    EndTime = se.EndTime,
                    DataProcessingMethod = se.DataProcessingMethod,
                    BizRules = se.BizRules
                }).ToList(),
                Reports = evt.Reports.Select(r => new SensorReport
                {
                    Index = r.Index,
                    SensorIndex = r.SensorIndex,
                    Type = r.Type,
                    DeviceId = r.DeviceId,
                    DeviceMetadata = r.DeviceMetadata,
                    RawData = r.RawData,
                    DataProcessingMethod = r.DataProcessingMethod,
                    Time = r.Time,
                    Microorganism = r.Microorganism,
                    ChemicalSubstance = r.ChemicalSubstance,
                    Value = r.Value,
                    Component = r.Component,
                    StringValue = r.StringValue,
                    BooleanValue = r.BooleanValue,
                    HexBinaryValue = r.HexBinaryValue,
                    UriValue = r.UriValue,
                    MinValue = r.MinValue,
                    MaxValue = r.MaxValue,
                    MeanValue = r.MeanValue,
                    SDev = r.SDev,
                    PercRank = r.PercRank,
                    PercValue = r.PercValue,
                    UnitOfMeasure = r.UnitOfMeasure,
                    CoordinateReferenceSystem = r.CoordinateReferenceSystem
                }).ToList(),
                PersistentDispositions = evt.PersistentDispositions.Select(pd => new PersistentDisposition
                {
                    Type = pd.Type,
                    Id = pd.Id
                }).ToList(),
                Fields = evt.Fields.Select(f => new Field
                {
                    Type = f.Type,
                    Name = f.Name,
                    Namespace = f.Namespace,
                    TextValue = f.TextValue,
                    NumericValue = f.NumericValue,
                    DateValue = f.DateValue,
                    Index = f.Index,
                    EntityIndex = f.EntityIndex,
                    ParentIndex = f.ParentIndex
                }).ToList()
            };

            newRequest.Events.Add(newEvent);
        }

        return newRequest;
    }

    /// <summary>
    /// Populates a database with mixed events for query benchmarks.
    /// </summary>
    public static void PopulateDatabaseWithEvents(EpcisContext context, int eventCount)
    {
        var bizSteps = new[]
        {
            "urn:epcglobal:cbv:bizstep:receiving",
            "urn:epcglobal:cbv:bizstep:shipping",
            "urn:epcglobal:cbv:bizstep:packing",
            "urn:epcglobal:cbv:bizstep:commissioning",
            "urn:epcglobal:cbv:bizstep:accepting"
        };

        var dispositions = new[]
        {
            "urn:epcglobal:cbv:disp:in_progress",
            "urn:epcglobal:cbv:disp:in_transit",
            "urn:epcglobal:cbv:disp:active",
            "urn:epcglobal:cbv:disp:dispensed",
            "urn:epcglobal:cbv:disp:destroyed"
        };

        var events = new List<Event>();
        var objectEventCount = (int)(eventCount * 0.7);
        var aggregationEventCount = (int)(eventCount * 0.2);
        var transformationEventCount = eventCount - objectEventCount - aggregationEventCount;

        // Generate ObjectEvents with varying business steps and dispositions
        for (int i = 0; i < objectEventCount; i++)
        {
            var evt = GenerateObjectEvent(10, i);
            evt.BusinessStep = bizSteps[i % bizSteps.Length];
            evt.Disposition = dispositions[i % dispositions.Length];
            evt.ReadPoint = $"urn:epc:id:sgln:0614141.07346.{i % 100}";
            evt.BusinessLocation = $"urn:epc:id:sgln:0614141.07346.{i % 50}";
            events.Add(evt);
        }

        // Generate AggregationEvents
        for (int i = 0; i < aggregationEventCount; i++)
        {
            var evt = GenerateAggregationEvent(10, objectEventCount + i);
            evt.BusinessStep = bizSteps[i % bizSteps.Length];
            evt.Disposition = dispositions[i % dispositions.Length];
            events.Add(evt);
        }

        // Generate TransformationEvents
        for (int i = 0; i < transformationEventCount; i++)
        {
            var evt = GenerateTransformationEvent(5, 5, objectEventCount + aggregationEventCount + i);
            evt.BusinessStep = bizSteps[i % bizSteps.Length];
            evt.Disposition = dispositions[i % dispositions.Length];
            events.Add(evt);
        }

        var request = CreateRequestFromEvents(events);
        context.Add(request);
        context.SaveChanges();
    }

    /// <summary>
    /// Generates events with custom extension fields.
    /// </summary>
    public static List<Event> GenerateEventsWithCustomFields(int count)
    {
        var events = GenerateMixedEvents(count);
        var namespaces = new[]
        {
            "http://example.com/epcis/extension1",
            "http://example.com/epcis/extension2",
            "http://company.org/custom"
        };

        foreach (var evt in events)
        {
            var fieldCount = Random.Next(3, 8);
            evt.Fields = new List<Field>();

            for (int i = 0; i < fieldCount; i++)
            {
                evt.Fields.Add(new Field
                {
                    Type = FieldType.CustomField,
                    Namespace = namespaces[i % namespaces.Length],
                    Name = $"customField{i}",
                    TextValue = $"value_{i}_{Random.Next(1000)}",
                    Index = i
                });
            }
        }

        return events;
    }

    /// <summary>
    /// Generates events with sensor elements and reports.
    /// </summary>
    public static List<Event> GenerateEventsWithSensorData(int count)
    {
        var events = GenerateMixedEvents(count);

        foreach (var evt in events)
        {
            var sensorElement = new SensorElement
            {
                Index = 0,
                DeviceId = $"urn:epc:id:giai:4000001.{Random.Next(1000, 9999)}",
                DeviceMetadata = "urn:epcglobal:cbv:sdt:temperature_sensor",
                RawData = "https://example.com/sensor/data",
                StartTime = evt.EventTime.AddMinutes(-10),
                EndTime = evt.EventTime,
                DataProcessingMethod = "https://example.com/sensor/processing"
            };

            var report = new SensorReport
            {
                Index = 0,
                SensorIndex = 0,
                Type = "Temperature",
                UnitOfMeasure = "CEL",
                Value = 20 + Random.Next(-5, 15),
                MinValue = 15,
                MaxValue = 25,
                MeanValue = 20,
                Time = evt.EventTime
            };

            evt.SensorElements = new List<SensorElement> { sensorElement };
            evt.Reports = new List<SensorReport> { report };
        }

        return events;
    }

    /// <summary>
    /// Creates a QueryResponse from a list of events.
    /// </summary>
    public static QueryResponse CreateQueryResponse(List<Event> events, string queryName = "SimpleEventQuery")
    {
        return new QueryResponse(queryName, new QueryData { EventList = events });
    }
}
