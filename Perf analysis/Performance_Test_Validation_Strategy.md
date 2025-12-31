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
Validate **30-40% improvement** in query response time and capture duration from:
1. Fixing configuration bugs (Constants.cs)
2. Optimizing O(n²) field reconstruction (XmlEventFormatter.FormatField)
3. Reducing EF Core transaction overhead (CaptureHandler.StoreAsync)

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

1. Fix `src/FasTnT.Domain/Constants.cs:6` - CaptureSizeLimit
2. Optimize `src/FasTnT.Host/Communication/Xml/Formatters/XmlEventFormatter.cs:221-229`
   - Pre-group fields by ParentIndex using `Dictionary<int, List<Field>>`
3. Optimize `src/FasTnT.Application/Handlers/CaptureHandler.cs:66`
   - Single SaveChangesAsync instead of dual calls

#### Step 3: Run Phase 1 Benchmarks

```bash
# After applying optimizations
dotnet run -c Release --filter *CaptureBenchmarks* --memory --exporters json md --artifacts artifacts/phase1
dotnet run -c Release --filter *SerializationBenchmarks* --memory --exporters json md --artifacts artifacts/phase1
```

#### Step 4: Compare Results

**Success Criteria:**
- Capture duration: ≥30% reduction (Phase 1 vs Phase 0)
- Serialization duration: ≥30% reduction (Phase 1 vs Phase 0)
- Memory usage: Moderate reduction or no regression

**Validation Gate:**
- [ ] Capture improvement ≥30%
- [ ] Serialization improvement ≥30%
- [ ] No functional regressions (all tests pass)
- **Decision:** If passed → Proceed to Phase 2

---

## Phase 2 Validation Strategy

### Goal
Validate **90% query serialization reduction, 70-80% memory reduction** from blob-based queries.

### Required: Add Blob-Based Benchmark

Phase 2 introduces FILESTREAM blobs. The existing SerializationBenchmarks tests reconstruction-based serialization. Need to add blob-path benchmark.

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
    // Phase 2 path: fetch pre-stored XML from FILESTREAM
    return await _blobStorage.FetchEventXmlAsync(eventIds);
}
```

### Validation Process

#### Step 1: Run Phase 1 Baseline

Use Phase 1 results as baseline for Phase 2 comparison.

#### Step 2: Implement Phase 2 (Dual-Read Mode)

1. Add FILESTREAM columns (EventBlobId, DocumentBlobId)
2. Implement dual-read logic (check if blob exists → fetch blob OR reconstruct)
3. New captures write blobs

#### Step 3: Run Phase 2 Benchmarks

```bash
# Query against NEW data (with blobs)
dotnet run -c Release --filter *SerializationBenchmarks* --memory --exporters json md --artifacts artifacts/phase2-new

# Query against OLD data (without blobs, legacy path)
dotnet run -c Release --filter *SerializationBenchmarks* --memory --exporters json md --artifacts artifacts/phase2-old
```

#### Step 4: Compare Results

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
- Run before/after, compare 30-40% improvement
- **Ready to use as-is** (minor: add 5000 event param)

**Phase 2 Validation:**
- Need to add blob-path benchmarks
- Compare blob vs reconstruction performance
- **Requires ~2-3 hours to add blob benchmarks**

**Total effort to make validation-ready:** ~3-4 hours
- Add FieldReconstructionBenchmarks.cs (1-2 hours)
- Add blob-path benchmarks for Phase 2 (2-3 hours)
- Update EventCount params to include 5000 (5 minutes)

---

## Next Steps

1. **Approve Phase 1 optimization approach**
2. **Run Phase 0 baseline benchmarks** (save to artifacts/phase0)
3. **Implement Phase 1 optimizations**
4. **Run Phase 1 benchmarks, validate ≥30% improvement**
5. **If validated → Approve Phase 2 implementation**
