# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

FasTnT EPCIS is a GS1 EPCIS 1.2 and 2.0 repository written in C# using .NET 10 and EntityFramework Core. It provides REST and SOAP endpoints for capturing and querying supply chain events, with support for SQL Server, PostgreSQL, and SQLite.

## Essential Commands

### Database Setup

Apply migrations (required before first run):
```bash
dotnet ef database update --project src/FasTnT.Host --connection "Data Source=fastnt.db;" -- --FasTnT.Database.Provider "Sqlite"
```

For PostgreSQL:
```bash
dotnet ef database update --project src/FasTnT.Host --connection "Host=localhost;Database=fastnt;Username=postgres;Password=..." -- --FasTnT.Database.Provider "Postgres"
```

For SQL Server:
```bash
dotnet ef database update --project src/FasTnT.Host --connection "Server=localhost;Database=fastnt;Integrated Security=true;" -- --FasTnT.Database.Provider "SqlServer"
```

### Running the Application

```bash
dotnet run --project src/FasTnT.Host/FasTnT.Host.csproj --urls "http://localhost:5102/" --connectionStrings:FasTnT.Database "Data Source=fastnt.db;" --FasTnT.Database.Provider "Sqlite"
```

### Building

Build the entire solution:
```bash
dotnet build FasTnT.Epcis.sln
```

Build in Release mode:
```bash
dotnet build FasTnT.Epcis.sln -c Release
```

### Testing

Run all unit and integration tests:
```bash
dotnet test
```

Run specific test project:
```bash
dotnet test tests/FasTnT.Application.Tests
dotnet test tests/FasTnT.IntegrationTests
```

Run tests with coverage:
```bash
dotnet test /p:CollectCoverage=true
```

### Performance Testing

Run all performance benchmarks (always use Release mode):
```bash
dotnet run -c Release --project tests/FasTnT.PerformanceTests
```

Run specific benchmark class:
```bash
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *CaptureBenchmarks*
```

Run specific benchmark method:
```bash
dotnet run -c Release --project tests/FasTnT.PerformanceTests --filter *CaptureBenchmarks.CapturePreParsedRequest*
```

See `tests/FasTnT.PerformanceTests/README.md` for detailed benchmark documentation.

### Database Migrations

Create a new migration (specify provider):
```bash
dotnet ef migrations add MigrationName --project src/Providers/FasTnT.Sqlite --startup-project src/FasTnT.Host
```

Remove last migration:
```bash
dotnet ef migrations remove --project src/Providers/FasTnT.Sqlite --startup-project src/FasTnT.Host
```

## Architecture

### Clean/Layered Architecture

The codebase follows clean architecture with strict layering:

1. **Domain Layer** (`src/FasTnT.Domain`)
   - Pure domain models with zero external dependencies
   - Models: Events, Masterdata, Queries, Subscriptions, Requests
   - Enumerations: EventType, EventAction, EpcType, FieldType
   - Custom EPCIS exceptions

2. **Application Layer** (`src/FasTnT.Application`)
   - Business logic and data access using Entity Framework Core
   - Handlers: CaptureHandler, QueriesHandler, DataRetrieverHandler, SubscriptionHandler
   - Database contexts: EpcisContext, EventQueryContext, MasterDataQueryContext
   - Services: Events, Notifications, Subscriptions, Users
   - Validators: Request and event validation
   - Depends on: Domain layer

3. **Host Layer** (`src/FasTnT.Host`)
   - Web API using .NET Minimal APIs
   - Endpoints: CaptureEndpoints, EventsEndpoints, QueriesEndpoints, SubscriptionEndpoints, DiscoveryEndpoints, SoapQueryService
   - Communication: Dual XML/JSON parsers and formatters (see below)
   - Infrastructure: Authentication, database configuration, background services
   - Depends on: Application layer and all providers

### Database Provider Pattern

Plugin-based provider system in `src/Providers/`:
- `FasTnT.SqlServer` - SQL Server support
- `FasTnT.Postgres` - PostgreSQL support (via Npgsql)
- `FasTnT.Sqlite` - SQLite support

Each provider:
- Implements a static `Configure(IServiceCollection, connectionString, timeout)` method
- Contains its own EF Core migrations in a `Migrations/` folder
- Is registered in `src/FasTnT.Host/Services/Database/DatabaseConfiguration.cs` via dictionary lookup

Provider selection is controlled by the `FasTnT.Database.Provider` configuration value ("SqlServer", "Postgres", or "Sqlite").

