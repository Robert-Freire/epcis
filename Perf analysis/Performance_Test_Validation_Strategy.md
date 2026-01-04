# FasTnT.PerformanceTests - Validation Strategy for Phased Migration

> **Performance Estimate Disclaimer**
>
> Performance improvements in this document (e.g., "40-60% improvement," "90% reduction")
> are **estimates** based on architectural analysis and preliminary profiling.
> Actual results will vary based on event complexity, workload characteristics, and infrastructure configuration.
> This validation strategy defines how to establish concrete baseline metrics and measure actual improvements.

> **Purpose:** This document describes how to use the existing FasTnT.PerformanceTests project to validate Phase 1 and Phase 2 performance improvements.

---

## Current Test Coverage (December 30, 2024)

The `tests/FasTnT.PerformanceTests` project uses BenchmarkDotNet and includes:

**Existing Benchmarks:**
- ✅ **CaptureBenchmarks** - XML/JSON capture (100, 500 events)
- ✅ **QueryBenchmarks** - Various query patterns (1K, 10K, 50K databases)
- ✅ **SerializationBenchmarks** - XML/JSON serialization (100, 500 results)
- ✅ **EndToEndQueryBenchmarks** - Full query pipeline
- ✅ **XmlParsingBenchmarks, JsonParsingBenchmarks**
- ✅ All benchmarks have `[MemoryDiagnoser]` enabled

**Strengths:**
- Comprehensive coverage of capture and query paths
- Multiple database sizes for scalability testing
- Memory profiling built-in
- Mixed event types (70% Object, 20% Aggregation, 10% Transformation)

---

## Phase 1 Validation Strategy

### Goal
Validate **40-60% improvement** in query response time and capture duration from:
1. Fixing configuration bugs (Constants.cs, CaptureDocumentRequest.cs)
2. Optimizing O(n²) field reconstruction (XmlEventFormatter.FormatField and JsonEventFormatter.BuildElement)
3. Adding database indexes on critical query fields
4. Applying EF Core 10 optimizations (Compiled Queries, AsNoTrackingWithIdentityResolution)
5. **Validation benchmark:** Measure serialization % vs SQL query % to determine Phase 2 path

### Validation Process

#### Step 1: Establish Baseline (Phase 0)

```bash
cd tests/FasTnT.PerformanceTests

# Run baseline benchmarks
dotnet run -c Release --filter *CaptureBenchmarks* --memory --exporters json md --artifacts artifacts/phase0
dotnet run -c Release --filter *SerializationBenchmarks* --memory --exporters json md --artifacts artifacts/phase0
```

**Expected baseline metrics:**
- Capture 500 events: ~X seconds, Y MB memory
- Serialize 500 events to XML: ~X seconds, Y MB memory

#### Step 2: Apply Phase 1 Optimizations

1. Fix `src/FasTnT.Domain/Constants.cs:6` - CaptureSizeLimit (verify deployment requirements)
2. Fix `src/FasTnT.Host/Endpoints/Interfaces/CaptureDocumentRequest.cs:16` - **Critical:** Validation compares ContentLength to MaxEventsReturnedInQuery instead of CaptureSizeLimit
3. Optimize `src/FasTnT.Host/Communication/Xml/Formatters/XmlEventFormatter.cs:221-229`
   - Pre-group fields by ParentIndex using `Dictionary<int, List<Field>>`
4. Optimize `src/FasTnT.Host/Communication/Json/Formatters/JsonEventFormatter.cs` (similar O(n²) pattern in BuildElement methods)
5. Add database indexes:
   - `Event.BusinessStep`, `Event.Disposition`, `Event.EventTime`, `Event.Request.UserId`
   - **Critical:** `Field.ParentIndex` (required for O(n²) fix effectiveness)
6. Apply EF Core 10 optimizations:
   - Compiled Queries in `src/FasTnT.Application/Database/DataSources/EventQueryContext.cs`
   - AsNoTrackingWithIdentityResolution for complex event queries

#### Step 3: Run Phase 1 Benchmarks

