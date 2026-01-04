
# EPCIS Architectural Decision Record

> **Performance Estimate Disclaimer**
>
> Performance improvements in this document (e.g., "40-60% improvement," "90% reduction")
> are **estimates** based on architectural analysis and preliminary profiling.
>
> Actual results will vary based on:
> - Event complexity (custom fields, ILMD depth, extension usage)
> - Workload characteristics (query patterns, capture frequency, document size distribution)
> - Infrastructure configuration (Azure region, tier selection, network latency)
>
> Phase 1 validation will establish concrete baseline metrics using the
> FasTnT.PerformanceTests benchmark suite.

> **Cost Estimation Disclaimer**
>
> Cost estimates in this document (e.g., "~€300/month," "~€1,400/month") are **examples** based on:
> - West Europe region pricing (January 2025)
> - Assumed workloads and Azure service tiers
>
> **Your actual costs will vary.** See [Azure Cost Assumptions (West Europe)](EPCIS_Azure_Cost_Assumptions.md) for detailed pricing and calculation worksheet.

> **Document Type:** Historical record of architectural exploration and decision-making process
>
> **Status:** Finalized - December 30, 2024
>
> **Related Documents:**
> - [Executive Summary](EPCIS_Performance_Architecture_Executive_Summary.md)
> - [Hybrid Strategy & Phased Migration](EPCIS_Performance_Architecture_Hybrid_Phasing.md)

## Purpose

This document captures the architectural alternatives evaluated during the EPCIS performance investigation, along with the reasoning for selecting the SQL Server Hybrid Architecture approach.

It serves as a **historical record** of:
- Options considered
- Trade-offs analyzed
- Decisions made
- Rejected approaches and why

This document is **analysis-only** and does not propose implementation commitments.

---

## Baseline Performance Summary

> **Note:** This baseline reflects the open-source FasTnT codebase. Production environments may have already optimized capture operations (e.g., using bulk inserts, batching strategies) which can reduce capture time from minutes to seconds. In such environments, **query serialization becomes the primary bottleneck**, making the query-focused optimizations in this document even more critical.

### Capture Flow (≈100 MB XML, ~5,000 events)

- XML DOM load via `XDocument.LoadAsync`
- Full XSD validation on in-memory DOM
- Recursive parsing of custom fields into `Field` entities
- EF Core persistence of deeply nested object graphs
- Single long-running transaction

**Observed characteristics (order-of-magnitude):**
- Peak memory: ~500–600 MB
- Synchronous processing time: ~120–130 seconds
- High GC pressure due to DOM and object retention

---

### Query Flow (≈1,000 events)

- Full materialization of `Event` aggregates and owned entities
- Recursive reconstruction of hierarchical custom fields
- XML / JSON serialization from normalized model

**Observed characteristics:**
- Response time: ~10–15 seconds
- CPU-heavy recursive formatting
- Memory proportional to number of events and extensions

---

## Root Bottlenecks Identified

1. Full DOM XML loading (memory-bound)
2. Full-tree XSD validation (CPU + memory)
3. Recursive custom field parsing and storage
4. Large in-memory object graphs per capture
5. Repeated reconstruction and serialization during queries

These bottlenecks account for the majority of latency and memory usage under large EPCIS workloads.

### Why These Bottlenecks Persist

The current architecture prioritizes:
- **Query flexibility**: Full normalization enables complex filtering on any field
- **Format conversion**: Events stored as entities can be serialized to XML or JSON
- **EPCIS spec compliance**: Strict validation at capture time

This design made sense when:
- Documents were smaller (<10 MB)
- Custom extensions were rare
- Synchronous validation was a hard requirement

Modern EPCIS workloads (100+ MB documents, deep ILMD hierarchies) expose the cost of this approach.

---

## Architectural Alternatives Evaluated

### Approach 1: Raw XML Blob Storage

#### Description
Persist the original EPCIS XML payload as a blob and reference it from indexed event metadata.

#### Variants
- **Blob-only**: Store XML only, parse on demand
- **Minimal index + blob**: Extract minimal indexed fields and retain full XML

#### Granularity Options
- **Per-capture blob**: Entire EPCIS document stored once
  - Pro: Minimal storage, preserves document structure
  - Con: Over-fetching when querying single events (fetch 100MB to return 10 events)
- **Per-event blob**: Individual event XML fragments
  - Pro: Precise fetching, enables event-level caching
  - Con: Higher storage cost, loses document context (header, masterdata)

