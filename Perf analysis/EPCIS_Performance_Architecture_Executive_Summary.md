
# EPCIS Performance Architecture — Technical Appendix

> **Note:** For a 2-page executive summary, see [EPCIS_Performance_Architecture_Executive_Summary_SHORT.md](EPCIS_Performance_Architecture_Executive_Summary_SHORT.md)
>
> This document provides extended technical details for stakeholders who need implementation specifics.

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

The recommended approach provides **three architecture options**, chosen based on Phase 1 validation bottleneck analysis:

**All deployments start with:**
- Normalized relational storage (SQL Server tables) for queryable EPCIS fields
- Full EPCIS query flexibility preserved
- Phase 1 optimizations applied

**Phase 2 adds ONE of the following:**

**Option A: Azure Cache for Redis**
- Distributed cache layer for query responses
- Choose if: SQL query execution is the bottleneck (>40% of query time)
- Benefit: Near-zero latency for cache hits (1-5 ms)

**Option B: SQL Server FILESTREAM Blob Storage**
- Dual-granularity blob storage (per-event + per-capture)
- Choose if: Serialization is the bottleneck (60-80% of query time)
- Benefit: Eliminates XML reconstruction (90% improvement)

**Option C: Hybrid (Both A + B)**
- Combines Redis cache + FILESTREAM blob storage
- Choose if: Both bottlenecks are significant
- Benefit: Best of both approaches

Key principles:
- SQL remains the authoritative query engine
- EPCIS semantics and expressiveness are preserved by default
- Performance improvements are achieved incrementally and reversibly
- **Phase 1 validation determines the correct Phase 2 path**

---

## Core Benefits

**Phase 2 (Recommended Stopping Point):**

**Option A - Redis Cache:**
- **Query performance:** Near-zero latency on cache hits (1-5 ms vs 500-2000 ms)
- **Implementation:** Simple (2-3 weeks)
- **Trade-off:** Cache miss = baseline performance, operational cost

**Option B - Blob Storage:**
- **Query performance:** ~90% reduction in serialization cost (for new data)
- **Query memory:** ~70-80% reduction in query-time memory usage (for new data)
- **Flexibility:** Full EPCIS payloads retained for compliance
- **Trade-off:** +200-300% storage for new data

**Option C - Hybrid:**
- **Query performance:** 1-5 ms on cache hits, 90% improvement on cache misses
- **Best of both:** Combines all benefits
- **Trade-off:** Higher complexity, both operational costs

**Phase 3 (Optional - Historical Data Backfill):**
- **Uniform performance:** All data (old and new) benefits from blob optimization
- **Code simplification:** Retire legacy reconstruction code after backfill complete

**Phase 4 (Optional - Capture Optimization):**
- **Capture performance:** ~80–86% reduction in synchronous capture duration
- **Capture memory:** ~90–96% reduction in peak memory usage during capture

---

## Phased Migration Strategy

The strategy is deliberately phased to allow early value and controlled adoption. Each phase includes validation gates to ensure value realization before proceeding.

### Phase 1 — Low-Risk Optimizations (2-3 weeks)
- Fix configuration bugs and O(n²) field reconstruction (both XML and JSON formatters)
- Add database indexes on critical query fields
- Apply EF Core 10 optimizations (Compiled Queries, AsNoTrackingWithIdentityResolution)
- **Validation benchmark:** Measure serialization % vs SQL query % to determine Phase 2 path
- **Benefit:** ~40–60% improvement, zero architectural change
- See Hybrid Phasing document for implementation details

### Phase 2 — Choose Architecture Based on Phase 1 Validation

**Phase 2A — Azure Redis Cache (4-6 weeks)** - Choose if SQL query is bottleneck
- Add Azure Cache for Redis layer
- Cache complete query responses with TTL strategy
- **Benefits:** 1-5 ms on cache hits, no schema changes
- **Trade-off:** Cache miss = baseline, operational cost
- See Hybrid Phasing document Phase 2A for details

**Phase 2B — Blob-Based Response Path (6-10 weeks)** - Choose if serialization is bottleneck
- Store XML in SQL Server FILESTREAM (per-event + per-capture blobs)
- Keep all existing SQL normalization (full query flexibility)
- **Deploy with dual-read mode:** New data uses blobs, existing data uses legacy reconstruction
- **Benefits:** ~90% query serialization reduction, ~70-80% memory reduction (new data only)
- **Trade-off:** No capture improvement, +200-300% storage for new data
- See Hybrid Phasing document Phase 2 for detailed implementation strategy

**Phase 2C — Hybrid (Both)** - Choose if both bottlenecks significant
- Combine Redis cache + FILESTREAM blob storage
- Best performance: cache hits 1-5 ms, cache misses 90% faster
- Higher complexity and operational cost

### Optional Phase 3 — Backfill Existing Data (1-6 months) - Only for Phase 2B/2C
- Background job writes blobs for existing data
- **Benefit:** Uniform performance, retire legacy code
- **Trade-off:** Database load during backfill
- Can skip if dual-read performance acceptable
- **Not applicable** for Phase 2A (Redis cache works for all data immediately)

