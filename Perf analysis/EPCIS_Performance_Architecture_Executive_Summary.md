
# EPCIS Performance Architecture — Executive Summary

## Background

The current EPCIS implementation exhibits significant performance and scalability limitations
when processing large EPCIS documents and executing complex queries. These issues manifest as:

- High peak memory usage during capture (hundreds of MB per request)
- Long synchronous capture times (up to several minutes for large payloads)
- Expensive query response generation due to repeated reconstruction and serialization
- Increased operational fragility under large or bursty workloads

A detailed technical analysis of these issues is documented in:

**“EPCIS Performance Architecture – Analysis & Design Options”**

This summary presents the **recommended architectural direction and migration strategy** derived
from that analysis.

---

## Architectural Direction

The recommended approach is a **Hybrid EPCIS Architecture**, combining:

- **Normalized relational storage (SQL Server)** for queryable EPCIS fields  
- **Per-event EPCIS XML blobs** for fidelity-preserving, low-cost response generation  
- **Optional selective denormalization** for frequently queried custom fields  

Key principles:
- SQL remains the authoritative query engine
- EPCIS semantics and expressiveness are preserved by default
- Performance improvements are achieved incrementally and reversibly

---

## Core Benefits

- **Read performance:** Up to ~90% reduction in serialization cost by serving XML from blobs
- **Memory efficiency:** Significant reduction in query-time memory usage
- **Flexibility:** Full EPCIS payloads retained for compliance and future needs
- **Safety:** No forced loss of query capability; trade-offs are explicit and optional

---

## Phased Migration Strategy

The strategy is deliberately phased to allow early value and controlled adoption.

### Phase 1 — Low-Risk Optimizations
- Algorithmic and configuration fixes
- ~30–40% performance improvement
- No architectural change

### Phase 2 — Blob-Based Response Path
- Store EPCIS XML per event in blob storage
- Serve XML responses directly from blobs
- Major query-time performance gains
- Full query flexibility preserved

### Optional Phase 3 — Hybrid Capture Optimization
- Reduce capture-time normalization for non-critical fields
- Trade immediate SQL query flexibility for faster ingestion
- Full data retained in blobs; flexibility can be restored via projections

### Optional Phase 4 — Streaming Ingestion
- Replace DOM-based XML parsing with streaming
- Eliminate large memory spikes during capture

Each phase is a valid stopping point with clearly defined benefits and trade-offs.

---

## Optional Asynchronous Capture

To address HTTP timeouts and improve user experience, an **additive asynchronous capture endpoint**
may be introduced:

- `POST /capture/async` → immediate acknowledgment (202 Accepted)
- Background ingestion with status tracking
- `GET /capture/{captureId}/status` exposes progress and outcome

This improves reliability and UX without altering existing synchronous behavior.
Async capture hides latency but does not eliminate ingestion cost, and is best combined
with Hybrid storage.

---

## Operational Considerations

The Hybrid approach trades simplicity for scalability:

- Increased storage footprint (SQL + Blob)
- Additional monitoring and lifecycle management
- Improved throughput, stability, and long-term scalability

These costs are explicit and manageable.

---

## Recommendation

Adopt the **Hybrid EPCIS Architecture with phased migration**, using SQL Server as the primary
query engine and blob storage as a fidelity-preserving response mechanism.

Use asynchronous capture as an optional, orthogonal enhancement where required.

This approach:
- Addresses root performance bottlenecks
- Preserves EPCIS correctness and flexibility
- Enables incremental adoption with clear decision points
- Avoids irreversible architectural commitments

---

## Related Documents

- (**EPCIS Performance Architecture – Analysis & Design Options**)[EPCIS_Performance_Architecture.md]
- (**EPCIS Performance Architecture – Hybrid Strategy & Phased Migration**)[EPCIS_Hybrid_Phased_Architecture.md]
- (**EPCIS Architectural Analysis**)[EPCIS_Architecture_Analysis.md]