#### Benefits
- Eliminates repeated serialization and reconstruction costs
- Preserves original EPCIS payload with full fidelity
- Version-agnostic and extension-friendly

#### Limitations
- Reduced query flexibility beyond indexed fields
- Potential over-fetching when blob granularity is per-capture but query selects few events
- Additional storage and operational complexity

---

### Approach 2: Streaming XML Parsing

#### Description
Replace DOM-based parsing with `XmlReader` streaming.

#### Impact
- Major reduction in peak memory usage
- DOM allocation eliminated
- No improvement to query-time serialization costs

#### Trade-offs
- Increased parsing complexity
- XSD validation more difficult
- Does not address recursive formatting bottleneck

---

### Approach 3: Hybrid Storage (Normalized + Intelligent Routing) ⭐ **OPTION B**

#### Description
**Recommended for Azure PaaS deployments** (Azure SQL Database + Azure Blob Storage)

Intelligent routing based on document size:
- **Small documents (< 5 MB):** Store serialized XML/JSON in JSON column within Azure SQL Database
- **Large documents (>= 5 MB):** Store in Azure Blob Storage, reference URI in Azure SQL Database
- **Threshold:** Configurable (default 5 MB) to tune based on production workload characteristics
- All EPCIS fields remain queryable (no loss of query flexibility)

**Why 5 MB threshold:**
- Documents < 5 MB perform acceptably in SQL LOB storage (minor overhead)
- Documents > 5 MB trigger significant performance degradation in SQL
- 5 MB ≈ 1,000-5,000 events (typical large captures stay inline)
- 20,000 events ≈ 20-100 MB (automatically routed to Blob Storage)

**Choose if:** Phase 1 validation shows serialization is the bottleneck (60-80% of query time) **AND** document size distribution shows high proportion of small documents (>80% < 5 MB)

#### Variant: Blob-Only Storage (Simpler Alternative)

If Phase 1 Gate 2 (document size distribution analysis) shows that **>50% of documents are >= 5 MB**, a simpler blob-only approach may be preferable:

**Simpler Implementation:**
- **All documents** → Azure Blob Storage (no intelligent routing)
- Single code path (no inline vs blob logic)
- Same schema changes (DocumentBlobUri, StorageType)
- StorageType always = BlobStorage (SerializedDocument column unused)

**Trade-offs vs Intelligent Routing:**
- ✅ **Simpler:** Single retrieval path, no threshold logic
- ✅ **Less complexity:** No dual-path maintenance
- ❌ **Network call for all:** Even small documents require blob fetch
- ❌ **Slightly higher latency:** No fast path for small documents

**When to choose blob-only:**
- Document size distribution: >50% of documents >= 5 MB
- Simplicity valued over optimal small-document performance
- All documents already large enough that blob overhead is acceptable

#### Schema Changes

```csharp
// In Request entity (src/FasTnT.Domain/Model/Request.cs)
public class Request
{
    // Existing properties...
    public int Id { get; set; }
    public DateTimeOffset RecordTime { get; set; }
    public int UserId { get; set; }

    // NEW: Hybrid storage fields
    public string? SerializedDocument { get; set; }    // JSON column for < 5 MB docs
    public string? DocumentBlobUri { get; set; }       // Blob URI for >= 5 MB docs
    public StorageType StorageType { get; set; }       // Enum: Inline, BlobStorage
}

// New enum (src/FasTnT.Domain/Enumerations/StorageType.cs)
public enum StorageType
{
    Inline = 0,      // Stored in SerializedDocument JSON column
    BlobStorage = 1  // Stored in Azure Blob Storage, URI in DocumentBlobUri
}
```

**Note:** Detailed implementation including compensation logic, orphaned blob cleanup, and retrieval patterns are documented in GitHub issues.

#### Benefits
- **Optimal performance:** Small documents stay inline (fast SQL access, no blob overhead)
- **No size limits:** Large documents handled gracefully via blob storage
- **Cost-effective:** Less blob storage usage
- **Azure PaaS compatible:** Fully managed services only
- **Configurable:** Threshold tunable based on production metrics
- Preserves full query flexibility (all EPCIS query parameters supported)
- **Estimated** ~90% serialization reduction for blob-stored documents
- Incremental migration path (Phase 1 → Phase 2B → optional Phase 3)

