
# EPCIS Performance Architecture — Executive Summary

## Background

The current EPCIS implementation exhibits significant performance and scalability limitations
when processing large EPCIS documents and executing complex queries. These issues manifest as:

- High peak memory usage during capture (hundreds of MB per request)
- Long synchronous capture times (up to several minutes for large payloads)
- Expensive query response generation due to repeated reconstruction and serialization
- Increased operational fragility under large or bursty workloads

A detailed technical analysis of these issues is documented in the companion documents:
- **EPCIS Performance Architecture – Hybrid Strategy & Phased Migration**
- **EPCIS Architectural Alternatives Analysis**

This summary presents the **recommended architectural direction and migration strategy** derived
from that analysis.

---

## Architectural Direction

The recommended approach is a **SQL Server Hybrid Architecture**, combining:

- **Normalized relational storage (SQL Server tables)** for queryable EPCIS fields
- **SQL Server FILESTREAM blobs** with dual granularity:
  - Per-event XML fragments for selective queries
  - Per-capture documents for compliance and full retrieval

Key principles:
- SQL remains the authoritative query engine
- EPCIS semantics and expressiveness are preserved by default
- Performance improvements are achieved incrementally and reversibly

---

## Core Benefits

**Phase 2 (Recommended Stopping Point):**
- **Query performance:** ~90% reduction in serialization cost by serving XML from blobs (for new data)
- **Query memory:** ~70-80% reduction in query-time memory usage (for new data)
- **Flexibility:** Full EPCIS payloads retained for compliance and future needs
- **Safety:** No loss of query capability - all EPCIS query parameters supported
- **Immediate deployment:** Dual-read mode - no backfill required

**Phase 2B (Optional - Historical Data Backfill):**
- **Uniform performance:** All data (old and new) benefits from blob optimization
- **Code simplification:** Retire legacy reconstruction code after backfill complete

**Phase 3 (Optional - Capture Optimization):**
- **Capture performance:** ~80–86% reduction in synchronous capture duration
- **Capture memory:** ~90–96% reduction in peak memory usage during capture

---

## Phased Migration Strategy

The strategy is deliberately phased to allow early value and controlled adoption. Each phase includes validation gates to ensure value realization before proceeding.

### Phase 1 — Low-Risk Optimizations
**Changes:**
- Fix configuration bugs (`Constants.cs:6` - CaptureSizeLimit incorrectly set to 1 KB)
- Remove O(n²) field reconstruction (`XmlEventFormatter.FormatField:221-229`)
  - Pre-group fields by ParentIndex using `Dictionary<int, List<Field>>`
- Reduce EF Core transaction overhead (`CaptureHandler.StoreAsync:66`)
  - Single SaveChangesAsync instead of dual calls

**Benefits:**
- ~30–40% improvement in query response time and capture duration
- No architectural change
- Zero operational impact

**Validation Gate:**
- [ ] >30% improvement verified in production
- [ ] No regressions in query functionality
- [ ] Decision: Proceed to Phase 2 or stop here

### Phase 2 — Blob-Based Response Path (Query Optimization)
- Store EPCIS XML in SQL Server FILESTREAM storage
  - **Per-event blobs:** Individual event XML fragments for selective queries
  - **Per-capture blobs:** Complete EPCIS document for compliance and full-document retrieval
- **Keep all existing SQL normalization** (all Event + Field entities persisted)
- Serve XML responses directly from blobs
- **Deploy with dual-read mode:**
  - Query checks if EventBlobId exists for each event
  - If YES: Fetch from FILESTREAM (fast path)
  - If NO: Reconstruct from Field entities (legacy path for existing data)
  - New captures immediately benefit, existing data continues to work

**Performance Gains:**
- **Query:** ~90% reduction in serialization cost, ~70-80% memory reduction (for new data)
- **Capture:** No improvement (keeps current normalization + adds blob writes)

**Full query flexibility preserved**

**Key Design Decisions:**
- **Blob Granularity:** Both per-event AND per-capture (dual storage for different use cases)
- **Transaction Model:** SQL Server FILESTREAM provides ACID transactions (no distributed transaction complexity)
- **Storage Technology:** SQL Server FILESTREAM (see Storage Strategy section below)
- **Deployment Strategy:** Dual-read mode enables immediate deployment without backfill

