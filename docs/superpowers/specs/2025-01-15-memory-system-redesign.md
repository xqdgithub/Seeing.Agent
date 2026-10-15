# Seeing.Agent.Memory 系统重设计规范

**版本**: 1.0  
**日期**: 2025-01-15  
**状态**: 已批准  
**作者**: AI Assistant

---

## 一、概述

### 1.1 背景

Seeing.Agent.Memory 是 Seeing.Agent 项目的记忆管理模块。当前系统存在以下核心问题：

1. **检索能力弱** - 仅支持遍历 + 元数据过滤，O(n) 复杂度
2. **无记忆演化机制** - 直接存储原始内容，无质量提升
3. **无知识图谱支持** - 记忆之间无关联，形成知识孤岛
4. **无 LLM 集成** - 无法利用 LLM 能力提取关键事实

### 1.2 目标

参考 ReMe (Python) 项目的设计理念，全面重写记忆系统，实现：

- **混合检索能力** - 向量语义检索 + BM25 关键词检索 + 图谱扩展
- **记忆演化机制** - LLM 增强的记忆提取、整合、合并
- **知识图谱支持** - Wikilink 链接、图谱遍历、关联发现
- **成本控制机制** - 缓存、批量、频率限制、配额管理

### 1.3 设计决策

| 决策项 | 选择 | 理由 |
|--------|------|------|
| 改造方案 | 全面重写 | 架构更清晰，完全对齐 ReMe 设计理念 |
| 文件格式 | ReMe 格式 | 更灵活，支持 Wikilink，用户可直接编辑 |
| 向量存储 | SQLite + sqlite-vec | 无外部依赖，统一存储 |
| 关键词索引 | SQLite FTS5 | 成熟稳定，内置支持 |
| 图谱存储 | SQLite 邻接表 | 简单高效，适合小规模图谱 |
| LLM 集成 | 复用 ILlmClient | 与现有架构一致 |
| Embedding | 抽象接口 | 支持用户配置不同模型 |
| 向后兼容 | 允许破坏性变更 | 简化设计，无需兼容旧接口 |

---

## 二、整体架构

### 2.1 架构分层