#### Limitations
- Dual code paths: Capture and query logic must handle both storage types
- Eventual consistency: Blob uploads require compensation logic (write-ahead pattern)
- Threshold tuning: Requires analysis of production document size distribution
- Does not help if SQL query execution is the bottleneck
- **Estimated** +45% storage increase (blended average: most docs inline, few in blob)

---

### Approach 4: Azure Cache for Redis (Distributed Cache) ⭐ **OPTION A**

#### Description
Cache complete XML/JSON query responses using Azure Cache for Redis. Bypasses SQL query execution and serialization entirely on cache hits.

**Choose if:** Phase 1 validation shows SQL query execution is the bottleneck (>40% of query time)

#### Implementation Options
- **Azure Cache for Redis**: Managed distributed cache (Standard C2+ tier recommended)
- **Cache key strategy**: `epcis:query:{userId}:{format}:{queryHash}`
- **TTL strategy**: 5-30 minutes based on query type (time-bounded vs open-ended)
- **Graceful degradation**: Cache failures fall back to normal query path

#### Benefits
- Near-zero response cost on cache hits (1-5 ms vs 500-2000 ms for full query path)
- Minimal architectural change (layer on top of existing query path)
- Distributed cache enables consistent performance across multiple app instances
- No schema changes or migrations required
- Can be implemented alongside blob storage (Option C: Hybrid)

#### Effectiveness Scenarios
- **High**: Repeated identical queries (e.g., dashboard polling, monitoring endpoints)
- **Medium**: Similar queries with overlapping events (common event sets)
- **Low**: Unique queries, ad-hoc exploration, frequent captures (cache churn from invalidation)

Can serve as **primary solution (Option A)** or **complement to blob storage (Option C: Hybrid)**

#### Limitations
- Cold-start penalty unchanged (first query still pays full cost)
- Eventually consistent (1-30 min TTL, configurable by query type)
- Cache invalidation complexity (must invalidate when events updated/deleted)
- Operational cost: Azure Redis €55-110/month (Standard C2-C3)
- Does not help if serialization is the bottleneck (cache miss performance unchanged)

---

### Approach 5: Asynchronous Capture Processing

#### Description
Accept capture requests immediately and process ingestion in the background.

#### Benefits
- Eliminates synchronous request timeouts
- Improves user experience

#### Limitations
- Does not reduce total processing cost
- Increased operational complexity
- EPCIS compliance: EPCIS 2.0 spec Section 8.2.7 requires capture validation before acknowledgment; async processing may violate spec unless validation happens synchronously before 202 response

---

### Approach 6: Optimized Custom Field Storage

#### Description
Persist hierarchical custom fields as pre-serialized JSON instead of flat EAV rows.

#### Benefits
- Reduces recursive reconstruction cost
- Fewer database rows

#### Limitations
- Reduced query flexibility
- Database-specific JSON querying
- Still requires full parsing at capture time

---

### Approach 7: Azure AI Search (Lucene-based Search Engine)

#### Description
Offload EPCIS query operations to Azure AI Search (Lucene-based managed service). Events indexed in Azure AI Search for fast retrieval while SQL Server remains system of record.

#### Benefits
- Superior query performance (sub-second complex queries)
- Full-text search on ILMD and custom extensions without EAV reconstruction
- Advanced capabilities: faceting, fuzzy matching, geo-spatial queries
- Azure ecosystem integration and managed scaling
- Can store pre-formatted responses in index

#### Limitations
- Does not address capture bottlenecks (parsing, validation, persistence unchanged)
- Data synchronization complexity and eventual consistency risks
- Dual storage overhead (SQL + search index)
- Higher operational complexity (+30-40%)
- Azure vendor lock-in
- Additional service cost (tier-based pricing)
- Cannot participate in EPCIS capture transactions (ACID compliance)

#### Complementary Option
Could be added as optional Phase 3 after SQL Server hybrid implementation:
- Route standard EPCIS queries to SQL Server (transactional consistency)
- Route full-text/analytics queries to Azure AI Search (advanced features)
- Defers decision until query patterns are understood

---

### Approach 8: SQL Server FILESTREAM

#### Description
Store pre-serialized EPCIS documents using SQL Server FILESTREAM, which provides transactional consistency between SQL metadata and binary large objects (BLOBs) stored in the file system.

FILESTREAM enables SQL Server to store varbinary(max) data directly in the NTFS file system while maintaining transactional integrity. This combines the benefits of structured metadata storage with efficient BLOB handling.

