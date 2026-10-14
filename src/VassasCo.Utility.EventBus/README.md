# VassasCo.Utility.EventBus

轻量级进程内事件总线 — 类型安全的事件发布/订阅，**零外部依赖**，支持特性自动注册。

发布者与订阅者通过事件类型解耦，互不感知。全局实例 `EventBus.Default`，**不引入 DI 容器**。

[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 目录

- [功能总览](#功能总览)
- [目标框架与依赖](#目标框架与依赖)
- [快速开始](#快速开始)
- [订阅与发布](#订阅与发布)
- [优先级](#优先级)
- [多通道](#多通道)
- [条件过滤](#条件过滤)
- [异步处理](#异步处理)
- [粘性事件](#粘性事件)
- [异常隔离](#异常隔离)
- [线程封送](#线程封送)
- [弱引用订阅](#弱引用订阅)
- [取消订阅](#取消订阅)
- [诊断与排漏](#诊断与排漏)
- [配置参考](#配置参考)
- [API 速查](#api-速查)
- [测试](#测试)

---

## 功能总览

| 类别 | 能力 |
|------|------|
| **解耦** | 发布方与订阅方只依赖事件类型，互不引用 |
| **优先级** | 数字越小越先执行，保证多个订阅者之间的顺序 |
| **多通道** | 同一事件类型按通道隔离，订阅方可监听多个通道 |
| **条件过滤** | Lambda 过滤器 / 特性指定过滤方法 / 按属性范围过滤 |
| **粘性事件** | 新订阅者立即收到该类型最后一次发布的粘性事件 |
| **异步处理** | 支持 `Func<TEvent, Task>` 订阅者，异步发布并行等待 |
| **异常隔离** | 单个订阅者抛异常不影响其他订阅者 |
| **线程封送** | 自动把处理器封送回订阅时的同步上下文（UI 线程） |
| **弱引用订阅** | 只弱持有处理器实例，防止「关窗不释放」泄漏 |
| **诊断快照** | 查看各类型订阅数、死订阅数，便于排查泄漏 |
| **零依赖** | 仅 BCL，不引入任何 NuGet 包 |

---

## 目标框架与依赖

| 目标框架 | 依赖 |
|----------|------|
| net472 | 无（仅 BCL） |
| netstandard2.0 | 无（仅 BCL） |
| net6.0 | 无（仅 BCL） |
| net8.0 | 无（仅 BCL） |
| net9.0 | 无（仅 BCL） |
| net10.0 | 无（仅 BCL） |

> 新增 **net472**：供 .NET Framework 4.7.2+（含 4.8）传统桌面项目直接引用。
> 构建期通过 `Microsoft.NETFramework.ReferenceAssemblies`（`PrivateAssets=all`）提供目标包，
> **不会作为依赖传递给使用方**。

---

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

### 2. 订阅 & 发布

```csharp
using VassasCo.Utility;

// 订阅
var token = EventBus.Default.Subscribe<OrderCreatedEvent>(e =>
    Console.WriteLine($"订单 {e.OrderId}，金额 {e.Amount:C}"));

// 发布
EventBus.Default.Publish(new OrderCreatedEvent("ORD-001", 999));

// 取消订阅
token.Dispose();
```

---

## 订阅与发布

每种组合都提供「单通道」和「多通道」两个重载：

```csharp
// 同步
Subscribe<TEvent>(Action<TEvent> handler, int priority = 100, string? channel = null, Func<TEvent, bool>? filter = null)
Subscribe<TEvent>(Action<TEvent> handler, string[]? channels, int priority = 100, Func<TEvent, bool>? filter = null)

// 异步
Subscribe<TEvent>(Func<TEvent, Task> asyncHandler, int priority = 100, string? channel = null, Func<TEvent, bool>? filter = null)
Subscribe<TEvent>(Func<TEvent, Task> asyncHandler, string[]? channels, int priority = 100, Func<TEvent, bool>? filter = null)
```

---

## 优先级

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

---

## 多通道

订阅方监听多个通道（命中任意即触发），发布方指定单个通道。

```csharp
// 订阅 Payment 和 Shipping 两个通道
EventBus.Default.Subscribe<OrderCreatedEvent>(
    handler: e => Console.WriteLine(e.OrderId),
    channels: new[] { "Payment", "Shipping" });

// 发布到 Payment 通道 → 上面会收到
EventBus.Default.Publish(e, channel: "Payment");
```

通道匹配规则：

| 订阅方通道 | 发布方通道 | 结果 |
|-----------|-----------|------|
| `null` | `null` | 匹配 |
| `null` | `"Payment"` | 匹配 |
| `["Payment"]` | `null` | 不匹配 |
| `["Payment","Shipping"]` | `"Payment"` | 匹配 |
| `["Payment"]` | `"Other"` | 不匹配 |

---

## 条件过滤

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

---

## 异步处理

```csharp
// 手动异步订阅
EventBus.Default.Subscribe<OrderCreatedEvent>(
    asyncHandler: async e => await SaveToDbAsync(e));

// 异步发布 — 等待所有订阅者完成
await EventBus.Default.PublishAsync(e);
```

> **重要语义变更（2.0.0）**
>
> 同步 `Publish` **不再阻塞等待**异步订阅者。旧实现用 `task.GetAwaiter().GetResult()` 等待，
> 在带 `SynchronizationContext` 的线程（WinForms/WPF UI 线程）上，
> 若异步处理器内部 `await` 后需要回到该上下文，就会**死锁**。
>
> 现在默认改为 fire-and-forget，异步订阅者的异常经 `HandlerError` 上报。
> 需要等待语义请改用 `PublishAsync`，或把 `EventBusOptions.AsyncHandlerStrategy` 设为 `Block`
> （仅在无同步上下文的线程上使用）。

---

## 粘性事件

发布后新注册的订阅者会立即收到该类型最后一次发布的粘性事件。

```csharp
EventBus.Default.PublishSticky(new UserLoggedInEvent("Admin", "管理员"));

// ... 稍后订阅 ...
EventBus.Default.Subscribe<UserLoggedInEvent>(e =>
    Console.WriteLine($"{e.UserName} 已登录")); // 立刻被触发！
```

粘性事件缓存**有上限**（默认 64 条，见 `EventBusOptions.MaxStickyEvents`），
超出时按 FIFO 淘汰最早的一条 —— 原实现为无上限缓存，长时间运行会单调增长。

---

## 异常隔离

某个订阅者抛异常不影响其他订阅者，异常通过 `HandlerError` 事件集中处理。

```csharp
EventBus.Default.HandlerError += (_, e) =>
{
    Log.Error($"订阅者异常: {e.Exception.Message}，事件: {e.Event}");
};
```

> **修复（2.0.0）**：`HandlerError` 处理器自身抛出的异常不再向外传播。
> 原实现中它会从 `Publish` 的 `catch` 块里冒出去，**中断后续订阅者的执行**，
> 直接破坏「异常隔离」这一核心承诺。现在这类异常被吞掉并转交诊断通道。

---

## 线程封送（2.0.0 新增）

订阅时会捕获 `SynchronizationContext.Current`；发布时若当前上下文不同，
自动把处理器 `Post` 回订阅时的上下文。

```csharp
// 在 UI 线程构造的窗体里订阅
public LogWindow()
{
    EventBus.Default.RegisterWeak(this);       // 捕获 UI 上下文
}

[EventSubscribe]
private void OnLog(LogEvent e)
{
    // 即使 Publish 来自 PLC 轮询线程，这里也已在 UI 线程上执行，
    // 可直接操作控件，无需手写 InvokeRequired / Dispatcher.Invoke
    _listBox.Items.Add(e.Message);
}
```

- 发布与订阅在同一上下文时直接同步执行，不引入额外调度开销。
- 在线程池/控制台等无上下文的线程上订阅时捕获结果为 `null`，等同于不封送。
- 可通过 `EventBusOptions.MarshalToContext = false` 关闭。

---

## 弱引用订阅（2.0.0 新增）

普通 `Subscribe` 会让事件总线**强持有**处理器所属实例。当事件源生命周期与进程等长
（例如静态日志事件转发），而订阅者是会反复创建销毁的窗体/控件时，这就是经典的内存泄漏：
**窗体关了，但总线还引用着它。**

`SubscribeWeak` / `RegisterWeak` 只弱持有目标实例，实例被 GC 回收后订阅自动失效并清理。

```csharp
// 方式一：手动弱引用订阅
EventBus.Default.SubscribeWeak<LogEvent>(this.OnLog);

// 方式二：特性 + Weak = true
public class LogWindow : Form
{
    private readonly IDisposable _token;

    public LogWindow()
    {
        _token = EventBus.Default.RegisterWeak(this);
    }

    [EventSubscribe(Weak = true)]
    private void OnLog(LogEvent e) => AppendLog(e.Message);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _token?.Dispose();   // 也可提前主动解绑
        base.Dispose(disposing);
    }
}
```

> ⚠️ **必须传方法组，不要传捕获 `this` 的 Lambda**
>
> `SubscribeWeak(e => this.OnLog(e))` 中，`handler.Target` 是**编译器生成的闭包对象**，
> 而闭包通常只被委托自己引用。一旦总线不强持委托，闭包会在下一次 GC 时被回收，
> 表现就是「订阅神秘失效」。
>
> 本库检测到弱引用订阅的目标是编译器生成类型时，会通过诊断通道给出提示。
> 正确写法是 `SubscribeWeak(this.OnLog)`。
>
> 另外，静态方法委托没有 `Target`，无法弱引用，会自动退化为强引用订阅并输出诊断提示。

---

## 取消订阅

```csharp
var token = EventBus.Default.Subscribe<OrderCreatedEvent>(OnOrder);
token.Dispose();                                   // 单条取消

EventBus.Default.UnsubscribeAll<OrderCreatedEvent>();  // 按类型全部取消
EventBus.Default.Clear();                          // 清空全部
```

按实例一键取消（配合 `Register` / `RegisterWeak` 使用）：

```csharp
var token = EventBus.Default.RegisterWeak(vm);

// 之后无需保管 token，直接按实例注销
EventBus.Default.Unregister(vm);
```

---

## 诊断与排漏

```csharp
var options = new EventBusOptions
{
    Diagnostics = msg => Console.WriteLine(msg)   // 签名不合规 / 弱引用闭包等提示
};
var bus = new EventBus(options);
```

查看订阅情况：

```csharp
foreach (var entry in EventBus.Default.GetSnapshot())
    Console.WriteLine(entry);
// 输出形如：LogEvent: 共 3 个订阅（弱引用 2，已失效 1）

int purged = EventBus.Default.PurgeDeadSubscriptions();   // 立即清理失效订阅
```

`Dead > 0` 说明存在目标已被回收但尚未清理的弱引用订阅，
它们会在下一次发布或调用 `PurgeDeadSubscriptions()` 时移除。

---

## 配置参考

### EventBusOptions

| 属性 | 默认值 | 说明 |
|------|--------|------|
| `MarshalToContext` | `true` | 是否捕获订阅时的 `SynchronizationContext` 并在发布时封送回去 |
| `AsyncHandlerStrategy` | `FireAndForget` | 同步 `Publish` 遇到异步订阅者的策略；`Block` 有死锁风险（详见上文） |
| `MaxStickyEvents` | `64` | 粘性事件缓存上限（最小 1），超出按 FIFO 淘汰 |
| `Diagnostics` | `null` | 诊断回调；未配置时静默忽略（与旧版一致） |

---

## API 速查

### EventBus（实例）

| 成员 | 说明 |
|------|------|
| `Default` | 全局默认实例（静态，`Lazy` 线程安全） |
| `Subscribe<TEvent>(...)` | 强引用订阅，同步/异步 × 单通道/多通道 共 4 个重载 |
| `SubscribeWeak<TEvent>(...)` | 弱引用订阅，重载同上 |
| `Publish<TEvent>(@event, channel?)` | 同步发布（不等待异步订阅者） |
| `PublishAsync<TEvent>(@event, channel?)` | 异步发布，并行等待所有异步订阅者 |
| `PublishSticky<TEvent>(@event, channel?)` | 发布粘性事件 |
| `ClearSticky<TEvent>()` / `ClearAllSticky()` | 清除粘性缓存 |
| `Unsubscribe(long id)` | 按 ID 退订（O(1)） |
| `UnsubscribeAll<TEvent>()` | 按事件类型全部退订 |
| `Unregister(object instance)` | 按实例注销其全部订阅 |
| `GetSnapshot()` | 订阅快照（各类型订阅数 / 弱引用数 / 失效数） |
| `PurgeDeadSubscriptions()` | 立即清理失效的弱引用订阅 |
| `Clear()` / `Dispose()` | 清空全部订阅与粘性事件 |

### EventBusExtensions

| 方法 | 说明 |
|------|------|
| `Register(instance)` | 扫描 `[EventSubscribe]` 方法并强引用订阅，返回聚合令牌 |
| `RegisterWeak(instance)` | 同上，但使用弱引用订阅 |
| `UnregisterInstance(instance)` | 注销该实例名下的全部订阅（等价 `Unregister`） |

### EventSubscribeAttribute

| 属性 | 默认 | 说明 |
|------|------|------|
| `Priority` | `100` | 优先级，越小越先执行 |
| `Channels` | `null` | 订阅通道列表；null/空 = 不限制通道 |
| `FilterMethod` | `null` | 指向实例上的自定义过滤方法名 |
| `FilterProperty` | `null` | 按属性名做范围过滤 |
| `FilterMin` / `FilterMax` | `double.MinValue` / `MaxValue` | 属性范围（含边界） |
| `Weak` | `false` | 是否弱引用订阅（详见上文注意事项） |

---

## 测试

```bash
cd VassasCo.Utility
dotnet test
```

EventBus 单元测试覆盖：基本订阅发布、优先级顺序、通道匹配、过滤器、异步发布、
异常隔离（含错误处理器自身抛异常）、弱引用订阅被 GC 后自动清理、
按实例注销、粘性事件（含上限淘汰）、订阅快照、Dispose 后操作抛 `ObjectDisposedException`。

---

## 更新说明

### 2.0.0（当前）

**新增**

- **线程封送**：订阅时捕获 `SynchronizationContext`，发布时自动 `Post` 回订阅上下文。
  UI 线程订阅、后台线程发布时无需手写 `Invoke`。可通过 `EventBusOptions.MarshalToContext` 关闭。
- **弱引用订阅**：新增 `SubscribeWeak<TEvent>()`（4 个重载）、`RegisterWeak(instance)`，
  以及 `[EventSubscribe(Weak = true)]`。事件总线只弱持有处理器实例，
  实例被 GC 回收后订阅自动失效清理，解决「短命订阅者 + 长命事件源」的泄漏问题。
  检测到闭包目标或静态方法委托时会输出诊断提示。
- **按实例注销**：新增 `Unregister(object instance)` 与扩展方法 `UnregisterInstance()`，
  无需长期保管令牌即可一键解绑。
- **诊断能力**：新增 `GetSnapshot()`（各事件类型订阅数 / 弱引用数 / 失效数）与
  `PurgeDeadSubscriptions()`；新增 `EventBusOptions.Diagnostics` 回调，
  用于上报原本被静默吞掉的问题（方法签名不合规、弱引用闭包风险等）。
- **粘性事件上限**：新增 `EventBusOptions.MaxStickyEvents`（默认 64），超出按 FIFO 淘汰。

**修复**

- **A1 重复代码**：`SubscribeInternal()` 与 `AddSubscription()` 逻辑几乎完全重复，
  维护时改一处漏一处。已合并为唯一入口 `SubscribeCore<TEvent>()`。
- **A2 异常隔离被破坏**：`OnHandlerError` 直接 `HandlerError?.Invoke(...)`，
  错误处理器自身抛出的异常会从 `Publish` 的 `catch` 块里冒出去，**中断后续订阅者执行**。
  现已统一捕获并转交诊断通道。
- **A3 退订复杂度**：`Unsubscribe(id)` 原需遍历所有事件类型的所有订阅（O(总订阅数)），
  新增 `id → 事件类型` 索引后退订降为 O(1)。
- **C1 反射调用开销**：发布路径与过滤器原用 `DynamicInvoke`，
  比直接调用慢 1~2 个数量级。现改为订阅时在泛型上下文中一次性构造强类型调用包装，
  发布路径零反射。
- **C2 死锁**：同步 `Publish` 遇到异步订阅者时原用 `GetAwaiter().GetResult()` 阻塞等待，
  在带 `SynchronizationContext` 的线程（UI 线程）上必然死锁。现默认 fire-and-forget，
  异常经 `HandlerError` 上报；需要等待请用 `PublishAsync`。
- **C3 静默跳过**：`Register()` 扫描到签名不合规的方法时原直接 `return null`，
  调用方完全无从得知「标了特性却没生效」。现改为经诊断通道明确上报。
- 反射注册路径（`Register`）原先用反射调用同程序集内的 `internal` 方法，
  现改为复用缓存的 `MethodInfo`，去掉无谓开销。

**兼容性**

- 目标框架：`net472` / `netstandard2.0` / `net6.0` / `net8.0` / `net9.0` / `net10.0`
- 既有公开 API（`Subscribe` / `Publish` / `PublishAsync` / `PublishSticky` /
  `Unsubscribe` / `UnsubscribeAll` / `Clear` / `Register` / `HandlerError` / `SubscriptionToken`）
  签名与语义均保持不变。
- 唯一的**行为变更**：同步 `Publish` 不再等待异步订阅者（详见上文「异步处理」）。
  若现有代码依赖该等待语义，请改为 `await PublishAsync(...)`，
  或将 `EventBusOptions.AsyncHandlerStrategy` 设为 `Block`。
- 默认开启线程封送：若订阅者已在正确线程上执行，或你希望完全由自己控制线程切换，
  可将 `EventBusOptions.MarshalToContext` 设为 `false` 以恢复原有行为。

---

## 许可证

MIT