```bash
# After applying optimizations
dotnet run -c Release --filter *CaptureBenchmarks* --memory --exporters json md --artifacts artifacts/phase1
dotnet run -c Release --filter *SerializationBenchmarks* --memory --exporters json md --artifacts artifacts/phase1
```

#### Step 4: Compare Results

**Success Criteria:**
- Capture duration: ≥40% reduction (Phase 1 vs Phase 0)
- Serialization duration: ≥40% reduction (Phase 1 vs Phase 0)
- Memory usage: Moderate reduction or no regression

**Validation Gate:**
- [ ] Capture improvement ≥40%
- [ ] Serialization improvement ≥40%
- [ ] No functional regressions (all tests pass)
- **Decision:** If passed → Proceed to Phase 1 Bottleneck Analysis

#### Step 5: Phase 1 Gate 1 - Bottleneck Analysis (MANDATORY)

**Goal:** Determine whether SQL query or serialization is the dominant bottleneck

**Add new benchmark to measure time breakdown:**
```csharp
// In SerializationBenchmarks.cs or new BottleneckAnalysisBenchmarks.cs

[Benchmark]
public QueryResponse ExecuteFullQueryPipeline()
{
    var stopwatch = Stopwatch.StartNew();

    // 1. SQL query execution
    var sqlStart = stopwatch.ElapsedMilliseconds;
    var events = _context.QueryEvents(parameters).ToList();
    var sqlTime = stopwatch.ElapsedMilliseconds - sqlStart;

    // 2. Serialization (field reconstruction + XML formatting)
    var serializationStart = stopwatch.ElapsedMilliseconds;
    var xml = XmlQueryResponseFormatter.Format(new QueryResponse { Events = events });
    var serializationTime = stopwatch.ElapsedMilliseconds - serializationStart;

    Console.WriteLine($"SQL Query: {sqlTime}ms ({sqlTime * 100.0 / stopwatch.ElapsedMilliseconds:F1}%)");
    Console.WriteLine($"Serialization: {serializationTime}ms ({serializationTime * 100.0 / stopwatch.ElapsedMilliseconds:F1}%)");

    return new QueryResponse { Events = events };
}
```

**Gate 1 Decision Criteria:**
- **If SQL Query >40%** → Phase 2A (Redis Cache) - Bypasses SQL on cache hits
- **If Serialization 60-80%** → Proceed to Gate 2 (document size distribution)
- **If Both high** → Phase 2C-Azure (Hybrid Storage + Redis) - Best of both approaches

**Expected Finding:** Serialization is 60-80% of query time → Proceed to Gate 2

---

#### Step 6: Phase 1 Gate 2 - Document Size Distribution Analysis (MANDATORY)

**Goal:** Determine whether intelligent routing (Hybrid Storage) or blob-only storage is appropriate

**Only execute if Gate 1 shows serialization is the bottleneck (60-80%)**

