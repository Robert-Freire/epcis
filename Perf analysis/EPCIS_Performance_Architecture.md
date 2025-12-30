
# EPCIS Performance Architecture Analysis & Design Options

## Context and Goal
This document analyzes performance bottlenecks in the current EPCIS implementation and evaluates architectural alternatives to improve capture throughput, memory usage, and query response time.

---

## Current Baseline Performance Characteristics

### Capture Flow (~100 MB XML, ~5,000 events)
- XML DOM loading via XDocument.LoadAsync
- Full XSD validation on in-memory DOM
- Recursive parsing of custom fields
- EF Core persistence with deep object graphs
- Long-running database transactions

Observed characteristics:
- Peak memory: ~500–600 MB
- Synchronous request duration: ~2 minutes

---

### Query Flow (~1,000 events)
- Full materialization of events and owned entities
- Recursive reconstruction of custom fields
- XML / JSON formatting from normalized model

Observed characteristics:
- Response time: ~10–15 seconds
- CPU-heavy recursive formatting

---

## Identified Bottlenecks
1. Full DOM XML loading
2. Full-tree XSD validation
3. Recursive custom field parsing
4. Large in-memory object graphs
5. Repeated reconstruction during queries

---

## Architectural Alternatives

### Minimal Index + Blob Storage (Recommended)
- Store raw EPCIS XML as blob
- Extract and index minimal queryable fields
- Serve query responses directly from stored XML

Expected impact:
- ~90% serialization cost reduction
- ~80–95% peak memory reduction
- ~50–70% reduction in synchronous capture time

---

## Staged Strategy
**Stage 1:** Low-risk optimizations (~30–40% improvement)  
**Stage 2:** Blob storage foundation  
**Stage 3:** Streaming ingestion  

---

## Conclusion
A minimal index plus blob approach addresses the root causes of performance issues while preserving EPCIS fidelity and extensibility.
