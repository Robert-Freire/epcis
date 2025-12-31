
# EPCIS Performance Architecture — Executive Summary

## The Problem

Current EPCIS implementation has critical performance limitations:
- High peak memory (500+ MB per capture request)
- Long synchronous capture times (2+ minutes for large payloads)
- Expensive query response generation (10-15 seconds for 1,000 events)
- System fragility under large or bursty workloads

These issues limit scalability and operational reliability.

---

## Recommended Solution: SQL Server Hybrid Architecture

**Approach:**
- **SQL Server tables** for queryable EPCIS fields (preserve full query flexibility)
- **SQL Server FILESTREAM** for XML storage (eliminate serialization cost)
- **Dual-read mode** for immediate deployment (no backfill required)

**Key Innovation:** Deploy Phase 2 immediately - new data uses blobs, existing data continues working with legacy code paths.

---

## Phased Implementation

### Phase 1: Low-Risk Optimizations (2-3 weeks)
- Fix configuration bugs and O(n²) field reconstruction
- **Expected Result:** 30-40% improvement, zero architectural change

### Phase 2: Blob-Based Queries (6-10 weeks) ⭐ **Recommended Stopping Point**
- Store XML in FILESTREAM, serve queries from blobs
- Deploy with dual-read mode (immediate deployment, no backfill)
- **Expected Result:** ~90% query serialization reduction, ~70-80% memory reduction (for new data)
- **Trade-off:** +200-300% storage for new data, no capture improvement

### Phase 3: Backfill Existing Data (1-6 months, optional)
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
| Quick wins, minimal risk | **Phase 1** | 2-3 weeks | 30-40% improvement, zero architecture change |
| Query performance critical | **Phase 2** | 2-3 months | 90% query improvement, immediate deployment |
| Historical data consistency | **Phase 3** | 3-9 months | Uniform performance for all data |
| Capture performance critical | **Phase 4** | 4-12 months | 80-86% capture improvement |

---

## Recommendation

**Execute immediately:**
1. **Approve Phase 1** (2-3 weeks) - Low risk, establish baseline metrics
2. **Define Phase 2 success criteria** - Based on Phase 1 baseline results
3. **Approve Phase 2** (6-10 weeks) - Production-ready after validation gates

**Total: 2-3 months** for focused team to reach production deployment

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

