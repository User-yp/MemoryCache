# MemoryCache —— 通用实体内存缓存组件

面向 **.NET 8** 的进程内实体缓存类库，解决“开关、配置”类小表读多写少、重复查询
浪费数据库 IO 的问题。核心思路是把这类表**全量镜像**到内存：业务查询只读内存快照、
永不触库；数据变化后按单个实体（部分刷新）或全部实体（全量刷新）重新加载。

> 当前版本：`0.1.0`（开发中，未发布到 NuGet 源）。
> 详细设计背景与演进过程见 [docs/开发设计文档.md](docs/开发设计文档.md)。

---

## 目录

- [1. 特性与定位](#1-特性与定位)
- [2. 快速开始](#2-快速开始)
- [3. 核心概念](#3-核心概念)
- [4. 实体定义与键](#4-实体定义与键)
- [5. 注册与配置](#5-注册与配置)
- [6. 查询](#6-查询)
- [7. 刷新与失效](#7-刷新与失效)
- [8. 日志与指标](#8-日志与指标)
- [9. 项目结构](#9-项目结构)
- [10. 示例与基准](#10-示例与基准)
- [11. 开发与验证](#11-开发与验证)
- [12. 设计约定与演进](#12-设计约定与演进)
- [13. 常见问题](#13-常见问题)

---

## 1. 特性与定位

### 1.1 核心特性

- **表级快照**：每类实体维护一份不可变快照（条目数组 + 键索引 + 版本号），
  读路径无锁；
- **ORM 无关**：组件不依赖任何 ORM，取数逻辑由使用方通过
  `IEntityLoader<TEntity>` 提供（EF Core、Dapper、MySqlConnector、远程 API 均可）；
- **一行注册**：`services.AddEntityMemoryCache(...)` 开启服务；
- **两种装配方式**：委托式 `AddEntity<T>(...)` 与
  `[CacheEntity]` + `ScanAssembly` 自动扫描；
- **复合键**：支持逗号分隔的复合属性键，查询可用
  `GetByKey(("Group1", "TestJob"))` 元组形式；
- **单飞刷新**：同一实体的并发刷新只执行一次加载；
- **失败保护**：刷新失败保留上一份快照继续服务，只记录错误；
- **失效语义**：`ReloadAsync` 主动刷新、`Invalidate` 后台刷新或仅标记失效、
  可选定时刷新兜底；
- **启动预热**：应用启动后自动加载全部已注册实体（可配置）；
- **可观测性**：内置日志与 `System.Diagnostics.Metrics` 指标；
- **包拆分**：`MemoryCache.Abstractions`（零依赖契约）+ `MemoryCache`（实现）。

### 1.2 本期明确不做

- 分布式 / Redis / 多进程共享缓存；
- 逐条过期、LRU、容量淘汰（组件面向小表，超阈值只告警）；
- 写穿、双写一致性与数据库变更自动感知（CDC/触发器）；
- 跨实体的事务性一致快照；
- 超大表缓存（超过 5 万条默认告警，需自行评估）。

---

## 2. 快速开始

### 2.1 环境要求

- .NET 8 SDK / 运行时；
- 一个 DI 容器（微软默认 `ServiceCollection` 即可）。

### 2.2 引入包

实现包尚未发布，当前直接通过工程引用使用：

```text
src/MemoryCache.Abstractions     # 契约包
src/MemoryCache                  # 实现包
```

`MemoryCache.csproj` 会自动带上对 `Abstractions` 的工程引用；业务工程只需引用
`MemoryCache`（并自行添加所用 ORM 的包）。

未来发布后可执行：

```bash
dotnet add package MemoryCache
```

### 2.3 最小示例（委托式 + 内存假数据）

```csharp
using MemoryCache;
using MemoryCache.Abstractions;
using Microsoft.Extensions.DependencyInjection;

// 1. 实体
[CacheEntity(KeyProperty = nameof(AppSwitch.Code))]
public sealed class AppSwitch
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Enabled { get; set; }
}

// 2. 加载器：内部可以是任何数据源
public sealed class AppSwitchLoader : IEntityLoader<AppSwitch>
{
    public Task<IReadOnlyCollection<AppSwitch>> LoadAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyCollection<AppSwitch>>(
        [
            new AppSwitch { Code = "Feature:Report", Value = "on", Enabled = true },
        ]);
}

// 3. 注册（一行开启 + 声明实体）
var services = new ServiceCollection();
services.AddEntityMemoryCache(builder =>
{
    builder.AddEntity<AppSwitch>(entity => entity
        .WithKey(item => item.Code)
        .WithLoader(_ => new AppSwitchLoader()));
});
await using var provider = services.BuildServiceProvider();

// 4. 加载 + 查询
var cache = provider.GetRequiredService<IEntityCacheService>();
await cache.ReloadAsync<AppSwitch>();           // 首次全量加载
var row = cache.Get<AppSwitch>().GetByKey("Feature:Report"); // O(1) 键查询
var open = row?.Enabled == true;
Console.WriteLine($"Report 开关状态：{open}");
```

### 2.4 EF Core 示例（推荐生产用法）

完整可运行代码见 `samples/MemoryCache.Sample.EFCore`。要点：

```csharp
[CacheEntity(KeyProperty = "Group,JobKeyName")]
public sealed class JobConfig { /* 实体属性 */ }

public sealed class JobConfigLoader(IDbContextFactory<JobConfigDbContext> db)
    : IEntityLoader<JobConfig>
{
    public async Task<IReadOnlyCollection<JobConfig>> LoadAsync(CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.JobConfigs
            .AsNoTracking()
            .OrderBy(x => x.Group)
            .ToListAsync(ct);
    }
}

services.AddPooledDbContextFactory<JobConfigDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(9, 4, 0))));
services.AddSingleton<IEntityLoader<JobConfig>, JobConfigLoader>();

services.AddEntityMemoryCache(builder =>
    builder.ScanAssembly(typeof(JobConfig).Assembly));
```

---

## 3. 核心概念

| 概念 | 说明 |
| --- | --- |
| 实体（Entity） | 对应一张小表/一组配置的 POCO，如 `AppSwitch` |
| 缓存项（Cache Entry） | 每个实体类型一个 `IEntityCache<TEntity>` |
| 快照（Snapshot） | 某次成功加载后的不可变数据集（条目数组 + 键索引 + 版本号） |
| 版本（Version） | 每成功刷新一次自增 1，可判断数据是否换过 |
| 加载器（Loader） | 使用方提供的取数逻辑，ORM 由使用方自选 |
| 失效（Invalidate） | 通知缓存“数据已变化”，按策略立即刷新或仅标记 |
| 单飞（Single-flight） | 同一实体同一时刻只执行一次加载，并发请求共享结果 |

### 3.1 一致性模型

- **读者永远无锁**：查询通过 volatile 读取当前快照，看到的一定是某一次完整加载结果；
- **写者原子发布**：刷新时先构建全新快照，成功后一次替换，读者不会看到半新半旧数据；
- **失败不破坏服务**：刷新失败保留上一份快照，`LastError` 记录原因，下一次刷新自动恢复；
- **不承诺跨实体一致**：各实体独立加载、独立版本；
- **快照按只读使用**：组件保存引用并返回只读集合，业务代码不得修改缓存内对象。

---

## 4. 实体定义与键

### 4.1 `[CacheEntity]` 特性

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CacheEntityAttribute : Attribute
{
    public string? KeyProperty { get; set; }        // 键属性；复合键用逗号分隔
    public string? Name { get; set; }               // 注册名/日志标签，默认类型名
    public long CapacityWarningThreshold { get; set; } = 50_000; // 条目数告警阈值
    public double RefreshIntervalSeconds { get; set; }          // 0 = 不启用定时刷新
}
```

示例：

```csharp
// 单键
[CacheEntity(KeyProperty = nameof(AppSwitch.Code))]
public sealed class AppSwitch { ... }

// 复合键（GROUP + JOB_KEYNAME 两列作为主键）
[CacheEntity(KeyProperty = "Group,JobKeyName")]
public sealed class JobConfig { ... }
```

> 特性只声明元数据，**不负责取数**；加载逻辑必须由加载器提供。

### 4.2 键规则

1. 显式 `WithKey(string)` 或特性 `KeyProperty` 优先；
2. 未配置时按惯例使用名为 `Id` 的公共实例属性；
3. 复合键顺序即属性声明顺序，查询元组需保持一致；
4. 完全没有键的实体仍可注册（只做快照/全量扫描查询），键查询 API 会抛
   `NotSupportedException`；
5. 键不允许为 `null`；
6. 重复键默认记录警告并保留后加载的条目，可改为 `KeepFirst` 或 `Throw`
   （整次刷新失败，旧快照保留）。

### 4.3 快照可变性约定

缓存内对象按引用保存，`GetSnapshot()` 返回的列表与条目**都不应被修改**。
若业务确需改动后回写数据库，请复制实体或新建对象，再走“改库 → 刷新”流程。

---

## 5. 注册与配置

### 5.1 入口

```csharp
services.AddEntityMemoryCache(Action<EntityCacheBuilder>? configure = null);
```

- 重复调用注册服务会抛 `InvalidOperationException`；
- 同一实体被重复注册会抛 `InvalidOperationException`（启动期即可发现）；
- 服务以单例注册。

### 5.2 方式 A：委托式（每个实体显式配置）

```csharp
services.AddEntityMemoryCache(builder =>
{
    builder.AddEntity<AppSwitch>(entity => entity
        .WithKey(x => x.Code)                       // 键：委托或属性名
        .WithLoader(provider => new AppSwitchLoader( // 加载器工厂
            provider.GetRequiredService<IDbContextFactory<AppDbContext>>()))
        .WithDuplicateKeyPolicy(DuplicateKeyPolicy.Throw)
        .WithInvalidationMode(InvalidationMode.ReloadInBackground)
        .WithRefreshInterval(TimeSpan.FromMinutes(5)));
});
```

### 5.3 方式 B：`[CacheEntity]` + `ScanAssembly`

实体标记特性后，扫描程序集自动注册；加载器必须能从 DI 解析为
`IEntityLoader<TEntity>`，否则服务首次解析时抛异常（fail-fast）：

```csharp
services.AddSingleton<IEntityLoader<JobConfig>, JobConfigLoader>();

services.AddEntityMemoryCache(builder =>
{
    builder.ScanAssembly(typeof(JobConfig).Assembly);      // 一个程序集
    builder.ScanAssemblies(asm1, asm2);                   // 或一次多个
});
```

### 5.4 全局选项（`EntityCacheBuilder` 层）

| 方法 | 默认 | 说明 |
| --- | --- | --- |
| `WithWarmupOnStartup(bool)` | `true` | 启动后自动预热全部实体 |
| `WithWarmupTimeout(TimeSpan)` | `30s` | 预热总超时；`TimeSpan.Zero` 表示不超时 |
| `WithStartupFailurePolicy(StartupFailurePolicy)` | `Continue` | 预热失败继续启动或抛出 |
| `WithReloadAllParallelism(int)` | `min(4, CPU)` | 全量刷新并行度；`1` 表示严格串行 |
| `WithMetrics(bool)` | `true` | 是否启用 `System.Diagnostics.Metrics` |

```csharp
services.AddEntityMemoryCache(builder =>
{
    builder.AddEntity<AppSwitch>(...);
    builder.WithWarmupOnStartup();
    builder.WithStartupFailurePolicy(StartupFailurePolicy.Throw);
    builder.WithReloadAllParallelism(2);
    builder.WithMetrics();
});
```

### 5.5 实体级选项（`EntityCacheEntityBuilder<T>`）

| 方法 | 默认 | 说明 |
| --- | --- | --- |
| `WithKey(string)` | `Id` 惯例 | 指定键属性；复合键逗号分隔 |
| `WithKey(Func<T, object?>)` | — | 委托取键，优先级最高 |
| `WithLoader(Func<IServiceProvider, IEntityLoader<T>>)` | DI 解析 | 显式加载器工厂 |
| `WithDuplicateKeyPolicy(DuplicateKeyPolicy)` | `LogWarningAndKeepLast` | 重复键策略 |
| `WithInvalidationMode(InvalidationMode)` | `ReloadInBackground` | `Invalidate` 行为 |
| `WithRefreshInterval(TimeSpan)` | 不启用 | 后台定时刷新周期 |

### 5.6 DI 作用域与 DbContext

- `IEntityCacheService` 是单例；
- 每次刷新在**独立 DI 作用域**内解析加载器，加载结束后作用域立即释放；
- 因此作用域/瞬时加载器也能安全使用；
- EF Core 场景推荐 `AddPooledDbContextFactory<TContext>()` +
  `IDbContextFactory<TContext>`，加载时 `AsNoTracking()`。

---

## 6. 查询

### 6.1 获取缓存项

```csharp
var cacheService = serviceProvider.GetRequiredService<IEntityCacheService>();

IEntityCache<AppSwitch> appSwitchCache = cacheService.Get<AppSwitch>(); // 未注册会抛异常
bool registered = cacheService.IsRegistered<AppSwitch>();
```

### 6.2 `IEntityCache<TEntity>` 查询 API

| API | 说明 |
| --- | --- |
| `Version` | 成功刷新次数 |
| `Count` | 当前条目数 |
| `IsStale` | 是否已失效但尚未成功刷新 |
| `LoadedAt` | 最近一次成功加载时间（未加载为 `null`） |
| `LastError` | 最近一次刷新错误（无则 `null`） |
| `GetSnapshot()` | 当前快照（只读） |
| `AsQueryable()` | LINQ 内存查询入口 |
| `ContainsKey(key)` | 键是否存在 |
| `TryGetValue(key, out value)` | 尝试取值 |
| `GetByKey(key)` | 按键取值，未命中返回 `null` |
| `Reloaded` 事件 | 每次刷新成功/失败后触发 |

```csharp
var cache = cacheService.Get<AppSwitch>();

var all = cache.GetSnapshot();                                  // 整表镜像
var enabled = cache.AsQueryable().Where(x => x.Enabled).ToList();
var one = cache.GetByKey("Feature:Report");                     // 单键

var job = jobCache.GetByKey(("Group1", "TestJob"));             // 复合键：元组
bool has = jobCache.ContainsKey(("Group1", "TestJob"));
```

查询 API 全部是同步的，**只读内存、不打数据库、不触发刷新、不阻塞等待**。

---

## 7. 刷新与失效

### 7.1 刷新入口

| API | 粒度 | 行为 |
| --- | --- | --- |
| `IEntityCacheService.ReloadAsync<T>()` | 部分 | 强制刷新单个实体 |
| `IEntityCacheService.ReloadAsync(Type)` | 部分 | 运行时类型版 |
| `IEntityCacheService.ReloadAllAsync()` | 全量 | 受控并行刷新全部 |
| `IEntityCache<T>.ReloadAsync()` | 部分 | 同上 |
| `IEntityCache<T>.EnsureFreshAsync()` | 部分 | 仅在“未加载/已失效/上次失败”时刷新 |

### 7.2 写入方推荐流程

```csharp
// 改数据库
await db.AppSwitches.Where(x => x.Code == code)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value));

// 刷新缓存（等待完成，保证后续读一定能看到新值）
await cacheService.ReloadAsync<AppSwitch>();

// 一次改多个表
await cacheService.ReloadAllAsync();
```

### 7.3 失效模式

适合“不知道谁改了数据库，只有通知”的场景：

```csharp
cacheService.Invalidate<AppSwitch>();       // 默认：标记失效 + 后台立即刷新
cacheService.Invalidate(typeof(AppSwitch));
```

| `InvalidationMode` | `Invalidate` 行为 |
| --- | --- |
| `ReloadInBackground`（默认） | 标记失效并立即后台刷新 |
| `MarkStaleOnly` | 只标记，等 `EnsureFreshAsync()` 或显式刷新 |

`Invalidated` 服务级事件在失效行为执行前触发，便于监控。

### 7.4 启动预热与定时刷新

- **启动预热**：默认开启，由 `EntityCacheWarmupHostedService` 执行，配合
  Generic Host 使用（`builder.Build().Run()` 会自动启动 Hosted Service）；
- **定时刷新**：为实体配置 `WithRefreshInterval(...)` 后，
  `EntityCachePeriodicRefresher` 每秒检查一次到期实体并后台刷新；
- 单次定时刷新失败不影响后续周期，只会记录日志并保留旧快照。
- **预热超时**：`WarmupTimeout` 到期按**预热失败**处理，交由
  `StartupFailurePolicy` 决定行为——`Continue`（默认）记录告警后继续启动，
  `Throw` 抛出 `TimeoutException`。超时只是不再等待，未完成的加载仍会在后台
  跑完并填入缓存；宿主关闭导致的取消不算预热失败，会照常向上传播。

> 若只是 `ServiceProvider` + 手动管理（如示例工程），Hosted Service 不会自动运行；
> 请手动调用 `ReloadAsync` / `ReloadAllAsync`，或自行调度
> `GetServices<IHostedService>()`。

### 7.5 `Reloaded` 事件

```csharp
var cacheService = serviceProvider.GetRequiredService<IEntityCacheService>();
var cache = cacheService.Get<AppSwitch>();
cache.Reloaded += (_, args) =>
{
    Console.WriteLine(
        $"刷新完成：{args.PreviousVersion} → {args.NewVersion}，" +
        $"失败={args.Failed}，错误={args.Error?.Message}");
};
```

---

## 8. 日志与指标

### 8.1 日志

组件通过 `ILogger<T>` 记录：

- Debug：无（当前刷新日志以告警为主）；
- Warning：刷新失败（保留旧快照）、重复键、条目数超阈值、定时刷新失败；
- Information：启动预热完成。

接入任一日志提供者（Console / Serilog / NLog）后即可看到。

### 8.2 指标（`System.Diagnostics.Metrics`）

| 指标 | 类型 | 标签 | 说明 |
| --- | --- | --- | --- |
| `memorycache.reloads_total` | Counter | `entity`, `outcome` | 刷新次数 |
| `memorycache.reload_duration_seconds` | Histogram | `entity` | 刷新耗时 |
| `memorycache.snapshot_items` | ObservableGauge | `entity` | 当前条目数 |
| `memorycache.registered_types` | ObservableGauge | — | 已注册类型数 |

可通过 `MeterListener`、`System.Diagnostics.Metrics` 或 OpenTelemetry .NET 直接采集；
无监听器时接近零开销；`WithMetrics(false)` 可整体关闭。

---

## 9. 项目结构

```text
MemoryCache.sln
├─ docs/
│  ├─ 设计思路.txt                    # 原始需求
│  └─ 开发设计文档.md                 # 完整设计文档（含里程碑）
├─ src/
│  ├─ MemoryCache.Abstractions/       # 契约：特性、接口、事件（零依赖）
│  └─ MemoryCache/                    # 实现：快照、注册、刷新、Hosted Service
├─ tests/
│  └─ MemoryCache.Tests/              # xUnit 测试
├─ samples/
│  ├─ MemoryCache.Sample/             # MySqlConnector 直连样例
│  └─ MemoryCache.Sample.EFCore/      # EF Core + Pomelo 样例
├─ benchmarks/
│  └─ MemoryCache.Benchmark/          # 轻量基准
└─ artifacts/packages/                # dotnet pack 输出（不入库）
```

---

## 10. 示例与基准

### 10.1 运行 MySQL 样例

两个样例默认连接本机开发库：

```text
Server=127.0.0.1;Port=3306;Database=quartz;User=root;Password=1234;SslMode=None
```

可通过环境变量 `MEMORY_CACHE_MYSQL` 覆盖；生产环境请改用配置中心/环境变量注入，
不要把口令写死在代码里。

```bash
dotnet run --project samples/MemoryCache.Sample
dotnet run --project samples/MemoryCache.Sample.EFCore
```

预期输出（本机 `job_config` 表）：

```text
Loaded 1 row(s), Version=1, LoaderCalls=1
[Group1/TestJob] cron=aas, trigger=testname, enabled=False
Composite key lookup (Group1, TestJob) => aas
```

### 10.2 基准

```bash
dotnet run --project benchmarks/MemoryCache.Benchmark -c Release
```

本机（10,000 条模拟配置，Release）参考数据：

```text
GetByKey  100 万次   ≈ 4.6M ops/s
GetSnapshot 100 万次 ≈ 128M ops/s
AsQueryable 全量过滤 ≈ 0.5 ms/次
```

数值依机器与负载而异，仅作相对参考。

---

## 11. 开发与验证

```bash
# 还原时若离线环境缺少 HOME，先指定全局包目录：
# $env:NUGET_PACKAGES = 'C:\Users\N05029B\.nuget\packages'

dotnet build MemoryCache.sln
dotnet test  MemoryCache.sln
dotnet pack  src/MemoryCache/MemoryCache.csproj -c Release -o artifacts/packages
dotnet pack  src/MemoryCache.Abstractions/MemoryCache.Abstractions.csproj -c Release -o artifacts/packages
```

质量标准：构建 0 警告 0 错误；当前 37 个测试全部通过。

---

## 12. 设计约定与演进

### 12.1 里程碑状态

| 里程碑 | 内容 | 状态 |
| --- | --- | --- |
| M0 | 工程骨架 + 契约接口 | ✅ |
| M1 | 注册与装配（委托/扫描/DI 校验） | ✅ |
| M2 | 快照存储与查询（含复合键、单飞、失效） | ✅ |
| M3 | 预热、定时刷新、失败策略、受控并行 | ✅ |
| M4 | EF Core 集成示例与真实 MySQL 联调 | ✅ |
| M5 | 指标、日志、XML 文档、打包、基准 | ✅ |

### 12.2 命名与版本

- 命名空间/包名前缀暂为占位 `MemoryCache.*`，正式发布前可整体改为公司前缀。
- 版本 `0.1.0` 为开发版，尚未发布到任何 NuGet 源。

### 12.3 未来可扩展方向

- EF Core / Dapper 官方适配包；
- 读取时克隆（`CloneOnRead`）选项；
- 外部失效桥（MQ/管理后台 → `Invalidate`），支撑多实例部署；
- 分布式缓存后端抽象（把 `IEntityCacheService` 换成混合缓存实现）；
- 数据库版本号比对的增量刷新。

---

## 13. 常见问题

**Q1：复合键怎么查询？**

按特性声明的顺序传元组：

```csharp
// KeyProperty = "Group,JobKeyName"
var job = cache.Get<JobConfig>().GetByKey(("Group1", "TestJob"));
```

**Q2：刷新抛异常了怎么办？**

`ReloadAsync` 会把加载器异常抛给调用方（便于明确失败语义），同时旧快照仍可用、
`LastError` 已记录；定时/后台刷新则只记日志。业务上可捕获后走告警，
下一次 `EnsureFreshAsync`/定时任务会自动重试。

**Q3：为什么读总是旧数据？**

组件默认**读永不触库**；变更后请调用
`await cacheService.ReloadAsync<T>()`（等待完成）再继续读。
也可用 `Invalidate` + 后台刷新 + 定时刷新兜底。

**Q4：缓存条目能不能改？**

不能。快照按引用保存并只读使用；需要“改配置”时应更新数据库后刷新缓存。

**Q5：键没配置时为什么抛异常？**

键查询依赖索引；未配置键的实体只能使用 `GetSnapshot()`/`AsQueryable()`。

**Q6：DbContext 是 Scoped，为什么单例缓存能安全用？**

每次刷新都会新建独立 DI 作用域解析加载器并立即释放；
推荐再配合 `IDbContextFactory<TContext>` 使用。

**Q7：多实例部署怎么办？**

当前为进程内缓存，各实例各自维护一份。多实例需要外部通知：
数据库变更后通过 MQ 等广播，收到消息的实例调用 `Invalidate`/`ReloadAsync`。

---

© 2026 MemoryCache Contributors · 本文档与代码以中文注释维护
