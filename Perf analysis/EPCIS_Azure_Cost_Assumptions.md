# EPCIS Azure Cost Assumptions (West Europe)

> **Document Purpose:** This document provides cost estimates for the EPCIS performance architecture options based on Azure West Europe region pricing as of January 2025.
>
> **Important:** These are EXAMPLE costs based on assumed workloads. You MUST measure your actual usage and calculate costs for YOUR deployment before making Phase 2 architecture decisions.

> **Pricing Date:** January 2025 (West Europe region)
>
> **Pricing Source:** [Azure Pricing Calculator](https://azure.microsoft.com/en-us/pricing/calculator/)

---

## Cost Assumptions Summary

| Architecture Option | Monthly Cost Calculation | Total (EUR) |
|-------------------|-------------------------|-------------|
| **Current (Baseline)** | SQL (€300-400) | **€300-400** |
| **Phase 2A: Redis Cache** | SQL (€300-400) + Redis (€55) | **€355-455** |
| **Phase 2B: Hybrid Storage** | SQL (€300-400) + Blob (€0-10) | **€300-410** |
| **Phase 2B-Simple: Blob-Only** | SQL (€300-400) + Blob (€0-20) | **€300-420** |
| **Phase 2C: Hybrid + Redis** | SQL (€300-400) + Blob (€0-10) + Redis (€55) | **€355-465** |
| **SQL Server on VM (IaaS)** | VM + SQL License + Disks + HA | **€1,400-1,800+** |

**Note:** All costs are approximate and based on assumed workloads. See detailed assumptions below.

---

## Azure SQL Database Costs (West Europe)

### Assumed Tier: S3 (100 DTUs)

| Tier | DTUs | Max Size | Monthly Cost (EUR) | When to Use |
|------|------|----------|-------------------|-------------|
| S0 | 10 | 250 GB | €13 | Development/testing only |
| S1 | 20 | 250 GB | €26 | Small workloads |
| S2 | 50 | 250 GB | €115 | Low-medium workloads |
| **S3** | **100** | **250 GB** | **~€300** | **Assumed baseline** |
| S4 | 200 | 250 GB | €460 | Medium-high workloads |
| S6 | 400 | 250 GB | €920 | High workloads |
| S9 | 800 | 250 GB | €1,835 | Very high workloads |

**Assumptions for S3 tier:**
- Database size: < 250 GB
- Query throughput: Moderate (100 DTUs sufficient)
- Concurrent connections: < 200
- Peak DTU usage: < 80% sustained

### How to Determine YOUR SQL Tier

**Step 1: Measure current database size**
```sql
SELECT
    SUM(size) * 8.0 / 1024 / 1024 AS DatabaseSizeGB
FROM sys.master_files
WHERE database_id = DB_ID('YourDatabaseName');
```

**Step 2: Measure DTU requirements**
- If running on Azure SQL: Check Azure Portal → SQL Database → Metrics → DTU percentage
- If running on-premises: Use [Azure SQL Database DTU Calculator](https://dtucalculator.azurewebsites.net/)

**Step 3: Choose tier**
- If database > 250 GB: Consider P-series (Premium) or vCore model
- If DTU usage > 80%: Scale up to next tier
- If database < 100 GB and DTU < 50: S2 tier (€115/month) may be sufficient

### Alternative: vCore Pricing Model

If your workload has variable usage or you need > 250 GB:

| vCore Tier | vCores | RAM | Storage | Monthly Cost (EUR) |
|-----------|--------|-----|---------|-------------------|
| General Purpose 2 vCore | 2 | 10.2 GB | 500 GB | ~€310 |
| General Purpose 4 vCore | 4 | 20.4 GB | 500 GB | ~€620 |
| Business Critical 2 vCore | 2 | 10.2 GB | 500 GB | ~€775 |

**When to use vCore:**
- Database size > 250 GB
- Need more granular scaling
- Variable workload (serverless option available)

---

## Azure Blob Storage Costs (West Europe)

### Storage Tiers

| Tier | Cost per GB/month (EUR) | When to Use |
|------|------------------------|-------------|
| **Hot** | **€0.0168** | **Frequently accessed (default)** |
| Cool | €0.0084 | Accessed < 1/month, 30+ day retention |
| Archive | €0.0018 | Rarely accessed, 180+ day retention |

**Assumption:** Hot tier (documents accessed frequently for queries)

### Transaction Costs

| Operation Type | Cost per 10,000 ops (EUR) | Assumption |
|---------------|--------------------------|-----------|
| Write operations (PUT/COPY) | €0.046 | ~1 per capture |
| Read operations (GET) | €0.0037 | ~1-5 per query |
| List operations | €0.046 | Minimal usage |

### Network Costs

| Data Transfer | Cost (EUR) |
|--------------|-----------|
| Within same region (SQL ↔ Blob) | **€0** |
| Outbound to internet (first 100 GB) | €0.0775/GB |
| Outbound to internet (100 GB - 10 TB) | €0.07/GB |

**Assumption:** All operations within West Europe region (no network egress cost)

### Cost Calculation Examples

#### Example 1: Low Volume (Assumed in docs)
**Workload:**
- 1,000 captures/month
- 5% are >= 5 MB (50 large documents)
- Average large document: 30 MB
- Queries: 5,000/month (average 2 blob reads per query)

**Calculation:**
```
Storage: (50 docs × 30 MB) / 1024 = 1.46 GB
Storage cost: 1.46 GB × €0.0168 = €0.025/month

Write ops: 50 writes/month → (50 / 10,000) × €0.046 = €0.0002/month
Read ops: 5,000 queries × 2 reads = 10,000 reads → (10,000 / 10,000) × €0.0037 = €0.004/month

Total: €0.025 + €0.0002 + €0.004 = €0.03/month
```

**This is the "€0.03/month" estimate in the architecture documents.**

#### Example 2: Medium Volume (More Realistic)
**Workload:**
- 10,000 captures/month
- 10% are >= 5 MB (1,000 large documents)
- Average large document: 50 MB
- Queries: 50,000/month (average 2 blob reads per query)

**Calculation:**
```
Storage: (1,000 docs × 50 MB) / 1024 = 48.8 GB
Storage cost: 48.8 GB × €0.0168 = €0.82/month

Write ops: 1,000 writes/month → (1,000 / 10,000) × €0.046 = €0.0046/month
Read ops: 50,000 queries × 2 reads = 100,000 reads → (100,000 / 10,000) × €0.0037 = €0.037/month

Total: €0.82 + €0.0046 + €0.037 = €0.86/month
```

#### Example 3: High Volume
**Workload:**
- 100,000 captures/month
- 15% are >= 5 MB (15,000 large documents)
- Average large document: 75 MB
- Queries: 500,000/month (average 3 blob reads per query)

**Calculation:**
```
Storage: (15,000 docs × 75 MB) / 1024 = 1,099 GB (~1.1 TB)
Storage cost: 1,099 GB × €0.0168 = €18.46/month

Write ops: 15,000 writes/month → (15,000 / 10,000) × €0.046 = €0.069/month
Read ops: 500,000 queries × 3 reads = 1,500,000 reads → (1,500,000 / 10,000) × €0.0037 = €0.555/month

Total: €18.46 + €0.069 + €0.555 = €19.08/month
```

### Blob-Only Storage Variant (All Documents)

If Phase 1 Gate 2 shows >50% documents >= 5 MB, you may choose blob-only storage (all documents to blob).

**Impact:** 10x higher storage and transaction costs (all captures go to blob, not just 5-15%)

**Example:** Medium volume workload
```
Storage: (10,000 docs × 5 MB avg) / 1024 = 48.8 GB
Storage cost: 48.8 GB × €0.0168 = €0.82/month

Write ops: 10,000 writes/month → (10,000 / 10,000) × €0.046 = €0.046/month
Read ops: 50,000 queries × 1 read = 50,000 reads → (50,000 / 10,000) × €0.0037 = €0.0185/month

Total: €0.82 + €0.046 + €0.0185 = €0.88/month
```

**Conclusion:** Even blob-only storage is very inexpensive for typical workloads (< €1/month).

---

## Azure Cache for Redis Costs (West Europe)

### Standard Tier (Recommended for Production)

| Tier | Cache Size | Monthly Cost (EUR) | When to Use |
|------|-----------|-------------------|-------------|
| C0 | 250 MB | €14 | Development/testing only |
| C1 | 1 GB | €28 | Small cache (< 200 queries) |
| **C2** | **2.5 GB** | **~€55** | **Assumed baseline** |
| C3 | 6 GB | €110 | Medium cache (500-1,000 queries) |
| C4 | 13 GB | €220 | Large cache (1,000-2,000 queries) |
| C5 | 26 GB | €440 | Very large cache (2,000+ queries) |

**Assumptions for C2 tier:**
- Average query response size: 5 MB
- Number of cached queries: ~500 unique query patterns
- Cache size needed: 500 × 5 MB = 2.5 GB
- TTL: 15 minutes (query responses expire after 15 min)

### Premium Tier (High Availability + Persistence)

| Tier | Cache Size | Monthly Cost (EUR) | Features |
|------|-----------|-------------------|----------|
| P1 | 6 GB | €200 | HA, persistence, clustering |
| P2 | 13 GB | €400 | HA, persistence, clustering |
| P3 | 26 GB | €800 | HA, persistence, clustering |

**When to use Premium:**
- Need 99.9% SLA with replication
- Need data persistence (Redis snapshots)
- Very high throughput (> 100,000 ops/sec)

### How to Calculate YOUR Redis Cache Size

**Formula:**
```
Cache size = (Unique queries per TTL window) × (Average response size)
```

**Step 1: Estimate unique queries per TTL window**
```
TTL window = 15 minutes (typical)
If 1,000 unique queries per day:
Queries per 15 min = 1,000 / (24 × 60 / 15) = ~10 active queries in cache

But you want to cache more than just active queries.
Better: Cache queries from last 1-2 hours
Queries in 2 hours = 1,000 / 12 = ~83 queries
```

**Step 2: Measure average response size**
```sql
-- If you're storing serialized responses in DB (after Phase 2B)
SELECT AVG(DATALENGTH(SerializedDocument)) / 1024 / 1024 AS AvgResponseSizeMB
FROM Request;
```

**Step 3: Calculate cache size**
```
Example:
- Unique queries in 2-hour window: 100
- Average response size: 5 MB
- Cache size needed: 100 × 5 MB = 500 MB → C1 tier (€28/month)

If you have 500 unique queries:
- 500 × 5 MB = 2.5 GB → C2 tier (€55/month)
```

### Redis Network Costs

**Good news:** No network egress charges when Redis and SQL Database are in the same region (West Europe).

---

## SQL Server on Azure VM Costs (West Europe) - NOT RECOMMENDED

### Why IaaS is More Expensive

**Option B-Azure (PaaS):** €300-410/month (SQL Database + Blob Storage)

**SQL Server on VM (IaaS):** €1,400-1,800+/month

**Breakdown:**

| Component | SKU | Monthly Cost (EUR) |
|-----------|-----|-------------------|
| Azure VM (4 vCPUs, 16 GB RAM) | Standard D4s v3 | €180 |
| SQL Server Standard License | Pay-as-you-go | €730 |
| Managed Disks (Premium SSD) | P30 (1 TB) | €120 |
| Backup Storage | 100 GB | €10 |
| **Subtotal (Single VM)** | | **€1,040/month** |
| | | |
| **High Availability (2 VMs)** | 2× above | **€2,080/month** |
| Load Balancer | Standard | €20/month |
| Virtual Network | | €5/month |
| **Total (HA deployment)** | | **€2,105/month** |

### Additional Operational Costs (Not in €)

- **Manual patching:** Windows Updates, SQL Server updates, security patches
- **HA configuration:** Set up Always On Availability Groups manually
- **Monitoring:** Configure SQL Server monitoring, alerts, backups
- **Disaster recovery:** Set up and test failover procedures
- **Security:** Firewall rules, NSGs, security audits

**Estimated operational overhead:** +40-60% additional effort vs. PaaS

### Why IaaS is Not Recommended

1. **5-7x higher cost** (€2,105/month vs €300-410/month)
2. **Manual management** (patching, HA, backups)
3. **No PaaS benefits** (built-in HA, automatic backups, easy scaling)
4. **Only benefit:** SQL Server FILESTREAM support (rejected in favor of Azure Blob Storage)

**Decision:** Use Azure SQL Database (PaaS) + Azure Blob Storage for fully managed services.

---

## Cost Estimation Worksheet

Use this worksheet to calculate costs for YOUR deployment based on YOUR workload.

### Step 1: Measure Your Workload (Phase 1 Validation)

| Metric | How to Measure | Your Value |
|--------|---------------|------------|
| **Database size (GB)** | `SELECT SUM(size)*8/1024/1024 FROM sys.master_files` | _____ GB |
| **Captures per month** | Count from last 30 days | _____ |
| **Document size distribution** | Phase 1 Gate 2 benchmark | <1MB: ___%, 1-5MB: ___%, 5-10MB: ___%, >10MB: ___% |
| **Average large doc size (MB)** | P50/P90 from histogram | _____ MB |
| **Queries per month** | Count from last 30 days | _____ |
| **Average response size (MB)** | Measure serialized XML/JSON | _____ MB |
| **Unique query patterns** | Analyze query logs | _____ |
| **DTU utilization (%)** | Azure Portal metrics (if on Azure) | _____ % |

### Step 2: Calculate Azure SQL Database Cost

1. Go to [Azure Pricing Calculator](https://azure.microsoft.com/en-us/pricing/calculator/)
2. Select: **Azure SQL Database** → **West Europe** region
3. Choose tier based on:
   - Database size: _____GB (from Step 1)
   - DTU utilization: _____% (from Step 1)
4. **Your SQL Database cost:** €_____ /month

### Step 3: Calculate Azure Blob Storage Cost (if Phase 2B)

**Only if Phase 1 Gate 1 shows serialization is bottleneck (60-80%)**

```
Captures per month: _____
Percentage >= 5 MB (from histogram): _____%
Large docs per month: _____ × _____% = _____

Average large doc size: _____ MB
Total storage GB: (_____ × _____ MB) / 1024 = _____ GB

Storage cost: _____ GB × €0.0168 = €_____ /month
Write ops cost: (_____ / 10,000) × €0.046 = €_____ /month
Read ops cost: (_____ queries × 2 / 10,000) × €0.0037 = €_____ /month

Your Blob Storage cost: €_____ /month
```

### Step 4: Calculate Azure Redis Cache Cost (if Phase 2A or 2C)

**Only if Phase 1 Gate 1 shows SQL query is bottleneck (>40%)**

```
Unique query patterns: _____
Average response size: _____ MB
Cache size needed: _____ × _____ MB = _____ GB

Choose tier:
□ C1 (1 GB): €28/month
□ C2 (2.5 GB): €55/month
□ C3 (6 GB): €110/month
□ C4 (13 GB): €220/month

Your Redis cost: €_____ /month
```

### Step 5: Total Monthly Cost Estimate

| Component | Your Cost (EUR/month) |
|-----------|--------------------|
| Azure SQL Database | €_____ |
| Azure Blob Storage | €_____ |
| Azure Cache for Redis | €_____ |
| **TOTAL** | **€_____** |

### Step 6: Compare to Budget

**Your budget:** €_____ /month

**Recommended architecture based on cost:**
- [ ] **Phase 2A (Redis Cache)** - If SQL query bottleneck + budget allows Redis
- [ ] **Phase 2B Variant 1 (Hybrid Storage)** - If serialization bottleneck + >80% small docs
- [ ] **Phase 2B Variant 2 (Blob-Only)** - If serialization bottleneck + >50% large docs
- [ ] **Phase 2C (Hybrid + Redis)** - If both bottlenecks + budget allows both

---

## Cost Optimization Strategies

### 1. Azure Reserved Instances (1-3 year commitment)

Save 30-40% on Azure SQL Database and VMs:

| Commitment | Discount | S3 Cost | C2 Redis Cost |
|-----------|---------|---------|---------------|
| Pay-as-you-go | 0% | €300/month | €55/month |
| 1-year reserved | ~30% | €210/month | €38/month |
| 3-year reserved | ~40% | €180/month | €33/month |

**Recommendation:** If Phase 2 proves successful, consider 1-year reserved instance.

### 2. Azure Hybrid Benefit (if you have SQL Server licenses)

If you have existing SQL Server licenses with Software Assurance:
- SQL Server on VM: Save €730/month on licensing (use existing license)
- Azure SQL Database vCore: Save ~40% (use existing license)

**Not applicable if:** You don't have existing SQL Server licenses (most common case).

### 3. Blob Storage Lifecycle Policies

Automatically tier old data to Cool or Archive storage:

```
Example lifecycle policy:
- Hot tier (0-90 days): €0.0168/GB/month
- Cool tier (90-180 days): €0.0084/GB/month (50% cheaper)
- Archive tier (>180 days): €0.0018/GB/month (90% cheaper)

If you have 1 TB of blobs and 70% are >180 days old:
Hot (300 GB): 300 × €0.0168 = €5.04/month
Archive (700 GB): 700 × €0.0018 = €1.26/month
Total: €6.30/month (vs €16.80/month all-hot)
Savings: €10.50/month (62% reduction)
```

**Setup:** Azure Blob Storage → Lifecycle Management → Add rule

### 4. Monitor and Right-Size

After Phase 2 deployment:
- Monitor DTU/vCore usage (scale down if < 50% utilized)
- Monitor Redis cache hit rate (scale down if hit rate < 30%)
- Monitor blob storage access patterns (move cold data to Cool tier)

**Expected savings:** 10-30% by right-sizing after observing actual usage

---

## Related Documents

- [EPCIS Performance Architecture – Hybrid Strategy & Phased Migration](EPCIS_Performance_Architecture_Hybrid_Phasing.md)
- [EPCIS Architectural Decision Record](EPCIS_Architectural_Decision_Record.md)
- [Performance Test Validation Strategy](Performance_Test_Validation_Strategy.md)
- [Executive Summary](EPCIS_Performance_Architecture_Executive_Summary.md)

---

## External Resources

- [Azure Pricing Calculator](https://azure.microsoft.com/en-us/pricing/calculator/)
- [Azure SQL Database Pricing (West Europe)](https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/)
- [Azure Blob Storage Pricing (West Europe)](https://azure.microsoft.com/en-us/pricing/details/storage/blobs/)
- [Azure Cache for Redis Pricing (West Europe)](https://azure.microsoft.com/en-us/pricing/details/cache/)
- [Azure SQL Database DTU Calculator](https://dtucalculator.azurewebsites.net/)

---

**Document Version:** 1.0 (January 2025)
**Last Updated:** 2025-01-04
**Pricing Region:** West Europe
**Currency:** EUR (€)