**Add benchmark to analyze document size distribution:**
```csharp
// In new DocumentSizeDistributionBenchmarks.cs

[Benchmark]
public void AnalyzeDocumentSizeDistribution()
{
    // Capture 1,000+ documents with realistic event counts
    var eventCounts = TestDataGenerator.GenerateRealisticEventCounts(1000);

    var sizeDistribution = new Dictionary<string, int>
    {
        { "<1MB", 0 },
        { "1-5MB", 0 },
        { "5-10MB", 0 },
        { "10-50MB", 0 },
        { ">50MB", 0 }
    };

    long totalSize = 0;
    var sizes = new List<double>();

    foreach (var count in eventCounts)
    {
        var document = TestDataGenerator.GenerateEpcisDocument(count);
        var sizeBytes = CalculateSize(document);
        var sizeMB = sizeBytes / (1024.0 * 1024.0);

        totalSize += sizeBytes;
        sizes.Add(sizeMB);

        if (sizeMB < 1) sizeDistribution["<1MB"]++;
        else if (sizeMB < 5) sizeDistribution["1-5MB"]++;
        else if (sizeMB < 10) sizeDistribution["5-10MB"]++;
        else if (sizeMB < 50) sizeDistribution["10-50MB"]++;
        else sizeDistribution[">50MB"]++;
    }

    // Calculate percentiles
    sizes.Sort();
    var p50 = sizes[(int)(sizes.Count * 0.50)];
    var p90 = sizes[(int)(sizes.Count * 0.90)];
    var p95 = sizes[(int)(sizes.Count * 0.95)];
    var p99 = sizes[(int)(sizes.Count * 0.99)];

    Console.WriteLine("Document Size Distribution:");
    foreach (var kvp in sizeDistribution)
    {
        Console.WriteLine($"{kvp.Key}: {kvp.Value / 1000.0:P1}");
    }
    Console.WriteLine($"\nPercentiles:");
    Console.WriteLine($"P50: {p50:F2} MB");
    Console.WriteLine($"P90: {p90:F2} MB");
    Console.WriteLine($"P95: {p95:F2} MB");
    Console.WriteLine($"P99: {p99:F2} MB");

    var smallDocsPercent = (sizeDistribution["<1MB"] + sizeDistribution["1-5MB"]) / 1000.0;
    var largeDocsPercent = (sizeDistribution["5-10MB"] + sizeDistribution["10-50MB"] + sizeDistribution[">50MB"]) / 1000.0;

    Console.WriteLine($"\n**Phase 2 Decision:**");
    if (smallDocsPercent > 0.80)
    {
        Console.WriteLine($"→ Phase 2B-Azure (Hybrid Storage with intelligent routing)");
        Console.WriteLine($"  Reason: {smallDocsPercent:P0} of documents < 5 MB (most stay inline, fast)");
    }
    else if (largeDocsPercent > 0.50)
    {
        Console.WriteLine($"→ Phase 2B-Azure-Simple (Blob-Only Storage)");
        Console.WriteLine($"  Reason: {largeDocsPercent:P0} of documents >= 5 MB (simpler to route all to blob)");
    }
    else
    {
        Console.WriteLine($"→ Mixed distribution - Recommend tuning threshold or operational preference");
        Console.WriteLine($"  Small (<5MB): {smallDocsPercent:P0}, Large (>=5MB): {largeDocsPercent:P0}");
    }
}
```

**Gate 2 Decision Criteria:**
- **If >80% of documents < 5 MB** → Phase 2B-Azure (Hybrid Storage with intelligent routing)
  - Most documents stay inline (fast SQL access, no network call)
  - Large documents routed to blob (handles edge cases like 20K events)
  - Trade-off: Dual code paths for inline vs blob retrieval
- **If >50% of documents >= 5 MB** → Phase 2B-Azure-Simple (Blob-Only Storage)
  - Simpler implementation (single code path, all documents to blob)
  - Avoids dual-path complexity
  - Trade-off: Network call for all documents, even small ones
- **If mixed distribution (40-80% < 5 MB)** → Tune threshold or choose based on operational preference

**Expected Finding:** >80% of documents < 5 MB → Phase 2B-Azure (Hybrid Storage with 5 MB threshold)

---

## Phase 2 Validation Strategy

Choose validation strategy based on Phase 1 bottleneck analysis:

---

### Phase 2A Validation Strategy (Redis Cache)

**Goal:** Validate near-zero latency (1-5 ms) on cache hits

#### Required: Add Redis Cache Benchmark

