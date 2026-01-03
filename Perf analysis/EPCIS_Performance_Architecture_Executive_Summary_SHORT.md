
# EPCIS Performance Architecture — Executive Summary

## The Problem

Current EPCIS implementation has critical performance limitations:
- High peak memory (500+ MB per capture request)
- Long synchronous capture times (2+ minutes for large payloads)
- Expensive query response generation (10-15 seconds for 1,000 events)
- System fragility under large or bursty workloads

These issues limit scalability and operational reliability.

---

## Recommended Solution: Options-Based Architecture

**Phase 1:** All deployments start with optimizations (40-60% improvement) + validation benchmark

**Phase 2:** Choose ONE based on Phase 1 bottleneck analysis:

**Option A: Azure Cache for Redis** - If SQL query is bottleneck (>40%)
- Near-zero latency on cache hits (1-5 ms)
- Simple implementation (4-6 weeks)
- No schema changes

**Option B: SQL Server FILESTREAM Blob Storage** - If serialization is bottleneck (60-80%)
- 90% query improvement for new data
- Dual-read mode (immediate deployment, no backfill)
- +200-300% storage for new data

**Option C: Hybrid (Both)** - If both bottlenecks significant
- Best of both approaches
- Higher complexity

**Expected finding:** Serialization is 60-80% → Option B recommended

---

## Phased Implementation

### Phase 1: Low-Risk Optimizations (2-3 weeks)
- Fix configuration bugs and O(n²) field reconstruction (XML and JSON)
- Add database indexes, EF Core 10 optimizations
- **Validation benchmark:** Measure serialization % vs SQL query %
- **Expected Result:** 40-60% improvement, zero architectural change

### Phase 2: Choose Architecture (4-16 weeks) ⭐ **Recommended Stopping Point**

**Phase 2A: Azure Redis Cache (4-6 weeks)** - If SQL query is bottleneck
- Cache complete query responses with TTL strategy
- **Result:** 1-5 ms cache hits, no schema changes
- **Trade-off:** Cache miss = baseline, operational cost

**Phase 2B: Blob-Based Queries (6-10 weeks)** - If serialization is bottleneck
- Store XML in FILESTREAM, serve queries from blobs
- Deploy with dual-read mode (immediate deployment, no backfill)
- **Result:** ~90% query serialization reduction, ~70-80% memory reduction (new data)
- **Trade-off:** +200-300% storage for new data, no capture improvement

**Phase 2C: Hybrid (10-16 weeks)** - If both bottlenecks significant
- Combine Redis + FILESTREAM
- **Result:** Best of both (1-5 ms cache hits, 90% cache misses)
- **Trade-off:** Higher complexity, both operational costs

### Phase 3: Backfill Existing Data (1-6 months, optional) - Phase 2B/2C only
- Background job writes blobs for historical data
- **Expected Result:** Uniform performance, retire legacy code
- **Decision:** Only if historical query consistency critical

### Phase 4: Streaming Capture (4-6 weeks, optional)
- Replace DOM parsing with streaming parser
- **Expected Result:** ~80-86% capture time reduction, ~95% memory reduction
- **Decision:** Only if capture performance problematic after Phase 2

> **Note:** Performance estimates are based on architectural analysis and preliminary profiling. Actual results will vary based on workload characteristics (event complexity, ILMD depth, extension usage). Phase 1 validation will establish concrete baseline metrics.

---

## Success Criteria (To Be Defined)

Success criteria for Phase 2 should be established after Phase 1 completes and baseline metrics are collected.

**Suggested targets:**
- Query response time: Significant improvement over Phase 1 baseline
- Query memory usage: Measurable reduction for blob-backed queries
- Capture performance: No regression from Phase 1
- Availability: No degradation in success rate

**Metrics collection:** Use existing FasTnT.PerformanceTest project to establish baselines and validate improvements.

---

## Cost Impact (Phase 2)

| Dimension | Current | Phase 2 | Change |
|-----------|---------|---------|--------|
| **Storage** | SQL tables only | SQL tables + FILESTREAM blobs | +200-300% (new data) |
| **Query Performance** | Baseline | Optimized | +300-500% faster |
| **Operational Complexity** | Baseline | Slightly higher | +10-15% |
| **Platform** | SQL Server | SQL Server | No change |

**Storage cost is incremental:** Only new captures incur additional storage. Existing data unchanged until optional backfill (Phase 3).

---

## Decision Matrix

| Your Priority | Stop After | Timeline | Key Benefit |
|--------------|-----------|----------|-------------|
| Quick wins, minimal risk | **Phase 1** | 2-3 weeks | 40-60% improvement, validation benchmark |
| SQL query is bottleneck | **Phase 2A** | 6-9 weeks | 1-5 ms cache hits, simple implementation |
| Serialization is bottleneck | **Phase 2B** | 8-13 weeks | 90% query improvement, immediate deployment |
| Both bottlenecks significant | **Phase 2C** | 12-19 weeks | Best of both approaches |
| Historical data consistency | **Phase 3** | +1-6 months | Uniform performance (Phase 2B/2C only) |
| Capture performance critical | **Phase 4** | +4-6 weeks | 80-86% capture improvement |

---

## Recommendation

**Execute immediately:**
1. **Approve Phase 1** (2-3 weeks) - Low risk, establish baseline metrics + validation benchmark
2. **Analyze Phase 1 validation results** - Determine bottleneck breakdown (serialization % vs SQL query %)
3. **Choose Phase 2 path** - Select Option A, B, or C based on bottleneck analysis
4. **Approve selected Phase 2** (4-16 weeks) - Production-ready after validation gates

**Total: 2-4 months** for focused team to reach production deployment

**Expected path:** Phase 1 → Phase 2B (Blob Storage) based on expected serialization bottleneck

**Defer Phase 3 and 4 decisions** until after Phase 2 operates in production and real-world metrics are collected.

---

## Key Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| Storage cost exceeds budget | Incremental (new data only), backfill optional |
| Dual-read code complexity | Acceptable trade-off for immediate deployment |
| Query performance inconsistent | Expected - new data fast, old data unchanged |
| Capture performance unchanged | Phase 4 available if needed |

---

## Why SQL Server FILESTREAM (Not External Blob Storage)?

- **Transactional consistency:** FILESTREAM participates in SQL transactions (ACID guarantees)
- **Operational simplicity:** Single platform, single backup strategy
- **Proven at scale:** Supports 2 GB blobs, multi-TB databases
- **No distributed transactions:** Eliminates reconciliation complexity

---

## Next Steps

1. **Approve Phase 1** - Begin immediately, establish baseline metrics
2. **Review FasTnT.PerformanceTest project** - Ensure adequate coverage for validation
3. **Approve storage cost budget** - Expect +200-300% for new data (incremental)
4. **Review detailed technical plan** - See *EPCIS Performance Architecture – Hybrid Strategy & Phased Migration*

---

## Related Documents

- [EPCIS Performance Architecture – Hybrid Strategy & Phased Migration](EPCIS_Performance_Architecture_Hybrid_Phasing.md) *(Full technical details)*
- [EPCIS Performance Architecture – Technical Appendix](EPCIS_Performance_Architecture_Executive_Summary.md) *(Extended summary with implementation details)*
- [EPCIS Architectural Decision Record](EPCIS_Architectural_Decision_Record.md) *(Historical: alternatives considered, decisions made)*
- [Performance Test Validation Strategy](Performance_Test_Validation_Strategy.md) *(How to use FasTnT.PerformanceTests for validation)*

