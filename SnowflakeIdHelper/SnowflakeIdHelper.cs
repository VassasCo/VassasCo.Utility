using System;
using System.Diagnostics;
using System.Threading;

namespace VassasCo.Utility
{
    /// <summary>
    /// 分布式雪花 ID 生成器（Twitter Snowflake 算法的 C# 实现）。
    /// 
    /// ID 结构：[1bit保留] [41bit时间戳] [5bit DC] [5bit Worker] [12bit Seq]
    /// 使用 Stopwatch 硬件单调计数器替代系统时钟，杜绝 NTP 校时/时钟回拨导致的重复 ID。
    /// 唯一性由 时间戳 + 机器码(1024节点) + 序列号(4096/ms) 三个维度保证。
    /// </summary>
    public class SnowflakeIdHelper
    {
        private static readonly object _defaultLock = new object();
        private static SnowflakeIdHelper? _defaultInstance;
        private static volatile bool _defaultInitialized;

        /// <summary>获取全局单例（首次访问时自动创建）</summary>
        public static SnowflakeIdHelper Default
        {
            get
            {
                if (!_defaultInitialized)
                {
                    lock (_defaultLock)
                    {
                        if (!_defaultInitialized)
                        {
                            _defaultInstance = Build().Create();
                            _defaultInitialized = true;
                        }
                    }
                }
                return _defaultInstance!;
            }
        }

        /// <summary>替换全局单例。应在程序启动早期调用。</summary>
        public static SnowflakeIdHelper SetDefault(Action<Builder> configure)
        {
            var builder = Build();
            configure(builder);
            var instance = builder.Create();
            lock (_defaultLock) { _defaultInstance = instance; _defaultInitialized = true; }
            return instance;
        }

        /// <summary>生成下一个唯一 ID（全局单例，单调时钟）</summary>
        public static long Next() => Default.NextId();

        /// <summary>为指定时间生成 ID（使用系统时钟，受回拨影响）</summary>
        public static long Next(DateTime time) => Default.NextId(time);

        private readonly long _epochMs;
        private readonly int _workerId;
        private readonly int _dataCenterId;
        private static readonly long _stopwatchFrequency = Stopwatch.Frequency;
        private readonly long _baseStopwatchTicks;

        private const int WorkerIdBits = 5;
        private const int DataCenterIdBits = 5;
        private const int SequenceBits = 12;
        private const int MaxWorkerId = (1 << WorkerIdBits) - 1;
        private const int MaxDataCenterId = (1 << DataCenterIdBits) - 1;
        private const int MaxSequence = (1 << SequenceBits) - 1;
        private const int WorkerIdShift = SequenceBits;
        private const int DataCenterIdShift = SequenceBits + WorkerIdBits;
        private const int TimestampShift = SequenceBits + WorkerIdBits + DataCenterIdBits;

        private long _sequence;
        private long _lastTimestamp = -1L;
        private readonly object _lock = new object();

        /// <summary>建造者 — 链式配置并创建 SnowflakeIdHelper 实例</summary>
        public class Builder
        {
            internal int WorkerId = -1;
            internal int DataCenterId = -1;
            internal DateTime Epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            /// <summary>设置工作节点 ID（0-31）</summary>
            public Builder SetWorkerId(int workerId)
            {
                if (workerId < 0 || workerId > MaxWorkerId)
                    throw new ArgumentOutOfRangeException(nameof(workerId),
                        $"WorkerId 必须在 0-{MaxWorkerId} 之间，当前值: {workerId}");
                WorkerId = workerId;
                return this;
            }

            /// <summary>设置数据中心 ID（0-31）</summary>
            public Builder SetDataCenterId(int dataCenterId)
            {
                if (dataCenterId < 0 || dataCenterId > MaxDataCenterId)
                    throw new ArgumentOutOfRangeException(nameof(dataCenterId),
                        $"DataCenterId 必须在 0-{MaxDataCenterId} 之间，当前值: {dataCenterId}");
                DataCenterId = dataCenterId;
                return this;
            }

            /// <summary>设置起始纪元（UTC 时间），默认 2024-01-01</summary>
            public Builder SetEpoch(DateTime epoch)
            {
                if (epoch.Kind == DateTimeKind.Local) epoch = epoch.ToUniversalTime();
                Epoch = epoch;
                return this;
            }

            /// <summary>创建 SnowflakeIdHelper 实例，同时锁定单调时钟起点</summary>
            public SnowflakeIdHelper Create()
            {
                var workerId = WorkerId >= 0 ? WorkerId : GetDefaultWorkerId();
                var dataCenterId = DataCenterId >= 0 ? DataCenterId : GetDefaultDataCenterId();
                return new SnowflakeIdHelper(workerId, dataCenterId, Epoch);
            }

            private static int GetDefaultWorkerId()
            {
                var hash = Math.Abs(Environment.MachineName.GetHashCode());
                return hash % (MaxWorkerId + 1);
            }

            private static int GetDefaultDataCenterId()
            {
                var source = Environment.CurrentDirectory;
                try
                {
                    var asm = System.Reflection.Assembly.GetEntryAssembly();
                    if (asm != null && !string.IsNullOrEmpty(asm.Location)) source = asm.Location;
                }
                catch { }
                return Math.Abs(source.GetHashCode()) % (MaxDataCenterId + 1);
            }
        }