**Suggested Addition:**
```csharp
// In new RedisCacheBenchmarks.cs

[Benchmark(Baseline = true)]
public async Task<string> QueryWithoutCache()
{
    // Current path: SQL query + reconstruction + serialization
    var events = await _context.QueryEvents(parameters).ToListAsync();
    return XmlQueryResponseFormatter.Format(new QueryResponse { Events = events });
}

[Benchmark]
public async Task<string> QueryWithCacheHit()
{
    // Phase 2A path: fetch from Redis cache
    var cacheKey = _cacheService.GenerateKey(parameters);
    return await _cacheService.GetAsync(cacheKey) ?? await QueryWithoutCache();
}

[Benchmark]
public async Task<string> QueryWithCacheMiss()
{
    // Phase 2A path: cache miss, populate cache
    var cacheKey = _cacheService.GenerateKey(parameters);
    await _cacheService.InvalidateAsync(cacheKey); // Force cache miss
    var result = await _context.QueryEvents(parameters).ToListAsync();
    var xml = XmlQueryResponseFormatter.Format(new QueryResponse { Events = result });
    await _cacheService.SetAsync(cacheKey, xml, TimeSpan.FromMinutes(15));
    return xml;
}
```

**Success Criteria:**
- Cache hit latency: ≤10 ms (target: 1-5 ms)
- Cache miss latency: Similar to Phase 1 baseline (no degradation)
- Cache hit rate: Track in production (target: 40-70%)

**Validation Gate:**
- [ ] Cache hit performance ≤10 ms
- [ ] Cache miss performance = Phase 1 baseline ±5%
- [ ] No functional regressions
- **Decision:** Production deployment approved

---

### Phase 2B-Azure Validation Strategy (Hybrid Storage: JSON Columns + Blob Storage)

**Goal:** Validate **estimated 90% query serialization reduction, estimated 70-80% memory reduction** from stored document queries.

#### Required: Add Hybrid Storage Benchmarks

Phase 2B-Azure introduces intelligent routing:
- Small documents (< 5 MB) → JSON column (SerializedDocument)
- Large documents (>= 5 MB) → Azure Blob Storage (DocumentBlobUri)

The existing SerializationBenchmarks tests reconstruction-based serialization. Need to add benchmarks for both storage paths.

**Suggested Addition:**
```csharp
// In SerializationBenchmarks.cs or new HybridStorageBenchmarks.cs

[Benchmark(Baseline = true)]
public string SerializeFromFieldEntities()
{
    // Current path: reconstruct from Field entities
    var response = _standardResponses[ResultSize];
    return XmlQueryResponseFormatter.Format(response);
}

[Benchmark]
public async Task<string> SerializeFromJsonColumn()
{
    // Phase 2B path: fetch pre-stored document from SerializedDocument column
    // Inline storage (fast path, no external network call)
    return await _context.Requests
        .Where(r => r.Id == requestId && r.StorageType == StorageType.Inline)
        .Select(r => r.SerializedDocument)
        .FirstOrDefaultAsync();
}

[Benchmark]
public async Task<string> SerializeFromAzureBlobStorage()
{
    // Phase 2B path: fetch pre-stored document from Azure Blob Storage
    // External storage (network call, for large documents >= 5 MB)
    var uri = await _context.Requests
        .Where(r => r.Id == requestId && r.StorageType == StorageType.BlobStorage)
        .Select(r => r.DocumentBlobUri)
        .FirstOrDefaultAsync();

    return await _azureBlobService.DownloadBlobAsync(uri);
}
```

#### Validation Process

**Step 1: Run Phase 1 Baseline**

Use Phase 1 results as baseline for Phase 2B comparison.

**Step 2: Implement Phase 2B-Azure (Dual-Read Mode)**

1. Add hybrid storage columns (SerializedDocument, DocumentBlobUri, StorageType)
2. Implement dual-read logic:
   ```
   If StorageType == Inline: Fetch from SerializedDocument column
   Else if StorageType == BlobStorage: Fetch from Azure Blob Storage via DocumentBlobUri
   Else (NULL): Reconstruct from Field entities (legacy path)
   ```
3. New captures write to JSON column or blob based on size threshold (default 5 MB)

**Step 3: Run Phase 2B Benchmarks**

```bash
# Query against NEW data with inline storage (< 5 MB)
dotnet run -c Release --filter *HybridStorageBenchmarks.SerializeFromJsonColumn* --memory --exporters json md --artifacts artifacts/phase2b-inline

# Query against NEW data with blob storage (>= 5 MB)
dotnet run -c Release --filter *HybridStorageBenchmarks.SerializeFromAzureBlobStorage* --memory --exporters json md --artifacts artifacts/phase2b-blob

# Query against OLD data (without stored documents, legacy path)
dotnet run -c Release --filter *SerializationBenchmarks.SerializeFromFieldEntities* --memory --exporters json md --artifacts artifacts/phase2b-legacy
```

