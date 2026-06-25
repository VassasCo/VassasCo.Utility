# EventBus

轻量级进程内事件总线 — 类型安全的事件发布/订阅，零依赖，支持特性自动注册。

微信公众号：VassasCo，欢迎关注。

## 文件说明

| 文件 | 内容 |
|------|------|
| `EventBus.cs` | 核心实现：订阅/发布、多通道、粘性事件、异常隔离 |
| `EventSubscribeAttribute.cs` | 特性定义：`[EventSubscribe]` 标注方法自动订阅 |
| `EventBusExtensions.cs` | 扩展方法：`Register()` 扫描特性并批量注册 |

## 快速开始

### 安装

```bash
# 单独安装
dotnet add package VassasCo.Utility.EventBus

# 或安装全套工具包
dotnet add package VassasCo.Utility
```

### 1. 定义事件

```csharp
public record OrderCreatedEvent(string OrderId, decimal Amount);
public record UserLoggedInEvent(string UserName, string Role);
```

### 2. 手动订阅 & 发布

```csharp
// 订阅
var token = EventBus.Default.Subscribe<OrderCreatedEvent>(e =>
    Console.WriteLine($"订单 {e.OrderId}，金额 {e.Amount:C}"));

// 发布
EventBus.Default.Publish(new OrderCreatedEvent("ORD-001", 999));

// 取消订阅
token.Dispose();
```

### 3. 特性订阅（推荐大型项目）

```csharp
public class OrderService : IDisposable
{
    private readonly IDisposable _token;

    public OrderService()
    {
        _token = EventBus.Default.Register(this);
    }

    [EventSubscribe]
    private void OnOrderCreated(OrderCreatedEvent e)
        => Console.WriteLine($"收到订单: {e.OrderId}");

    [EventSubscribe(Channels = new[] { "Payment" })]
    private void OnPaymentOrder(OrderCreatedEvent e)
        => Console.WriteLine($"[Payment] {e.OrderId}");

    public void Dispose() => _token.Dispose();
}

// 一行注册，一行解绑
var service = new OrderService();
EventBus.Default.Publish(new OrderCreatedEvent("ORD-002", 1500));
service.Dispose();
```

## 功能详解

### 优先级

数字越小越先执行，默认 100。适用于多个订阅者之间存在执行顺序依赖的场景。

```csharp
EventBus.Default.Subscribe<OrderCreatedEvent>(OnHandle1, priority: 10);
EventBus.Default.Subscribe<OrderCreatedEvent>(OnHandle2, priority: 20);
// 发布时 OnHandle1 先执行，OnHandle2 后执行
```

特性写法：

```csharp
[EventSubscribe(Priority = 10)]
private void OnHandle1(OrderCreatedEvent e) { }
```

### 多通道

订阅方监听多个通道（命中任意即触发），发布方指定单个通道。

```csharp
// 订阅 Payment 和 Shipping 两个通道
EventBus.Default.Subscribe<OrderCreatedEvent>(
    handler: e => Console.WriteLine(e.OrderId),
    channels: new[] { "Payment", "Shipping" });

// 发布到 Payment 通道 → 上面会收到
EventBus.Default.Publish(e, channel: "Payment");

// 不带通道发布 → 只有 channels = null 的订阅者收到
EventBus.Default.Publish(e);
```

通道匹配规则：

| 订阅方通道 | 发布方通道 | 结果 |
|-----------|-----------|------|
| `null` | `null` | 匹配 |
| `null` | `"Payment"` | 匹配 |
| `["Payment"]` | `null` | 不匹配 |
| `["Payment","Shipping"]` | `"Payment"` | 匹配 |
| `["Payment"]` | `"Other"` | 不匹配 |

### 条件过滤

手动订阅用 Lambda，特性订阅用 `FilterMethod` 或 `FilterProperty`。

```csharp
// Lambda 过滤 — 任意复杂度
EventBus.Default.Subscribe<OrderCreatedEvent>(
    handler: e => Handle(e),
    filter: e => e.Amount >= 1000 && e.OrderId.StartsWith("VIP"));

// 特性 — 自定义过滤方法
[EventSubscribe(FilterMethod = nameof(ShouldHandle))]
private void OnOrder(OrderCreatedEvent e) { }
private bool ShouldHandle(OrderCreatedEvent e) => e.Amount >= 1000;

// 特性 — 按属性值范围过滤（省去写方法）
[EventSubscribe(FilterProperty = "Amount", FilterMin = 100, FilterMax = 500)]
private void OnMidRange(OrderCreatedEvent e) { }
```

### 异步处理

```csharp
// 手动异步订阅
EventBus.Default.Subscribe<OrderCreatedEvent>(
    asyncHandler: async e => await SaveToDbAsync(e));

// 特性异步订阅
[EventSubscribe]
private async Task OnOrderAsync(OrderCreatedEvent e)
{
    await Task.Delay(100);
    Console.WriteLine($"异步处理: {e.OrderId}");
}

// 异步发布 — 等待所有订阅者完成
await EventBus.Default.PublishAsync(e);
```

### 粘性事件

发布后新注册的订阅者会立即收到该类型最后一次发布的粘性事件。

```csharp
// 发布粘性事件
EventBus.Default.PublishSticky(new UserLoggedInEvent("Admin", "管理员"));

// ... 稍后订阅 ...
EventBus.Default.Subscribe<UserLoggedInEvent>(e =>
    Console.WriteLine($"{e.UserName} 已登录")); // 立刻被触发！
```

### 异常隔离

某个订阅者抛异常不影响其他订阅者，异常通过 `HandlerError` 事件集中处理。

```csharp
EventBus.Default.HandlerError += (_, e) =>
{
    Log.Error($"订阅者异常: {e.Exception.Message}，事件: {e.Event}");
};
```

### 取消订阅

```csharp
var token = EventBus.Default.Subscribe<OrderCreatedEvent>(OnOrder);
token.Dispose(); // 单条取消

EventBus.Default.UnsubscribeAll<OrderCreatedEvent>(); // 按类型全部取消
EventBus.Default.Clear(); // 清空全部
```

## 兼容性

- .NET Standard 2.0
- .NET 6
- .NET 8
- .NET 9
- .NET 10
