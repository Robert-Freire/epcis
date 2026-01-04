# EPCIS Performance Architecture — Hybrid Strategy & Phased Migration

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
> Cost estimates in this document (e.g., "~€300/month," "~€360/month") are **examples** based on:
> - West Europe region pricing (January 2025)
> - Assumed low-volume workload (1,000 captures/month, 5% large documents)
> - Azure SQL Database S3 tier (100 DTUs)
>
> **Your actual costs will vary** based on database size, captures/month, document size distribution, and query volume.
> Use the [Cost Estimation Worksheet](EPCIS_Azure_Cost_Assumptions.md#cost-estimation-worksheet) to calculate costs for YOUR deployment.

> **Code References:** File paths and line numbers in this document are accurate as of December 30, 2024. If line numbers have changed, search for the code pattern or function name described.

## Purpose

This document defines the EPCIS performance optimization strategy using a **Hybrid architecture**:
- **Normalized Azure SQL Database tables** for query flexibility (all EPCIS query parameters)
- **Intelligent routing based on document size** (configurable 5 MB threshold):
  - Small documents (< 5 MB) → JSON column (SerializedDocument) in Azure SQL Database
  - Large documents (>= 5 MB) → Azure Blob Storage with URI reference (DocumentBlobUri)
- **Phased implementation** with clear validation gates

The intent is to:
- Improve performance incrementally (2-4 month timeline for Phase 1+2)
- Preserve full EPCIS query semantics (no loss of functionality)
- Make trade-offs explicit at each stopping point
- Enable production deployment after Phase 2 (sufficient for most workloads)
- Support 20,000 event documents (20-100 MB) with automatic blob routing

This is a **design document**, not an implementation plan.

---

## Design Principles

1. **Preserve query semantics**
   - SQL-based querying remains authoritative
   - No loss of EPCIS query expressiveness by default

2. **Remove dominant cost drivers**  
   - Focus on eliminating known hot‑path behaviors (recursive serialization, repeated XML parsing)

3. **Allow staged adoption**
   - Each phase delivers value independently
   - Each phase has clear trade-offs if adopted alone

4. **Operational transparency**
   - Higher performance may imply higher operational cost
   - Those costs must be explicit and measurable

---

## Target Architecture Options

> **Note:** This document targets Azure PaaS deployments (Azure SQL Database + Azure Blob Storage). The final architecture choice (Redis cache, blob storage, or hybrid) is determined after Phase 1 benchmarking validates the actual performance bottleneck.

### Architecture Decision Framework

**All deployments start with:**
- Azure SQL Database normalized tables (all queryable EPCIS fields indexed)
- Full query flexibility preserved
- Phase 1 optimizations applied (O(n²) fixes, database indexes, EF Core optimizations)

**Phase 2 adds ONE of the following, based on Phase 1 validation:**

**Option A: Redis Distributed Cache**
- External Azure Cache for Redis layer
- Caches complete XML/JSON responses (keyed by query parameters)
- **Choose if:** SQL query execution is the bottleneck (>40% of query time)
- **Benefit:** Near-zero latency for cache hits (1-5 ms), bypasses SQL entirely

**Option B-Azure: Hybrid Storage (JSON Columns + Blob Storage)**
- Intelligent routing based on document size (configurable 5 MB threshold)
- **Small documents (< 5 MB):** JSON column (SerializedDocument) in Azure SQL Database (~95% of workload)
- **Large documents (>= 5 MB):** Azure Blob Storage with URI reference (DocumentBlobUri) (edge cases)
- **Choose if:** Serialization is the bottleneck (60-80% of query time)
- **Benefit:** Estimated 90% query improvement for blob-stored documents, handles 20K event requirement

**Option C-Azure: Hybrid Storage + Redis**
- Combines Azure Cache for Redis + Hybrid Storage (JSON columns + Blob Storage)
- **Choose if:** Both bottlenecks are significant, very high query volume
- **Benefit:** 1-5 ms for cache hits, estimated 90% improvement for cache misses

**The Decision:** Phase 1 benchmarking measures the actual bottleneck breakdown to determine which option solves the real problem.

---

## Storage Model Details

This section describes the storage components used across the different architecture options.

### Normalized Relational Data (SQL) - Used in All Options

Stored in SQL Server tables:
- Event identifiers (EventId, unique)
- Temporal fields (eventTime, recordTime)
- Core EPCIS dimensions (bizStep, disposition, readPoint, bizLocation)
- Event metadata (type, action, transformationId)

Purpose:
- Preserve full EPCIS query flexibility
- Enable complex filters, joins, aggregations, and pagination
- Support all EPCIS query parameters (100+ standard parameters)

---

### Hybrid Storage (JSON Columns + Azure Blob Storage) - Option B-Azure Only

Stored using intelligent routing based on document size:

**Small Documents (< 5 MB):**
- Stored in `SerializedDocument` JSON column in Azure SQL Database
- Complete EPCIS document (XML or JSON format preserved)
- Fast retrieval (inline with Request record, no external network call)
- Handles typical documents with 1,000-5,000 events efficiently

**Large Documents (>= 5 MB):**
- Stored in Azure Blob Storage
- Complete EPCIS document as originally submitted (20-100 MB for 20,000 events)
- URI stored in `DocumentBlobUri` column (reference to blob)
- Preserves document context (EPCIS Header, MasterData, original structure)
- Used for 20,000 event requirement and extreme-scale documents

**Storage Type Tracking:**
- `StorageType` enum field: Inline (0) or BlobStorage (1)
- Enables dual-read logic (fetch from JSON column or blob based on type)

Purpose:
- Serve responses without field reconstruction (eliminate O(n²) field formatting)
- Preserve full EPCIS fidelity and extensions
- Reduce CPU cost during queries (estimated ~90% reduction for blob-stored documents)
- Eventual consistency with compensation logic (two-phase commit for blob uploads)
- Support 20,000 event documents without SQL performance degradation

> **Implementation Note:** SerializedDocument, DocumentBlobUri, and StorageType fields do not exist in the current schema. Phase 2B requires EF Core migrations to add these columns to the Request entity. See GitHub issues for detailed implementation examples.

---

## Query Handling Strategy (Option B-Azure - Hybrid Storage)

This strategy applies when hybrid storage (Option B-Azure) is chosen based on Phase 1 validation.

| Query Type | Execution Path |
|-----------|----------------|
| **Event filtering** (EQ_bizStep, GE_eventTime, etc.) | SQL query on normalized Event table |
| **Result identification** | SQL returns list of RequestIds matching criteria |
| **Document retrieval (inline storage)** | Fetch from `SerializedDocument` JSON column (fast, same SQL query) |
| **Document retrieval (blob storage)** | Fetch from Azure Blob Storage via `DocumentBlobUri` (network call) |
| **Format conversion** | Return stored format (XML/JSON) or transform as needed |
| **Compliance exports** | Fetch complete document (inline or blob based on `StorageType`) |

**Query Routing Logic:**
```
1. SQL query filters events (same as always)
2. For each matching Request:
   - Check StorageType field
   - If Inline: Fetch from SerializedDocument column (fast path)
   - If BlobStorage: Fetch from Azure Blob Storage via DocumentBlobUri (blob path)
3. Return response
```

**Trade-off:**
- Storage: Estimated +45% (blended average: 95% inline, 5% blob)
- I/O: Network call for blob-stored documents (5% of queries)
- CPU: Major reduction (estimated ~90% less field reconstruction)
- Memory: Major reduction (no in-memory Field entity object graphs for serialization)
- Cost: €300-410/month (Azure SQL Database S3: €300-400 + Blob Storage: €0-10)

**Uncertainty:**
 - Optimal threshold (default 5 MB) may be workload-dependent
 - Document size distribution will affect actual blob usage percentage

> **Implementation Note:** High-level routing logic shown here. Detailed code examples and blob upload patterns (two-phase commit with compensation) will be documented in GitHub issues to keep this document focused on architecture decisions.

---

## Phased Migration Plan

### Phase 1 — Algorithmic Corrections (Low Risk)

**Intent**  
Remove clearly sub‑optimal behaviors without architectural change.

**Changes**
- Fix configuration bugs:
  - `src/FasTnT.Domain/Constants.cs:6` - CaptureSizeLimit set to 1 KB (may be intentionally conservative, verify deployment requirements)
  - `src/FasTnT.Host/Endpoints/Interfaces/CaptureDocumentRequest.cs:16` - **Critical bug:** Validation compares `ContentLength` to `MaxEventsReturnedInQuery` (20,000) instead of `CaptureSizeLimit`. This effectively limits requests to 20 KB, not the intended limit, and displays incorrect error message
- Remove O(n²) field reconstruction bottleneck:
  - **Code:** `src/FasTnT.Host/Communication/Xml/Formatters/XmlEventFormatter.cs:221-229` (recursive ParentIndex lookup)
  - **Fix:** Pre-group fields by ParentIndex using `Dictionary<int, List<Field>>` → O(n) lookup instead of O(n²) linear scans
  - **Note:** Similar pattern exists in `src/FasTnT.Host/Communication/Json/Formatters/JsonEventFormatter.cs` methods (lines 285-313)
- Add database indexes for query performance:
  - **Code:** `src/FasTnT.Application/Database/EpcisModelConfiguration.cs`
  - **Add:** Indexes on `Event.BusinessStep`, `Event.Disposition`, `Event.EventTime`, `Event.Request.UserId` (multi-tenancy)
  - **Critical:** Index on `Field.ParentIndex` (required for O(n²) fix effectiveness)
- Apply EF Core 10 optimizations:
  - **Compiled Queries:** Pre-compile repeated query patterns in `src/FasTnT.Application/Database/DataSources/EventQueryContext.cs` (10-30% query improvement)
  - **AsNoTrackingWithIdentityResolution:** For complex event queries with shared references

**Benefits**
- Noticeable performance improvement in queries and capture (expected ~40–60%)
- No architectural change
- Zero operational impact

**Note - Dual SaveChangesAsync Pattern:** The dual `SaveChangesAsync` in `CaptureHandler.cs` is EPCIS spec-compliant (RecordTime must be set by repository after insertion) and should not be optimized.

**Phase 1 Validation - Two-Stage Gating Decision:**
Before proceeding to Phase 2, perform two validation analyses to determine the optimal architecture:

**Gate 1: Bottleneck Analysis**
Benchmark and quantify the actual performance bottlenecks:
1. **Serialization Cost:** Measure time spent in `XmlEventFormatter.FormatField` and `JsonEventFormatter.BuildElement`
2. **SQL Query Cost:** Measure time spent in database queries (`EventQueryContext` filtering)
3. **Field Loading Cost:** Measure time spent loading Field entities from database
4. **Comparison:** Calculate percentage breakdown to determine the dominant bottleneck

**Gate 1 Decision Criteria:**
- **If SQL query >40% of query time** → Phase 2A (Redis Cache) - Bypasses SQL entirely on cache hits
- **If serialization 60-80% of query time** → Proceed to Gate 2 (document size analysis)
- **If both are significant** → Phase 2C-Azure (Hybrid Storage + Redis)

**Gate 2: Document Size Distribution Analysis** *(Only if serialization is the bottleneck)*
Analyze production or realistic test workload to understand document size patterns:
1. **Capture document sizes:** Measure size distribution over representative workload (1,000+ captures)
2. **Generate histogram:** Calculate percentages for size buckets (<1MB, 1-5MB, 5-10MB, 10-50MB, >50MB)
3. **Identify P50, P90, P95, P99 percentiles:** Understand typical vs edge-case document sizes

**Gate 2 Decision Criteria:**
- **If high proportion of small documents (>80% < 5 MB)** → Phase 2B-Azure (Hybrid Storage with intelligent routing)
  - **Benefit:** Most documents stay inline (fast SQL access), large documents routed to blob (handles edge cases)
  - **Trade-off:** Dual code paths, but handles typical workload efficiently
- **If high proportion of large documents (>50% >= 5 MB)** → Phase 2B-Azure-Simple (Blob-Only Storage)
  - **Benefit:** Simpler implementation (single code path, all documents to blob)
  - **Trade-off:** Network call for all documents, but avoids dual-path complexity
- **If mixed distribution** → Tune threshold or choose based on operational preference

**Expected Finding:**
- Gate 1: Serialization accounts for 60-80% of query response time (justifying blob storage)
- Gate 2: 90% of documents < 5 MB (justifying intelligent routing with 5 MB threshold)

**Trade-offs if stopping here**
- High peak memory usage remains for large result sets
- Large captures still memory-intensive (blob storage helps, but streaming parser needed for full optimization)

---

## Alternative Phase 2A — Azure Redis Distributed Cache

**Intent**
Provide a caching layer that delivers performance improvements without architectural complexity or schema changes.

**Changes**
- Add Azure Cache for Redis (Standard C2+ tier)
- Implement QueryCacheService using IDistributedCache interface
- Cache complete XML/JSON responses keyed by: `epcis:query:{userId}:{format}:{queryHash}`
- Integrate cache check into query endpoints (GET /events, GET /queries/{name})
- Implement TTL strategy: 5-30 minutes based on query type (time-bounded vs open-ended)
- Graceful degradation: cache failures fall back to normal query path (no user errors)

**Cache Flow:**
```
Query Request → Check Redis → Cache HIT (1-5 ms) OR Cache MISS (SQL query + format + store)
```

**Technology Stack:**
- Azure Cache for Redis (managed service)
- Microsoft.Extensions.Caching.StackExchangeRedis NuGet package
- New service: `src/FasTnT.Application/Services/QueryCacheService.cs`
- Modify: `src/FasTnT.Host/Endpoints/EventsEndpoints.cs`, `QueriesEndpoints.cs`

**Benefits**
- **Cache hit performance:** Near-zero latency (1-5 ms vs 500-2000 ms for query+format)
- **Cache miss performance:** Same as current (no degradation)
- **No schema changes:** No migrations, no storage overhead
- **Simpler implementation:** 2-3 weeks vs 6-10 weeks for blob storage
- **Expected hit rate:** 40-70% for typical workloads (dashboards, compliance queries)

**Operational Cost**
- Azure Redis pricing: €55-110/month (Standard C2-C3)
- No SQL storage overhead (cache is separate)
- Monitoring via Application Insights (hit rate, latency, eviction rate)
- Operational complexity: Low (managed service, no schema changes)

**Trade-offs**
- Eventually consistent (1-30 min TTL, configurable by query type)
- Cache misses have same performance as current (no improvement until cached)
- Operational cost (Azure Redis vs SQL storage for blob approach)
- Cache warm-up period (first queries populate cache)

**Decision Criteria - Based on Phase 1 Bottleneck Analysis:**

The choice between Redis cache and blob storage depends on the **actual bottleneck** identified in Phase 1 validation:

**If Serialization is the Bottleneck (60-80% of query time):**
- **Hybrid Storage (Phase 2B-Azure)** is the right solution → Stores complete documents (inline or blob), eliminates field reconstruction
- Redis cache helps for repetitive queries, but doesn't solve the underlying problem
- Benefit: Estimated 90% improvement for ALL queries (not just cache hits)

**If SQL Query is the Bottleneck (>40% of query time):**
- **Redis Cache (Phase 2A)** is the right solution → Bypasses SQL entirely for cache hits
- Hybrid storage doesn't help → Still requires SQL query to find which documents to retrieve
- Benefit: Near-zero latency for cache hits, no benefit for cache misses

**Additional Considerations:**
- **Repetitive query patterns:** Redis cache more effective (higher hit rate)
- **Unpredictable query patterns:** Hybrid storage more effective (helps all queries, not just hits)
- **Consistency requirements:** Hybrid storage offers eventual consistency with compensation logic, Redis is eventually consistent (TTL)

**Expected Finding:** Phase 1 benchmarks should confirm serialization is 60-80% of query time, validating hybrid storage as the correct approach.

---

### Phase 2B — Hybrid Storage (JSON Columns + Azure Blob Storage)

**Intent**
Remove field reconstruction from the query hot path while supporting 20,000 event documents.

**Changes**
- Store complete EPCIS documents using intelligent routing:
  - **Small documents (< 5 MB):** JSON column (SerializedDocument) in Azure SQL Database
  - **Large documents (>= 5 MB):** Azure Blob Storage with URI reference (DocumentBlobUri)
- Keep existing SQL normalization intact (all queryable fields indexed)
- Serve responses directly from stored documents (no field reconstruction)
- **Deploy with dual-read mode** to handle existing data:
  ```
  Query Logic:
  1. SQL query filters events (same as always)
  2. For each matching Request:
     - Check StorageType field
     - If Inline: Fetch from SerializedDocument column (fast path)
     - If BlobStorage: Fetch from Azure Blob Storage via DocumentBlobUri (blob path)
     - If NULL (legacy): Reconstruct from Field entities (legacy path for existing data)
  3. Return response
  ```

**Schema Changes:**
- Add `SerializedDocument` nvarchar(max) column to Request table
- Add `DocumentBlobUri` nvarchar(500) column to Request table
- Add `StorageType` tinyint column to Request table (0=Inline, 1=BlobStorage)

**Benefits**
- **Query performance:** Eliminates field reconstruction cost (estimated ~90% reduction in query response time for stored documents)
- **Query memory:** Reduces memory usage by avoiding Field entity reconstruction, which otherwise creates large in-memory object graphs
- **Preserves full query flexibility:** All EPCIS query parameters supported
- **Handles 20K events:** Documents with 20,000 events (20-100 MB) automatically routed to blob storage
- **Handles typical documents:** 95% of documents stay inline (fast, no external network call)
- **Eventual consistency:** Two-phase commit with compensation logic for blob uploads
- **Immediate deployment:** No backfill required - graceful degradation for existing data

**Capture Performance Impact**
- **No improvement** in capture time (keeps all current normalization + adds document storage)
- May see slight increase in capture duration (+5-10%) due to JSON column or blob write overhead
- Capture optimization requires Phase 4 (streaming parser)

**Operational Cost**
- Storage: Estimated +45% (blended average: 95% inline in JSON column, 5% in blob storage) for new data only
- Azure Blob Storage: ~€0.03/month for typical workloads (negligible)
- Azure SQL Database: Same tier (S3 recommended, €300-400/month)
- Code complexity: Dual-read paths must be maintained

**Implementation Timeline:**
- 6-10 weeks for focused team

**Trade-offs if stopping here**
- Excellent query performance gains for new data
- Performance inconsistent (new data fast, old data slow)
- Legacy reconstruction code must be maintained
- Capture remains slow for large documents (Phase 4 needed for capture optimization)

**Metrics to Track:**
- Storage type distribution (target: ~95% inline, ~5% blob)
- Blob usage growth (documents >= 5 MB)
- Legacy reconstruction calls (should decrease as new data arrives)
- Query response time distribution (inline vs blob vs legacy)

---

### Optional Phase 3 — Backfill Existing Data

**Only applicable for Phase 2B (Hybrid Storage).** Not needed for Phase 2A (Redis Cache).

**Only pursue if:** Performance consistency needed for historical queries or legacy code retirement desired.

**Changes**
- Background job reconstructs complete documents from existing Field entities
- Writes to SerializedDocument JSON column or Azure Blob Storage (based on size threshold)
- Can prioritize hot data (recently queried) over cold data
- Enables retirement of legacy reconstruction code once complete

**Backfill Strategy Options:**

#### **Option 1: Full Backfill**
```
Migration Job (low priority, background):
1. SELECT RequestId FROM Request WHERE StorageType IS NULL
2. For each request:
   - Reconstruct complete document from Field entities (using current formatters)
   - Check document size
   - If < 5 MB: Write to SerializedDocument column, set StorageType=Inline
   - If >= 5 MB: Write to Azure Blob Storage, set DocumentBlobUri, set StorageType=BlobStorage
3. Run continuously until all requests backfilled
```

**Pros:**
- Eventually achieves uniform performance
- Can retire legacy reconstruction code
- Complete migration

**Cons:**
- Backfill could take days/weeks (millions of requests)
- Requires reconstructing complete documents
- Database load during backfill

**Timeline:** 2-4 weeks to implement job, 1-6 months to complete backfill (depends on data volume)

---

#### **Option 2: Selective Backfill (Recommended)**
```
Migration Strategy:
1. Identify hot data (queries in last 30-90 days)
2. Backfill hot data first (prioritize by query frequency)
3. Skip cold data (never queried or >1 year old)
4. Optional: Full backfill for remaining data (low priority)
```

**Pros:**
- Fast time-to-value (hot data optimized quickly)
- Reduced total backfill cost (skip cold data)
- Flexible stopping point

**Cons:**
- Requires query analytics to identify hot data
- Permanent dual-read for cold data (or eventual full backfill)

**Timeline:** 2-4 weeks for hot data (20-30% of total), 3-6 months for full backfill (optional)

---

#### **Option 3: Cutover Date (No Backfill)**
```
Query Logic:
- Requests where RecordTime < Phase2DeploymentDate: Use legacy reconstruction
- Requests where RecordTime >= Phase2DeploymentDate: Use stored documents (inline or blob)
```

**Pros:**
- No backfill work needed
- Predictable performance (date-based)
- Simpler implementation (no StorageType check)

**Cons:**
- Historical data never benefits from optimization
- Must maintain two code paths indefinitely
- Performance cliff at cutover date

**Timeline:** None (use dual-read date check instead of StorageType check)

---

**Benefits (Phase 3)**
- Uniform performance across all data (old and new)
- Can retire legacy reconstruction code
- Consistent query response times

**Operational Cost**
- Database load during backfill (CPU + I/O for reconstruction)
- Storage: Estimated +45% for backfilled data (blended average: 95% inline, 5% blob)
- 1-6 months of backfill runtime (depends on data volume and strategy)

**Trade-offs if stopping here**
- Requires reconstructing complete documents (ironic - the thing we're avoiding in queries)
- Database load during migration
- Can be skipped if dual-read performance is acceptable

**Metrics to Track:**
- Backfill progress (requests migrated per day)
- Storage type distribution (should approach ~95% inline, ~5% blob)
- Legacy reconstruction calls (should trend to zero)

---

### Optional Phase 4 — Streaming Ingestion (Stop Point D)

**Only pursue if:** Phase 2 insufficient for extreme-scale documents (>500 MB) or memory constraints remain critical.

**Changes**
- Replace DOM parsing (`XDocument.LoadAsync`) with streaming XML parser (`XmlReader`)
- Extract indexes and write FILESTREAM blobs in single pass
- Memory usage: 300-500 MB → 10-20 MB during capture

**Benefits**
- **Capture performance:** Major improvement (~80-86% reduction in capture duration)
  - Eliminates DOM loading (10s saved)
  - Eliminates Field entity creation and DB inserts (30-50s saved)
- **Capture memory:** Eliminates DOM memory spikes (~95% reduction: 300-500 MB → 10-20 MB)
- **Supports extreme-scale:** Documents >500 MB, up to 1 GB+
- **Improves stability:** No memory exhaustion on large captures

**Operational Cost**
- Higher implementation complexity (streaming parser + schema validation)
- More complex XSD validation (schema-validating `XmlReader` or separate pass)
- Harder debugging and validation (can't inspect full DOM)

**Implementation Timeline:**
- 4-6 weeks

**Trade-offs if stopping here**
- Query performance already optimized in Phase 2
- This phase purely for capture optimization

---

## Optional Extension: Asynchronous Capture Endpoint

For HTTP timeout prevention, an optional asynchronous capture endpoint can be added:

**Endpoint:** `POST /capture/async`
- Full XSD validation (synchronous, 5-15s) → 202 Accepted
- Persistence happens asynchronously (FILESTREAM + SQL in single transaction)
- Status endpoint: `GET /capture/{captureId}/status` returns current state

**EPCIS 2.0 Compliance:**
- Spec Section 8.2.7 requires validation before acknowledgment
- Full XSD validation occurs synchronously before 202 response (compliant)
- Only persistence happens asynchronously

**Trade-off:**
- Hides persistence latency but doesn't reduce total work
- Best combined with Phase 2 Hybrid storage

---

## Operational Cost Considerations

| Dimension | Phase 1 (Current) | Phase 2B-Azure (Hybrid Storage) | Change |
|-----------|-------------------|--------------------------------|---------|
| **Storage** | SQL tables only | SQL tables + JSON columns + Azure Blob Storage | Est. +45% (blended) |
| **Disk I/O** | Heavy (complex joins, field reconstruction) | Light (JSON column reads or blob streaming) | -40-60% |
| **Backup** | Azure SQL backup | Azure SQL backup + Azure Blob Storage backup | Minimal added complexity |
| **Monitoring** | Azure SQL only | Azure SQL + Blob Storage metrics | +10-15% complexity |
| **Platform** | Azure SQL Database (PaaS) | Azure SQL Database + Azure Blob Storage (PaaS) | No IaaS overhead |
| **Scalability** | Limited (memory-bound queries) | Improved (handles 20K events, horizontal scale) | Significant gain |

### Cost Summary (Example Workload)

**Assumed workload:**
- 1,000 captures/month
- 5% of captures >= 5 MB (intelligent routing to blob)
- Azure SQL Database S3 tier (100 DTUs)

**Estimated monthly cost (West Europe):**
- Azure SQL Database S3: ~€300/month
- Azure Blob Storage: ~€0.03/month (negligible)
- **Total: ~€300/month**

The Hybrid model with intelligent routing trades **modest storage cost** for **performance and Azure PaaS simplicity**.

---

## Open Design Questions & Deferred Decisions

The following areas have been intentionally left open at this stage.
They represent **known architectural decision points** that should be resolved based on
operational constraints, observed usage patterns, and compliance requirements.

### Question 1: Document Size Distribution Analysis
- **Question:** What percentage of captures are >5 MB, >10 MB, >50 MB, >100 MB?
- **Impact:** Determines whether to use intelligent routing (Hybrid Storage) or blob-only storage (simpler)
- **Data Needed:**
  - Histogram of capture sizes over representative workload (1,000+ captures)
  - P50, P90, P95, P99 percentiles
- **Recommendation:** **MANDATORY Phase 1 Gate 2 validation** - Must be measured before Phase 2 decision
- **Status:** ✅ **Moved to Phase 1 validation** (Gate 2: Document Size Distribution Analysis)
- **Decision Tree:**
  - High proportion small docs (>80% <5MB) → Phase 2B-Azure (Hybrid Storage with intelligent routing)
  - High proportion large docs (>50% >=5MB) → Phase 2B-Azure-Simple (Blob-Only Storage, simpler)
  - Mixed distribution → Tune threshold or choose based on operational preference

### Question 2: Query Pattern Analysis
- **Question:** Are queries predominantly repetitive (dashboards, compliance) or unique (ad-hoc exploration)?
- **Impact:** Determines whether Redis Cache (Phase 2A) or Hybrid Storage (Phase 2B) provides better ROI
- **Data Needed:**
  - Query logs for last 3-6 months
  - Unique query patterns vs. total queries ratio
  - Top 10 most frequent queries
- **Recommendation:** Measure during Phase 1 validation to inform Phase 2 decision
- **Expected Finding:** Mix of both patterns → Phase 2B (Hybrid Storage) recommended

### Question 3: Multi-Region Deployment
- **Question:** Is global distribution needed, or is single-region deployment acceptable?
- **Impact:** Affects blob storage replication strategy
- **Options:**
  - **Single region:** Azure Blob Storage GRS (Geo-Redundant Storage) sufficient
  - **Multi-region:** Consider Azure Front Door + CDN for edge caching
- **Recommendation:** Start single-region, add multi-region if needed
- **Status:** Deferred to Phase 2 implementation

### Question 4: Compliance and Data Retention
- **Question:** Are there data retention requirements (e.g., 7+ years for regulatory compliance)?
- **Impact:** Long retention enables Azure Blob Archive tier cost optimization
- **Options:**
  - Long retention: Azure Blob Archive tier (€0.0018/GB) can reduce costs 90%
  - Lifecycle policies can automatically tier cold data (e.g., >180 days → Archive)
- **Recommendation:** Implement blob lifecycle policies for cost optimization
- **Status:** Deferred to Phase 2 implementation

### Question 5: Storage Strategy Consistency Requirements
- **Question:** How critical is atomic consistency between SQL metadata and blob storage?
- **Context:** Azure Blob Storage does not support distributed transactions with Azure SQL Database
- **Options:**
  - **Strong consistency:** Two-phase commit with compensation logic (+2-3 weeks implementation)
  - **Eventual consistency:** Simple write-ahead pattern (acceptable for EPCIS workloads)
- **Recommendation:** Eventual consistency with compensation logic is sufficient
- **Status:** Resolved - Implement two-phase commit with compensation logic for Phase 2B

### Phase 4 Streaming Ingestion Validation Strategy
- Schema validation approach when using streaming XML parsing (`XmlReader` with schema validation)
- Security constraints (entity expansion, depth limits, size limits) - need tighter controls with streaming
- Performance impact of schema-validating reader vs. DOM validation

### Cosmos DB Migration Trigger Criteria
- At what scale does Azure SQL Database + Hybrid Storage become insufficient? (>10M captures/month? >100 TB?)
- Cost/benefit analysis: Azure SQL Database vs. Cosmos DB RU costs
- Query pattern analysis: When do document queries dominate over relational joins?
- **Note:** Cosmos DB has 2 MB document size limit, making it unsuitable for 20,000-event documents without chunking

### MasterData Queries
- **Status:** Out of scope for this document
- MasterData volumes are typically smaller and may not require blob optimization
- Can be revisited if performance issues are observed

These topics are explicitly acknowledged as future design decisions and do not invalidate
the phased migration strategy described in this document.

---

## Final Recommendation

### Recommended Phased Approach

**Phase 1 (2-3 weeks) - Foundational Optimizations** ✅ **ALL DEPLOYMENTS**
- Algorithmic optimizations (O(n²) fix for XML/JSON formatters)
- Database indexing (BusinessStep, Disposition, EventTime, ParentIndex, UserId)
- EF Core 10 optimizations (compiled queries, identity resolution)
- Configuration bug fixes (CaptureSizeLimit validation)
- **Expected improvement:** 40-60% (updated from 30-40%)
- **Validation gate:** Benchmark serialization vs SQL query breakdown

---

### Phase 2 Decision Point - Choose Based on Deployment Context

After Phase 1 benchmarking, choose one of three paths:

#### **Path A: Azure Redis Cache** (For highly repetitive query patterns)
- **Timeline:** 2-3 weeks implementation, 4-6 weeks total
- **Use if:**
  - ✅ Repetitive query patterns (cache hit rate >50%)
  - ✅ Want operational simplicity (no schema changes)
  - ✅ Budget allows Azure Redis (€55-110/month)
- **Benefits:**
  - Near-zero latency for cache hits (1-5 ms)
  - No storage overhead
  - Faster implementation
- **Trade-offs:**
  - Eventually consistent (1-15 min TTL)
  - Cache misses same as current performance
  - Operational cost (€355-455/month total with Azure SQL)

#### **Path B-Azure: Hybrid Storage (JSON Columns + Blob Storage)** (Recommended)
- **Timeline:** 6-10 weeks implementation, 8-13 weeks total
- **Use if:**
  - ✅ Azure PaaS deployment (cost-effective)
  - ✅ Need to support 20,000 event documents (20-100 MB)
  - ✅ Unpredictable query patterns (low cache hit rate)
  - ✅ Want Azure PaaS simplicity (no IaaS VM management)
- **Benefits:**
  - Estimated 90% query improvement for all queries (not just cache hits)
  - Handles typical documents efficiently (95% inline, fast)
  - Handles 20K event edge cases (5% blob routing, automatic)
  - Eventual consistency with compensation logic
  - Lower total cost (€300-410/month)
- **Trade-offs:**
  - Estimated +45% storage overhead (blended average)
  - Complex implementation (migrations, dual-read logic, blob management)

#### **Path C-Azure: Hybrid Storage + Redis** (For high-scale Azure deployments)
- **Timeline:** 8-13 weeks implementation, 10-16 weeks total
- **Use if:**
  - ✅ Azure PaaS with very high query volume
  - ✅ Need best possible performance
  - ✅ Can manage increased operational complexity
  - ✅ Budget allows both Redis + storage overhead (€355-465/month)
- **Benefits:**
  - Best of both: cache hot data, stored documents for consistency
  - 1-5 ms for cache hits, estimated 90% improvement for cache misses
- **Trade-offs:**
  - Highest operational complexity
  - Highest cost (€355-465/month)

---

### Optional Phases (After Phase 2)

**Phase 3 (optional, 1-6 months):** Backfill existing data
- Only for Path B (blob storage)
- Uniform performance for historical data
- Can skip if dual-read acceptable

**Phase 4 (optional, 4-6 weeks):** Streaming capture parser
- For extreme-scale documents (>500 MB)
- 80-86% capture improvement
- Reduces memory from 300-500 MB to 10-20 MB

---

### Decision Framework

**Start Here:** Always begin with **Phase 1** (all deployments benefit)

**Then Choose:**

| Scenario | Recommended Path | Timeline |
|----------|-----------------|----------|
| Need 20K events support, cost-effective | **Path B-Azure (Hybrid Storage)** ⭐ | 8-13 weeks total |
| Repetitive query patterns, budget allows | **Path A (Redis Cache)** | 4-6 weeks total |
| Very high query volume, budget allows | **Path C-Azure (Hybrid + Redis)** | 10-16 weeks total |
| Budget-constrained | **Phase 1 only** (40-60% improvement) | 2-3 weeks |

**Recommendation for Azure PaaS Deployments:**
1. **Phase 1** (2-3 weeks) - Algorithmic fixes + indexing
2. **Benchmark** (1 week) - Measure serialization vs SQL, document size distribution
3. **Phase 2B-Azure (Hybrid Storage)** (6-10 weeks) - Handles 20K events, €300-410/month
4. **Evaluate:** If query volume increases significantly, add Phase 2A (Redis) later for Phase 2C

**Expected Path:** Phase 1 → Phase 2B-Azure (Hybrid Storage) based on:
- Cost-effective Azure PaaS deployment
- 20,000 event support requirement
- Azure PaaS deployment preference
- Expected serialization bottleneck (60-80% of query time)

---

### Summary

This strategy:
- ✅ Preserves EPCIS query semantics (no functionality loss)
- ✅ Delivers major performance gains (40-60% Phase 1, up to 95%+ with caching)
- ✅ Enables validation gates (benchmark before committing to Phase 2)
- ✅ Offers flexible stopping points based on actual needs
- ✅ Minimizes implementation risk (start simple, add complexity only if needed)

---

## Role of This Document

This document:
- Provides complete technical specification for implementation
- Makes trade-offs explicit at each phase
- Enables informed decision-making with validation gates
- Serves as reference for implementation team

## Related Documents

- [Executive Summary](EPCIS_Performance_Architecture_Executive_Summary.md)
- [Architectural Decision Record](EPCIS_Architectural_Decision_Record.md) *(Historical: alternatives considered)*
- [Performance Test Validation Strategy](Performance_Test_Validation_Strategy.md) *(How to validate improvements)*
- [Azure Cost Assumptions (West Europe)](EPCIS_Azure_Cost_Assumptions.md) *(Detailed cost breakdowns and estimation worksheet)*
