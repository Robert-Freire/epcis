
# EPCIS Performance Architecture — Hybrid Strategy & Phased Migration

> **Code References:** File paths and line numbers in this document are accurate as of December 30, 2024. If line numbers have changed, search for the code pattern or function name described.

## Purpose

This document defines the EPCIS performance optimization strategy using a **Hybrid architecture**:
- **Normalized SQL Server tables** for query flexibility (all EPCIS query parameters)
- **SQL Server FILESTREAM blobs** for performance (dual granularity: per-event + per-capture)
- **Phased implementation** with clear validation gates

The intent is to:
- Improve performance incrementally (2-3 month timeline for Phase 1+2)
- Preserve full EPCIS query semantics (no loss of functionality)
- Make trade-offs explicit at each stopping point
- Enable production deployment after Phase 2 (sufficient for most workloads)

This is a **design document**, not an implementation plan.

---

## Design Principles

1. **Preserve query expressiveness**
   - SQL-based querying remains authoritative
   - No loss of EPCIS query semantics by default

2. **Reduce repeated work**
   - Avoid repeated XML parsing and recursive serialization

3. **Allow staged adoption**
   - Each phase delivers value independently
   - Each phase has clear trade-offs if adopted alone

4. **Operational transparency**
   - Higher performance may imply higher operational cost
   - Those costs must be explicit and measurable

---

## Target Architecture: SQL Server Hybrid (Tables + FILESTREAM)

### Core Idea

The architecture combines SQL Server relational and FILESTREAM storage:

**SQL Server Tables (Normalized):**
- All queryable EPCIS fields indexed
- Supports all EPCIS query parameters
- Full query flexibility preserved

**SQL Server FILESTREAM (Dual Blob Storage):**
- **Per-event blobs:** Individual XML fragments (~20 KB each) for selective queries
- **Per-capture blobs:** Complete EPCIS documents (~400 MB) for compliance and bulk retrieval

This keeps SQL Server as both the query engine AND blob storage platform (single platform, transactional consistency), while eliminating expensive recursive serialization.

---

## Storage Model Overview

### Normalized Relational Data (SQL)

Stored in SQL Server tables:
- Event identifiers (EventId, unique)
- Temporal fields (eventTime, recordTime)
- Core EPCIS dimensions (bizStep, disposition, readPoint, bizLocation)
- Event metadata (type, action, transformationId)
- References to FILESTREAM blobs (BlobId for per-event, CaptureId for per-capture)

Purpose:
- Preserve full EPCIS query flexibility
- Enable complex filters, joins, aggregations, and pagination
- Support all EPCIS query parameters (100+ standard parameters)

---

### Blob Storage (SQL Server FILESTREAM)

Stored in SQL Server FILESTREAM (on NTFS, transactional):

**Per-Event Blobs:**
- Individual event XML fragments (~20 KB each, compressed)
- Enables selective retrieval (query 10 events → fetch 10 blobs, not entire document)
- Referenced via `Event.EventBlobId` foreign key

**Per-Capture Blobs:**
- Complete EPCIS document as originally submitted (~400 MB for 20,000 events, compressed)
- Preserves document context (EPCIS Header, MasterData, original structure)
- Referenced via `Request.DocumentBlobId` foreign key
- Used for compliance exports, auditing, full-document retrieval

Purpose:
- Serve XML responses without reconstruction (eliminate O(n²) field formatting)
- Preserve full EPCIS fidelity and extensions
- Reduce CPU cost during queries (~90% reduction)
- Transactional consistency (FILESTREAM participates in SQL transactions)

---

## Query Handling Strategy

| Query Type | Execution Path |
|-----------|----------------|
| **Event filtering** (EQ_bizStep, GE_eventTime, etc.) | SQL query on normalized Event table |
| **Result identification** | SQL returns list of EventIds matching criteria |
| **XML response (small result sets <100 events)** | Fetch per-event blobs via FILESTREAM (10 events → 10 blob reads) |
| **XML response (large result sets >100 events)** | Fetch per-capture blobs, stream response (1 blob read for entire capture) |
| **JSON responses** | Fetch per-event XML blobs → transform to JSON (or cache JSON variant) |
| **Compliance exports** | Fetch per-capture blob (original EPCIS document, unmodified) |

**Query Optimizer Logic:**
```
If result_count < 100:
    Fetch per-event blobs (selective)
Else:
    Fetch per-capture blob (bulk)
```

**Trade-off:**
- Storage: +200-300% (dual blobs)
- I/O: Slight increase for blob retrieval (streaming, not full load)
- CPU: Major reduction (~90% less field reconstruction)
- Memory: Major reduction (no in-memory object graphs for serialization)

---

## Phased Migration Plan

### Phase 1 — Low-Risk Optimizations (Stop Point A)