**Validation Gate:**
- [ ] Query pattern analysis confirms read-heavy workload (>70% reads)
- [ ] Storage cost approved for 3-4x increase (dual blob storage for new data)
- [ ] Success criteria met (see below)
- [ ] Decision: Production deployment or further optimization

### Optional Phase 2B — Backfill Existing Data
**Only pursue if:** Performance consistency needed for historical queries or legacy code retirement desired.

- Background job reconstructs XML from existing Field entities
- Writes blobs to FILESTREAM for events where EventBlobId IS NULL
- Can prioritize hot data (recently queried) over cold data
- Enables retirement of legacy reconstruction code once complete

**Performance Gains:**
- Uniform performance across all data (old and new)
- Eventually eliminates dual-read code paths

**Operational Cost:**
- Database load during backfill (low priority background job)
- 1-6 months to complete (depends on data volume)

**Trade-offs:**
- Requires reconstructing XML (ironic - the thing we're avoiding)
- Can be skipped if dual-read performance is acceptable

### Optional Phase 3 — Streaming Ingestion (Capture Optimization)
**Only pursue if:** Capture performance remains problematic (large documents, timeout issues).

- Replace DOM-based XML parsing with streaming (`XmlReader`)
- **Major capture improvements:**
  - ~80-86% reduction in capture duration (eliminates DOM load + Field parsing)
  - ~95% reduction in capture memory (300-500 MB → 10-20 MB)
- Supports extreme-scale documents (>500 MB)

Each phase is a valid stopping point with clearly defined benefits and trade-offs.

---

## Success Criteria

Define measurable targets before implementing Phase 2:

### Must Achieve (Phase 2 - Query Optimization)
- **Query response:** < 2 seconds for 1,000 events with full XML serialization
- **Query memory:** < 50 MB per query request
- **Capture time:** No regression (accept current baseline ~120s for 100 MB document)
- **Availability:** No degradation in HTTP 200 success rate

### Stretch Goals (Phase 3 - Capture Optimization)
- **Capture time:** < 30 seconds for 100 MB document (5,000 events)
- **Capture memory:** < 50 MB per capture request
- **Extreme scale:** Support 500 MB documents without timeout

---

## Optional Asynchronous Capture

To address HTTP timeouts and improve user experience, an **additive asynchronous capture endpoint**
may be introduced:

- `POST /capture/async` → **full XSD validation** (synchronous) → 202 Accepted
- Background: FILESTREAM persistence (XML to filesystem) + SQL indexing + notifications
- `GET /capture/{captureId}/status` exposes progress and outcome

**EPCIS Compliance:**
- EPCIS 2.0 spec Section 8.2.7 requires validation before acknowledgment
- **Full XSD validation occurs synchronously** before 202 response (spec-compliant)
- **Persistence happens asynchronously** (FILESTREAM blobs + SQL indexes in single ACID transaction)
- Balances spec compliance with timeout prevention

**Timeline:**
- Validation: 5-15 seconds (synchronous, in HTTP request)
- Persistence: 10-60 seconds (asynchronous, background job with single SQL transaction)
- User sees 202 after validation completes

This improves reliability and UX without altering existing synchronous behavior.
Async capture hides persistence latency while maintaining validation guarantees.

---

## Storage Strategy: SQL Server FILESTREAM

### Why SQL Server FILESTREAM?

**Decision:** Use SQL Server FILESTREAM for Phase 2 blob storage.

**Rationale:**
1. **Transactional Consistency:** FILESTREAM participates in SQL transactions (ACID guarantees)
   - Eliminates distributed transaction complexity
   - Atomic commit of SQL indexes + XML blobs
   - No reconciliation jobs needed

2. **Performance:** Optimized for large objects
   - Stores blobs on NTFS (not in SQL pages)
   - Streaming access (no full load into memory)
   - Supports compression
   - Efficient for 20,000-event documents (400+ MB)

3. **Operational Simplicity:**
   - Single backup strategy (SQL backup includes FILESTREAM)
   - Existing SQL Server infrastructure
   - No new platform to learn/manage
   - Integrated monitoring

4. **Capacity:** Proven at scale
   - Supports multi-TB FILESTREAM databases
   - Single blob limit: 2 GB (far exceeds 400 MB typical document)
   - 20,000 events/capture well within capacity

**Estimated Costs (Phase 2 with FILESTREAM):**
- Storage: +200-300% (dual blob storage: per-event + per-capture) for NEW data only
  - Existing data: No storage increase until backfilled (optional)
  - Incremental growth as new captures arrive
- Disk I/O: Minimal increase (streaming, not loading into memory)
- Operational complexity: +10-15% (simpler than external blob storage)
- Performance: +300-500% improvement for queries hitting blob storage (new data immediately, old data after backfill)

---

## Future Scalability: Cosmos DB Consideration

### When to Consider Cosmos DB Migration

**Keep SQL Server FILESTREAM if:**
- Document volume < 1M captures/month
- Query patterns remain relational (complex joins, aggregations)
- Team expertise is SQL Server
- Geographic distribution not required

**Consider Cosmos DB migration if:**
- **Extreme scale:** >10M captures/month, >100 TB storage
- **Geographic distribution:** Multi-region active-active replication required
- **Schema flexibility:** Frequent EPCIS extension changes
- **Query patterns shift:** Document-centric access dominates over relational queries

### Cosmos DB Trade-offs

**Advantages:**
- Unlimited horizontal scale (auto-partitioning)
- Global distribution (99.999% availability)
- Flexible schema (native JSON support)
- Multiple query APIs (SQL, MongoDB, Gremlin)

**Challenges for EPCIS:**
- **Document size limits:** 2 MB per item (4 MB with bulk executor)
  - 20,000-event documents (~400 MB) require partitioning into multiple documents
  - Complexity: event-level storage only, capture document reconstructed on demand
- **Query semantics:** No complex joins (denormalization required)
- **Cost model:** RU-based pricing (can be expensive for write-heavy workloads)
- **Migration effort:** Significant rewrite of query layer

### Migration Path (if needed)

**Phase 2A (SQL Server FILESTREAM):**
- Implement blob storage with FILESTREAM
- Validate performance meets targets
- Operate for 6-12 months, collect metrics

**Phase 2B (Cosmos DB evaluation):**
- **Only if** SQL Server shows saturation (>80% capacity, query latency degradation)
- Prototype: Dual-write to Cosmos DB (shadow deployment)
- Compare cost, performance, complexity
- Gradual migration: new captures → Cosmos DB, existing → SQL Server

**Recommendation:** Start with SQL Server FILESTREAM. Migrate to Cosmos DB only when scale demands it (likely 2+ years out).

---

## Phase Decision Matrix

Use this matrix to determine optimal stopping point:

| Your Priority | Stop After | Key Benefit | Trade-off |
|--------------|-----------|-------------|-----------|
| Quick wins, minimal risk | **Phase 1** | 30-40% improvement, zero architecture change | High memory usage remains |
| Query performance critical | **Phase 2** | 90% query serialization reduction, 70-80% query memory reduction (new data) | 3-4x storage cost for new data, capture unchanged, dual-read code paths |
| Historical query consistency | **Phase 2B** | Uniform performance for all data, retire legacy code | 1-6 months backfill time, database load |
| Capture performance critical | **Phase 3** | 80-86% capture time reduction, 95% capture memory reduction | High implementation complexity |

---

## Recommendation

**Commit to Phase 1 immediately.** Minimal risk, clear value.

**Commit to Phase 2 after:**
1. Phase 1 validation gate passed
2. Success criteria defined and approved
3. Storage cost budget approved (expect 3-4x increase)

**Implementation Timeline (Phase 1 + 2):**
- Phase 1: 2-3 weeks
- Phase 2: 6-10 weeks
- **Total: 2-3 months** for focused team

**Make Phase 2B optional:** Only if historical query performance consistency is required or legacy code retirement is desired (1-6 months backfill time).

**Make Phase 3 optional:** Only if capture performance remains problematic after Phase 2 (large documents timing out, memory pressure during ingestion).

This approach:
- Addresses root performance bottlenecks incrementally
- Preserves EPCIS correctness and flexibility
- Enables informed decision-making at each phase
- Avoids irreversible architectural commitments
- Allows early stopping if goals are met

---

## Related Documents

- [EPCIS Performance Architecture – Hybrid Strategy & Phased Migration](EPCIS_Performance_Architecture_Hybrid_Phasing.md)
- [EPCIS Architectural Alternatives Analysis](EPCIS_Architectural_Alternatives_Analysis.md)
