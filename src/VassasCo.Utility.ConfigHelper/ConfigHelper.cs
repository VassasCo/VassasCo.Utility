using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>
    /// 通用配置管理器：实体类 ⇄ JSON/XML 配置文件双向映射。
    /// 读写均走自定义解析器并共用同一份元数据，保证读写严格一致。
    /// 支持热重载、原子保存、注释写入、列表/字典展开、属性别名、非侵入式 Fluent 映射、事件监听、自定义转换器。
    /// </summary>
    /// <typeparam name="T">配置实体类（必须有无参构造函数）。</typeparam>
    public class ConfigHelper<T> : IDisposable where T : class, new()
    {
        private readonly string _filePath;
        private readonly ConfigFormat _format;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ConfigMetadataProvider _metadata;
        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);
        private T _config = default!;
        private FileSystemWatcher? _fileWatcher;
        private volatile bool _saving;
        private int _lastHotReloadTick;
        private DateTime _lastWriteTime;
        private bool _disposed;

        /// <summary>配置变更事件（同步触发）。</summary>
        public event EventHandler<ConfigChangedEventArgs<T>>? ConfigChanged;

        /// <summary>保存前拦截事件（可取消保存）。</summary>
        public event EventHandler<ConfigSavingEventArgs<T>>? ConfigSaving;

        /// <summary>
        /// 创建配置管理器。
        /// </summary>
        /// <param name="filePath">配置文件路径（相对路径基于程序集目录解析）。</param>
        /// <param name="format">配置文件格式。</param>
        /// <param name="jsonOptions">
        /// 可选 JSON 选项。本库读写走自研解析器，仅 <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> 生效；
        /// 其余选项（Converters、NumberHandling、WriteIndented 等）会被忽略。
        /// </param>
        /// <param name="autoLoad">是否在构造时立即加载。</param>
        public ConfigHelper(string filePath, ConfigFormat format = ConfigFormat.Json,
            JsonSerializerOptions? jsonOptions = null, bool autoLoad = true)
            : this(filePath, format, jsonOptions, autoLoad, null)
        {
        }

        internal ConfigHelper(
            string filePath,
            ConfigFormat format,
            JsonSerializerOptions? jsonOptions,
            bool autoLoad,
            IReadOnlyDictionary<string, PropertyConfigurator>? fluentRules)
        {
            _filePath = ResolvePath(filePath);
            _format = format;
            _jsonOptions = jsonOptions ?? CreateDefaultJsonOptions();
            _metadata = new ConfigMetadataProvider(typeof(T), _jsonOptions.PropertyNamingPolicy, fluentRules);
            _config = new T();

            if (autoLoad)
                _config = Load();
        }

        private static JsonSerializerOptions CreateDefaultJsonOptions() => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>将相对路径解析为基于程序集目录的绝对路径；绝对路径原样返回。</summary>
        private static string ResolvePath(string filePath)
        {
            if (Path.IsPathRooted(filePath)) return Path.GetFullPath(filePath);
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, filePath));
        }

        /// <summary>当前配置实例（返回可变引用，请勿在未加锁情况下并发修改）。</summary>
        public T Value
        {
            get
            {
                ThrowIfDisposed();
                _rwLock.EnterReadLock();
                try { return _config; }
                finally { _rwLock.ExitReadLock(); }
            }
        }

        public string FilePath => _filePath;
        public bool FileExists => File.Exists(_filePath);
        public Exception? LastError { get; private set; }

        /// <summary>顶层属性的解析后元数据（只读）。</summary>
        public IReadOnlyList<PropMeta> Properties => _metadata.TopLevelMetas.AsReadOnly();

        public bool TryLoad(out T config)
        {
            try { config = Load(); LastError = null; return true; }
            catch (Exception ex) { config = _config; LastError = ex; return false; }
        }

        public bool TryReload(out T config)
        {
            try { config = Reload(); LastError = null; return true; }
            catch (Exception ex) { config = _config; LastError = ex; return false; }
        }

        public T Load()
        {
            ThrowIfDisposed();
            _rwLock.EnterWriteLock();
            try
            {
                var old = _config;

                if (!File.Exists(_filePath))
                {
                    var created = CreateWithDefaults();
                    InitializeObject(created);
                    _config = created;
                    SerializeToFile(created);
                    RaiseChanged(new ConfigChangedEventArgs<T>(old, created, ConfigChangeType.Initial));
                    return created;
                }

                try
                {
                    var loaded = DeserializeFromFile(out var present);
                    InitializeObject(loaded);
                    ApplyDefaults(loaded, present);
                    ValidateRequired(loaded);
                    _config = loaded;
                    RaiseChanged(new ConfigChangedEventArgs<T>(old, loaded, ConfigChangeType.Initial));
                    return loaded;
                }
                catch (Exception ex) when (IsCorruption(ex))
                {
                    if (!TryBackupCorruptedFile())
                        throw new InvalidOperationException(
                            $"配置文件损坏且备份失败，已中止重建以避免覆盖原文件。文件: {_filePath}", ex);

                    var created = CreateWithDefaults();
                    InitializeObject(created);
                    _config = created;
                    SerializeToFile(created);
                    RaiseChanged(new ConfigChangedEventArgs<T>(null, created, ConfigChangeType.Initial));
                    return created;
                }
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public T Reload() => ReloadInternal(ConfigChangeType.Reloaded, false);

        private T ReloadInternal(ConfigChangeType changeType, bool skipIfUnchanged)
        {
            ThrowIfDisposed();
            _rwLock.EnterWriteLock();
            try
            {
                if (!File.Exists(_filePath)) return _config;

                var old = Clone(_config);
                var loaded = DeserializeFromFile(out var present);
                InitializeObject(loaded);
                ApplyDefaults(loaded, present);
                ValidateRequired(loaded);

                if (skipIfUnchanged && AreEqual(old, loaded)) return _config;

                _config = loaded;
                RaiseChanged(new ConfigChangedEventArgs<T>(old, loaded, changeType));
                return loaded;
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public void Save()
        {
            ThrowIfDisposed();
            _saving = true;
            _rwLock.EnterWriteLock();
            try
            {
                var savingArgs = new ConfigSavingEventArgs<T>(_config);
                ConfigSaving?.Invoke(this, savingArgs);
                if (savingArgs.Cancel) return;

                var toSave = savingArgs.Config ?? _config;
                ValidateRequired(toSave);

                var old = Clone(_config);
                SerializeToFile(toSave);
                _config = toSave;
                _lastWriteTime = File.GetLastWriteTimeUtc(_filePath);
                RaiseChanged(new ConfigChangedEventArgs<T>(old, toSave, ConfigChangeType.Saved));
            }
            finally
            {
                _rwLock.ExitWriteLock();
                _saving = false;
            }
        }

        /// <summary>将指定实例保存到文件（不改变内存中的 <see cref="Value"/>）。</summary>
        public void SaveCopy(T config)
        {
            ThrowIfDisposed();
            _saving = true;
            _rwLock.EnterWriteLock();
            try
            {
                ValidateRequired(config);
                SerializeToFile(config);
                _lastWriteTime = File.GetLastWriteTimeUtc(_filePath);
            }
            finally
            {
                _rwLock.ExitWriteLock();
                _saving = false;
            }
        }

        /// <summary>导出当前配置为 JSON 字符串（与 Save 写出的 JSON 一致）。</summary>
        public string ExportToJson()
        {
            ThrowIfDisposed();
            _rwLock.EnterReadLock();
            try { return JsonConfigSerializer.Serialize(_config, typeof(T), _metadata); }
            finally { _rwLock.ExitReadLock(); }
        }

        public void EnableHotReload()
        {
            ThrowIfDisposed();
            if (_fileWatcher != null) return;

            var dir = Path.GetDirectoryName(_filePath) ?? ".";
            var name = Path.GetFileName(_filePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            _fileWatcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = false,
                InternalBufferSize = 65536
            };
            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.Created += OnFileChanged;
            _fileWatcher.Renamed += OnFileChanged;

            if (File.Exists(_filePath))
                _lastWriteTime = File.GetLastWriteTimeUtc(_filePath);
            Interlocked.Exchange(ref _lastHotReloadTick, Environment.TickCount);
            _fileWatcher.EnableRaisingEvents = true;
        }

        public void DisableHotReload()
        {
            if (_fileWatcher == null) return;
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Changed -= OnFileChanged;
            _fileWatcher.Created -= OnFileChanged;
            _fileWatcher.Renamed -= OnFileChanged;
            _fileWatcher.Dispose();
            _fileWatcher = null;
            _lastWriteTime = default;
            _lastHotReloadTick = 0;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath?.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true) return;
            if (_saving) return;

            if (_lastWriteTime != default)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(_filePath) <= _lastWriteTime) return;
                }
                catch { }
            }

            var now = Environment.TickCount;
            var last = Volatile.Read(ref _lastHotReloadTick);
            if (unchecked(now - last) < 500) return;
            Interlocked.Exchange(ref _lastHotReloadTick, now);

            try { ReloadInternal(ConfigChangeType.HotReload, skipIfUnchanged: true); }
            catch (Exception ex)
            {
                LastError = ex;
                System.Diagnostics.Debug.WriteLine($"[ConfigHelper] 热重载失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisableHotReload();
            _rwLock.Dispose();
        }

        // ── 内部 ──

        private void SerializeToFile(T config)
        {
            string content = _format == ConfigFormat.Xml
                ? XmlConfigSerializer.Serialize(config, typeof(T), _metadata, GetXmlRootName())
                : JsonConfigSerializer.Serialize(config, typeof(T), _metadata);

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = _filePath + ".tmp";
            File.WriteAllText(tmpPath, content, new UTF8Encoding(false));
#if NETSTANDARD2_0
            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(tmpPath, _filePath);
#else
            File.Move(tmpPath, _filePath, overwrite: true);
#endif
        }

        private T DeserializeFromFile(out HashSet<string> present)
        {
            present = new HashSet<string>(StringComparer.Ordinal);
            var content = File.ReadAllText(_filePath);
            object? obj = _format == ConfigFormat.Xml
                ? XmlConfigSerializer.Deserialize(content, typeof(T), _metadata, present)
                : JsonConfigSerializer.Deserialize(content, typeof(T), _metadata, present);
            return (T)(obj ?? new T());
        }

        private string GetXmlRootName()
        {
            var attr = typeof(T).GetCustomAttribute<XmlConfigAttribute>();
            return attr?.RootName ?? typeof(T).Name;
        }

        private T CreateWithDefaults()
        {
            var instance = new T();
            foreach (var meta in _metadata.TopLevelMetas)
                ApplyConfigDefault(instance, meta);
            return instance;
        }

        private void ApplyDefaults(T instance, HashSet<string>? present)
        {
            WalkObjectGraph(instance, new HashSet<object>(), obj =>
            {
                foreach (var meta in _metadata.GetMetas(obj.GetType()))
                {
                    if (!meta.HasConfigDefault || meta.ConfigDefaultValue == null || !meta.CanWrite) continue;
                    if (present != null && present.Contains(ConfigTypeUtil.PresentKey(obj.GetType(), meta.Property.Name))) continue;
                    ApplyConfigDefault(obj, meta);
                }
            });
        }

        private static void ApplyConfigDefault(object instance, PropMeta meta)
        {
            if (!meta.HasConfigDefault || meta.ConfigDefaultValue == null || !meta.CanWrite) return;

            var targetType = meta.Property.PropertyType;
            var value = meta.ConfigDefaultValue;
            if (!targetType.IsInstanceOfType(value))
            {
                try { value = Convert.ChangeType(value, targetType); }
                catch { return; }
            }
            meta.Property.SetValue(instance, value);
        }

        private void ValidateRequired(T instance)
        {
            WalkObjectGraph(instance, new HashSet<object>(), obj =>
            {
                foreach (var meta in _metadata.GetMetas(obj.GetType()))
                {
                    if (!meta.IsRequired) continue;
                    var value = meta.Property.GetValue(obj);
                    if (value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
                        throw new ConfigValidationException(
                            $"配置必填字段 '{meta.Property.Name}' 缺失或为空。文件: {_filePath}");
                }
            });
        }

        /// <summary>递归遍历对象图（含嵌套对象、集合元素、字典值），对每个对象执行回调。</summary>
        private void WalkObjectGraph(object? root, HashSet<object> visited, Action<object> onObject)
        {
            if (root == null || !visited.Add(root)) return;
            if (root is string || ConfigValueCodec.IsScalarType(root.GetType())) return;
            onObject(root);

            foreach (var meta in _metadata.GetMetas(root.GetType()))
                WalkNestedValue(meta.Property.GetValue(root), visited, onObject);
        }

        private void WalkNestedValue(object? value, HashSet<object> visited, Action<object> onObject)
        {
            if (value == null) return;
            if (value is string) return;

            if (value is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                    WalkObjectGraph(entry.Value, visited, onObject);
                return;
            }

            if (value is IEnumerable en)
            {
                foreach (var item in en)
                    WalkObjectGraph(item, visited, onObject);
                return;
            }

            if (!ConfigValueCodec.IsScalarType(value.GetType()))
                WalkObjectGraph(value, visited, onObject);
        }

        private void InitializeObject(object instance)
        {
            InitializeObject(instance, new HashSet<object>());
        }

        private void InitializeObject(object instance, HashSet<object> visited)
        {
            if (instance == null || !visited.Add(instance)) return;

            foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;

                var value = prop.GetValue(instance);
                if (value == null)
                {
                    var pt = prop.PropertyType;
                    if (ConfigTypeUtil.IsCollectionType(pt, out _, out _))
                    {
                        try { prop.SetValue(instance, Activator.CreateInstance(pt)); }
                        catch { }
                    }
                    continue;
                }

                if (value is IEnumerable en && !(value is string))
                {
                    foreach (var item in en) InitializeObject(item, visited);
                }
                else if (!ConfigValueCodec.IsScalarType(value.GetType()))
                {
                    InitializeObject(value, visited);
                }
            }
        }

        private T Clone(T config)
        {
            if (_format == ConfigFormat.Xml)
            {
                var xml = XmlConfigSerializer.Serialize(config, typeof(T), _metadata, GetXmlRootName());
                return (T)(XmlConfigSerializer.Deserialize(xml, typeof(T), _metadata, null) ?? new T());
            }
            var json = JsonConfigSerializer.Serialize(config, typeof(T), _metadata);
            return (T)(JsonConfigSerializer.Deserialize(json, typeof(T), _metadata, null) ?? new T());
        }

        private bool AreEqual(T a, T b) => ToCanonicalString(a) == ToCanonicalString(b);

        private string ToCanonicalString(T config) =>
            _format == ConfigFormat.Xml
                ? XmlConfigSerializer.Serialize(config, typeof(T), _metadata, GetXmlRootName())
                : JsonConfigSerializer.Serialize(config, typeof(T), _metadata);

        private static bool IsCorruption(Exception ex) =>
            ex is JsonException || ex is System.Xml.XmlException;

        private bool TryBackupCorruptedFile()
        {
            try
            {
                if (!File.Exists(_filePath)) return true;
                var backup = _filePath + ".corrupted_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Move(_filePath, backup);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RaiseChanged(ConfigChangedEventArgs<T> e) => ConfigChanged?.Invoke(this, e);

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConfigHelper<T>));
        }
    }
}
