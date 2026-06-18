# SnowflakeIdHelper

分布式雪花 ID 生成器（Twitter Snowflake 算法的 C# 实现）。

## 文件说明

| 文件 | 内容 |
|------|------|
| `SnowflakeIdHelper.cs` | ID 生成器、建造者、ID 反解、单例管理 |

## 核心特点

- **杜绝时钟回拨**：使用 Stopwatch 硬件单调计数器代替系统时钟
- **线程安全**：lock + Interlocked 保证并发正确
- **全局单例**：`SnowflakeIdHelper.Next()` 开箱即用
- **集群支持**：WorkerId + DataCenterId 共 10bit，最多 1024 节点

## ID 结构

```
[1bit保留] [41bit时间戳] [5bit DataCenter] [5bit Worker] [12bit 序列号]
```

- 每毫秒 4096 个 ID
- 每节点每天可生成约 3500 亿个

## 快速开始

```csharp
// 开箱即用
long id = SnowflakeIdHelper.Next();

// 自定义机器码
SnowflakeIdHelper.SetDefault(b => b.SetWorkerId(5).SetDataCenterId(2));
long id = SnowflakeIdHelper.Next();

// 创建独立实例
var gen = SnowflakeIdHelper.Build()
    .SetWorkerId(10)
    .SetDataCenterId(1)
    .SetEpoch(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    .Create();
long id = gen.NextId();
```

## ID 反解

```csharp
var info = SnowflakeIdHelper.Parse(id);
Console.WriteLine($"时间: {info.Timestamp}");
Console.WriteLine($"DC: {info.DataCenterId}, Worker: {info.WorkerId}");
Console.WriteLine($"序号: {info.Sequence}");
```

## 集群 WorkerId 分配方案

| 方案 | 适用规模 | 实现 |
|------|---------|------|
| 配置文件 | 5-10 台 | 手动配置唯一的 WorkerId+DataCenterId |
| 数据库自增 | 50 台内 | INSERT + LAST_INSERT_ID() % 1024 |
| Redis INCR | 100+ 台 | `redis.Incr("snowflake:counter") % 1024` |
| ZooKeeper | 弹性扩缩 | 临时顺序节点序号 % 1024 |

## 特殊情况

```csharp
// 指定时间生成（使用系统时钟，受回拨影响）
long id = SnowflakeIdHelper.Next(DateTime.UtcNow);
```
