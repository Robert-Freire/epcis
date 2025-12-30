
# EPCIS Architectural Alternatives & Performance Analysis

## Purpose

This document consolidates the architectural alternatives and performance analyses produced during the exploratory phase of the EPCIS performance investigation.  
It is intended as a **design exploration companion** to the main architecture proposal, capturing reasoning, trade-offs, and rejected options.

This document is **analysis-only** and does not propose implementation commitments.

---

## Baseline Performance Summary

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

---

## Architectural Alternatives Evaluated

### Approach 1: Raw XML Blob Storage

#### Description
Persist the original EPCIS XML payload as a blob and reference it from indexed event metadata.

#### Variants
- **Blob-only**: Store XML only, parse on demand
- **Minimal index + blob**: Extract minimal indexed fields and retain full XML

#### Benefits
- Eliminates repeated serialization and reconstruction costs
- Preserves original EPCIS payload with full fidelity
- Version-agnostic and extension-friendly

#### Limitations
- Reduced query flexibility beyond indexed fields
- Potential over-fetching depending on blob granularity
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

### Approach 3: Hybrid (Normalized + Blob)

#### Description
Combine minimal indexed normalization with blob storage and selective custom field persistence.

#### Benefits
- Strong query flexibility
- Blob-based response performance
- Incremental migration path

#### Limitations
- Highest implementation complexity
- Increased storage footprint
- Requires careful schema design

---

### Approach 4: Pre-Serialized Response Cache

#### Description
Cache formatted XML / JSON responses per event.

#### Benefits
- Near-zero response cost on cache hits
- Minimal architectural change

#### Limitations
- Cold-start penalty unchanged
- Cache invalidation complexity
- Increased storage / memory consumption

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
- EPCIS compliance considerations

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

## Comparative Summary

| Approach | Serialization Cost | Memory Usage | Sync Duration | Complexity | Query Flexibility |
|--------|--------------------|--------------|---------------|------------|------------------|
| Current | Baseline | Baseline | Baseline | Low | High |
| Blob + Minimal Index | -90% | -80–95% | -50–70% | Medium | Low |
| Streaming Parser | 0% | -80–85% | -10–20% | High | High |
| Hybrid | -95% | -80–90% | -40–60% | High | Medium–High |
| Response Cache | -100%* | +50% | 0%* | Low | High |
| Async Capture | 0% | 0% | -100%† | Medium | High |
| JSON Fields | -70% | -50% | -30% | Low | Medium |

\* Cache hit dependent  
† Hides latency, does not reduce total work

---

## Key Observations

- Most performance issues stem from **repeated normalization and reconstruction**
- XML DOM parsing dominates peak memory usage
- Query-time formatting is a major CPU bottleneck
- Blob-based approaches directly address root causes rather than symptoms

---

## Open Design Questions

- Optimal blob granularity (per capture vs per event)
- Strategy for complex custom-field queries
- XML vs JSON response handling strategy
- Operational concerns: lifecycle, backup, compression

---

## Role of This Document

This document serves as:
- A record of explored architectural options
- A rationale for selected directions
- A reference during design stress-testing

It complements, but does not replace, the primary architecture proposal.