        /// <summary>创建建造者实例</summary>
        public static Builder Build() => new Builder();

        private SnowflakeIdHelper(int workerId, int dataCenterId, DateTime epoch)
        {
            _workerId = workerId;
            _dataCenterId = dataCenterId;
            _epochMs = (long)(epoch - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            _baseStopwatchTicks = Stopwatch.GetTimestamp();
        }

        /// <summary>当前 WorkerId</summary>
        public int WorkerId => _workerId;

        /// <summary>当前 DataCenterId</summary>
        public int DataCenterId => _dataCenterId;

        /// <summary>机器指纹（0~1023），集群中每节点必须唯一</summary>
        public int MachineFingerprint => (_dataCenterId << WorkerIdBits) | _workerId;

        /// <summary>Stopwatch 基准嘀嗒数（仅供诊断）</summary>
        public long BaseStopwatchTicks => _baseStopwatchTicks;

        /// <summary>生成下一个唯一 ID（单调时钟，永不回拨）</summary>
        public long NextId() => NextIdCore(GetMonotonicTimestamp());

        /// <summary>为指定时间生成 ID（使用系统时钟，受回拨影响）</summary>
        public long NextId(DateTime time)
        {
            if (time.Kind == DateTimeKind.Local) time = time.ToUniversalTime();
            var timestamp = (long)(time - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return NextIdCore(timestamp);
        }

        private long NextIdCore(long timestamp)
        {
            lock (_lock)
            {
                if (timestamp < _lastTimestamp)
                {
                    throw new InvalidOperationException(
                        $"时间戳回退 {_lastTimestamp - timestamp}ms。" +
                        $"请使用无参 NextId()（单调时钟）。" +
                        $"WorkerId={_workerId}, DataCenterId={_dataCenterId}");
                }

                if (timestamp == _lastTimestamp)
                {
                    _sequence = (_sequence + 1) & MaxSequence;
                    if (_sequence == 0) timestamp = WaitNextMillis(_lastTimestamp);
                }
                else
                {
                    _sequence = 0;
                }

                _lastTimestamp = timestamp;

                return ((timestamp - _epochMs) << TimestampShift)
                       | ((long)_dataCenterId << DataCenterIdShift)
                       | ((long)_workerId << WorkerIdShift)
                       | _sequence;
            }
        }

        /// <summary>雪花 ID 信息</summary>
        public readonly struct SnowflakeIdInfo
        {
            /// <summary>UTC 时间戳</summary>
            public DateTime Timestamp { get; init; }
            /// <summary>转为本地时间的 DateTimeOffset</summary>
            public DateTimeOffset DateTimeOffset => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToLocalTime();
            /// <summary>数据中心 ID</summary>
            public int DataCenterId { get; init; }
            /// <summary>工作节点 ID</summary>
            public int WorkerId { get; init; }
            /// <summary>毫秒内序列号</summary>
            public int Sequence { get; init; }
            /// <summary>原始 ID 值</summary>
            public long RawId { get; init; }

            /// <inheritdoc />
            public override string ToString()
                => $"Id={RawId}, Time={Timestamp:yyyy-MM-dd HH:mm:ss.fff}, " +
                   $"DC={DataCenterId}, Worker={WorkerId}, Seq={Sequence}";
        }

        /// <summary>反解雪花 ID（使用默认纪元 2024-01-01 UTC）</summary>
        public static SnowflakeIdInfo Parse(long id)
            => Parse(id, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        /// <summary>使用指定纪元反解雪花 ID</summary>
        public static SnowflakeIdInfo Parse(long id, DateTime epoch)
        {
            if (epoch.Kind == DateTimeKind.Local) epoch = epoch.ToUniversalTime();
            var epochMs = (long)(epoch - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

            var timestamp = (id >> TimestampShift) + epochMs;
            var workerId = (int)((id >> WorkerIdShift) & MaxWorkerId);
            var dataCenterId = (int)((id >> DataCenterIdShift) & MaxDataCenterId);
            var sequence = (int)(id & MaxSequence);

            var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return new SnowflakeIdInfo
            {
                RawId = id,
                Timestamp = unixEpoch.AddMilliseconds(timestamp),
                WorkerId = workerId,
                DataCenterId = dataCenterId,
                Sequence = sequence,
            };
        }

        /// <summary>获取单调时间戳（基于 Stopwatch 硬件计数器，与系统时钟解耦）</summary>
        private long GetMonotonicTimestamp()
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - _baseStopwatchTicks;
            var elapsedMs = elapsedTicks * 1000L / _stopwatchFrequency;
            return _epochMs + elapsedMs;
        }

        /// <summary>自旋等待直到进入下一毫秒（序列号耗尽时使用）</summary>
        private long WaitNextMillis(long lastTimestamp)
        {
            long timestamp;
            do { Thread.Sleep(0); timestamp = GetMonotonicTimestamp(); }
            while (timestamp <= lastTimestamp);
            return timestamp;
        }
    }
}