**Changes**
- Fix configuration bugs (`Constants.cs:6` - CaptureSizeLimit incorrectly set to 1 KB)
- Remove O(n²) field reconstruction bottleneck:
  - **Code:** `XmlEventFormatter.FormatField:221-229` (recursive ParentIndex lookup)
  - **Fix:** Pre-group fields by ParentIndex using `Dictionary<int, List<Field>>` → O(n) lookup instead of O(n²) linear scans
- Reduce EF Core transaction overhead:
  - **Code:** `CaptureHandler.StoreAsync:66` (dual SaveChangesAsync calls)
  - **Fix:** Single SaveChangesAsync with RecordTime set before persistence

**Benefits**
- ~30–40% performance improvement
- No architectural change
- Zero operational impact

**Trade-offs if stopping here**
- High peak memory usage remains
- Large captures still slow and fragile

---

### Phase 2 — Blob-Based Response Path (Stop Point B)

**Changes**
- Store EPCIS XML in SQL Server FILESTREAM storage with dual granularity:
  - **Per-event blobs:** Individual event XML fragments (~20 KB each) for selective queries
  - **Per-capture blobs:** Complete EPCIS document for compliance exports and full retrieval
- Keep existing SQL normalization intact (all queryable fields indexed)
- Serve XML responses directly from FILESTREAM (no reconstruction)
- **Deploy with dual-read mode** to handle existing data:
  ```
  Query Logic:
  1. SQL query filters events (same as always)
  2. For each matching event:
     - Check if EventBlobId exists
     - If YES: Fetch from FILESTREAM (fast path)
     - If NO: Reconstruct from Field entities (legacy path for existing data)
  3. Return response
  ```

**Benefits**
- **Query performance:** Eliminates recursive serialization cost (~90% reduction in query response time for new data)
- **Query memory:** Reduces memory usage (~70-80% for large result sets - no Field entity reconstruction)
- **Preserves full query flexibility:** All EPCIS query parameters supported
- **Transactional consistency:** FILESTREAM participates in SQL transactions (no reconciliation jobs)
- **Supports scale:** 20,000-event documents efficiently (400+ MB, well within 2 GB FILESTREAM limit)
- **Immediate deployment:** No backfill required - graceful degradation for existing data

**Capture Performance Impact**
- **No improvement** in capture time (keeps all current normalization + adds blob writes)
- May see slight increase in capture duration (+5-10%) due to blob write overhead
- Capture optimization requires Phase 3 (streaming parser)

**Operational Cost**
- Storage: +200-300% (dual blob storage: per-event + per-capture) for new data only
- Disk I/O: Minimal increase (streaming access)
- Operational complexity: +10-15% (simpler than external blob storage)
- Code complexity: Dual-read paths must be maintained

**Implementation Timeline:**
- 6-10 weeks for focused team

**Trade-offs if stopping here**
- Excellent query performance gains for new data
- Performance inconsistent (new data fast, old data slow)
- Legacy reconstruction code must be maintained
- Capture remains slow for large documents (Phase 3 needed for capture optimization)

**Metrics to Track:**
- Blob hit rate (target: >80% of queries within 1 month of deployment)
- Legacy reconstruction calls (should decrease as new data arrives)
- Query response time distribution (new vs old data)

---

### Optional Phase 3 — Backfill Existing Data

**Only pursue if:** Performance consistency needed for historical queries or legacy code retirement desired.

**Changes**
- Background job reconstructs XML from existing Field entities
- Writes blobs to FILESTREAM for events where EventBlobId IS NULL
- Can prioritize hot data (recently queried) over cold data
- Enables retirement of legacy reconstruction code once complete

**Backfill Strategy Options:**

#### **Option 1: Full Backfill**
```
Migration Job (low priority, background):
1. SELECT EventId FROM Event WHERE EventBlobId IS NULL
2. For each event:
   - Reconstruct XML from Field entities (using current formatters)
   - Write to FILESTREAM
   - Update Event.EventBlobId
3. Run continuously until all events backfilled
```

**Pros:**
- Eventually achieves uniform performance
- Can retire legacy reconstruction code
- Complete migration