**Step 4: Compare Results**

**Success Criteria (New Data - Inline Storage):**
- Serialization duration: Estimated ≥90% reduction vs Phase 1
- Memory usage: Estimated 70-80% reduction vs Phase 1
- Query response time: < 2 seconds for 1,000 events

**Success Criteria (New Data - Blob Storage):**
- Serialization duration: Estimated ≥90% reduction vs Phase 1 (after network latency)
- Memory usage: Estimated 70-80% reduction vs Phase 1
- Network latency: Measure and document baseline (Azure region-dependent)

**Expected (Old Data without Stored Documents):**
- Performance similar to Phase 1 (legacy reconstruction path)

**Validation Gate:**
- [ ] Inline storage query performance estimated ≥90% faster
- [ ] Blob storage query performance estimated ≥90% faster (excluding network latency)
- [ ] Network latency baseline documented for Azure Blob Storage
- [ ] Inline vs blob storage distribution matches expectations (~95% inline, ~5% blob)
- [ ] Old data performance unchanged (dual-read works)
- [ ] No functional regressions
- **Decision:** Production deployment approved

---

### Phase 2C-Azure Validation Strategy (Hybrid Storage + Redis)

**Goal:** Validate combined benefits of Redis cache + Hybrid Storage (JSON Columns + Azure Blob Storage)

**Approach:** Run both Phase 2A and Phase 2B-Azure validation strategies

**Expected Results:**
- Cache hit performance: 1-5 ms (Redis)
- Cache miss performance (new data, inline): Estimated 90% faster than Phase 1
- Cache miss performance (new data, blob): Estimated 90% faster than Phase 1 (after network latency)
- Cache miss performance (old data): Same as Phase 1 (reconstruction)

**Validation Gate:**
- [ ] All Phase 2A success criteria met
- [ ] All Phase 2B-Azure success criteria met
- [ ] No conflicts between Redis cache and hybrid storage
- [ ] Cache invalidation works correctly when new data captured
- **Decision:** Production deployment approved

---

## Recommended Benchmark Additions

### 1. Large Document Benchmark (20,000 Event Requirement)

Current benchmarks use 100-500 events. Need to validate 20,000 event documents (20-100 MB).

**Add to CaptureBenchmarks.cs:**
```csharp
[Params(100, 500, 5000, 20000)]
public int EventCount { get; set; }
```

**Why:**
- Validate capture performance for 20,000 event requirement
- Confirm automatic blob routing for large documents (>= 5 MB threshold)
- Measure actual document size at 20,000 events

### 2. Dense Custom Fields Benchmark

Phase 1 optimizes O(n²) field reconstruction. Need worst-case test.

**Add: FieldReconstructionBenchmarks.cs**
```csharp
[Params(100, 500, 1000)]
public int EventCount { get; set; }

[Params(50, 100, 200)]
public int FieldsPerEvent { get; set; }

[Benchmark]
public string SerializeEventsWithDenseCustomFields()
{
    // Events with many custom fields (worst case for O(n²))
    var events = TestDataGenerator.GenerateEventsWithCustomFields(
        EventCount,
        fieldsPerEvent: FieldsPerEvent
    );
    return XmlQueryResponseFormatter.Format(new QueryResponse { Events = events });
}
```

**Expected:**
- Phase 0: Exponential slowdown as FieldsPerEvent increases
- Phase 1: Linear scaling (O(n) after optimization)

### 3. Threshold Boundary Testing (Phase 2B-Azure)

Test documents around the 5 MB threshold to validate routing logic and performance crossover.

