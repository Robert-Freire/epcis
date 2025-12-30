
Based on the identified bottlenecks, evaluate architectural changes that reduce:
- repeated serialization costs
- peak memory usage
- synchronous request duration

One hypothesis is storing the original EPCIS XML in blob storage.
Analyze this and any competing approaches.

EPCIS Performance Architecture Analysis & Design Options
Context and Goal

This document analyzes performance bottlenecks in the current EPCIS implementation and evaluates architectural alternatives to improve:

Capture throughput for large EPCIS XML documents

Peak memory usage during ingestion

Query response latency and CPU cost

System scalability under real-world EPCIS workloads

The focus is on design and reasoning, not implementation.

1. Current Baseline Performance Characteristics
Capture Flow (≈100 MB XML, ~5,000 events)
Phase	Characteristics
XML loading	Full DOM load via XDocument.LoadAsync
Validation	Full XSD validation on in-memory DOM
Parsing	Recursive event and custom field parsing
Persistence	EF Core insert of deeply nested object graphs
Transaction	Single long-running transaction

Observed characteristics (order-of-magnitude):

Peak memory: ~500–600 MB

Total synchronous request duration: ~2 minutes

Large GC pressure due to DOM + object graph retention

Primary contributing code paths include:

XmlDocumentParser.LoadDocument

XmlDocumentParser.ParseDocument

XmlEventParser.ParseCustomFields

CaptureHandler.StoreAsync

Query Flow (≈1,000 events)
Phase	Characteristics
Data retrieval	Full materialization of events + owned entities
Reconstruction	Recursive hierarchy rebuild of custom fields
Serialization	XML / JSON formatting from normalized model

Observed characteristics:

Response time: ~10–15 s

CPU-heavy recursive formatting

Memory use proportional to number of events and extensions

Key code paths:

DataRetrieverHandler.QueryEventsAsync

XmlEventFormatter.FormatField

JsonEventFormatter.BuildExtensionFields

2. Identified Bottlenecks (Summary)

The most impactful bottlenecks are:

Full DOM XML loading (memory-bound)

Full-tree XSD validation (CPU + memory)

Recursive custom field parsing and storage

Large in-memory object graphs per capture

Repeated reconstruction and serialization during queries

Together, these account for the majority of latency and memory consumption under large workloads.

3. Architectural Alternatives Evaluated
Approach 1: Store Raw EPCIS XML as a Blob

Concept

Persist the original EPCIS XML payload in blob storage and reference it from indexed event records.

Variants

Blob-only: No normalization, parse on demand

Minimal index + blob (recommended): Extract and index a small set of queryable fields while retaining the full XML

Approach 1B: Minimal Index + Blob (Recommended Initial Direction)
Design Overview

Store raw EPCIS XML payload as a blob (database BLOB or external storage)

Extract only commonly queried fields (e.g. event time, type, business step)

Associate indexed event rows with the blob reference

Serve query responses directly from stored XML when possible

Expected Impact (Relative)
Serialization Cost

Eliminates recursive field reconstruction for common response paths

Reduces response formatting CPU cost by ~90% for XML-based queries

Memory Usage

Avoids building large Field collections and hierarchical object graphs

Reduces peak capture memory by ~80–95%, depending on parsing strategy

Synchronous Request Duration

Removes DOM load and deep normalization steps

Reduces capture request duration by ~50–70%, depending on validation strategy

Trade-offs

Advantages

Preserves original EPCIS payload with full fidelity

Significantly reduces CPU and memory pressure

Version-agnostic (EPCIS 1.x and 2.0)

Naturally extensible to unknown extensions

Limitations

Query flexibility limited to indexed fields

Complex extension queries may require hybrid indexing

Additional storage cost (blob + indexes)

XML→JSON conversion may still be required for some clients

4. Alternative Approaches (Brief Comparison)
Approach	Key Benefit	Key Limitation
Streaming XML parser	Major memory reduction	Serialization costs unchanged
Hybrid (normalized + blob)	Best query flexibility	Highest complexity
Response caching	Excellent for hot queries	Cold-start cost unchanged
Async capture	Eliminates request timeout	Does not reduce total work
JSON-stored fields	Simplifies storage	Limited portability and queries
5. Recommended Staged Strategy
Stage 1: Low-Risk Optimizations

Fix capture size configuration issues

Eliminate O(n²) field reconstruction

Reduce duplicate database round-trips

Expected improvement: ~30–40%

Stage 2: Blob Storage Foundation

Introduce minimal index + blob storage

Retain existing normalization temporarily

Enable direct XML response paths

Expected improvement: Major reduction in memory and serialization cost

Stage 3: Streaming Ingestion

Replace DOM parsing with streaming parser

Extract indexes and write blob in a single pass

Optional async validation

Expected improvement: Further memory reduction during capture

6. Open Design Questions

Blob granularity: per capture vs per event

Strategy for complex custom-field queries

JSON response generation strategy

Operational considerations (backup, lifecycle, compression)

7. Conclusion

The current performance limitations stem primarily from repeated parsing, normalization, and reconstruction of large EPCIS documents.
A minimal index + blob approach directly addresses these root causes while preserving EPCIS fidelity and extensibility.

This direction allows incremental adoption, measurable gains, and a clear path toward more scalable ingestion and query handling.