Before moving on, confirm:
- Which parts of the capture and query flows are CPU-bound
- Which are memory-bound
- Which are I/O-bound

Base this strictly on the code paths already identified.

-----

 I/O-Bound Operations

  1. HTTP Request Reading (CaptureDocumentRequest.BindAsync:25,32)
    - context.Request.Body stream reading
    - Network I/O
  2. XML/JSON Loading (XmlDocumentParser.LoadDocument:44)
    - XDocument.LoadAsync(input, ...) reads entire stream
    - Critical: Loads full document into memory (not streaming)
  3. Database Persistence (CaptureHandler.StoreAsync:63,66)
    - context.SaveChangesAsync() within transaction
    - Two database round-trips for RecordTime update
    - Database I/O

  CPU-Bound Operations

  1. XSD Schema Validation (XmlDocumentParser.ParseDocument:29)
    - document.Validate(schema, ...)
    - Tree traversal and pattern matching against compiled schemas
  2. Event Parsing (XmlEventParser.ParseEvent:18 and subclasses)
    - Recursive XML element parsing
    - String parsing (float.TryParse, date parsing)
    - Object allocation (Event, Epc, Field entities)
    - Hierarchical field reconstruction
  3. Business Validation (CaptureHandler.StoreAsync:44,52)
    - RequestValidator.IsValid(), EventValidator.IsValid()
    - Logic checks
  4. EventID Generation (CaptureHandler.StoreAsync:58)
    - EventHash.Compute() - currently GUID generation

  Memory-Bound Operations

  1. XML DOM Loading (XmlDocumentParser.LoadDocument:44)
    - XDocument.LoadAsync() creates entire DOM in memory
    - Large documents (1000s of events) = large memory footprint
    - No streaming parser used
  2. Event Object Graphs (during parsing)
    - Full Request aggregate with all Events and nested collections
    - Each Event contains: Epcs, Fields (hierarchical), Sources, Destinations, SensorElements, Transactions

  ---
  QUERY FLOW

  I/O-Bound Operations

  1. First Query - Event IDs (DataRetrieverHandler.QueryEventsAsync:25-28)
    - context.QueryEvents(...).Select(x => x.Id).ToListAsync()
    - Database query
    - Lightweight (only IDs)
  2. Second Query - Full Events (DataRetrieverHandler.QueryEventsAsync:39-41)
    - context.Set<Event>().Where(...).ToListAsync()
    - Database query with related entities
    - EF Core loads all owned entities (Epcs, Fields, Sources, Destinations, SensorElements, etc.)
  3. HTTP Response Writing
    - JSON/XML serialization to response stream
    - Network I/O

  CPU-Bound Operations

  1. Query Parameter Parsing (EventQueryContext.ParseParameter:33-100+)
    - Switch statement routing
    - Regex pattern matching for complex parameters
    - Type conversions: param.AsDate(), param.AsInt(), param.AsFloat()
    - String parsing
  2. LINQ Expression Building (EventQueryContext.ParseParameter)
    - Filter(x => ...) constructs Expression<Func<Event, bool>>
    - Expression tree composition
    - Multiple filters chained
  3. Result Ordering (DataRetrieverHandler.QueryEventsAsync:43)
    - events.OrderBy(e => eventIds.IndexOf(e.Id))
    - In-memory sort by index lookup - O(n×m) complexity
  4. JSON/XML Formatting (JsonEventFormatter.FormatEvent:25-80+)
    - Dictionary construction
    - Recursive BuildExtensionFields() for hierarchical custom fields
    - String concatenation, namespace prefix generation
    - Object traversal

  Memory-Bound Operations

  1. Event Result Sets (DataRetrieverHandler.QueryEventsAsync:39-41)
    - Full Event entities loaded with all owned collections:
        - Epcs collection
      - Fields collection (hierarchical - can be large for events with many extensions)
      - Sources, Destinations collections
      - SensorElements, SensorReports collections
      - Transactions collection
      - Request navigation property
    - NoTracking enabled (good - no change tracking overhead)
    - Limited by maxEventCount (default from Constants.MaxEventsReturnedInQuery)
  2. Response Object Construction (JsonEventFormatter.FormatEvent)
    - Building dictionaries for each event
    - Recursive field hierarchy reconstruction
    - Entire response body in memory before serialization

  ---
  Critical Bottleneck Identification

  Memory Bottlenecks:

  1. Capture: XDocument.LoadAsync() - entire XML DOM in memory (no streaming)
  2. Query: Full event object graphs with all nested collections
  3. Query: Hierarchical Field entities (deeply nested custom extensions)

  CPU Bottlenecks:

  1. Capture: XSD validation (tree walking entire DOM)
  2. Capture: Recursive XML parsing and object construction
  3. Query: Expression tree building for complex filters
  4. Query: In-memory OrderBy with IndexOf - O(n×m)
  5. Query: Recursive JSON/XML formatting of hierarchical fields

  I/O Bottlenecks:

  1. Capture: Database transaction with two SaveChangesAsync() calls
  2. Query: Two-step database query pattern (IDs then full entities)
  3. Both: Network I/O for request/response

  ---
  Key Observations

  1. No streaming in capture flow - entire document must fit in memory
  2. Two database round-trips in query - by design to check count before loading full data
  3. Generic Field storage - flexible but memory/CPU intensive for deeply nested hierarchies
  4. NoTracking queries - properly optimized for read-heavy workload
  5. In-memory ordering - could be optimized if event ID ordering matches database ordering