### EPCIS Capture Flow

1. Request arrives at `CaptureEndpoints.cs` (POST /Capture)
2. Content-Type detection (application/json or application/xml) in `CaptureDocumentRequest.BindAsync()`
3. Parsing via `JsonCaptureRequestParser` or `XmlCaptureRequestParser`
4. Validation pipeline in `CaptureHandler`:
   - `RequestValidator` - validates request structure
   - `HeaderValidator` - validates EPCIS headers
   - `EventValidator` - validates individual events
5. EventId computation using `EventHash` for events without IDs
6. Single database transaction saves request and all events via `EpcisContext.SaveChangesAsync()`
7. Event notification to `IEventNotifier` triggers subscription processing

### EPCIS Query Flow

1. Request arrives at `EventsEndpoints.cs` (GET /events) or `QueriesEndpoints.cs`
2. Query parameters extracted and passed to `DataRetrieverHandler`
3. `EventQueryContext.cs` builds dynamic LINQ query from 40+ parameter types (EQ_, GE_, LT_, WD_, MATCH_)
4. Two-phase query: fetch matching IDs, then load full entities with relationships
5. Results formatted via `JsonEventFormatter` or `XmlEventFormatter`

**Query Extensibility:** To add new query parameters, update the switch statement in `EventQueryContext.ParseParameter()` and add the filter logic.

### XML/JSON Dual Format Support

Located in `src/FasTnT.Host/Communication/`:

- `Json/Parsers/` - JSON parsing using System.Text.Json
  - JsonCaptureRequestParser, JsonEventParser, JsonMasterdataParser
- `Json/Formatters/` - JSON output formatting
- `Xml/Parsers/` - XML parsing (9 parser files for EPCIS 1.2 and 2.0)
  - XmlCaptureRequestParser, XmlEventParser (V1/V2), XmlMasterdataParser, XmlQueryParser
- `Xml/Formatters/` - XML and SOAP output formatting

Content-Type routing uses the `BindAsync` pattern to detect format and invoke the appropriate parser.

EPCIS XSD schemas are embedded as resources in the Host project.

### Event Notification System

The `EpcisEvents` class (registered as singleton) implements three interfaces:
- `IEventNotifier` - receives capture notifications
- `ISubscriptionListener` - receives subscription change notifications
- `ICaptureListener` - listens for captures to trigger subscriptions

This acts as an in-memory event bus using C# events. The `SubscriptionBackgroundService` subscribes to these events and spawns background jobs for webhook and websocket subscriptions.

### Multi-Tenancy and User Isolation

`ICurrentUser` interface provides:
- User identification from HTTP Basic auth header hash
- `DefaultQueryParameters` that automatically filter queries by UserId

The UserId (hash of authorization header) is stored with each capture. By default, queries only return data captured by the same user. This provides automatic multi-tenant isolation.

### Dependency Injection

Service registration occurs in three locations:

1. `src/FasTnT.Application/EpcisConfiguration.cs`
   - `AddEpcisServices()` registers all handlers as Transient
   - Registers SubscriptionRunner and health checks

2. `src/FasTnT.Host/Services/Database/DatabaseConfiguration.cs`
   - `AddEpcisStorage()` configures selected database provider

3. `src/FasTnT.Host/Program.cs`
   - Authentication (BasicAuthentication scheme)
   - Current user (ICurrentUser → HttpContextCurrentUser)
   - Event notification (EpcisEvents singleton serving 3 interfaces)
   - Background services (SubscriptionBackgroundService)

## Testing Architecture

### Unit Tests

**FasTnT.Application.Tests** - Application layer unit tests
- Framework: MSTest
- Database: Sqlite in-memory
- Organization: Tests/{Feature}/ (Capture, Context, DataSources, Discovery, Queries, Subscriptions)

**FasTnT.Host.Tests** - Host layer unit tests
- Tests host-specific features

### Integration Tests

**FasTnT.IntegrationTests** - End-to-end API tests
- Uses WebApplicationFactory pattern (`FasTnTApplicationFactory`)
- Organization: v1_2/ and v2_0/ folders for EPCIS version-specific tests
- Database: Real Sqlite database per test with schema migrations
- Tests both XML/SOAP and JSON/REST endpoints

### Performance Tests

**FasTnT.PerformanceTests** - BenchmarkDotNet benchmarks
- Console application (OutputType: Exe)
- Located in: `tests/FasTnT.PerformanceTests/Benchmarks/`
- Benchmark classes: Capture, Query, EndToEnd, JsonParsing, XmlParsing, Serialization, StressTest, LimitTest
- MUST run in Release mode for accurate results

