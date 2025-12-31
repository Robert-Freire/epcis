
# EPCIS Architectural Decision Record

> **Document Type:** Historical record of architectural exploration and decision-making process
>
> **Status:** Finalized - December 30, 2024
>
> **Related Documents:**
> - [Executive Summary](EPCIS_Performance_Architecture_Executive_Summary_SHORT.md)
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

### Approach 3: Hybrid (Normalized + Blob) ⭐ **SELECTED APPROACH**

#### Description
Combine full normalized SQL Server tables with SQL Server FILESTREAM blob storage at dual granularity:
- All EPCIS fields remain queryable (no loss of query flexibility)
- Per-event XML blobs for selective retrieval
- Per-capture XML blobs for compliance and bulk retrieval

#### Benefits
- Preserves full query flexibility (all EPCIS query parameters supported)
- Major blob-based response performance (~90% serialization reduction)
- Transactional consistency (FILESTREAM participates in SQL transactions)
- Incremental migration path (Phase 1 → Phase 2 → optional Phase 3)

#### Limitations
- Increased storage footprint (3-4x: SQL indexes + per-event blobs + per-capture blobs)
- Operational complexity increase (+10-15%: FILESTREAM monitoring)
- Storage cost: +200-300% (mitigated by compression and cheap storage)

---

### Approach 4: Pre-Serialized Response Cache

#### Description
Cache formatted XML / JSON responses per event using distributed caching (e.g., Azure Cache for Redis in Azure environments, or in-memory caching for single-instance deployments).

#### Implementation Options
- **Azure Cache for Redis**: Managed distributed cache, ideal for multi-instance deployments in Azure
- **In-memory cache**: Lower latency but limited to single instance, no cross-instance sharing
- **Cache strategy**: Cache blob references or pre-formatted responses with TTL-based expiration

#### Benefits
- Near-zero response cost on cache hits (~1ms vs 100ms+ from storage)
- Minimal architectural change (layer on top of existing query path)
- Distributed cache (Redis) enables consistent performance across multiple app instances
- Can cache both blob-backed responses (Phase 2+) and legacy reconstructed responses

#### Effectiveness Scenarios
- **High**: Repeated identical queries (e.g., dashboard polling, monitoring endpoints)
- **Medium**: Similar queries with overlapping events (common event sets)
- **Low**: Unique queries, ad-hoc exploration, frequent captures (cache churn from invalidation)

Best suited as a **complement** to other approaches, not a primary solution.

#### Limitations
- Cold-start penalty unchanged (first query still pays full cost)
- Cache invalidation complexity (must invalidate when events updated/deleted)
- Increased storage / memory consumption (Redis: ~50% of original data size for cached responses)
- Azure Cache for Redis cost (tier-based pricing: Basic tier ~$15-50/month, Standard ~$55-200/month)

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

## Comparative Summary

| Approach | Serialization Cost | Memory Usage | Sync Duration | Complexity | Query Flexibility |
|--------|--------------------|--------------|---------------|------------|------------------|
| Current | Baseline | Baseline | Baseline | Low | High |
| Blob + Minimal Index | -90% | -90–96% | -80–86% | Medium | Low |
| Streaming Parser | 0% | -80–85% | -10–20% | High | High |
| **Hybrid (Selected)** | **-90%** | **-90–96%** | **-80–86%** | **Medium** | **High** |
| Response Cache | -100%* | +50% | 0%* | Low | High |
| Async Capture | 0% | 0% | -100%† | Medium | High |
| JSON Fields | -70% | -50% | -30% | Low | Medium |
| Azure AI Search | -95%‡ | 0% | 0% | High | Very High |

\* Cache hit dependent
† Hides latency, does not reduce total work
‡ Query-time only; capture performance unchanged

---

## Key Observations

1. **Root cause**: The EAV pattern for custom fields (`Field` entity) creates O(n²) reconstruction cost
   - Code: `XmlEventFormatter.FormatField:221-229` (recursive ParentIndex lookup)
   - Impact: 1,000 events × 100 fields = 100,000 recursive calls with linear scans

2. **Memory spike**: XML DOM (300-500 MB) dominates capture memory
   - Code: `XmlDocumentParser.LoadDocument:44` (loads entire tree via `XDocument.LoadAsync`)
   - Impact: 100 MB XML → 300-500 MB in-memory DOM (3-5x overhead)

3. **Storage cost**: For 500 events with extensions, 25,000+ Field rows >> 500 Event rows
   - Code: `EpcisModelConfiguration.cs:229-242` (Field as owned entity)
   - Impact: Database write time dominated by owned entity cascade inserts

4. **Blob-based approaches address root causes**; other approaches address symptoms
   - Eliminates field reconstruction entirely
   - Reduces database write complexity from 50,000 rows to 5,000 rows

5. **Azure AI Search vs SQL Server hybrid both address the same bottleneck**
   - Primary production bottleneck: Query serialization (EAV reconstruction + XML/JSON formatting)
   - Both approaches eliminate serialization: Hybrid via blob retrieval (90% improvement), Azure AI Search via pre-indexed documents (95-99% improvement)
   - Marginal difference: ~5-9% additional query improvement vs +30-40% operational complexity
   - **Hybrid advantage**: Also addresses capture bottlenecks (80-86% improvement); Azure AI Search does not
   - **Azure AI Search advantage**: Advanced search capabilities (full-text, faceting, geo-spatial) beyond standard EPCIS queries
   - **Recommended approach**: Start with SQL Server hybrid; evaluate Azure AI Search in Phase 3 if advanced search or extreme query performance is required

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

## Design Decisions (Resolved)

### Blob Granularity
**Decision:** Store BOTH per-event AND per-capture blobs (dual granularity)
- Per-event blobs: Selective queries (<100 events)
- Per-capture blobs: Bulk queries, compliance exports
- Storage cost: 3-4x increase, justified by performance gains

### Storage Technology
**Decision:** SQL Server FILESTREAM
- Transactional consistency (ACID guarantees)
- Single platform (no external blob storage)
- Proven at scale (supports 20,000-event documents)

### Query Strategy
**Decision:** Full normalization preserved
- All EPCIS query parameters supported
- No loss of query flexibility
- Blob storage used only for response generation

---

## Remaining Open Questions

- **Phase 3 (Streaming):** XSD validation strategy with `XmlReader` (schema-validating reader vs. separate validation pass)
- **Cosmos DB migration criteria:** At what scale (>10M captures/month?) does SQL Server FILESTREAM become insufficient?
- **Response format handling:** Store both XML and JSON blobs, or convert on-demand with caching?
- **Compression format:** gzip (standard) vs. brotli (better compression, CPU cost)?

---

## Role of This Document

This document serves as:
- A record of explored architectural options
- A rationale for selected directions
- A reference during design stress-testing

It complements, but does not replace, the primary architecture proposal.

