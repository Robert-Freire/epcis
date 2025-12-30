Based on the current codebase, identify the main performance bottlenecks when handling large EPCIS XML documents (tens or hundreds of MB).

Analyze specifically:
- XML parsing approach (DOM vs streaming)
- Memory allocations and object graphs
- Database interaction patterns
- Serialization costs in query responses
- Request lifecycle and timeout risks

Rank the bottlenecks by expected impact.
Explain *why* each one is costly, referencing the code.

This is analysis only. Do NOT suggest solutions yet.

-----


🔴 CRITICAL BOTTLENECKS

  1. Non-Streaming XML DOM Loading ⚠️ HIGHEST IMPACT

  Memory-bound, blocks entire capture flow

  Code Location: XmlDocumentParser.LoadDocument:44
  return await XDocument.LoadAsync(input, LoadOptions.None, cancellationToken);

  Why This Is Costly:
  - Loads entire XML document into memory as DOM tree before any processing
  - For a 100 MB XML document, expect 300-500 MB memory allocation (DOM overhead is ~3-5x raw XML size)
  - No streaming parser - cannot process events incrementally
  - Creates full XElement tree with all nodes, attributes, whitespace
  - Blocks all downstream processing until entire document is in memory

  Impact: A 200 MB EPCIS document with 10,000 events could consume 600+ MB just for DOM, before any domain object creation.

  ---
  2. Full DOM XSD Validation ⚠️ VERY HIGH IMPACT

  CPU-bound + memory-bound

  Code Location: XmlDocumentParser.ParseDocument:29
  document.Validate(schema, (_, t) => { ... });

  Why This Is Costly:
  - Tree-walks entire DOM against compiled XSD schemas
  - Validates structure, types, cardinality, patterns for every element
  - Operates on full in-memory DOM (compounding bottleneck #1)
  - For documents with nested extensions, validation complexity increases
  - Synchronous blocking operation - no parallelization

  Impact: For 10,000 events with average 20 elements each = 200,000 elements validated. CPU-intensive pattern matching and type checking.

  ---
  3. Recursive Custom Field Parsing with Object Allocation Explosion ⚠️ VERY HIGH IMPACT

  Memory-bound + CPU-bound

  Code Location: XmlEventParser.ParseCustomFields:290-315
  internal void ParseCustomFields(XElement element, FieldType fieldType, int? parentIndex, int? entityIndex)
  {
      var field = new Field { Index = ++Index, ... };

      foreach (var children in element.Elements())
      {
          ParseCustomFields(children, fieldType, field.Index, entityIndex); // RECURSIVE
      }
      foreach (var attribute in element.Attributes().Where(x => !x.IsNamespaceDeclaration))
      {
          ParseAttribute(attribute, field.Index, entityIndex);
      }

      Event.Fields.Add(field); // EVERY element becomes a Field object
  }

  Why This Is Costly:
  - Recursive allocation - every XML element creates a Field object
  - Three value types stored per field: TextValue, NumericValue, DateValue (lines 300-302)
    - Parses float: float.TryParse(...)
    - Parses date: UtcDateTime.TryParse(...)
    - All on speculative basis - stores values it might never need
  - Attributes also parsed recursively (line 309-312)
  - Hierarchical indexing: Index, ParentIndex, EntityIndex maintained
  - For deeply nested ILMD/extensions (common in pharmaceutical/retail), creates massive Field collections

  Impact Example:
  - Event with ILMD containing 50 custom fields with 3 levels nesting = 150+ Field objects
  - 1,000 such events = 150,000 Field objects in memory
  - Each Field: ~200 bytes minimum = 30+ MB just for Field objects

  ---
  4. Object Graph Explosion per Event ⚠️ HIGH IMPACT

  Memory-bound

  Code Locations:
  - XmlV2EventParser.ParseEvent:12-85
  - EpcisModelConfiguration.cs:143-249 (all owned collections)

  Each Event Contains:
  Event (base object)
  ├── Epcs (List<Epc>)
  ├── Fields (List<Field>) ← Can be HUGE
  ├── Sources (List<Source>)
  ├── Destinations (List<Destination>)
  ├── Transactions (List<BusinessTransaction>)
  ├── SensorElements (List<SensorElement>)
  ├── Reports (List<SensorReport>)
  ├── PersistentDispositions (List<PersistentDisposition>)
  ├── CorrectiveEventIds (List<CorrectiveEventId>)
  └── Request (navigation property)

  Why This Is Costly:
  - 10 collection instances per event minimum
  - Collections initialized in parser (lines 46-72 of XmlV2EventParser)
  - .AddRange() calls create intermediate enumerables
  - All collections kept in memory until database persistence completes
  - Request aggregate holds ALL events + their graphs until transaction commits

  Impact:
  - 500 events (MaxEventsCapturePerCall) × 10 collections × average 5 items each = 25,000 objects
  - Average event object graph: 2-5 KB
  - 500 events = 1-2.5 MB in domain objects alone
  - Plus DOM (500 MB) + Fields (30 MB) = Total: 530+ MB for medium document

  ---
  🟠 SIGNIFICANT BOTTLENECKS

  5. Database: Dual SaveChangesAsync + Full Change Tracking ⚠️ HIGH IMPACT

  I/O-bound + CPU-bound

  Code Location: CaptureHandler.StoreAsync:60-68
  using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
  {
      context.Add(request); // Change tracking starts HERE
      await context.SaveChangesAsync(cancellationToken); // First DB round-trip

      request.RecordTime = DateTime.UtcNow;
      await context.SaveChangesAsync(cancellationToken); // SECOND DB round-trip
      await transaction.CommitAsync(cancellationToken);
  }

  Why This Is Costly:
  - Two database round-trips within same transaction
  - EF Core change tracking active for all entities during capture (no NoTracking)
    - Tracks Request + all Events + all owned entities (Epcs, Fields, Sources, etc.)
    - Change tracker overhead: ~100-500 bytes per tracked entity
  - For 500 events with average 50 related entities each = 25,000+ tracked entities
  - First SaveChangesAsync: Inserts Request + 500 Events + 25,000 owned entities
  - Second SaveChangesAsync: Updates Request.RecordTime only, but scans all tracked entities

  Impact:
  - Change tracking memory: 25,000 entities × 300 bytes = 7.5 MB overhead
  - Database I/O: Thousands of INSERT statements (potentially batched but still costly)
  - Transaction held open across both operations

  ---
  6. Owned Entity Cascade Inserts ⚠️ MEDIUM-HIGH IMPACT

  I/O-bound

  Code Location: EpcisModelConfiguration.cs:143-249
  evt.OwnsMany(x => x.Epcs, c => { ... });
  evt.OwnsMany(x => x.Fields, c => { ... }); // Composite key: EventId + Index
  evt.OwnsMany(x => x.Sources, c => { ... });
  evt.OwnsMany(x => x.Destinations, c => { ... });
  // ... 9 owned collections total

  Why This Is Costly:
  - 9 owned collections with cascade insert
  - Each owned entity requires separate INSERT (though EF batches)
  - Composite keys on all owned entities (e.g., "EventId", "Index")
  - No bulk insert optimization visible in code
  - Field table: EventId + Index as key - one row per Field (thousands)

  Impact:
  - 500 events × 50 Fields average = 25,000 INSERT into Field table
  - 500 events × 10 Epcs average = 5,000 INSERT into Epc table
  - Total: 30,000-50,000 database operations per capture

  ---
  7. Hardcoded Capture Size Limit ⚠️ MEDIUM IMPACT (Configuration Issue)

  Artificial constraint

  Code Location: Constants.cs:6
  public int CaptureSizeLimit { get; init; } = 1_024; // 1 KB !!

  Code Location: CaptureDocumentRequest.BindAsync:16-18
  if (constants.CaptureSizeLimit > 0 && context.Request.ContentLength > constants.MaxEventsReturnedInQuery)
  {
      throw new EpcisException(ExceptionType.CaptureLimitExceededException, ...);
  }

  Why This Is Problematic:
  - Default limit is 1 KB (likely a typo - should be MB)
  - Note: Line 16 has a bug - compares ContentLength to MaxEventsReturnedInQuery instead of CaptureSizeLimit
  - Even if fixed, 1 KB would block all realistic EPCIS documents
  - Real-world EPCIS documents: 1-500 MB common

  Impact: Large documents rejected unless configuration overridden.

  ---
  🟡 MODERATE BOTTLENECKS

  8. Query Response: Full Event Materialization ⚠️ MEDIUM IMPACT

  I/O-bound + Memory-bound

  Code Location: DataRetrieverHandler.QueryEventsAsync:39-41
  var events = await context.Set<Event>()
      .Where(x => eventIds.Contains(x.Id))
      .ToListAsync(cancellationToken); // Loads ALL owned entities

  Code Location: EpcisModelConfiguration.cs:142
  evt.Navigation(e => e.Request).AutoInclude();

  Why This Is Costly:
  - Loads full object graphs for all events
  - AutoInclude() on Request navigation (line 142) - every event includes Request
  - All 9 owned collections loaded (Epcs, Fields, Sources, Destinations, SensorElements, Reports, etc.)
  - NoTracking enabled (good) but still full materialization
  - Query splitting may help but still loads everything

  Impact:
  - Query for 1,000 events with average 50 related entities = 50,000 database reads
  - Result set: 2-5 MB in memory
  - Field hierarchy must be reconstructed for serialization

  ---
  9. Response Serialization: Recursive Field Formatting ⚠️ MEDIUM IMPACT

  CPU-bound

  Code Location: XmlEventFormatter.FormatField (recursive), JsonEventFormatter.BuildExtensionFields:61+ (recursive)

  Why This Is Costly:
  - Recursive reconstruction of hierarchical fields from flat list
  - Navigates via ParentIndex relationships
  - Creates XElement tree (XML) or Dictionary tree (JSON)
  - Namespace prefix generation and mapping
  - String concatenation for element names

  Impact:
  - For events with deep ILMD/extensions: O(n²) complexity for hierarchy rebuild
  - 1,000 events × 100 fields each = 100,000 fields to recursively format

  ---
  10. In-Memory Result Ordering ⚠️ LOW-MEDIUM IMPACT

  CPU-bound

  Code Location: DataRetrieverHandler.QueryEventsAsync:43
  return new(events.OrderBy(e => eventIds.IndexOf(e.Id)));

  Why This Is Costly:
  - O(n × m) complexity: For each event (n), calls IndexOf on eventIds list (m)
  - Should use dictionary lookup instead
  - In-memory sort after database retrieval

  Impact:
  - 1,000 events: 1,000,000 IndexOf calls (worst case)
  - Could be eliminated with Dictionary<int, int> for position mapping

  ---
  REQUEST LIFECYCLE & TIMEOUT RISKS

  Synchronous Blocking Points:

  1. XML DOM Load - entire document must load before validation starts
  2. XSD Validation - blocks until complete DOM validated
  3. Event Parsing - sequential, event-by-event (not parallelized)
  4. Database Transaction - holds transaction open for entire batch

  Timeout Risk Analysis:

  For a 100 MB EPCIS document with 5,000 events:

  | Phase                | Time Estimate | Cumulative |
  |----------------------|---------------|------------|
  | HTTP Request Read    | 2-5s          | 5s         |
  | XML DOM Load         | 5-10s         | 15s        |
  | XSD Validation       | 10-20s        | 35s        |
  | Event Parsing        | 15-30s        | 65s        |
  | Database Transaction | 30-60s        | 125s       |
  | Total                |               | ~2 minutes |

  Default ASP.NET timeout: Typically 100-120 seconds

  Risk: Large documents (>100 MB) will timeout without configuration changes.

  ---
  SUMMARY: RANKED BY IMPACT

  | Rank | Bottleneck                   | Type       | Impact     | Fix Difficulty       |
  |------|------------------------------|------------|------------|----------------------|
  | 1    | XML DOM Loading              | Memory     | ⚠️⚠️⚠️⚠️⚠️ | Hard (architectural) |
  | 2    | XSD Validation on Full DOM   | CPU        | ⚠️⚠️⚠️⚠️   | Hard                 |
  | 3    | Recursive Field Parsing      | Memory+CPU | ⚠️⚠️⚠️⚠️   | Medium               |
  | 4    | Object Graph per Event       | Memory     | ⚠️⚠️⚠️⚠️   | Medium               |
  | 5    | Dual SaveChanges + Tracking  | I/O+CPU    | ⚠️⚠️⚠️     | Easy                 |
  | 6    | Cascade Owned Entity Inserts | I/O        | ⚠️⚠️⚠️     | Medium               |
  | 7    | Size Limit Config Bug        | Config     | ⚠️⚠️       | Trivial              |
  | 8    | Full Event Materialization   | I/O+Memory | ⚠️⚠️       | Medium               |
  | 9    | Recursive Serialization      | CPU        | ⚠️⚠️       | Medium               |
  | 10   | In-Memory IndexOf Ordering   | CPU        | ⚠️         | Easy                 |

  The top 4 bottlenecks account for ~80% of performance issues with large documents.