**Add: ThresholdBoundaryBenchmarks.cs**
```csharp
[Params(4.5, 4.9, 5.0, 5.1, 10.0, 20.0)]
public double DocumentSizeMB { get; set; }

[Benchmark]
public async Task CaptureAndQueryDocumentAtThreshold()
{
    // Generate document of specified size
    var eventCount = CalculateEventCountForSize(DocumentSizeMB);
    var document = TestDataGenerator.GenerateEpcisDocument(eventCount);

    // Capture (should route to JSON column or blob based on size)
    await _captureService.CaptureAsync(document);

    // Query and measure performance
    var result = await _queryService.GetEventsAsync(/* query params */);
    return result;
}
```

**Why:**
- Validate 5 MB threshold routing works correctly
- Identify performance crossover point (when blob storage becomes beneficial)
- Confirm no performance cliff at threshold boundary
- Validate that documents just under 5 MB go to JSON column
- Validate that documents just over 5 MB go to Azure Blob Storage

**Expected Results:**
- Documents < 5 MB: Inline storage (fast, no network call)
- Documents >= 5 MB: Blob storage (network call, but handles large docs)
- No significant performance drop at 5 MB boundary

### 4. Azure Blob Storage Network Latency Baseline (Phase 2B-Azure)

Measure baseline network latency for Azure Blob Storage operations.

**Add: AzureBlobLatencyBenchmarks.cs**
```csharp
[Benchmark]
public async Task MeasureBlobUploadLatency()
{
    // Measure time to upload blob to Azure Storage
    var document = TestDataGenerator.GenerateEpcisDocument(20000); // Large doc
    var stopwatch = Stopwatch.StartNew();
    var uri = await _azureBlobService.UploadBlobAsync(document);
    stopwatch.Stop();

    Console.WriteLine($"Blob upload latency: {stopwatch.ElapsedMilliseconds}ms");
    return uri;
}

[Benchmark]
public async Task MeasureBlobDownloadLatency()
{
    // Measure time to download blob from Azure Storage
    var uri = await GetExistingBlobUri();
    var stopwatch = Stopwatch.StartNew();
    var document = await _azureBlobService.DownloadBlobAsync(uri);
    stopwatch.Stop();

    Console.WriteLine($"Blob download latency: {stopwatch.ElapsedMilliseconds}ms");
    return document;
}

[Params(1, 5, 10, 20, 50, 100)]
public int DocumentSizeMB { get; set; }

[Benchmark]
public async Task MeasureBlobLatencyBySize()
{
    // Measure network latency for different document sizes
    var document = GenerateDocumentOfSize(DocumentSizeMB);
    var stopwatch = Stopwatch.StartNew();

    var uri = await _azureBlobService.UploadBlobAsync(document);
    var downloaded = await _azureBlobService.DownloadBlobAsync(uri);

    stopwatch.Stop();
    Console.WriteLine($"Round-trip latency ({DocumentSizeMB}MB): {stopwatch.ElapsedMilliseconds}ms");
    return downloaded;
}
```

**Why:**
- Establish baseline network latency for Azure Blob Storage in target Azure region
- Understand latency overhead for blob-stored documents vs inline storage
- Validate that blob storage is acceptable for large documents (trade latency for memory/CPU savings)
- Measure round-trip time (upload + download) for cost/benefit analysis

**Expected Results:**
- Upload latency: 50-200 ms (Azure region-dependent)
- Download latency: 30-150 ms (Azure region-dependent)
- Round-trip latency scales with document size (network bandwidth)
- Latency acceptable for large documents (edge case, 5% of workload)

**Important:** Network latency will vary by:
- Azure region (same-region = lower latency)
- Network conditions
- Blob storage tier (Hot, Cool, Archive)
- Document size

### 5. Document Size Distribution Analysis (MOVED TO PHASE 1 GATE 2)

✅ **This benchmark is now MANDATORY for Phase 1 validation** - See "Step 6: Phase 1 Gate 2 - Document Size Distribution Analysis"

This analysis determines whether to use intelligent routing (Hybrid Storage) or blob-only storage (simpler).

---

## Summary