```
┌─────────────────────────────────────────────────────────────────┐
│                     Integration Layer                            │
│  MemoryTools (Tool 注解)  │  MemoryHooks (Hook 集成)            │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Service Layer                               │
│  MemoryService (核心服务，协调所有子模块)                         │
│  MemoryEvolutionService (记忆演化：提取、合并、整合)              │
│  MemoryRetrievalService (混合检索：向量+BM25+图谱)                │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Storage Layer                               │
│  FileStore (Markdown 文件存储，ReMe 格式)                        │
│  VectorIndex (向量索引，sqlite-vec)                              │
│  KeywordIndex (BM25 索引，SQLite FTS5)                           │
│  MemoryGraph (知识图谱，SQLite 邻接表)                           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Infrastructure Layer                          │
│  IEmbeddingService (Embedding 抽象，支持多 Provider)             │
│  ILlmClient (复用现有接口)                                       │
│  IEventPublisher (事件发布，用于缓存失效等)                       │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 核心设计决策

| 决策 | 选择 | 理由 |
|------|------|------|
| **文件格式** | ReMe 格式 | 更灵活，支持 Wikilink，用户可直接编辑 |
| **索引存储** | SQLite + sqlite-vec + FTS5 | 统一存储，无外部依赖 |
| **图谱存储** | SQLite 邻接表 | 简单高效，适合小规模图谱 |
| **LLM 集成** | 复用 ILlmClient | 与现有架构一致 |
| **Embedding** | 抽象接口 | 支持用户配置不同模型 |

---

## 三、核心接口设计

### 3.1 存储层接口

```csharp
/// <summary>
/// 文件存储接口 - ReMe 格式的 Markdown 文件存储
/// </summary>
public interface IFileStore
{
    // ===== 文件操作 =====
    Task<FileNode> WriteAsync(string path, string content, CancellationToken ct = default);
    Task<FileNode?> ReadAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    
    // ===== 列表操作 =====
    Task<IReadOnlyList<FileNode>> ListAsync(string? pattern = null, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> ListByPrefixAsync(string prefix, CancellationToken ct = default);
    
    // ===== 批量操作 =====
    Task WriteBatchAsync(IEnumerable<(string path, string content)> items, CancellationToken ct = default);
    
    // ===== 变更监听 =====
    IObservable<FileChangeEventArgs> Changes { get; }
}

/// <summary>
/// 文件节点 - 代表一个记忆文件
/// </summary>
public record FileNode(
    string Path,                    // 相对路径 (如: daily/2025-01-15/session-abc.md)
    string Content,                 // Markdown 内容
    DateTimeOffset ModifiedTime,    // 修改时间
    FileMetadata Metadata,          // YAML frontmatter 元数据
    IReadOnlyList<string> Links     // [[wikilink]] 列表
);

/// <summary>
/// 文件元数据 (YAML frontmatter)
/// </summary>
public record FileMetadata(
    string Id,                      // 唯一 ID
    string Type,                    // 类型: session, daily, digest
    string? Title,                  // 标题
    IReadOnlyList<string> Tags,     // 标签
    double Importance,              // 重要性
    double Confidence,              // 置信度
    DateTimeOffset CreatedAt,       // 创建时间
    DateTimeOffset? ExpiresAt       // 过期时间
);
```

### 3.2 索引接口

```csharp
/// <summary>
/// 混合检索索引接口
/// </summary>
public interface IMemoryIndex
{
    // ===== 索引操作 =====
    Task IndexAsync(FileNode node, CancellationToken ct = default);
    Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task RebuildAsync(CancellationToken ct = default);
    
    // ===== 检索操作 =====
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query, 
        CancellationToken ct = default);
    
    // ===== 统计 =====
    Task<IndexStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// 搜索查询
/// </summary>
public record SearchQuery(
    string Text,                    // 查询文本
    SearchMode Mode,                // 检索模式: Vector, Keyword, Hybrid
    int Limit = 10,                 // 返回数量
    double VectorWeight = 0.5,      // 向量权重
    string? TypeFilter = null,      // 类型过滤
    IReadOnlyList<string>? TagFilter = null  // 标签过滤
);

/// <summary>
/// 搜索结果
/// </summary>
public record SearchHit(
    FileNode Node,                  // 文件节点
    double Score,                   // 综合得分
    double VectorScore,             // 向量得分
    double KeywordScore,            // 关键词得分
    string? Highlight               // 高亮片段
);

public enum SearchMode { Vector, Keyword, Hybrid }
```

### 3.3 图谱接口

```csharp
/// <summary>
/// 记忆图谱接口
/// </summary>
public interface IMemoryGraph
{
    // ===== 链接操作 =====
    Task AddLinkAsync(string fromPath, string toPath, string? relation = null, CancellationToken ct = default);
    Task RemoveLinkAsync(string fromPath, string toPath, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryLink>> GetOutLinksAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryLink>> GetInLinksAsync(string path, CancellationToken ct = default);
    
    // ===== 遍历操作 =====
    Task<IReadOnlyList<GraphNode>> TraverseAsync(
        string startPath, 
        int depth = 2, 
        TraverseDirection direction = TraverseDirection.Both,
        CancellationToken ct = default);
    
    // ===== 解析 =====
    IReadOnlyList<string> ParseWikilinks(string content);
    
    // ===== 统计 =====
    Task<GraphStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// 记忆链接
/// </summary>
public record MemoryLink(
    string FromPath,
    string ToPath,
    string? Relation,
    double Weight
);

/// <summary>
/// 图节点 (遍历结果)
/// </summary>
public record GraphNode(
    FileNode Node,
    int Depth,
    string? Relation
);

public enum TraverseDirection { Out, In, Both }
```

### 3.4 演化接口

```csharp
/// <summary>
/// 记忆演化接口 - LLM 增强的记忆提取和整合
/// </summary>
public interface IMemoryEvolution
{
    // ===== 从对话提取 =====
    Task<IReadOnlyList<FileNode>> ExtractFromSessionAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default);
    
    // ===== 从 Daily 整合到 Digest =====
    Task<IReadOnlyList<FileNode>> ConsolidateDailyAsync(
        DateTimeOffset date,
        CancellationToken ct = default);
    
    // ===== 合并相似记忆 =====
    Task<FileNode> MergeAsync(
        IReadOnlyList<FileNode> nodes,
        CancellationToken ct = default);
    
    // ===== 成本控制 =====
    EvolutionStats GetStats();
}

/// <summary>
/// 演化统计 (用于成本控制)
/// </summary>
public record EvolutionStats(
    int TotalExtractions,           // 总提取次数
    int TotalConsolidations,        // 总整合次数
    int TotalTokensUsed,            // 总 Token 消耗
    int CacheHits,                  // 缓存命中次数
    DateTimeOffset LastExecution    // 最后执行时间
);
```

### 3.5 Embedding 接口

```csharp
/// <summary>
/// Embedding 服务接口 - 支持多种 Provider
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// 获取单个文本的 Embedding
    /// </summary>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default);
    