See `tests/FasTnT.PerformanceTests/README.md` for comprehensive benchmark documentation.

## Key Implementation Patterns

### Minimal APIs with Extension Methods

All endpoints use .NET Minimal APIs with extension methods:
```csharp
public static IEndpointRouteBuilder MapCaptureEndpoints(this IEndpointRouteBuilder app)
{
    app.MapPost("/Capture", async (CaptureDocumentRequest request, ...) => { ... });
    return app;
}
```

Registration in `Program.cs`:
```csharp
app.MapCaptureEndpoints()
   .MapEventEndpoints()
   .MapQueryEndpoints();
```

### Filter Chain Pattern for Queries

`EventQueryContext` uses a filter chain:
1. Each query parameter adds a filter function to a list
2. All filters are applied sequentially to the IQueryable
3. Very extensible - just add new filter cases

### Central Package Management

The project uses Central Package Management (CPM):
- Package versions defined once in `Directory.Packages.props`
- Project files reference packages without version numbers
- Ensures consistent versions across all projects

## Important File Locations

- **Entry point:** `src/FasTnT.Host/Program.cs`
- **DB context:** `src/FasTnT.Application/Database/EpcisContext.cs`
- **Service registration:** `src/FasTnT.Application/EpcisConfiguration.cs`
- **Query engine:** `src/FasTnT.Application/Database/DataSources/EventQueryContext.cs`
- **Event domain model:** `src/FasTnT.Domain/Model/Events/Event.cs`
- **Request domain model:** `src/FasTnT.Domain/Model/Request.cs`
- **Database config:** `src/FasTnT.Host/Services/Database/DatabaseConfiguration.cs`

## Technology Stack

- .NET 10.0
- Entity Framework Core 10.0.1
- Minimal APIs (no controllers)
- MSTest for unit/integration testing
- BenchmarkDotNet for performance testing
- Npgsql for PostgreSQL
- System.Text.Json for JSON parsing
- JsonSchema.Net for JSON schema validation

## EPCIS API Endpoints

### REST (EPCIS 2.0)
- `POST /Capture` - Capture events (JSON or XML)
- `GET /events` - Query events
- `POST /queries` - Create named query
- `GET /queries/{name}` - Execute named query
- `DELETE /queries/{name}` - Delete named query
- Discovery endpoints: `/eventTypes`, `/epcs`, `/bizSteps`, `/bizLocations`, `/readPoints`, `/dispositions`

### SOAP (EPCIS 1.2)
- `POST /Query.svc` - SOAP query service (GetVendorVersion, GetStandardVersion, Poll, Subscribe, etc.)

All endpoints support HTTP Basic authentication. The auth header hash is used as the UserId for multi-tenancy.

## Development Notes

### Adding a New Database Provider

1. Create new project in `src/Providers/FasTnT.YourProvider/`
2. Reference `FasTnT.Application` and appropriate EF Core provider package
3. Implement static `Configure(IServiceCollection, connectionString, timeout)` method
4. Create initial migration using `dotnet ef migrations add Initial`
5. Register in `DatabaseConfiguration.cs` dictionary
6. Update README.md supported databases list

### Adding a New Query Parameter

1. Add enum value to `FieldType` in `src/FasTnT.Domain/Enumerations/FieldType.cs`
2. Add case to `EventQueryContext.ParseParameter()` switch statement
3. Implement filter logic (usually adds to `_filters` list)
4. Add integration test in `tests/FasTnT.IntegrationTests/`

### Adding a New Event Type or Field

1. Update domain model in `src/FasTnT.Domain/Model/Events/`
2. Update `EpcisContext` DbSet if new entity
3. Create migration for each provider (SqlServer, Postgres, Sqlite)
4. Update parsers in `src/FasTnT.Host/Communication/Json/Parsers/` and `Xml/Parsers/`
5. Update formatters in `src/FasTnT.Host/Communication/Json/Formatters/` and `Xml/Formatters/`
6. Add validation logic to `src/FasTnT.Application/Validators/`

### Debugging Tips

- Integration tests create real databases - check test output for connection strings
- Performance benchmarks must run in Release mode (Debug mode has overhead)
- Database migrations are per-provider - ensure you run migrations for your chosen provider
- SOAP endpoint uses different XML namespaces than REST XML endpoint (1.2 vs 2.0)