**Phase 1 Validation (Two-Stage Gating):**
- Use existing CaptureBenchmarks and SerializationBenchmarks
- Run before/after, compare estimated 40-60% improvement
- **Gate 1 (MANDATORY):** Add bottleneck analysis benchmark to measure serialization % vs SQL query %
- **Gate 2 (MANDATORY if serialization is bottleneck):** Add document size distribution analysis
- **Requires ~4-5 hours** to add both gating benchmarks

**Phase 2A Validation (Redis Cache):**
- Need to add Redis cache benchmarks (cache hit, cache miss)
- Compare cache hit vs baseline performance
- **Requires ~2-3 hours** to add Redis cache benchmarks

**Phase 2B-Azure Validation (Hybrid Storage: JSON Columns + Blob Storage):**
- Need to add hybrid storage benchmarks (inline + blob paths)
- Need to add threshold boundary testing benchmarks
- Need to add Azure Blob Storage network latency benchmarks
- Need to add document size distribution analysis
- Compare inline vs blob vs reconstruction performance
- **Requires ~6-8 hours** to add all Phase 2B-Azure benchmarks

**Phase 2C-Azure Validation (Hybrid Storage + Redis):**
- Run both Phase 2A and Phase 2B-Azure validation strategies
- **Requires ~8-11 hours** (both benchmark sets)

**Total effort to make validation-ready:** ~12-16 hours
- Add bottleneck analysis benchmark (2-3 hours)
- Add FieldReconstructionBenchmarks.cs (1-2 hours)
- Add Redis cache benchmarks for Phase 2A (2-3 hours)
- Add HybridStorageBenchmarks.cs for Phase 2B-Azure (2-3 hours)
- Add ThresholdBoundaryBenchmarks.cs (1-2 hours)
- Add AzureBlobLatencyBenchmarks.cs (2-3 hours)
- Add DocumentSizeDistributionBenchmarks.cs (1-2 hours)
- Update EventCount params to include 20000 (5 minutes)

---

## Next Steps

1. **Approve Phase 1 optimization approach**
2. **Run Phase 0 baseline benchmarks** (save to artifacts/phase0)
3. **Implement Phase 1 optimizations** (including database indexes, EF Core optimizations)
4. **Run Phase 1 benchmarks, validate estimated ≥40% improvement**
5. **Run Phase 1 Gate 1: Bottleneck Analysis** - Determine serialization % vs SQL query %
   - If SQL query >40% → Phase 2A (Redis Cache)
   - If serialization 60-80% → Proceed to Gate 2
   - If both high → Phase 2C-Azure (Hybrid + Redis)
6. **Run Phase 1 Gate 2: Document Size Distribution** - Only if serialization is bottleneck
   - If >80% documents < 5 MB → Phase 2B-Azure (Hybrid Storage with intelligent routing)
   - If >50% documents >= 5 MB → Phase 2B-Azure-Simple (Blob-Only Storage, simpler)
   - If mixed → Tune threshold or choose based on preference
7. **Choose Phase 2 path** - Select based on two-stage Phase 1 validation:
   - **2A (Redis Cache)** - If SQL query is bottleneck (>40%)
   - **2B-Azure (Hybrid Storage)** - If serialization bottleneck + high proportion small docs **[Expected path]**
   - **2B-Azure-Simple (Blob-Only)** - If serialization bottleneck + high proportion large docs
   - **2C-Azure (Hybrid + Redis)** - If both bottlenecks significant
8. **Approve and implement selected Phase 2 option**
9. **Add Azure-specific benchmarks** (threshold boundary, network latency) for Phase 2B-Azure variants
10. **Run Phase 2 validation benchmarks for selected option**
11. **Production deployment after validation gates pass**

**Expected Path:** Phase 1 → Gate 1 (serialization 60-80%) → Gate 2 (>80% < 5 MB) → Phase 2B-Azure (Hybrid Storage with intelligent routing) based on:
- Cost-effective Azure PaaS deployment
- 20,000 event support requirement
- Expected document size distribution (95% < 5 MB)
