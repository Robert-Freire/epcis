# Answers to review


## CRITICAL QUESTIONS TO ANSWER

Before proceeding with implementation, these questions must be answered:

### 1. Azure Deployment Model

**Question:** Is Azure SQL Database (PaaS) mandatory, or is SQL Server on Azure VM acceptable?

**Impact:**
- **If PaaS mandatory:** FILESTREAM is not an option → Use Azure Blob Storage
- **If Azure VM acceptable:** FILESTREAM is viable → But adds 40-60% operational overhead

**Recommendation:** Azure SQL Database (PaaS) for operational simplicity and cost

**Answer:** Use PaaS. FILESTREAM strategy has to be replaced with blob storage
---

### 2. Transactional Consistency Requirement

**Question:** How critical is atomic consistency between SQL metadata and blob storage?

**Scenarios:**
- **Strong consistency required:** Azure Blob Storage needs two-phase commit with compensation logic
- **Eventual consistency acceptable:** Azure Blob Storage with simple write-ahead pattern

**Impact:**
- Strong consistency: +2-3 weeks implementation complexity
- Eventual consistency: Can accept orphaned blobs (cleanup job)

**Recommendation:** Eventual consistency with compensation logic is sufficient for EPCIS workloads

**Answer:** Follow recomendation
---

### 3. Document Size Distribution

**Question:** What percentage of captures are >10MB, >50MB, >100MB?

**Impact:**
- If most captures <10MB: JSON columns in Azure SQL Database may be viable (simplest option)
- If most captures >100MB: Azure Blob Storage mandatory

**Data Needed:**
- Histogram of capture sizes over last 3-6 months
- P50, P90, P95, P99 percentiles

**Answer:** Add to open questions sections. But I need some clarification. We know that we have to support documents with 20000 events. What happens with this documents? 
---

### 4. Query Pattern Analysis

**Question:** Are queries predominantly repetitive (dashboards, compliance) or unique (ad-hoc exploration)?

**Impact:**
- **Repetitive:** Azure Cache for Redis (Option A) will have high hit rate (40-70%)
- **Unique:** Redis provides minimal benefit → Azure Blob Storage (Option B-Azure) better ROI

**Data Needed:**
- Query logs for last 3-6 months
- Unique query patterns vs. total queries ratio
- Top 10 most frequent queries

**Answer:** Add to open questions sections.
---

### 5. Multi-Region Requirement

**Question:** Is global distribution needed, or is single-region deployment acceptable?

**Impact:**
- **Single region:** Azure Blob Storage (GRS) sufficient
- **Multi-region:** Consider Azure Front Door + CDN for edge caching

**Recommendation:** Start single-region, add multi-region if needed

**Answer:** Add to open questions sections. but follow recomendation
---

### 6. Compliance and Data Retention

**Question:** Are there data retention requirements (e.g., 7+ years for regulatory compliance)?

**Impact:**
- Long retention: Azure Blob Archive tier ($0.002/GB) can reduce costs 90%
- Lifecycle policies can automatically tier cold data

**Recommendation:** Implement blob lifecycle policies for cost optimization

**Answer:** Add to open questions sections. with recomendation
---

### 7. Acceptable Azure Monthly Cost

**Question:** What is the acceptable monthly Azure infrastructure cost?

**Options:**
- **Budget <$400/month:** Option A (Redis + SQL Database) or Option B-Azure (Blob + SQL Database)
- **Budget $400-1000/month:** Option C-Azure (Redis + Blob + SQL Database)
- **Budget >$1500/month:** Azure SQL Managed Instance becomes viable

**Recommendation:** Start with Option A or B-Azure (<$400/month), scale as needed

**Answer:** Less than 1500, we will go with Blob