    /// <summary>
    /// 批量获取 Embedding (更高效)
    /// </summary>
    Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Embedding 维度
    /// </summary>
    int Dimensions { get; }
    
    /// <summary>
    /// Provider 名称
    /// </summary>
    string ProviderName { get; }
}

/// <summary>
/// Embedding 结果
/// </summary>
public record EmbeddingResult(
    string Text,                    // 原始文本
    float[] Vector,                 // 向量
    int TokenCount                  // Token 数量
);
```

### 3.6 统一服务接口

```csharp
/// <summary>
/// 记忆服务接口 - 统一的入口点
/// </summary>
public interface IMemoryService
{
    // ===== 写入操作 =====
    Task<FileNode> WriteAsync(string path, string content, CancellationToken ct = default);
    Task<FileNode> WriteMemoryAsync(MemoryContent memory, CancellationToken ct = default);
    
    // ===== 读取操作 =====
    Task<FileNode?> ReadAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> ListAsync(string? pattern = null, CancellationToken ct = default);
    
    // ===== 搜索操作 =====
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default);
    
    // ===== 图谱操作 =====
    Task<IReadOnlyList<GraphNode>> TraverseAsync(string path, int depth = 2, CancellationToken ct = default);
    
    // ===== 演化操作 =====
    Task EvolveFromSessionAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
    Task ConsolidateDailyAsync(DateTimeOffset date, CancellationToken ct = default);
    
    // ===== 生命周期 =====
    Task InitializeAsync(CancellationToken ct = default);
    Task ShutdownAsync(CancellationToken ct = default);
}

/// <summary>
/// 记忆内容 (用于创建记忆)
/// </summary>
public record MemoryContent(
    string? Path,                   // 自定义路径 (可选)
    MemoryType Type,                // 类型
    string Title,                   // 标题
    string Content,                 // Markdown 内容
    IReadOnlyList<string>? Tags,    // 标签
    double Importance = 0.5,        // 重要性
    double Confidence = 1.0         // 置信度
);