#### Benefits
- **Transactional consistency**: BLOB operations participate in SQL Server transactions (ACID guarantees)
- **Single storage system**: No separate blob storage service required
- **Optimal I/O**: File system streaming bypasses SQL buffer pool for large objects
- **Backup integration**: FILESTREAMs included in standard SQL Server backups
- **Unified security**: Same authentication and authorization as SQL Server
- **Eliminates serialization cost**: Store once, retrieve directly without reconstruction

#### Limitations
- **Azure PaaS compatibility**: Not supported in Azure SQL Database or Azure SQL Managed Instance
- **IaaS requirement**: Only available in SQL Server on Azure Virtual Machines
- **Operational overhead**: Requires manual infrastructure management (patching, updates, HA configuration)
- **Higher cost**: Azure VM deployment ~€1,400-1,800/month vs €300-400/month for Azure SQL Database
- **Complexity increase**: +40-60% operational overhead compared to PaaS

#### Platform Availability

| Platform | FILESTREAM Support |
|----------|-------------------|
| SQL Server on Azure VM (IaaS) | ✅ Supported |
| Azure SQL Managed Instance (PaaS) | ❌ Not supported |
| Azure SQL Database (PaaS) | ❌ Not supported |
| SQL Server on-premises | ✅ Supported |

#### Cost & Operational Comparison (Azure)

| Dimension | Azure SQL Database (PaaS) | SQL Server on Azure VM (IaaS) |
|-----------|---------------------------|-------------------------------|
| Management | Fully managed | Manual patching, updates |
| High Availability | Built-in (99.99% SLA) | Manual Always On configuration |
| Cost | ~€300/month (S3 tier) | ~€1,400-1,800/month (VM + licensing) |
| Operational Complexity | Low | High (+40-60% overhead) |
| FILESTREAM Support | ❌ Not available | ✅ Available |

#### Use Case Fit
- **Good for**: On-premises deployments with full SQL Server infrastructure
- **Poor for**: Azure PaaS environments prioritizing managed services and operational simplicity
- **Alternative**: Approach 3 (Hybrid Storage) provides similar benefits while remaining PaaS-compatible

---

## Comparative Summary

| Approach | Serialization Cost (Est.) | Memory Usage (Est.) | Sync Duration (Est.) | Complexity | Query Flexibility | Decision |
|--------|---------------------------|---------------------|----------------------|------------|------------------|----------|
| Current | Baseline | Baseline | Baseline | Low | High | - |
| Blob + Minimal Index | -90% | -90–96% | -80–86% | Medium | Low | - |
| Streaming Parser | 0% | -80–85% | -10–20% | High | High | - |
| **Option A: Redis Cache** | **-100%\*** | **+0%** | **0%\*** | **Low** | **High** | **If SQL query is bottleneck** |
| **Option B-Azure: Hybrid Storage** | **-90%†** | **-70%†** | **-60%†** | **Medium** | **High** | **If serialization is bottleneck** |
| **Option C-Azure: Hybrid + Redis** | **-95%** | **-70%** | **-60%** | **High** | **High** | **If both are bottlenecks** |
| Async Capture | 0% | 0% | -100%‡ | Medium | High | - |
| JSON Fields | -70% | -50% | -30% | Low | Medium | - |
| Azure AI Search | -95%§ | 0% | 0% | High | Very High | Phase 3 consideration |
| SQL Server FILESTREAM | -90% | -70% | -60% | Very High¶ | High | Not PaaS-compatible |

\* Cache hit dependent; cache miss = baseline performance
† For documents routed to blob storage (>= 5 MB); inline documents have minimal overhead
‡ Hides latency, does not reduce total work
§ Query-time only; capture performance unchanged
¶ Requires Azure VM (IaaS) infrastructure; 5x cost increase and +40-60% operational overhead vs PaaS

---

## Key Observations

1. **Root cause**: The EAV pattern for custom fields (`Field` entity) creates O(n²) reconstruction cost
   - Code: `XmlEventFormatter.FormatField:221-229` and `JsonEventFormatter.BuildElement` (recursive ParentIndex lookup)
   - Impact: 1,000 events × 100 fields = 100,000 recursive calls with linear scans
   - **Phase 1 fix**: Pre-group fields by ParentIndex using Dictionary → O(n) lookup

2. **Memory spike**: XML DOM (300-500 MB) dominates capture memory
   - Code: `XmlDocumentParser.LoadDocument:44` (loads entire tree via `XDocument.LoadAsync`)
   - Impact: 100 MB XML → 300-500 MB in-memory DOM (3-5x overhead)

