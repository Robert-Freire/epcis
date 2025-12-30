
# EPCIS Performance Architecture — Hybrid Strategy & Phased Migration

## Purpose

This document refines the EPCIS performance proposal to explicitly preserve **query flexibility**
by favoring a **Hybrid architecture** (normalized SQL + blob storage), combined with an **optional,
phased introduction of asynchronous capture processing**.

The intent is to:
- Improve performance incrementally
- Keep SQL-based querying as a first-class capability
- Make trade-offs explicit at each stopping point
- Allow the system to stop at intermediate phases if required

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

## Target Architecture: Hybrid (Normalized + Blob)

### Core Idea

The Hybrid approach combines:
- **Normalized relational storage** for queryable EPCIS fields
- **Raw EPCIS XML blobs** for fidelity-preserving responses
- **Selective denormalization** for frequently queried custom fields

This keeps SQL Server (or equivalent RDBMS) as the primary query engine,
while eliminating the most expensive serialization paths.

---

## Storage Model Overview

### Normalized Relational Data (SQL)

Stored in SQL Server:
- Event identifiers
- Temporal fields (eventTime, recordTime)
- Core EPCIS dimensions (bizStep, disposition, readPoint, bizLocation)
- Frequently queried custom fields (configurable subset)

Purpose:
- Preserve EPCIS query flexibility
- Enable complex filters, joins, and pagination

---

### Blob Storage

Stored externally (or as DB BLOB):
- Original EPCIS XML payload, stored **per event** (compressed)

Purpose:
- Serve XML responses without reconstruction
- Preserve full EPCIS fidelity and extensions
- Reduce CPU cost during queries

Each persisted EPCIS event is associated with exactly one immutable blob payload via a SQL reference.

---

## Query Handling Strategy

| Query Type | Execution Path |
|-----------|----------------|
| Core EPCIS filters | SQL (normalized tables) |
| Custom-field heavy queries | SQL + optional denormalized tables |
| XML response generation | Blob retrieval |
| JSON responses | Blob → transform (cached if needed) |

Trade-off:
- Slight increase in I/O cost for blob retrieval
- Major reduction in CPU and memory usage

---

## Phased Migration Plan

### Phase 1 — Low-Risk Optimizations (Stop Point A)

**Changes**
- Fix configuration and correctness issues
- Remove O(n²) field reconstruction (algorithmic optimization to O(n) by pre-grouping by ParentId)
- Reduce EF Core transaction overhead

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
- Store the full EPCIS payload in blob storage (per event)
- Keep existing SQL normalization intact
- Serve XML responses directly from blobs when possible

**Benefits**
- Eliminates recursive serialization cost (~90%)
- Preserves full query flexibility
- Improves read performance significantly

**Operational Cost**
- Additional storage (blob + SQL)
- Blob lifecycle management

**Trade-offs if stopping here**
- Capture path still expensive
- Peak memory during ingestion unchanged

---

### Optional Phase 3 — Hybrid Capture Optimization (Stop Point C)

**Changes**
- Reduce capture-time normalization, deliberately trading immediate SQL query flexibility for non-projected fields in exchange for improved ingestion performance, while preserving full EPCIS data in blob storage for future query or projection needs.
- Optionally introduce query-optimized projections in SQL Server for indexed and frequently queried fields, based on observed query patterns, while retaining the full normalized EPCIS model.
- Blob storage is used solely to persist the full EPCIS payload; SQL remains the authoritative query engine.

**Benefits**
- Peak memory reduction of ~70–85% (depending on normalization strategy)
- Faster ingestion
- Retains most query expressiveness

**Operational Cost**
- Schema complexity increases
- Requires governance of indexed custom fields

**Trade-offs if stopping here**
- Some niche queries may require additional indexing later
- Increased operational complexity

---

### Optional Phase 4 — Streaming Ingestion (Stop Point D)

**Changes**
- Replace DOM parsing with streaming XML parser
- Extract indexes and write blob in a single pass

**Benefits**
- Eliminates DOM memory spikes
- Improves ingestion stability for very large files