public enum MemoryType { Session, Daily, Digest }
```

---

## 四、数据模型与存储格式

### 4.1 文件存储结构

采用 ReMe 的目录结构：

```
<workspace_dir>/
├── session/                           # 原始对话记录
│   └── <session_id>.md                # 单个会话记录
│
├── daily/                             # 每日浅加工记忆
│   └── YYYY-MM-DD/                    # 日期目录
│       ├── <session_id>.md            # 会话摘要
│       └── <resource_name>.md         # 资源解读
│
├── digest/                            # 长期记忆 (LLM 整合)
│   ├── personal/                      # 个人偏好、事实
│   ├── procedure/                     # 可复用流程、经验
│   └── wiki/                          # 通用知识节点
│
└── .index/                            # 索引存储 (隐藏目录)
    ├── vectors.db                     # 向量索引
    ├── keywords.db                    # BM25 索引 (FTS5)
    └── graph.db                       # 图谱存储
```

### 4.2 文件格式示例

```markdown
---
id: mem_abc123
type: daily
title: "API 配置最佳实践"
tags:
  - api
  - configuration
  - best-practices
importance: 0.85
confidence: 0.95
created_at: 2025-01-15T10:30:00Z
expires_at:
---

# API 配置最佳实践

用户询问了关于 REST API 配置的最佳实践，主要建议包括：

1. 使用环境变量管理敏感配置
2. 实现配置热加载机制
3. 添加配置验证层

相关文档: [[digest/wiki/rest-api-design]]
参考案例: [[daily/2025-01-10/api-debug-session]]
```

### 4.3 YAML Frontmatter 规范

```yaml
id: string                    # 必填，唯一标识符
type: string                  # 必填，session | daily | digest
title: string                 # 必填，标题
tags: string[]                # 可选，标签列表
importance: number            # 可选，重要性 0.0-1.0，默认 0.5
confidence: number            # 可选，置信度 0.0-1.0，默认 1.0
created_at: datetime          # 必填，创建时间 (ISO 8601)
expires_at: datetime          # 可选，过期时间
links: string[]               # 可选，显式链接列表 (自动解析 [[wikilink]])
custom_fields: object         # 可选，自定义扩展字段
```

### 4.4 SQLite 索引 Schema

**向量索引:**

```sql
CREATE TABLE vectors (
    path TEXT PRIMARY KEY,
    embedding BLOB NOT NULL,        -- float32 数组
    dimensions INTEGER NOT NULL,
    created_at TEXT NOT NULL
);
```

**关键词索引 (FTS5):**

```sql
CREATE VIRTUAL TABLE keywords USING fts5(
    path,
    title,
    content,
    tags,
    tokenize='porter unicode61'
);
```

**图谱存储:**

```sql
CREATE TABLE links (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    from_path TEXT NOT NULL,
    to_path TEXT NOT NULL,
    relation TEXT,
    weight REAL DEFAULT 1.0,
    created_at TEXT NOT NULL,
    UNIQUE(from_path, to_path, relation)
);

CREATE INDEX idx_links_from ON links(from_path);
CREATE INDEX idx_links_to ON links(to_path);
```

---

## 五、自动初始化机制

### 5.1 Schema 自动管理

```csharp
/// <summary>
/// SQLite Schema 管理器 - 自动创建和迁移表结构
/// </summary>
internal class SchemaManager
{
    private readonly SqliteConnection _connection;
    
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var tx = await _connection.BeginTransactionAsync(ct);
        await CreateVersionTableAsync(ct);
        var currentVersion = await GetSchemaVersionAsync(ct);
        await MigrateAsync(currentVersion, ct);
        await tx.CommitAsync(ct);
    }
}
```

### 5.2 迁移脚本

```csharp
internal interface IMigration
{
    int Version { get; }
    Task ExecuteAsync(SqliteConnection connection, CancellationToken ct);
}

internal class V1_InitialSchema : IMigration
{
    public int Version => 1;
    
    public async Task ExecuteAsync(SqliteConnection connection, CancellationToken ct)
    {
        // 创建所有表 (vectors, keywords FTS5, links)
        // 创建索引
    }
}
```

### 5.3 初始化流程

```
MemoryService.InitializeAsync()
    │
    ▼
检查 .index/ 目录 → 不存在则创建
    │
    ▼