**Cons:**
- Backfill could take days/weeks (millions of events)
- Requires reconstructing XML
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
- Events where RecordTime < Phase2DeploymentDate: Use legacy reconstruction
- Events where RecordTime >= Phase2DeploymentDate: Use FILESTREAM blobs
```

**Pros:**
- No backfill work needed
- Predictable performance (date-based)
- Simpler implementation (no blob existence check)

**Cons:**
- Historical data never benefits from optimization
- Must maintain two code paths indefinitely
- Performance cliff at cutover date

**Timeline:** None (use dual-read date check instead of blob existence check)

---

**Benefits (Phase 3)**
- Uniform performance across all data (old and new)
- Can retire legacy reconstruction code
- Consistent query response times

**Operational Cost**
- Database load during backfill (CPU + I/O for reconstruction)
- Storage: +200-300% for backfilled data
- 1-6 months of backfill runtime (depends on data volume and strategy)

**Trade-offs if stopping here**
- Requires reconstructing XML (ironic - the thing we're avoiding in queries)
- Database load during migration
- Can be skipped if dual-read performance is acceptable

**Metrics to Track:**
- Backfill progress (events migrated per day)
- Blob hit rate (should approach 100% as backfill progresses)
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

| Dimension | Phase 1 (Current) | Phase 2 (FILESTREAM) | Change |
|-----------|-------------------|----------------------|---------|
| **Storage** | SQL tables only | SQL tables + FILESTREAM (per-event + per-capture blobs) | +200-300% |
| **Disk I/O** | Heavy (complex joins, field reconstruction) | Light (streaming blob reads) | -40-60% |
| **Backup** | SQL backup | SQL backup (includes FILESTREAM automatically) | No change in process |
| **Monitoring** | SQL Server only | SQL Server + FILESTREAM disk usage | +10-15% complexity |
| **Platform** | SQL Server | SQL Server (no new tech) | No change |
| **Scalability** | Limited (memory-bound queries) | Improved (streaming, horizontal scale) | Significant gain |

**Storage Cost Breakdown (20,000 events/capture):**
- Normalized SQL: ~50 MB (Event rows, indexes)
- Per-event blobs: ~200 MB compressed (20,000 × 10 KB avg)
- Per-capture blob: ~150 MB compressed (single document)
- **Total: ~400 MB per capture** (vs. 50 MB current)

**Cost Justification:**
- Storage: Cheap ($0.02-0.10/GB/month for SQL Server storage)
- Performance: +300-500% improvement in critical paths
- Operational: Simpler than external blob storage (single platform, single backup)

The Hybrid model with FILESTREAM trades **storage cost** for **performance and operational simplicity**.

---

## Open Design Questions & Deferred Decisions

The following areas have been intentionally left open at this stage.
They represent **known architectural decision points** that should be resolved based on
operational constraints, observed usage patterns, and compliance requirements.

### Storage Strategy (Phase 2)
- **Resolved:** Use SQL Server FILESTREAM for transactional consistency
- FILESTREAM participates in SQL transactions (atomic commit of SQL + blobs)
- No distributed transaction complexity or reconciliation jobs needed
- Single backup strategy (SQL backup includes FILESTREAM)

### Phase 4 Streaming Ingestion Validation Strategy
- Schema validation approach when using streaming XML parsing (`XmlReader` with schema validation)
- Security constraints (entity expansion, depth limits, size limits) - need tighter controls with streaming
- Performance impact of schema-validating reader vs. DOM validation

### Cosmos DB Migration Trigger Criteria
- At what scale does SQL Server FILESTREAM become insufficient? (>10M captures/month? >100 TB?)
- Cost/benefit analysis: SQL licensing vs. Cosmos DB RU costs
- Query pattern analysis: When do document queries dominate over relational joins?

These topics are explicitly acknowledged as future design decisions and do not invalidate
the phased migration strategy described in this document.

---

## Final Recommendation

Adopt a **Hybrid architecture with phased migration**:
- **Phase 1 (2-3 weeks):** Algorithmic optimizations → 30-40% improvement
- **Phase 2 (6-10 weeks):** SQL Server FILESTREAM dual blob storage with dual-read mode → 90% query improvement for new data (no capture improvement)
- **Phase 3 (optional, 1-6 months):** Backfill existing data → uniform performance for all data, retire legacy code
- **Phase 4 (optional, 4-6 weeks):** Streaming parser → 80-86% capture improvement

**Storage Strategy:** SQL Server FILESTREAM (transactional, single platform, proven at scale)

**Deployment Strategy:** Dual-read mode enables immediate Phase 2 deployment without backfill

**Async capture:** Optional enhancement, EPCIS-compliant (full XSD validation before 202)

**Total timeline:** 2-3 months for focused team to reach production-ready Phase 2

This strategy:
- Preserves EPCIS query semantics (no functionality loss)
- Delivers major performance gains (Phase 2 sufficient for most workloads)
- Uses existing SQL Server infrastructure (no new platforms)
- Enables immediate deployment with graceful degradation (dual-read mode)
- Makes backfilling truly optional (Phase 3)

---

## Role of This Document

This document:
- Provides complete technical specification for implementation
- Makes trade-offs explicit at each phase
- Enables informed decision-making with validation gates
- Serves as reference for implementation team

## Related Documents

- [Executive Summary (Short Version)](EPCIS_Performance_Architecture_Executive_Summary_SHORT.md) *(2-page decision-maker version)*
- [Technical Appendix](EPCIS_Performance_Architecture_Executive_Summary.md) *(Extended summary)*
- [Architectural Decision Record](EPCIS_Architectural_Decision_Record.md) *(Historical: alternatives considered)*
- [Performance Test Validation Strategy](Performance_Test_Validation_Strategy.md) *(How to validate improvements)*