3. **Storage cost**: For 500 events with extensions, 25,000+ Field rows >> 500 Event rows
   - Code: `EpcisModelConfiguration.cs:229-242` (Field as owned entity)
   - Impact: Database write time dominated by owned entity cascade inserts

4. **Phase 1 improvements (**estimated** 40-60% improvement)**:
   - O(n²) → O(n) field reconstruction (both XML and JSON formatters)
   - Database indexing on Event.BusinessStep, Event.Disposition, Event.EventTime, Field.ParentIndex
   - EF Core 10 optimizations: Compiled Queries, AsNoTrackingWithIdentityResolution
   - Configuration bugs fixed (CaptureSizeLimit validation)

5. **Phase 1 validation determines Phase 2 path (Two-Stage Gating)**:
   - **Gate 1:** Benchmark measures serialization cost % vs SQL query cost %
     - **If SQL query >40%** → Option A: Redis Cache (near-zero latency on cache hits)
     - **If serialization 60-80%** → Proceed to Gate 2 (document size analysis)
     - **If both high** → Option C-Azure: Hybrid Storage + Redis (best of both)
   - **Gate 2:** Analyze document size distribution (only if serialization is bottleneck)
     - **If >80% documents < 5 MB** → Option B-Azure: Hybrid Storage with intelligent routing (**target** 90% improvement for blob-stored docs)
     - **If >50% documents >= 5 MB** → Option B-Azure-Simple: Blob-Only Storage (simpler, all docs to blob)
     - **If mixed (40-80% < 5 MB)** → Tune threshold or choose based on operational preference

6. **Hybrid storage approach (Option B-Azure) addresses serialization bottleneck**:
   - Small documents stay inline (minimal overhead, fast SQL access)
   - Large documents routed to blob storage (eliminates field reconstruction)
   - Supports 20,000 event requirement (20-100 MB documents) without SQL performance degradation
   - Does not help if SQL query execution is the bottleneck

7. **Redis cache (Option A) addresses SQL query bottleneck**:
   - Bypasses SQL entirely on cache hits (1-5 ms response)
   - No schema changes, minimal implementation complexity
   - Does not help if serialization is the bottleneck (cache miss = baseline performance)

8. **Azure AI Search vs Options A/B/C**:
   - Primary production bottleneck: Query performance (SQL execution + serialization)
   - Options A/B/C address this bottleneck with lower operational complexity
   - Azure AI Search advantage: Advanced search capabilities beyond standard EPCIS queries
   - **Recommended approach**: Start with Option A, B, or C based on Phase 1 validation; evaluate Azure AI Search in Phase 3 if advanced search required

---

## Migration & Risk Mitigation

### Staged Rollout Options
1. **Write-both mode**: Persist both normalized + blob during transition
   - Enables A/B testing and gradual query migration
   - Allows performance validation before full cutover

2. **Read-preferring-blob**: Query blob first, fallback to normalized
   - Minimizes risk during transition
   - Provides automatic rollback path

3. **Event-type-based routing**: Use blob only for high-extension event types
   - TransformationEvent, AssociationEvent typically have more extensions
   - ObjectEvent with minimal ILMD can remain normalized

### Rollback Strategy
- Blob storage remains append-only during transition
- Normalized entities can be regenerated from blobs if needed
- Allows safe experimentation with query patterns
- Database schema changes are additive (add blob reference column)

---

## Design Decisions

### Phase 2 Architecture Choice
**Decision Framework:** Choose based on Phase 1 validation bottleneck analysis
- **Option A (Redis Cache):** If SQL query execution >40% of query time
- **Option B-Azure (Hybrid Storage):** If serialization 60-80% of query time
- **Option C-Azure (Hybrid + Redis):** If both bottlenecks are significant

**For Azure PaaS with 20K event requirement:** Option B-Azure (Hybrid Storage) is prioritized due to:
- Handles typical small documents efficiently (inline JSON column)
- Supports 20,000 event cases (automatic blob routing)
- Minimal cost overhead (~€300/month vs ~€360/month for Redis)
- No data size limits

### Storage Strategy (Option B-Azure/C-Azure)
**Decision:** Hybrid storage with intelligent routing
- **Small documents (< 5 MB):** JSON column (SerializedDocument) within Azure SQL Database
- **Large documents (>= 5 MB):** Azure Blob Storage with URI reference (DocumentBlobUri)
- **Threshold:** 5 MB (configurable)
- **Storage cost:** **Estimated** +45% blended average (most docs inline, minimal blob usage)