打开 SQLite 连接 (自动创建文件)
    │
    ▼
SchemaManager.EnsureSchemaAsync()
    │
    ▼
加载 sqlite-vec 扩展 (如使用向量功能)
    │
    ▼
重建内存缓存 (从现有 Markdown 文件)
```

### 5.4 配置选项

```csharp
public class MemoryStoreOptions
{
    public string WorkspacePath { get; set; } = "~/.seeing/memory";
    public bool AutoCreateDirectory { get; set; } = true;
    public bool AutoInitializeSchema { get; set; } = true;
    public bool EnableVectorIndex { get; set; } = true;
    public bool EnableKeywordIndex { get; set; } = true;
    public bool EnableGraph { get; set; } = true;
}
```

---

## 六、成本控制机制

### 6.1 架构

```
┌─────────────────────────────────────────────────────────────────┐
│ EmbeddingService                                                │
│   ┌─────────────┐    ┌─────────────┐    ┌────────────┐         │
│   │EmbeddingCache│───►│ BatchQueue  │───►│ RateLimiter│         │
│   └─────────────┘    └─────────────┘    └────────────┘         │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ MemoryEvolutionService                                          │
│   ┌─────────────┐    ┌─────────────┐    ┌────────────┐         │
│   │ExtractionCache│──►│TokenTracker │───►│ QuotaManager│        │
│   └─────────────┘    └─────────────┘    └────────────┘         │
└─────────────────────────────────────────────────────────────────┘
```

### 6.2 Embedding 缓存

```csharp
public interface IEmbeddingCache
{
    Task<CacheEntry?> TryGetAsync(string textHash, CancellationToken ct = default);
    Task SetAsync(string textHash, float[] embedding, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    CacheStats GetStats();
}
```

**缓存策略：**
- 使用文本 SHA256 哈希作为 Key
- 默认缓存大小：10000 条 (可配置)
- 过期策略：LRU + 7 天未访问自动清理
- 持久化：可选 (SQLite 本地存储)

### 6.3 批量队列

```csharp
public interface IBatchQueue
{
    Task<BatchItem> EnqueueAsync(string text, CancellationToken ct = default);
    int QueueSize { get; }
}
```

**批量策略：**
- 批量窗口：100ms (可配置)
- 最大批量：100 条 (可配置)
- 自动合并请求

### 6.4 频率限制

```csharp
public interface IRateLimiter
{
    Task WaitForQuotaAsync(int tokens, CancellationToken ct = default);
    QuotaStatus GetStatus();
}

public class RateLimiterOptions
{
    public int MaxRequestsPerMinute { get; set; } = 500;
    public int MaxTokensPerMinute { get; set; } = 100000;
    public bool Enabled { get; set; } = true;
}
```

### 6.5 Token 追踪与配额管理

```csharp
public interface ITokenTracker
{
    Task RecordAsync(TokenUsage usage, CancellationToken ct = default);
    Task<TokenStatistics> GetStatisticsAsync(TimeSpan? period = null, CancellationToken ct = default);
    int GetCurrentPeriodUsage();
}

public interface IQuotaManager
{
    Task<bool> CheckQuotaAsync(int estimatedTokens, CancellationToken ct = default);
    Task ConsumeAsync(int tokens, CancellationToken ct = default);
    QuotaState GetState();
}

public class QuotaOptions
{
    public int DailyTokenLimit { get; set; } = 100000;
    public bool Enabled { get; set; } = true;
    public QuotaExceededBehavior OnExceeded { get; set; } = QuotaExceededBehavior.Throw;
}

public enum QuotaExceededBehavior { Throw, Skip, Warn }
```

### 6.6 成本控制配置

```csharp
public class CostControlOptions
{
    public CacheOptions Cache { get; set; } = new();
    public BatchOptions Batch { get; set; } = new();
    public RateLimiterOptions RateLimiter { get; set; } = new();
    public QuotaOptions Quota { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

public class CacheOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 10000;
    public TimeSpan Expiration { get; set; } = TimeSpan.FromDays(7);
    public bool Persist { get; set; } = true;
}

public class BatchOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Window { get; set; } = TimeSpan.FromMilliseconds(100);
    public int MaxBatchSize { get; set; } = 100;
}
```

---

## 七、集成与生命周期管理

### 7.1 DI 注册设计

```csharp
public static class MemoryServiceExtensions
{
    public static IServiceCollection AddMemorySystem(
        this IServiceCollection services,
        Action<MemoryOptions>? configure = null)
    {
        // 1. 配置选项
        services.Configure<MemoryOptions>(options => configure?.Invoke(options));
        
        // 2. 核心服务
        services.AddSingleton<IMemoryService, MemoryService>();
        
        // 3. 存储层
        services.AddSingleton<IFileStore, LocalFileStore>();
        
        // 4. 索引层
        services.AddSingleton<IMemoryIndex, HybridMemoryIndex>();
        
        // 5. 图谱层
        services.AddSingleton<IMemoryGraph, SqliteMemoryGraph>();
        
        // 6. 演化层
        services.AddSingleton<IMemoryEvolution, LlmMemoryEvolution>();
        
        // 7. Embedding 服务
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IEmbeddingCache, SqliteEmbeddingCache>();
        
        // 8. 成本控制
        services.AddSingleton<IBatchQueue, EmbeddingBatchQueue>();
        services.AddSingleton<IRateLimiter, TokenBucketRateLimiter>();
        services.AddSingleton<ITokenTracker, SqliteTokenTracker>();
        services.AddSingleton<IQuotaManager, DailyQuotaManager>();
        
        // 9. 后台服务
        services.AddHostedService<MemoryIndexingService>();
        services.AddHostedService<MemoryConsolidationService>();
        
        return services;
    }
}
```

### 7.2 生命周期流程

```
应用启动
    │
    ▼
MemoryIndexingService (IHostedService)
    ├── 检查/创建工作空间目录
    ├── 初始化 SQLite 连接
    ├── 执行 Schema 迁移
    ├── 加载 sqlite-vec 扩展
    ├── 扫描现有 Markdown 文件
    └── 重建内存索引
    │
    ▼
MemoryConsolidationService (IHostedService)
    └── 启动定时任务 (每日整合、索引优化、缓存清理)
    │
    ▼
系统就绪
    │
    ▼
运行时
    ├── 写入: FileStore → Index → Graph (并行)
    ├── 检索: Index → Graph Expansion
    └── 演化: EvolutionService (异步)
    │
    ▼
应用关闭
    └── 清理资源 (刷新缓存、关闭连接、等待后台任务)
```

### 7.3 后台服务

```csharp
/// <summary>
/// 索引后台服务 - 处理索引更新和重建
/// </summary>
public class MemoryIndexingService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeAsync(stoppingToken);
        await foreach (var change in _fileStore.Changes.WithCancellation(stoppingToken))
        {
            await HandleFileChangeAsync(change, stoppingToken);
        }
    }
}

