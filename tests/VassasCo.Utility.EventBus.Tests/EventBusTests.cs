// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VassasCo.Utility.Tests
{
    #region 测试用事件与订阅者

    public sealed class PingEvent
    {
        public string Text = string.Empty;
        public int Amount;
    }

    public sealed class OtherEvent
    {
        public string Text = string.Empty;
    }

    /// <summary>作为弱引用订阅目标的测试对象。</summary>
    public sealed class WeakTarget
    {
        public int Count;

        public void OnPing(PingEvent e)
        {
            Count++;
            GC.KeepAlive(this);
        }
    }

    /// <summary>验证 [EventSubscribe(Weak = true)] 特性的测试订阅者。</summary>
    public sealed class WeakAttributedSubscriber
    {
        public int Count;

        [EventSubscribe(Weak = true)]
        private void OnPing(PingEvent e)
        {
            Count++;
            GC.KeepAlive(this);
        }
    }

    /// <summary>用于特性自动注册的测试订阅者。</summary>
    public sealed class AttributedSubscriber
    {
        public readonly List<string> Received = new List<string>();

        [EventSubscribe(Priority = 10)]
        private void HighPriority(PingEvent e) => Received.Add("high:" + e.Text);

        [EventSubscribe(Priority = 20)]
        private void LowPriority(PingEvent e) => Received.Add("low:" + e.Text);

        [EventSubscribe(Channels = new[] { "Payment" })]
        private void PaymentOnly(PingEvent e) => Received.Add("payment:" + e.Text);

        [EventSubscribe(FilterMethod = nameof(OnlyBig))]
        private void BigOnly(PingEvent e) => Received.Add("big:" + e.Text);

        private bool OnlyBig(PingEvent e) => e.Amount >= 1000;

        [EventSubscribe(FilterProperty = "Amount", FilterMin = 100, FilterMax = 500)]
        private void MidRange(PingEvent e) => Received.Add("mid:" + e.Text);

        [EventSubscribe]
        private void OnOther(OtherEvent e) => Received.Add("other:" + e.Text);
    }

    #endregion

    public class EventBusTests
    {
        #region 基本订阅 / 发布

        [Fact]
        public void Subscribe_ReceivesPublishedEvent()
        {
            var bus = new EventBus();
            string? received = null;

            bus.Subscribe<PingEvent>(e => received = e.Text);
            bus.Publish(new PingEvent { Text = "hello" });

            Assert.Equal("hello", received);
        }

        [Fact]
        public void Subscribe_UnsubscribedToken_StopsReceiving()
        {
            var bus = new EventBus();
            int count = 0;

            var token = bus.Subscribe<PingEvent>(_ => count++);
            bus.Publish(new PingEvent());
            token.Dispose();
            bus.Publish(new PingEvent());

            Assert.Equal(1, count);
        }

        [Fact]
        public void Subscribe_DifferentEventTypes_AreIsolated()
        {
            var bus = new EventBus();
            int ping = 0, other = 0;

            bus.Subscribe<PingEvent>(_ => ping++);
            bus.Subscribe<OtherEvent>(_ => other++);

            bus.Publish(new PingEvent());

            Assert.Equal(1, ping);
            Assert.Equal(0, other);
        }

        [Fact]
        public void Subscribe_NullHandler_Throws()
        {
            var bus = new EventBus();
            Assert.Throws<ArgumentNullException>(() => bus.Subscribe<PingEvent>((Action<PingEvent>)null!));
        }

        [Fact]
        public void Publish_NullEvent_Throws()
        {
            var bus = new EventBus();
            Assert.Throws<ArgumentNullException>(() => bus.Publish<PingEvent>(null!));
        }

        #endregion

        #region 优先级

        [Fact]
        public void Publish_RespectsPriorityOrder()
        {
            var bus = new EventBus();
            var order = new List<int>();

            bus.Subscribe<PingEvent>(_ => order.Add(2), priority: 20);
            bus.Subscribe<PingEvent>(_ => order.Add(1), priority: 10);
            bus.Subscribe<PingEvent>(_ => order.Add(3), priority: 30);

            bus.Publish(new PingEvent());

            Assert.Equal(new[] { 1, 2, 3 }, order);
        }

        #endregion

        #region 多通道

        [Theory]
        [InlineData(null, null, true)]
        [InlineData(null, "Payment", true)]
        [InlineData("Payment", null, false)]
        [InlineData("Payment", "Payment", true)]
        [InlineData("Payment", "Other", false)]
        public void Publish_ChannelMatching(string? subscribed, string? published, bool expected)
        {
            var bus = new EventBus();
            int count = 0;

            bus.Subscribe<PingEvent>(_ => count++,
                channels: subscribed == null ? null : new[] { subscribed });

            bus.Publish(new PingEvent(), channel: published);

            Assert.Equal(expected ? 1 : 0, count);
        }

        #endregion

        #region 条件过滤

        [Fact]
        public void Publish_FilterExcludesNonMatching()
        {
            var bus = new EventBus();
            var received = new List<int>();

            bus.Subscribe<PingEvent>(e => received.Add(e.Amount),
                filter: e => e.Amount >= 100);

            bus.Publish(new PingEvent { Amount = 50 });
            bus.Publish(new PingEvent { Amount = 150 });

            Assert.Equal(new[] { 150 }, received);
        }

        #endregion

        #region 异步处理

        [Fact]
        public async Task PublishAsync_AwaitsAsyncHandlers()
        {
            var bus = new EventBus();
            int completed = 0;

            bus.Subscribe<PingEvent>(async _ => { await Task.Delay(20); Interlocked.Increment(ref completed); });
            bus.Subscribe<PingEvent>(async _ => { await Task.Delay(20); Interlocked.Increment(ref completed); });

            await bus.PublishAsync(new PingEvent());

            Assert.Equal(2, completed);
        }

        /// <summary>
        /// 同步 Publish 不等待 async handler 完成。
        /// 阻塞等待（<c>GetAwaiter().GetResult()</c>）在带 SynchronizationContext 的线程上会死锁：
        /// handler 内部 await 之后需要回到该上下文，而上下文正被阻塞。
        /// 此处由 TestSyncContext 模拟该场景，Publish 应能正常返回。
        /// </summary>
        [Fact]
        public void SyncPublish_DoesNotDeadlock_WhenAsyncHandlerAndSyncContextPresent()
        {
            var ctx = new TestSyncContext();
            Exception? failure = null;

            ctx.Post(_ =>
            {
                try
                {
                    // 在带 SynchronizationContext 的线程上构造总线并订阅异步处理器
                    var bus = new EventBus();
                    bus.Subscribe<PingEvent>(async _ => await Task.Delay(50));

                    // 同步 Publish 必须能正常返回，不得死锁
                    var finished = new ManualResetEventSlim(false);
                    var t = new Thread(() => { bus.Publish(new PingEvent()); finished.Set(); });
                    t.IsBackground = true;
                    t.Start();

                    Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
                }
                catch (Exception ex) { failure = ex; }
            }, null);

            ctx.Post(_ => ctx.Complete(), null);
            Assert.True(ctx.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        }

        #endregion

        #region 异常隔离

        [Fact]
        public void Publish_HandlerException_DoesNotStopOtherSubscribers()
        {
            var bus = new EventBus();
            var errors = new List<Exception>();
            int afterThrow = 0;

            bus.HandlerError += (_, e) => errors.Add(e.Exception);

            bus.Subscribe<PingEvent>(_ => throw new InvalidOperationException("boom"), priority: 1);
            bus.Subscribe<PingEvent>(_ => afterThrow++, priority: 2);

            bus.Publish(new PingEvent());

            Assert.Single(errors);
            Assert.Equal("boom", errors[0].Message);
            Assert.Equal(1, afterThrow);      // 抛异常之后订阅者仍然执行
        }

        /// <summary>
        /// A2 的回归测试：HandlerError 处理器自己抛异常时，
        /// 异常不得从 Publish 里冒出来，后续订阅者仍须执行。
        /// </summary>
        [Fact]
        public void Publish_WhenErrorHandlerItselfThrows_StillContinues()
        {
            var bus = new EventBus();
            int afterThrow = 0;

            bus.HandlerError += (_, e) => throw new InvalidOperationException("error handler blew up");

            bus.Subscribe<PingEvent>(_ => throw new ApplicationException("first"), priority: 1);
            bus.Subscribe<PingEvent>(_ => afterThrow++, priority: 2);

            var ex = Record.Exception(() => bus.Publish(new PingEvent()));

            Assert.Null(ex);                  // 不得向外抛
            Assert.Equal(1, afterThrow);      // 后续订阅者仍执行
        }

        [Fact]
        public async Task PublishAsync_ReportsAllHandlerExceptions()
        {
            var bus = new EventBus();
            var errors = new List<Exception>();

            bus.HandlerError += (_, e) => errors.Add(e.Exception);
            bus.Subscribe<PingEvent>(async _ => { await Task.Yield(); throw new InvalidOperationException("a"); });
            bus.Subscribe<PingEvent>(async _ => { await Task.Yield(); throw new InvalidOperationException("b"); });

            await bus.PublishAsync(new PingEvent());

            Assert.Equal(2, errors.Count);
            Assert.Contains("a", errors[0].Message);
            Assert.Contains("b", errors[1].Message);
        }

        #endregion

        #region 弱引用订阅

        [Fact]
        public void SubscribeWeak_ReceivesEvent_WhileTargetAlive()
        {
            var bus = new EventBus();
            var target = new WeakTarget();

            bus.SubscribeWeak<PingEvent>(target.OnPing);
            bus.Publish(new PingEvent());

            Assert.Equal(1, target.Count);
            GC.KeepAlive(target);
        }

        [Fact]
        public void SubscribeWeak_IsRemovedAfterTargetCollected()
        {
            var bus = new EventBus();

            var weakRef = SubscribeWeakAndForget(bus);

            // 目标仍存活时，订阅存在
            Assert.True(weakRef.IsAlive);
            Assert.Single(bus.GetSnapshot());

            CollectUntil(() => !weakRef.IsAlive);
            Assert.False(weakRef.IsAlive);

            // 发布后死订阅被清理
            bus.Publish(new PingEvent());
            Assert.Empty(bus.GetSnapshot());
        }

        /// <summary>验证特性上的 Weak 属性生效：即使调用的是强引用版 Register()，
        /// 标注了 [EventSubscribe(Weak = true)] 的方法仍然走弱引用订阅。</summary>
        [Fact]
        public void Register_WithWeakAttribute_UsesWeakSubscription()
        {
            var bus = new EventBus();
            var weakRef = RegisterAndForget(bus);

            Assert.True(weakRef.IsAlive);
            Assert.Single(bus.GetSnapshot());

            CollectUntil(() => !weakRef.IsAlive);
            Assert.False(weakRef.IsAlive);

            bus.Publish(new PingEvent());
            Assert.Empty(bus.GetSnapshot());
        }

        /// <summary>在独立方法中创建目标并订阅，返回后局部变量不再根化对象。</summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference SubscribeWeakAndForget(EventBus bus)
        {
            var target = new WeakTarget();
            bus.SubscribeWeak<PingEvent>(target.OnPing);
            return new WeakReference(target);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference RegisterAndForget(EventBus bus)
        {
            var target = new WeakAttributedSubscriber();
            bus.Register(target);
            return new WeakReference(target);
        }

        private static void CollectUntil(Func<bool> condition, int maxAttempts = 10)
        {
            for (int i = 0; i < maxAttempts && !condition(); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        #endregion

        #region 按实例注销

        [Fact]
        public void Unregister_RemovesAllSubscriptionsOfInstance()
        {
            var bus = new EventBus();
            var subscriber = new AttributedSubscriber();

            bus.Register(subscriber);
            Assert.NotEmpty(bus.GetSnapshot());

            int removed = bus.Unregister(subscriber);
            Assert.True(removed > 0);

            bus.Publish(new PingEvent());
            bus.Publish(new OtherEvent());
            Assert.Empty(subscriber.Received);
        }

        #endregion

        #region 特性自动注册

        [Fact]
        public void Register_SubscribesAllAttributedMethods()
        {
            var bus = new EventBus();
            var subscriber = new AttributedSubscriber();

            bus.Register(subscriber);
            bus.Publish(new PingEvent { Text = "x", Amount = 1000 });

            Assert.Contains("high:x", subscriber.Received);
            Assert.Contains("low:x", subscriber.Received);
            Assert.Contains("big:x", subscriber.Received);
            Assert.Contains("mid:x", subscriber.Received);
            Assert.DoesNotContain("payment:x", subscriber.Received);   // 通道不匹配
        }

        [Fact]
        public void Register_ThenDisposeToken_UnsubscribesAll()
        {
            var bus = new EventBus();
            var subscriber = new AttributedSubscriber();

            var token = bus.Register(subscriber);
            token.Dispose();

            bus.Publish(new PingEvent { Text = "x" });
            Assert.Empty(subscriber.Received);
        }

        [Fact]
        public void Register_SignatureMismatch_ReportsDiagnostics()
        {
            var messages = new List<string>();
            var bus = new EventBus(new EventBusOptions { Diagnostics = messages.Add });

            bus.Register(new BadSubscriber());

            Assert.Contains(messages, m => m.Contains("跳过订阅"));
        }

        private sealed class BadSubscriber
        {
            [EventSubscribe]
            private void TooManyParameters(PingEvent a, OtherEvent b) { }

            [EventSubscribe]
            private int WrongReturnType(PingEvent a) => 1;
        }

        #endregion

        #region 粘性事件

        [Fact]
        public void PublishSticky_NewSubscriberReceivesLastEvent()
        {
            var bus = new EventBus();

            bus.PublishSticky(new PingEvent { Text = "sticky" });

            string? received = null;
            bus.Subscribe<PingEvent>(e => received = e.Text);

            Assert.Equal("sticky", received);
        }

        [Fact]
        public void ClearSticky_RemovesCachedEvent()
        {
            var bus = new EventBus();
            bus.PublishSticky(new PingEvent { Text = "sticky" });
            bus.ClearSticky<PingEvent>();

            string? received = null;
            bus.Subscribe<PingEvent>(e => received = e.Text);

            Assert.Null(received);
        }

        [Fact]
        public void PublishSticky_RespectsMaxStickyEvents()
        {
            var bus = new EventBus(new EventBusOptions { MaxStickyEvents = 2 });

            bus.PublishSticky(new StickyA { Text = "A" });
            bus.PublishSticky(new StickyB { Text = "B" });
            bus.PublishSticky(new StickyC { Text = "C" });   // 淘汰最早的 A

            string? gotA = null, gotB = null, gotC = null;
            bus.Subscribe<StickyA>(e => gotA = e.Text);
            bus.Subscribe<StickyB>(e => gotB = e.Text);
            bus.Subscribe<StickyC>(e => gotC = e.Text);

            Assert.Null(gotA);
            Assert.Equal("B", gotB);
            Assert.Equal("C", gotC);
        }

        private sealed class StickyA { public string Text = string.Empty; }
        private sealed class StickyB { public string Text = string.Empty; }
        private sealed class StickyC { public string Text = string.Empty; }

        #endregion

        #region 诊断

        [Fact]
        public void GetSnapshot_ReportsSubscriptionCounts()
        {
            var bus = new EventBus();

            bus.Subscribe<PingEvent>(_ => { });
            bus.Subscribe<PingEvent>(_ => { });
            bus.Subscribe<OtherEvent>(_ => { });

            var snapshot = bus.GetSnapshot();
            Assert.Equal(2, snapshot.Count);

            var ping = snapshot.FirstOrDefault(s => s.EventType == typeof(PingEvent));
            Assert.NotNull(ping);
            Assert.Equal(2, ping!.Total);
            Assert.Equal(0, ping.Weak);
            Assert.Equal(0, ping.Dead);
        }

        [Fact]
        public void Dispose_ClearsEverything_AndThrowsAfterwards()
        {
            var bus = new EventBus();
            bus.Subscribe<PingEvent>(_ => { });
            bus.PublishSticky(new OtherEvent());

            bus.Dispose();

            Assert.Empty(bus.GetSnapshot());
            Assert.Throws<ObjectDisposedException>(() => bus.Subscribe<PingEvent>(_ => { }));
            Assert.Throws<ObjectDisposedException>(() => bus.Publish(new PingEvent()));
        }

        #endregion
    }

    /// <summary>
    /// 测试用同步上下文：把 Post 过来的回调放到一条专属线程上按 FIFO 顺序执行，
    /// 用于验证线程封送确实把处理器送回了订阅时的上下文。
    /// </summary>
    internal sealed class TestSyncContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue
            = new BlockingCollection<(SendOrPostCallback, object?)>();

        private readonly Thread _thread;

        public int ThreadId { get; }

        public TestSyncContext()
        {
            _thread = new Thread(Pump) { IsBackground = true };
            ThreadId = _thread.ManagedThreadId;
            _thread.Start();
        }

        private void Pump()
        {
            // 让这条线程也带上本上下文，便于被测代码识别"当前上下文"
            SetSynchronizationContext(this);
            foreach (var item in _queue.GetConsumingEnumerable())
                item.Callback(item.State);
        }

        public override void Post(SendOrPostCallback d, object? state)
            => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException("测试上下文不支持 Send");

        public void Complete() => _queue.CompleteAdding();

        public bool Join(TimeSpan timeout) => _thread.Join(timeout);
    }
}