### Rejected Option: SQL Server FILESTREAM

**Why not chosen for Azure PaaS deployment:**

SQL Server FILESTREAM provides transactional consistency between SQL metadata and binary large objects (BLOBs), making it an attractive option for storing pre-serialized EPCIS documents. However:

❌ **Not supported in Azure SQL Database** (PaaS)
❌ **Not supported in Azure SQL Managed Instance** (PaaS)
✅ **Only available in SQL Server on Azure Virtual Machines** (IaaS)

**Impact of using FILESTREAM (IaaS deployment):**

| Dimension | Azure SQL Database (PaaS) | SQL Server on Azure VM (IaaS) |
|-----------|---------------------------|-------------------------------|
| Management | Fully managed | Manual patching, updates |
| High Availability | Built-in (99.99% SLA) | Manual Always On configuration |
| Cost | ~€300/month (S3 tier) | ~€1,400-1,800/month (VM + licensing) |
| Operational Complexity | Low | High (+40-60% overhead) |

**Decision:** Given the preference for fully managed services, FILESTREAM is not recommended. The hybrid storage approach (JSON columns + Azure Blob Storage) provides comparable benefits while remaining fully PaaS-compatible.

**Sources:**
- [SQL Server to Azure SQL Managed Instance Migration - Assessment Rules](https://learn.microsoft.com/en-us/data-migration/sql-server/managed-instance/assessment-rules)
- [T-SQL Differences: SQL Server vs Azure SQL Managed Instance](https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/transact-sql-tsql-differences-sql-server)

### Cache Technology (Option A/C)
**Decision:** Azure Cache for Redis (Standard C2+ tier)
- Distributed cache for multi-instance deployments
- TTL strategy: 5-30 minutes based on query type
- Graceful degradation on cache failures

### Query Strategy (All Options)
**Decision:** Full normalization preserved
- All EPCIS query parameters supported
- No loss of query flexibility
- Redis/blobs used only for response generation

---

## Error Recovery and Compensation Logic (Phase 2B)

### Overview

Phase 2B (Hybrid Storage with Azure Blob Storage) introduces **eventual consistency challenges** because Azure Blob Storage and Azure SQL Database cannot participate in the same distributed transaction. This section documents the error recovery strategy and compensation logic.

### Error Scenarios and Recovery Strategies

```
┌─────────────────────────────────────────────────────────────────┐
│                    CAPTURE REQUEST ARRIVES                       │
│              (XML/JSON document via POST /Capture)               │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
                    ┌────────────────────┐
                    │  Parse & Validate  │
                    │   (in-memory)      │
                    └────────────────────┘
                              │
                              ▼
                    ┌────────────────────┐
                    │ Check Document Size│
                    └────────────────────┘
                              │
                ┌─────────────┴─────────────┐
                │                           │
         < 5 MB │                           │ >= 5 MB
                ▼                           ▼
    ┌──────────────────────┐    ┌──────────────────────┐
    │   INLINE PATH        │    │    BLOB PATH         │
    │  (Fast, Transacted)  │    │  (Two-Phase Commit)  │
    └──────────────────────┘    └──────────────────────┘
                │                           │
                ▼                           ▼
    ┌──────────────────────┐    ┌──────────────────────┐
    │ SQL Transaction:     │    │ PHASE 1:             │
    │ - Save Request       │    │ Upload to Blob       │
    │ - Save Events        │    │ Storage              │
    │ - Store Serialized   │    │                      │
    │   Document in JSON   │    └──────────────────────┘
    │   column             │                │
    └──────────────────────┘                ▼
                │                   ┌────────────────┐
                │                   │ Success?       │
                │                   └────────────────┘
                │                           │
                │                 ┌─────────┴─────────┐
                │                 │                   │
                │               YES                  NO
                │                 │                   │
                │                 ▼                   ▼
                │      ┌──────────────────┐  ┌───────────────┐
                │      │ PHASE 2:         │  │ Return HTTP   │
                │      │ SQL Transaction: │  │ 500 Error     │
                │      │ - Save Request   │  │ (No cleanup   │
                │      │ - Save Events    │  │  needed -     │
                │      │ - Store Blob URI │  │  blob upload  │
                │      │ - Set StorageType│  │  is idempotent│
                │      └──────────────────┘  └───────────────┘
                │                 │
                │                 ▼
                │         ┌────────────────┐
                │         │ Success?       │
                │         └────────────────┘
                │                 │
                │       ┌─────────┴─────────┐
                │       │                   │
                │     YES                  NO
                │       │                   │
                ▼       ▼                   ▼
    ┌──────────────────────┐    ┌──────────────────────┐
    │ Return HTTP 200 OK   │    │ COMPENSATION:        │
    │ (Success)            │    │ - Delete Blob        │
    └──────────────────────┘    │ - Return HTTP 500    │
                                └──────────────────────┘
```

### Detailed Error Scenarios

#### Scenario 1: Inline Storage Path (< 5 MB)

**Error:** SQL transaction fails (constraint violation, timeout, connection error)

**Recovery:**
- Transaction automatically rolled back by SQL Server
- No external state to clean up
- Return HTTP 500 error to client
- Client can retry request

**Result:** Strong consistency guaranteed (ACID transaction)

#### Scenario 2: Blob Storage Path - Phase 1 Failure (>= 5 MB)

**Error:** Blob upload fails (network error, Azure Blob Storage unavailability, quota exceeded)

**Recovery:**
- No SQL transaction started yet
- No cleanup needed (no blob created)
- Return HTTP 500 error to client with error details
- Client can retry request

**Result:** No orphaned resources

#### Scenario 3: Blob Storage Path - Phase 2 Failure (>= 5 MB)

**Error:** SQL transaction fails after successful blob upload

**Recovery (Compensation Logic):**
```csharp
try
{
    // Phase 1: Upload blob
    var blobUri = await _azureBlobService.UploadDocumentAsync(document);

    try
    {
        // Phase 2: SQL transaction
        using var transaction = await _context.Database.BeginTransactionAsync();

        var request = new Request
        {
            StorageType = StorageType.BlobStorage,
            DocumentBlobUri = blobUri,
            // ... other fields
        };

        _context.Requests.Add(request);
        _context.Events.AddRange(events);

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok();
    }
    catch (Exception sqlException)
    {
        // COMPENSATION: Delete orphaned blob
        try
        {
            await _azureBlobService.DeleteBlobAsync(blobUri);
            _logger.LogWarning("Deleted orphaned blob {BlobUri} after SQL transaction failure", blobUri);
        }
        catch (Exception deleteException)
        {
            // Blob deletion failed - orphaned blob will be cleaned up by background service
            _logger.LogError(deleteException,
                "Failed to delete orphaned blob {BlobUri}. Will be cleaned by background service.",
                blobUri);
        }

        throw; // Return HTTP 500 to client
    }
}
catch (Exception blobException)
{
    _logger.LogError(blobException, "Blob upload failed");
    throw; // Return HTTP 500 to client
}
```

**Result:** Best-effort compensation with background cleanup fallback

#### Scenario 4: Orphaned Blob (Compensation Failed)

**Error:** Blob uploaded successfully, SQL transaction failed, compensation deletion also failed

**Recovery (Background Service):**
- Orphaned Blob Cleanup Service (Issue #20) runs every 24 hours
- Identifies blobs not referenced in SQL database
- Deletes blobs older than 7 days (configurable threshold)
- Logs cleanup operations for audit

```csharp
// Simplified background service logic
var allBlobs = await _azureBlobService.ListBlobsAsync();
var referencedUris = await _context.Requests
    .Where(r => r.StorageType == StorageType.BlobStorage)
    .Select(r => r.DocumentBlobUri)
    .ToListAsync();

var orphanedBlobs = allBlobs
    .Where(blob => !referencedUris.Contains(blob.Uri))
    .Where(blob => blob.CreatedOn < DateTime.UtcNow.AddDays(-7));

foreach (var orphan in orphanedBlobs)
{
    await _azureBlobService.DeleteBlobAsync(orphan.Uri);
    _logger.LogInformation("Cleaned up orphaned blob {BlobUri} (age: {Age} days)",
        orphan.Uri,
        (DateTime.UtcNow - orphan.CreatedOn).TotalDays);
}
```

**Result:** Eventually consistent cleanup (within 7-31 days)

### Query Path Error Handling

#### Scenario 5: Blob Download Failure During Query

**Error:** SQL query succeeds, but blob download fails (blob deleted, Azure unavailable, network error)

**Recovery:**
```csharp
try
{
    var request = await _context.Requests
        .Where(r => r.Id == requestId)
        .FirstOrDefaultAsync();

    if (request.StorageType == StorageType.BlobStorage)
    {
        try
        {
            var document = await _azureBlobService.DownloadBlobAsync(request.DocumentBlobUri);
            return Ok(document);
        }
        catch (BlobNotFoundException)
        {
            _logger.LogError("Blob not found: {BlobUri} for Request {RequestId}",
                request.DocumentBlobUri, requestId);
            return StatusCode(500, "Document storage corrupted - blob not found");
        }
        catch (Exception blobException)
        {
            _logger.LogError(blobException, "Failed to download blob {BlobUri}", request.DocumentBlobUri);
            return StatusCode(503, "Temporary storage unavailability - retry later");
        }
    }
    else
    {
        // Inline storage - no external dependency
        return Ok(request.SerializedDocument);
    }
}
catch (Exception ex)
{
    _logger.LogError(ex, "Query failed");
    return StatusCode(500, "Internal server error");
}
```

**Result:**
- HTTP 500 if blob permanently missing (data corruption)
- HTTP 503 if temporary Azure unavailability (client can retry)

### Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Two-phase commit (blob first, SQL second)** | Blob upload is slower; fail fast if blob upload fails before starting SQL transaction |
| **Synchronous compensation (delete blob on SQL failure)** | Immediate cleanup reduces orphaned blob count; background service is fallback only |
| **7-day orphan threshold** | Balances cleanup aggressiveness with debugging/audit window |
| **24-hour cleanup interval** | Low operational cost; orphaned blobs are rare (only on SQL failures) |
| **Idempotent blob upload** | Same blobUri for retry allows client retries without duplicate blobs |
| **HTTP 503 for transient errors, 500 for permanent** | Enables client retry logic vs. permanent failure handling |

### Monitoring and Alerting

**Key Metrics:**
- Blob upload failures (Phase 1 errors)
- SQL transaction failures after blob upload (Phase 2 errors requiring compensation)
- Compensation deletion failures (triggers background cleanup)
- Orphaned blob count (tracked by background service)
- Blob download failures during queries

**Recommended Alerts:**
- Alert if orphaned blob count > 100 (indicates systemic SQL transaction failures)
- Alert if blob download failure rate > 1% (indicates storage corruption or Azure issues)
- Alert if compensation deletion failure rate > 10% (indicates Azure Blob Storage issues)

### Testing Strategy

**Unit Tests:**
- Mock blob service to simulate upload/download failures
- Verify compensation logic executes
- Verify error codes returned to client

**Integration Tests:**
- Use Azurite (Azure Blob Storage emulator)
- Simulate Azure unavailability (stop Azurite mid-transaction)
- Verify orphaned blob cleanup service

**Chaos Testing:**
- Random blob upload failures (1% rate)
- Random SQL transaction failures (1% rate)
- Azure Blob Storage network partitions
- Verify system recovers gracefully

---

## Remaining Open Questions

- **Phase 1 validation results:** What is the actual bottleneck breakdown (serialization % vs SQL query %)?
- **Phase 2 architecture choice:** Option A (Redis), Option B (Blob Storage), or Option C (Hybrid)?
- **Cache hit rate expectations:** What is the expected cache hit rate for typical EPCIS workloads?
- **Phase 3 (Streaming):** XSD validation strategy with `XmlReader` (schema-validating reader vs. separate validation pass)
- **Response format handling:** Store both XML and JSON blobs, or convert on-demand with caching?
- **Compression format:** gzip (standard) vs. brotli (better compression, CPU cost)?
- **Cosmos DB migration criteria:** At what scale (>10M captures/month?) does SQL Server become insufficient?

---

## Role of This Document

This document serves as:
- A record of explored architectural options
- A rationale for selected directions
- A reference during design stress-testing

It complements, but does not replace, the primary architecture proposal.

---

## Related Documents

- [EPCIS Performance Architecture – Hybrid Strategy & Phased Migration](EPCIS_Performance_Architecture_Hybrid_Phasing.md) *(Full technical details and phased approach)*
- [Executive Summary](EPCIS_Performance_Architecture_Executive_Summary.md)
- [Performance Test Validation Strategy](Performance_Test_Validation_Strategy.md) *(How to validate improvements)*
- [Azure Cost Assumptions (West Europe)](EPCIS_Azure_Cost_Assumptions.md) *(Detailed cost breakdowns and estimation worksheet)*