/// <summary>
/// 整合后台服务 - 定时执行每日整合
/// </summary>
public class MemoryConsolidationService : BackgroundService
{
    private readonly TimeSpan _consolidationTime = TimeSpan.FromHours(2);
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 定时执行每日整合
    }
}
```

### 7.4 错误处理策略

```csharp
public class MemoryErrorHandling
{
    public FallbackStrategy EmbeddingFallback { get; set; } = FallbackStrategy.UseKeywordOnly;
    public FallbackStrategy LlmFallback { get; set; } = FallbackStrategy.SkipEvolution;
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

public enum FallbackStrategy { Throw, UseKeywordOnly, SkipEvolution, DisableFeature }
```

### 7.5 完整配置类

```csharp
public class MemoryOptions
{
    public MemoryStoreOptions Store { get; set; } = new();
    public MemoryIndexOptions Index { get; set; } = new();
    public MemoryGraphOptions Graph { get; set; } = new();
    public MemoryEvolutionOptions Evolution { get; set; } = new();
    public EmbeddingOptions Embedding { get; set; } = new();
    public CostControlOptions CostControl { get; set; } = new();
    public MemoryErrorHandling ErrorHandling { get; set; } = new();
}
```

---

## 八、WebUI 页面设计

### 8.1 页面结构

```
/memory                          记忆首页 (改造)
├── 搜索面板 (混合检索)
├── 记忆列表 (按类型分组)
└── 快速操作

/memory/browse                  记忆浏览 (新增)
├── 目录树导航
└── 文件预览

/memory/graph                   知识图谱 (新增)
├── 图谱可视化
├── 节点详情
└── 关系探索

/memory/{path*}                 记忆详情 (新增)
├── Markdown 渲染
├── 元数据编辑
├── 链接展示 (Wikilink)
└── 反向链接

/memory/stats                   统计分析 (新增)
├── 记忆分布
├── Token 使用量
└── 成本统计
```

### 8.2 记忆首页改造

- 混合检索面板 (向量/关键词/混合模式选择)
- 按类型分组的记忆卡片
- 统计卡片 (记忆总数、链接数、向量索引数)

### 8.3 知识图谱页面

- 图谱可视化画布 (力导向布局)
- 节点详情面板
- 出边/入边链接展示
- 相关记忆推荐

### 8.4 记忆详情页面

- Markdown 渲染
- Wikilink 解析和渲染
- 元数据编辑器
- 链接面板 (出边/入边)
- 操作按钮 (编辑、触发演化、删除)

### 8.5 目录浏览页面

- 左侧目录树 (session/daily/digest)
- 右侧文件列表
- 支持列表/卡片视图切换

### 8.6 统计分析页面

- 统计卡片 (记忆总数、链接数、向量索引、Token 使用)
- 记忆分布图表
- Token 使用趋势
- 每日配额进度
- 检索统计

### 8.7 新增组件清单

| 组件 | 用途 | 文件路径 |
|------|------|----------|
| `MemoryTypeSection` | 按类型分组的记忆卡片列表 | `Components/Memory/MemoryTypeSection.razor` |
| `GraphCanvas` | 图谱可视化画布 | `Components/Memory/GraphCanvas.razor` |
| `NodeDetailPanel` | 图谱节点详情面板 | `Components/Memory/NodeDetailPanel.razor` |
| `MarkdownRenderer` | Markdown 渲染器 | `Components/Memory/MarkdownRenderer.razor` |
| `WikilinkRenderer` | Wikilink 解析渲染 | `Components/Memory/WikilinkRenderer.razor` |
| `MetadataEditor` | 元数据编辑器 | `Components/Memory/MetadataEditor.razor` |
| `DirectoryTree` | 目录树导航 | `Components/Memory/DirectoryTree.razor` |
| `FileList` | 文件列表 | `Components/Memory/FileList.razor` |
| `StatCard` | 统计卡片 | `Components/StatCard.razor` |
| `SearchPanel` | 搜索面板 | `Components/Memory/SearchPanel.razor` |

---

## 九、实施计划

### 9.1 分阶段交付

| Phase | 内容 | 工作量 |
|-------|------|--------|
| Phase 1 | 存储层 + 索引层 | 1.5 周 |
| Phase 2 | 图谱层 + 演化层 | 1.5 周 |
| Phase 3 | 成本控制 + 集成 | 1 周 |
| Phase 4 | WebUI 页面 | 1 周 |
| Phase 5 | 测试 + 文档 | 0.5 周 |

**总工作量**: 约 5.5 周

### 9.2 Phase 1 详细任务

- 定义核心接口 (IFileStore, IMemoryIndex, IMemoryService)
- 实现 LocalFileStore (ReMe 格式存储)
- 实现 HybridMemoryIndex (向量 + BM25)
- Schema 自动初始化
- 单元测试

### 9.3 Phase 2 详细任务

- 实现 IMemoryGraph 接口
- 实现 SqliteMemoryGraph
- 实现 IMemoryEvolution 接口
- 实现 LlmMemoryEvolution
- 提示词模板设计
- 单元测试

### 9.4 Phase 3 详细任务

- 实现成本控制组件
- DI 注册扩展
- 后台服务
- 错误处理
- 集成测试

### 9.5 Phase 4 详细任务

- 改造 MemoryPage.razor
- 新增 GraphPage.razor
- 新增 MemoryDetailPage.razor
- 新增 BrowsePage.razor
- 新增 StatsPage.razor
- 新增组件开发

### 9.6 Phase 5 详细任务

- 集成测试
- 性能测试
- 文档编写
- 代码审查

---

## 十、测试策略

### 10.1 单元测试

| 测试类 | 测试重点 |
|--------|---------|
| `LocalFileStoreTests` | 文件读写、YAML 解析、Wikilink 提取 |
| `HybridMemoryIndexTests` | 向量检索、BM25 检索、RRF 融合 |
| `SqliteMemoryGraphTests` | 链接操作、图谱遍历、Wikilink 解析 |
| `LlmMemoryEvolutionTests` | 对话提取、整合、合并 |
| `EmbeddingCacheTests` | 缓存命中、过期、持久化 |
| `QuotaManagerTests` | 配额检查、消耗、重置 |

### 10.2 性能基准

| 指标 | 目标值 |
|------|--------|
| 向量检索延迟 | < 100ms (1000 条记忆) |
| BM25 检索延迟 | < 50ms (1000 条记忆) |
| 混合检索延迟 | < 150ms (1000 条记忆) |
| 索引构建时间 | < 10s (1000 条记忆) |
| 图谱遍历延迟 | < 50ms (深度 3) |
| LLM 提取延迟 | < 5s (10 轮对话) |

### 10.3 集成测试场景

- 完整检索流程 (写入 → 索引 → 检索)
- 图谱发现流程 (写入 → 链接 → 遍历)
- 记忆演化流程 (对话 → 提取 → 整合)
- 成本控制流程 (配额 → 消耗 → 限制)

---

## 十一、风险与缓解

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|----------|
| sqlite-vec 集成难度 | 中 | 中 | 使用成熟的 NuGet 包，提供降级方案 |
| Embedding API 成本 | 高 | 中 | 缓存机制、批量请求、可选启用 |
| 图谱查询性能 | 低 | 低 | SQLite 邻接表适合小规模图谱 |
| LLM 调用延迟 | 中 | 中 | 异步处理、后台执行 |

---

## 十二、附录

### 12.1 配置示例

```json
{
  "Memory": {
    "Store": {
      "WorkspacePath": "~/.seeing/memory",
      "AutoCreateDirectory": true
    },
    "Index": {
      "EnableVectorIndex": true,
      "EnableKeywordIndex": true
    },
    "Graph": {
      "Enabled": true
    },
    "Evolution": {
      "Enabled": true,
      "LlmProviderId": "default",
      "AutoConsolidate": true
    },
    "Embedding": {
      "Provider": "OpenAI",
      "Model": "text-embedding-3-small",
      "Dimensions": 1536
    },
    "CostControl": {
      "Enabled": true,
      "Cache": { "MaxEntries": 10000 },
      "Quota": { "DailyTokenLimit": 100000 }
    }
  }
}
```

### 12.2 参考项目

- **ReMe**: https://github.com/owner/ReMe (Python 记忆管理系统)
- **sqlite-vec**: https://github.com/asg017/sqlite-vec (SQLite 向量扩展)
