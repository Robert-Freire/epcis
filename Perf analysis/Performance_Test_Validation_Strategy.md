# FasTnT.PerformanceTests - Validation Strategy for Phased Migration

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

#### Step 5: Phase 1 Bottleneck Analysis (NEW)

**Goal:** Determine which Phase 2 option to pursue

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

**Decision Criteria:**
- **If Serialization 60-80%** → Phase 2B (Blob Storage) - Eliminates serialization bottleneck
- **If SQL Query >40%** → Phase 2A (Redis Cache) - Bypasses SQL on cache hits
- **If Both high** → Phase 2C (Hybrid) - Best of both approaches

**Expected Finding:** Serialization is 60-80% of query time → Phase 2B recommended

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

### Phase 2B Validation Strategy (Blob Storage)

**Goal:** Validate **90% query serialization reduction, 70-80% memory reduction** from blob-based queries.

#### Required: Add Blob-Based Benchmark

Phase 2B introduces FILESTREAM blobs. The existing SerializationBenchmarks tests reconstruction-based serialization. Need to add blob-path benchmark.

**Suggested Addition:**
```csharp
// In SerializationBenchmarks.cs or new BlobSerializationBenchmarks.cs

[Benchmark(Baseline = true)]
public string SerializeFromFieldEntities()
{
    // Current path: reconstruct XML from Field entities
    var response = _standardResponses[ResultSize];
    return XmlQueryResponseFormatter.Format(response);
}

[Benchmark]
public async Task<string> SerializeFromBlob()
{
    // Phase 2B path: fetch pre-stored XML from FILESTREAM
    return await _blobStorage.FetchEventXmlAsync(eventIds);
}
```

#### Validation Process

**Step 1: Run Phase 1 Baseline**

Use Phase 1 results as baseline for Phase 2B comparison.

**Step 2: Implement Phase 2B (Dual-Read Mode)**

1. Add FILESTREAM columns (EventBlobId, DocumentBlobId)
2. Implement dual-read logic (check if blob exists → fetch blob OR reconstruct)
3. New captures write blobs

**Step 3: Run Phase 2B Benchmarks**

```bash
# Query against NEW data (with blobs)
dotnet run -c Release --filter *SerializationBenchmarks* --memory --exporters json md --artifacts artifacts/phase2-new

# Query against OLD data (without blobs, legacy path)
dotnet run -c Release --filter *SerializationBenchmarks* --memory --exporters json md --artifacts artifacts/phase2-old
```

**Step 4: Compare Results**

**Success Criteria (New Data with Blobs):**
- Serialization duration: ≥90% reduction vs Phase 1
- Memory usage: 70-80% reduction vs Phase 1
- Query response time: < 2 seconds for 1,000 events

**Expected (Old Data without Blobs):**
- Performance similar to Phase 1 (legacy path)

**Validation Gate:**
- [ ] New data query performance ≥90% faster
- [ ] New data memory usage 70-80% lower
- [ ] Old data performance unchanged (dual-read works)
- [ ] No functional regressions
- **Decision:** Production deployment approved

---

### Phase 2C Validation Strategy (Hybrid)

**Goal:** Validate combined benefits of Redis cache + Blob storage

**Approach:** Run both Phase 2A and Phase 2B validation strategies

**Expected Results:**
- Cache hit performance: 1-5 ms (Redis)
- Cache miss performance (new data): 90% faster than Phase 1 (blobs)
- Cache miss performance (old data): Same as Phase 1 (reconstruction)

**Validation Gate:**
- [ ] All Phase 2A success criteria met
- [ ] All Phase 2B success criteria met
- [ ] No conflicts between Redis and blob storage
- **Decision:** Production deployment approved

---

## Recommended Benchmark Additions

### 1. Large Document Benchmark

Current benchmarks use 100-500 events. Documentation mentions "100 MB document (5,000 events)."

**Add to CaptureBenchmarks.cs:**
```csharp
[Params(100, 500, 5000)]
public int EventCount { get; set; }
```

**Why:** Validate "~120s capture time for 100 MB" baseline claim.

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

---

## Summary

**Phase 1 Validation:**
- Use existing CaptureBenchmarks and SerializationBenchmarks
- Run before/after, compare 40-60% improvement
- **NEW:** Add bottleneck analysis benchmark to measure serialization % vs SQL query %
- **Requires ~2-3 hours** to add bottleneck analysis benchmark

**Phase 2A Validation (Redis Cache):**
- Need to add Redis cache benchmarks (cache hit, cache miss)
- Compare cache hit vs baseline performance
- **Requires ~2-3 hours** to add Redis cache benchmarks

**Phase 2B Validation (Blob Storage):**
- Need to add blob-path benchmarks
- Compare blob vs reconstruction performance
- **Requires ~2-3 hours** to add blob benchmarks

**Phase 2C Validation (Hybrid):**
- Run both Phase 2A and Phase 2B validation strategies
- **Requires ~4-6 hours** (both benchmarks)

**Total effort to make validation-ready:** ~6-8 hours
- Add bottleneck analysis benchmark (2-3 hours)
- Add FieldReconstructionBenchmarks.cs (1-2 hours)
- Add Redis cache benchmarks for Phase 2A (2-3 hours)
- Add blob-path benchmarks for Phase 2B (2-3 hours)
- Update EventCount params to include 5000 (5 minutes)

---

## Next Steps

1. **Approve Phase 1 optimization approach**
2. **Run Phase 0 baseline benchmarks** (save to artifacts/phase0)
3. **Implement Phase 1 optimizations** (including database indexes, EF Core optimizations)
4. **Run Phase 1 benchmarks, validate ≥40% improvement**
5. **Run Phase 1 bottleneck analysis** - Determine serialization % vs SQL query %
6. **Choose Phase 2 path** - Select 2A (Redis), 2B (Blob Storage), or 2C (Hybrid) based on bottleneck
7. **Approve and implement selected Phase 2 option**
8. **Run Phase 2 validation benchmarks for selected option**
9. **Production deployment after validation gates pass**