**Operational Cost**
- Higher implementation complexity
- Harder debugging and validation

**Trade-offs if stopping here**
- Query flexibility unchanged
- Serialization costs already solved in earlier phases

---

## Optional Extension: Asynchronous Capture Endpoint

### Rationale

Large EPCIS documents can exceed HTTP and reverse-proxy timeout limits.
Asynchronous capture decouples request acceptance from ingestion processing.

This endpoint is **additive** and does not modify the behavior or contract of existing synchronous capture endpoints.

---

### Proposed Model

Add a new endpoint:

```
POST /capture/async
→ 202 Accepted
→ Returns CaptureId
```

Processing occurs in background:
- Parse
- Validate
- Persist
- Notify

---

### Capture Status Endpoint

To support asynchronous processing, a status endpoint is introduced to allow clients
to track ingestion progress and outcomes.

```
GET /capture/{captureId}/status
```

#### Possible States

| Status | Meaning |
|------|--------|
| Received | Request accepted, not yet processed |
| Processing | Ingestion in progress |
| Completed | Ingestion completed successfully |
| Failed | Ingestion failed (validation or processing error) |

#### Example Response

```json
{
  "captureId": "abc123",
  "status": "Processing",
  "submittedAt": "2025-01-10T10:12:00Z",
  "lastUpdatedAt": "2025-01-10T10:12:45Z",
  "error": null
}
```

**Contract Guarantees**
- Events become queryable only after status = `Completed`
- Failed captures do not expose partial data
- Status transitions are idempotent and monotonic

---

### Trade-offs by Adoption Stage

| Adoption Stage | User Experience | System Load | Complexity |
|---------------|-----------------|-------------|------------|
| Not adopted | Long blocking calls | High | Low |
| Async only | Immediate response | Same total work | Medium |
| Async + Hybrid | Immediate + faster processing | Lower | High |

Important:
- Async capture **hides latency**, it does not eliminate cost
- Best combined with Hybrid storage

---

## Operational Cost Considerations

| Dimension | Cost Impact |
|--------|-------------|
| Storage | Increased (SQL + Blob) |
| Monitoring | Blob lifecycle + async jobs |
| Backups | Dual storage strategy |
| Complexity | Higher, but controlled |
| Scalability | Significantly improved |

The Hybrid model trades **infrastructure simplicity** for **performance and scalability**.

---

## Open Design Questions & Deferred Decisions

The following areas have been intentionally left open at this stage.
They represent **known architectural decision points** that should be resolved based on
operational constraints, observed usage patterns, and compliance requirements.

### Source of Truth and Ingestion Consistency
- Atomicity guarantees between SQL persistence and blob storage
- Failure and retry semantics when one persistence layer succeeds and the other fails
- Need for ingestion state tracking and reconciliation jobs

### Phase 3 Operating Mode (Normalization Strategy)
- Whether reduced normalization implies:
  - deferred full normalization (background processing), or
  - selective normalization with fallback query paths
- Impact on existing EPCIS query endpoints and subscriptions

### Streaming Ingestion Validation Strategy
- Schema validation approach when using streaming XML parsing
- Security constraints (entity expansion, depth limits, size limits)
- Compliance implications of synchronous vs asynchronous validation

### Query-Optimized Projection Governance
- Criteria for promoting fields to query-optimized projections
- Backfill strategy for historical EPCIS payloads
- Versioning and rollback of projection schemas

These topics are explicitly acknowledged as future design decisions and do not invalidate
the phased migration strategy described in this document.

---

## Final Recommendation

Adopt a **Hybrid architecture with phased migration**, using SQL Server as the primary query engine
and blob storage as a fidelity-preserving response path.

Introduce asynchronous capture as an **optional, orthogonal capability**, not a prerequisite.

This strategy:
- Preserves EPCIS query semantics
- Allows early stopping with real gains
- Avoids irreversible architectural decisions
- Supports gradual operational adoption

---

## Role of This Document

This document:
- Refines the original proposal
- Makes trade-offs explicit
- Serves as input for architectural stress-testing
- Enables informed decision-making at each phase
