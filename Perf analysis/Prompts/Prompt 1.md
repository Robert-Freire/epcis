You are an experienced backend architect.

Explore this repository and explain the high-level architecture of the EPCIS solution.

Focus on:
- Capture endpoints and request handling
- XML parsing and validation flow
- Persistence layer (what is stored, where, and how)
- Query endpoints and response generation
- Where EPCIS XML is serialized and deserialized

Reference concrete folders, files, classes, and methods.
If something is unclear, state the uncertainty explicitly.

Do NOT propose changes yet.
Do NOT implement anything.

-----

  This is a .NET 10.0-based EPCIS solution supporting both EPCIS 1.x (SOAP) and EPCIS 2.0 (REST) standards for supply chain event data sharing.

  ---
  1. Capture Endpoints & Request Handling

  HTTP Endpoints

  File: src/FasTnT.Host/Endpoints/CaptureEndpoints.cs

  Endpoints:
  - POST /capture - Captures EPCIS documents (XML/JSON)
  - POST /events - Captures single events
  - GET /capture - Lists all captures (paginated)
  - GET /capture/{captureId} - Retrieves specific capture

  Request Processing Flow

  HTTP POST → CaptureDocumentRequest.BindAsync()
           → Content-type detection (XML vs JSON)
           → Parser selection (XmlCaptureRequestParser / JsonCaptureRequestParser)
           → CaptureHandler.StoreAsync()
           → Validation → Database persistence

  Key Handler: src/FasTnT.Application/Handlers/CaptureHandler.cs
  - Method: StoreAsync() - Main capture logic
  - Validates using RequestValidator and HeaderValidator
  - Generates EventIDs via EventHash.Compute() if missing
  - Uses database transactions for atomicity
  - Triggers notifications via IEventNotifier
  - Enforces Constants.MaxEventsCapturePerCall limit

  ---
  2. XML Parsing & Validation Flow

  Schema Validation

  Location: src/FasTnT.Host/Communication/Xml/Schemas/
  - v1/ - EPCIS 1.0, 1.1, 1.2 schemas
  - v2/ - EPCIS 2.0 schemas
  - shared/ - Common XSD (SBDH, BasicTypes, EPCglobal)

  Parsing Pipeline

  Entry Point: src/FasTnT.Host/Communication/Xml/Parsers/XmlDocumentParser.cs
  - Method: ParseAsync(Stream input, CancellationToken)
  - Loads XML via XDocument.LoadAsync()
  - Detects version from schemaVersion attribute
  - XSD validation: XDocument.Validate() against compiled schemas
  - Routes to version-specific parser

  Version-Specific Parsers:

  1. XmlV1EventParser.cs - EPCIS 1.x
    - Nested extension hierarchy: baseExtension → extension → sub-extension
    - Supports legacy QuantityEvent
  2. XmlV2EventParser.cs - EPCIS 2.0
    - Flat structure, all event types first-class
    - Simplified extension model

  Document Parser: XmlEpcisDocumentParser.cs
  - Orchestrates parsing
  - Parses EPCIS Header (SBDH, MasterData)
  - Parses EPCIS Body (EventList, VocabularyList, QueryResults)

  Event Transformation: XmlEventParser.cs (abstract base)
  - Method: ParseEvent() - Main entry
  - Parses complex structures:
    - EPCs (List, ChildEPC, InputEPC, OutputEPC, Quantities)
    - Business Transactions
    - Sources/Destinations
    - Sensor Elements/Reports
    - Custom fields (ILMD, extensions, attributes)
  - Maintains hierarchy via Index and ParentIndex

  Validation Layers:
  1. XSD schema validation (structural)
  2. Business rules via EventValidator and RequestValidator in src/FasTnT.Application/Validators/
  3. Example rule: AggregationEvent with Add/Delete requires ParentID

  ---
  3. Persistence Layer

  Database Technology

  Entity Framework Core 10.0 with provider abstraction:
  - SQL Server (FasTnT.SqlServer)
  - PostgreSQL (FasTnT.Postgres)
  - SQLite (FasTnT.Sqlite)

  Config: src/FasTnT.Host/Services/Database/DatabaseConfiguration.cs

  Core Entities

  Location: src/FasTnT.Domain/Model/

  1. Request (Request.cs)
  - Root aggregate for captures
  - Properties: CaptureId, UserId, DocumentTime, RecordTime, SchemaVersion
  - Navigation: Events, Masterdata, StandardBusinessHeader

  2. Event (Events/Event.cs)
  - Central entity for EPCIS events
  - Properties: EventId, EventTime, Type, Action, BusinessStep, Disposition, ReadPoint, BusinessLocation, TransformationId
  - Corrective support: CorrectiveDeclarationTime, CorrectiveReason, CorrectiveEventIds
  - Owned collections:
    - Epcs - EPC identifiers
    - Transactions - Business transactions
    - Sources/Destinations - Source/destination info
    - SensorElements/Reports - IoT sensor data
    - Fields - Generic extension field storage ⭐

  3. Field (Events/Field.cs) - Key Design Pattern
  - EAV (Entity-Attribute-Value) pattern for extensibility
  - Properties: Type, Name, Namespace, TextValue, NumericValue, DateValue
  - Hierarchical: Index, ParentIndex, EntityIndex
  - Enables schema-less custom extensions without migrations

  4. MasterData (Masterdata/MasterData.cs)
  - CBV (Core Business Vocabulary) storage
  - Hierarchical relationships

  5. Subscription (Subscriptions/Subscription.cs)
  - Standing queries with schedules

  Database Mapping

  File: src/FasTnT.Application/Database/EpcisModelConfiguration.cs

  Schema Organization:
  - Epcis - Events, requests, EPCs, transactions
  - Cbv - Master data vocabulary
  - Sbdh - Standard Business Document Header
  - Subscriptions - Subscription data

  Table Structure:
  Request (parent)
  ├── Event (cascade delete)
  │   ├── Epc (owned)
  │   ├── Source (owned)
  │   ├── Destination (owned)
  │   ├── BusinessTransaction (owned)
  │   ├── Field (owned) ← Hierarchical custom fields
  │   ├── SensorElement (owned)
  │   └── SensorReport (owned)
  ├── MasterData (cascade delete)
  └── StandardBusinessHeader (1-to-1)

  Data Access: src/FasTnT.Application/Database/EpcisContext.cs
  - DbContext with NoTracking by default
  - Methods:
    - QueryEvents(IEnumerable<QueryParameter>) - Filtered event queries
    - QueryMasterData(IEnumerable<QueryParameter>) - Filtered master data
  - Query splitting strategy for performance

  ---
  4. Query Endpoints & Response Generation

  REST Endpoints

  Files: src/FasTnT.Host/Endpoints/

  EventsEndpoints.cs:
  - GET /events - All events with filtering
  - GET /events/{eventId} - Single event
  - GET /eventTypes/{type}/events - By type
  - GET /epcs/{epc}/events - By EPC
  - GET /bizSteps/{bizStep}/events - By business step
  - GET /bizLocations/{location}/events - By location
  - GET /readPoints/{readPoint}/events - By read point
  - GET /dispositions/{disposition}/events - By disposition

  QueriesEndpoints.cs:
  - GET /queries - List custom queries
  - GET /queries/{queryName}/events - Execute named query
  - POST /queries - Create named query
  - WebSocket support for streaming

  SoapQueryService.cs: EPCIS 1.x compatibility
  - Endpoint: POST /query.svc
  - Methods: Poll, Subscribe, Unsubscribe, GetQueryNames

  Query Processing

  Handler: src/FasTnT.Application/Handlers/DataRetrieverHandler.cs
  - Method: QueryEventsAsync() - Main query execution
  - Pagination support
  - Throws QueryTooLargeException if exceeds maxEventCount

  Query Builder: src/FasTnT.Application/Database/DataSources/EventQueryContext.cs

  Supports 100+ Query Parameters:
  - Simple filters: EQ_bizStep, EQ_disposition, EQ_eventID
  - Time ranges: GE_eventTime, LT_eventTime, GE_recordTime
  - Pattern matching: MATCH_anyEPC, MATCH_epc (wildcards)
  - Hierarchical: WD_readPoint, WD_bizLocation (with-descendants)
  - Sensor filters: EQ_type, EQ_deviceID, GE_percRank
  - Custom fields: EQ_ILMD_*, EQ_INNER_*, EXISTS_*
  - Numeric: GT_quantity, GE_quantity, LT_*
  - Pagination: nextPageToken, perPage, eventCountLimit

  Implementation: Dynamic LINQ expression building via Expression<Func<Event, bool>>

  Response Formatters

  1. XML: src/FasTnT.Host/Communication/Xml/Formatters/XmlResponseFormatter.cs
  - Method: FormatPoll() - Creates EPCIS QueryResults XML
  - Namespace handling for extensions

  2. JSON: src/FasTnT.Host/Communication/Json/Formatters/JsonResponseFormatter.cs
  - Creates EPCIS 2.0 JSON-LD responses
  - Includes @context with custom namespaces
  - ISO 8601 date formatting

  3. SOAP: src/FasTnT.Host/Communication/Xml/Formatters/SoapResponseFormatter.cs
  - SOAP envelope wrapping
  - SOAP fault generation

  ---
  5. XML/JSON Serialization & Deserialization

  XML Deserialization (Parsing)

  Covered in Section 2 - Entry via XmlCaptureRequestParser.ParseAsync()

  XML Serialization (Formatting)

  Base Class: XmlEventFormatter (abstract)

  Implementations:

  1. XmlV1EventFormatter.cs - EPCIS 1.x
  - Nested extension format
  - Supports ObjectEvent, AggregationEvent, TransactionEvent, QuantityEvent

  2. XmlV2EventFormatter.cs - EPCIS 2.0
  - Flat structure, all event types first-class

  Key Methods:
  - FormatEvent() - Dispatches by type
  - CreateReadPoint(), CreateBusinessLocation() - Locations
  - CreateEpcList(), CreateQuantityList() - EPCs
  - CreateSensorElementList() - IoT data
  - CreateIlmdFields() - Item-level master data
  - FormatField() - Recursive custom field reconstruction

  Field Serialization:
  - Hierarchical reconstruction via ParentIndex
  - Namespace prefixing
  - Attribute vs element distinction
  - Array/object nesting support

  JSON Deserialization

  Parser: src/FasTnT.Host/Communication/Json/Parsers/JsonEventParser.cs
  - Method: Parse() - Entry point
  - Uses System.Text.Json.JsonElement
  - Namespace context from @context and GS1-Extensions header
  - Handles JSON-LD compaction
  - Recursive custom field parsing

  JSON Serialization

  Formatter: src/FasTnT.Host/Communication/Json/Formatters/JsonEventFormatter.cs
  - Method: FormatEvent() - Creates JSON object dictionary
  - Dynamic property inclusion (excludes nulls)
  - Namespace prefix generation
  - Recursive methods:
    - BuildExtensionFields() - Top-level
    - BuildElement() - Nested objects
    - BuildArrayElement() - Arrays

  JSON-LD Context:
  - Custom namespace mapping
  - Dynamic prefix generation (ext1, ext2...)
  - Standard EPCIS 2.0 context URL

  ---
  Architecture Patterns

  Layered Architecture

  ┌─────────────────────────────────────┐
  │   FasTnT.Host (Presentation)        │
  │   - Endpoints (Minimal APIs)        │
  │   - Parsers/Formatters (XML/JSON)   │
  └─────────────────┬───────────────────┘
                    │
  ┌─────────────────▼───────────────────┐
  │   FasTnT.Application                │
  │   - Handlers (Use Cases)            │
  │   - EpcisContext (Data Access)      │
  │   - Validators                      │
  └─────────────────┬───────────────────┘
                    │
  ┌─────────────────▼───────────────────┐
  │   FasTnT.Domain                     │
  │   - Entities (Request, Event)       │
  │   - Enumerations                    │
  └─────────────────────────────────────┘

  Key Patterns

  1. Strategy Pattern - Format parsing (XML/JSON, V1/V2)
  2. Provider Pattern - Database abstraction (SQL Server/Postgres/SQLite)
  3. Builder Pattern - Query expression building
  4. EAV Pattern - Generic field storage for extensions
  5. Event Sourcing Characteristics - Immutable events, append-only captures

  ---
  Data Flow Examples

  Capture Flow (XML):

  POST /capture (XML)
    → CaptureDocumentRequest.BindAsync()
    → XmlDocumentParser.ParseAsync() [XSD validation]
    → XmlV2EventParser.ParseEvent()
    → CaptureHandler.StoreAsync()
    → RequestValidator.IsValid()
    → EventValidator.IsValid()
    → EventHash.Compute()
    → EpcisContext.SaveChangesAsync()
    → HTTP 201 Created

  Query Flow:

  GET /events?EQ_bizStep=shipping&GE_eventTime=2024-01-01
    → DataRetrieverHandler.QueryEventsAsync()
    → EventQueryContext.ParseParameter()
    → Expression tree building
    → EpcisContext.QueryEvents()
    → EF Core query execution
    → JsonResponseFormatter.Format()
    → JSON-LD response
    → HTTP 200 OK

  ---
  Uncertainties

  1. EventHash Implementation: Currently generates GUID. EPCIS standard specifies content-based hashing - may be future work.
  2. SOAP WSDL: WsdlPath in SoapQueryService.cs:56 is empty - WSDL may be embedded/served differently.
  3. WebSocket Streaming: Mentioned in query endpoints but implementation details not fully explored.
  4. Subscription Execution: SubscriptionBackgroundService runs scheduled queries as hosted service - execution details not examined.

  This architecture demonstrates a well-structured, standards-compliant EPCIS implementation with clear separation of concerns and excellent extensibility through the generic Field storage pattern.