### Optional Phase 4 — Streaming Ingestion (4-6 weeks)
- Replace DOM parsing with streaming (`XmlReader`)
- **Benefits:** ~80-86% capture time reduction, ~95% memory reduction
- Only needed if capture performance problematic after Phase 2

---

## Success Criteria (Phase 2)

- Query response: < 2 seconds for 1,000 events
- Query memory: < 50 MB per query
- No capture regression (baseline ~120s for 100 MB)
- No availability degradation

---

## Optional: Asynchronous Capture

For HTTP timeout prevention, an optional async endpoint can be added:
- `POST /capture/async` → XSD validation (synchronous) → 202 Accepted → background persistence
- EPCIS 2.0 compliant (validation before acknowledgment, persistence asynchronous)
- See Hybrid Phasing document for full specification

---

## Storage Strategy: SQL Server FILESTREAM

**Why FILESTREAM?**
- Transactional consistency (ACID guarantees, no distributed transactions)
- Stores blobs on NTFS filesystem, managed by SQL Server
- Single backup strategy (SQL backup includes FILESTREAM)
- Proven at scale (supports 2 GB blobs, multi-TB databases)

**Cost Impact:**
- Storage: +200-300% for new data (dual blobs: per-event + per-capture)
- Operational complexity: +10-15%
- Performance: +300-500% improvement for queries

---

## Future Consideration: Cosmos DB

**Keep SQL Server FILESTREAM unless:**
- Volume exceeds >10M captures/month, >100 TB storage
- Multi-region active-active replication required
- Query patterns shift from relational to document-centric

**Cosmos DB challenges for EPCIS:**
- 2 MB document size limit (20,000-event documents won't fit)
- No complex joins
- RU-based pricing (expensive for write-heavy workloads)

**Recommendation:** Start with SQL Server. Consider Cosmos DB only at extreme scale 

---

## Alternative Consideration: Azure AI Search

**Consider Azure AI Search (Phase 3+ addition) if:**
- Advanced search requirements (full-text, faceting, geo-spatial) beyond standard EPCIS queries
- Extremely read-heavy workload (1000:1 query-to-capture ratio)
- Multi-tenant analytics or business intelligence dashboards

**Azure AI Search limitations for EPCIS:**
- Does not address capture bottlenecks (parsing, validation, persistence unchanged)
- Data synchronization complexity and eventual consistency risks
- Additional service cost and operational overhead (+30-40% complexity)

**Recommendation:** Start with Option A, B, or C based on Phase 1 validation. Options A/B/C already achieve 90-100% query improvement for typical EPCIS workloads. Azure AI Search provides value mainly if advanced search features are required beyond standard EPCIS query parameters. See [Architectural Decision Record](EPCIS_Architectural_Decision_Record.md) for detailed analysis.

---

## Phase Decision Matrix

Use this matrix to determine optimal stopping point:

| Your Priority | Stop After | Key Benefit | Trade-off |
|--------------|-----------|-------------|-----------|
| Quick wins, minimal risk | **Phase 1** | 40-60% improvement, zero architecture change | Phase 1 validation required to choose Phase 2 path |
| SQL query is bottleneck | **Phase 2A (Redis)** | 1-5 ms cache hits, simple implementation | Cache miss = baseline, operational cost |
| Serialization is bottleneck | **Phase 2B (Blob Storage)** | 90% query improvement (new data) | 3-4x storage cost for new data, dual-read code paths |
| Both bottlenecks significant | **Phase 2C (Hybrid)** | Best of both (1-5 ms cache hits, 90% cache misses) | Higher complexity, both operational costs |
| Historical query consistency | **Phase 3 (Backfill)** | Uniform performance for all data (Phase 2B/2C only) | 1-6 months backfill time, database load |
| Capture performance critical | **Phase 4 (Streaming)** | 80-86% capture time reduction | High implementation complexity |

---

## Recommendation

**Recommended Path:**
1. **Phase 1 immediately** (2-3 weeks) - Low risk, 40-60% improvement + validation benchmark
2. **Phase 2 after validation** - Choose based on Phase 1 bottleneck analysis:
   - **Phase 2A (Redis):** 4-6 weeks if SQL query is bottleneck
   - **Phase 2B (Blob Storage):** 6-10 weeks if serialization is bottleneck
   - **Phase 2C (Hybrid):** 10-16 weeks if both are bottlenecks
3. **Phase 3 optional** - Only if historical data consistency needed (Phase 2B/2C only)
4. **Phase 4 optional** - Only if capture performance problematic

**Total: 2-4 months** for Phase 1+2 (production-ready)

**Expected finding:** Serialization is 60-80% of query time → Phase 2B recommended

---

## Related Documents

- [Executive Summary (Short Version)](EPCIS_Performance_Architecture_Executive_Summary_SHORT.md) *(2-page decision-maker version)*
- [EPCIS Performance Architecture – Hybrid Strategy & Phased Migration](EPCIS_Performance_Architecture_Hybrid_Phasing.md) *(Full technical specification)*
- [EPCIS Architectural Decision Record](EPCIS_Architectural_Decision_Record.md) *(Historical: alternatives considered)*
- [Performance Test Validation Strategy](Performance_Test_Validation_Strategy.md) *(Validation approach using FasTnT.PerformanceTests)*
