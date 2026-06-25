using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace VassasCo.Utility
{
    /// <summary>
    /// 通用配置管理器 — 实体类 ⇄ JSON/XML 配置文件双向映射。
    /// 支持热重载、原子保存、注释写入、列表初始化。
    /// </summary>
    /// <typeparam name="T">配置实体类（必须有无参构造函数）</typeparam>
    public class ConfigHelper<T> : IDisposable where T : class, new()
    {
        private readonly string _filePath;
        private readonly ConfigFormat _format;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);
        private T _config = default!;
        private FileSystemWatcher? _fileWatcher;
        private volatile bool _saving;
        private int _lastHotReloadTick;
        private DateTime _lastWriteTime;
        private bool _disposed;
        private readonly List<PropMeta> _propMetas = new();

        /// <summary>配置变更事件（在线程池触发）</summary>
        public event EventHandler<ConfigChangedEventArgs<T>>? ConfigChanged;

        /// <summary>保存前拦截事件</summary>
        public event EventHandler<ConfigSavingEventArgs<T>>? ConfigSaving;

        public ConfigHelper(string filePath, ConfigFormat format = ConfigFormat.Json,
            JsonSerializerOptions? jsonOptions = null, bool autoLoad = true)
        {
            _filePath = Path.GetFullPath(filePath);
            _format = format;
            _jsonOptions = jsonOptions ?? CreateDefaultJsonOptions();
            BuildPropMetaCache();
            _config = new T();

            if (autoLoad)
                _config = Load();
        }

        private static JsonSerializerOptions CreateDefaultJsonOptions() => new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new CustomDateTimeConverter()
            }
        };

        public T Value
        {
            get
            {
                _rwLock.EnterReadLock();
                try { return _config; }
                finally { _rwLock.ExitReadLock(); }
            }
        }

        public string FilePath => _filePath;
        public bool FileExists => File.Exists(_filePath);
        public Exception? LastError { get; private set; }
        public IReadOnlyList<PropMeta> Properties => _propMetas.AsReadOnly();

        /// <summary>安全加载：失败不抛异常，返回 false</summary>
        public bool TryLoad(out T config)
        {
            try { config = Load(); LastError = null; return true; }
            catch (Exception ex) { config = _config; LastError = ex; return false; }
        }

        /// <summary>安全重载：失败不抛异常</summary>
        public bool TryReload(out T config)
        {
            try { config = Reload(); LastError = null; return true; }
            catch (Exception ex) { config = _config; LastError = ex; return false; }
        }

        public T Load()
        {
            _rwLock.EnterWriteLock();
            try
            {
                T? oldConfig = _config;
                if (!File.Exists(_filePath))
                {
                    _config = CreateWithDefaults().EnsureListsInitialized().EnsureDefaultsForNulls();
                    SerializeToFile(_config);
                    RaiseChanged(new ConfigChangedEventArgs<T>(oldConfig, _config, ConfigChangeType.Initial));
                    return _config;
                }

                T loaded = DeserializeFromFile();
                loaded.EnsureListsInitialized();
                ApplyDefaultForNullProperties(loaded);
                ValidateRequired(loaded);

                _config = loaded;
                RaiseChanged(new ConfigChangedEventArgs<T>(oldConfig, _config, ConfigChangeType.Initial));
                return _config;
            }
            catch (Exception ex) when (ex is not ConfigValidationException)
            {
                TryBackupCorruptedFile();
                _config = CreateWithDefaults().EnsureListsInitialized().EnsureDefaultsForNulls();
                SerializeToFile(_config);
                RaiseChanged(new ConfigChangedEventArgs<T>(null, _config, ConfigChangeType.Initial));
                return _config;
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public T Reload() => ReloadInternal(ConfigChangeType.Reloaded);

        private T ReloadInternal(ConfigChangeType changeType, bool skipIfUnchanged = false)
        {
            _rwLock.EnterWriteLock();
            try
            {
                if (!File.Exists(_filePath)) return _config;
                var oldConfig = CloneConfig(_config);
                var loaded = DeserializeFromFile();
                loaded.EnsureListsInitialized();
                ApplyDefaultForNullProperties(loaded);
                ValidateRequired(loaded);

                if (skipIfUnchanged)
                {
                    var oldJson = JsonSerializer.Serialize(oldConfig, _jsonOptions);
                    var newJson = JsonSerializer.Serialize(loaded, _jsonOptions);
                    if (oldJson == newJson) return _config;
                }

                _config = loaded;
                RaiseChanged(new ConfigChangedEventArgs<T>(oldConfig, _config, changeType));
                return _config;
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public void Save()
        {
            _saving = true;
            _rwLock.EnterReadLock();
            try
            {
                var savingArgs = new ConfigSavingEventArgs<T>(_config);
                ConfigSaving?.Invoke(this, savingArgs);
                if (savingArgs.Cancel) return;

                var toSave = savingArgs.Config ?? _config;
                ValidateRequired(toSave);
                var oldConfig = CloneConfig(_config);
                SerializeToFile(toSave);
                _lastWriteTime = File.GetLastWriteTimeUtc(_filePath);
                RaiseChanged(new ConfigChangedEventArgs<T>(oldConfig, _config, ConfigChangeType.Saved));
            }
            finally
            {
                _rwLock.ExitReadLock();
                _saving = false;
            }
        }

        public void SaveCopy(T config)
        {
            _saving = true;
            _rwLock.EnterReadLock();
            try
            {
                ValidateRequired(config);
                SerializeToFile(config);
                _lastWriteTime = File.GetLastWriteTimeUtc(_filePath);
            }
            finally
            {
                _rwLock.ExitReadLock();
                _saving = false;
            }
        }

        private void SerializeToFile(T config)
        {
            string content = _format == ConfigFormat.Xml
                ? XmlCommentWriter.Serialize(config, this)
                : JsonCommentWriter.Serialize(config, this);

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = _filePath + ".tmp";
            File.WriteAllText(tmpPath, content, Encoding.UTF8);
#if NETSTANDARD2_0
            if (File.Exists(_filePath)) File.Delete(_filePath);
            File.Move(tmpPath, _filePath);
#else
            File.Move(tmpPath, _filePath, overwrite: true);
#endif
        }

        private T DeserializeFromFile()
        {
            var content = File.ReadAllText(_filePath);
            if (_format == ConfigFormat.Xml)
            {
                var serializer = XmlConfigHelper.CreateSerializer<T>();
                using var reader = new StringReader(content);
                return (T)(serializer.Deserialize(reader) ?? new T());
            }

            content = StripJsonComments(content);
            return JsonSerializer.Deserialize<T>(content, _jsonOptions) ?? new T();
        }

        private static string StripJsonComments(string json)
        {
            var sb = new StringBuilder(json.Length);
            var lines = json.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//")) continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        public void EnableHotReload()
        {
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

        public T GetSnapshot()
        {
            _rwLock.EnterReadLock();
            try { return CloneConfig(_config); }
            finally { _rwLock.ExitReadLock(); }
        }

        public void Update(Action<T> updateAction)
        {
            _rwLock.EnterWriteLock();
            try
            {
                var old = CloneConfig(_config);
                updateAction(_config);
                RaiseChanged(new ConfigChangedEventArgs<T>(old, _config, ConfigChangeType.Saved));
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public object? GetPropertyValue(string name)
        {
            _rwLock.EnterReadLock();
            try
            {
                var prop = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new ArgumentException($"属性 '{name}' 不存在");
                return prop.GetValue(_config);
            }
            finally { _rwLock.ExitReadLock(); }
        }

        public void SetPropertyValue(string name, object? value)
        {
            _rwLock.EnterWriteLock();
            try
            {
                var prop = typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new ArgumentException($"属性 '{name}' 不存在");
                var old = CloneConfig(_config);
                prop.SetValue(_config, Convert.ChangeType(value, prop.PropertyType));
                RaiseChanged(new ConfigChangedEventArgs<T>(old, _config, ConfigChangeType.Saved));
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public void ImportFromJson(string json)
        {
            _rwLock.EnterWriteLock();
            try
            {
                var imported = JsonSerializer.Deserialize<T>(json, _jsonOptions)
                    ?? throw new InvalidOperationException("反序列化失败");
                imported.EnsureListsInitialized();
                ApplyDefaultForNullProperties(imported);
                var old = CloneConfig(_config);
                _config = imported;
                RaiseChanged(new ConfigChangedEventArgs<T>(old, _config, ConfigChangeType.Saved));
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public string ExportToJson(bool indented = true)
        {
            _rwLock.EnterReadLock();
            try { return JsonSerializer.Serialize(_config, new JsonSerializerOptions(_jsonOptions) { WriteIndented = indented }); }
            finally { _rwLock.ExitReadLock(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisableHotReload();
            _rwLock.Dispose();
        }

        internal IEnumerable<PropMeta> GetPropMetas() => _propMetas;
        internal T GetConfig() => _config;

        private void BuildPropMetaCache()
        {
            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;

                var cfg = prop.GetCustomAttribute<ConfigAttribute>();
                if (cfg == null)
                {
                    cfg = BuildCompatConfig(prop);
                    if (cfg?.Ignore == true) continue;
                }
                else
                {
                    if (cfg.Ignore) continue;
                    cfg.Validate(prop, typeof(T).Name);
                }

                var desc = cfg?.Desc ?? prop.GetCustomAttribute<DescriptionAttribute>()?.Description;

                _propMetas.Add(new PropMeta
                {
                    Property = prop,
                    JsonKey = ResolveJsonKey(prop, cfg),
                    XmlElementName = ResolveXmlElementName(prop, cfg),
                    HasConfigDefault = cfg?.Default != null,
                    ConfigDefaultValue = cfg?.Default,
                    IsRequired = cfg?.Required ?? false,
                    MaxStringLength = cfg?.StringLength > 0 ? cfg.StringLength : null,
                    Description = desc,
                    ConverterType = cfg?.Converter,
                    ConverterArgs = cfg?.ConverterArgs,
                    CanWrite = prop.CanWrite
                });
            }
        }

        private static ConfigAttribute? BuildCompatConfig(PropertyInfo prop)
        {
            bool hasAny = false;
            var cfg = new ConfigAttribute();

            if (prop.GetCustomAttribute<ConfigIgnoreAttribute>() != null) { cfg.Ignore = true; hasAny = true; }
            if (prop.GetCustomAttribute<ConfigKeyAttribute>() is { } ck) { cfg.Key = ck.Key; hasAny = true; }
            if (prop.GetCustomAttribute<ConfigDefaultAttribute>() is { } cd) { cfg.Default = cd.Value; hasAny = true; }
            if (prop.GetCustomAttribute<ConfigRequiredAttribute>() != null) { cfg.Required = true; hasAny = true; }
            if (prop.GetCustomAttribute<ConfigStringLengthAttribute>() is { } sl) { cfg.StringLength = sl.MaxLength; hasAny = true; }
            if (prop.GetCustomAttribute<ConfigConverterAttribute>() is { } cc) { cfg.Converter = cc.ConverterType; hasAny = true; }

            return hasAny ? cfg : null;
        }

        private string ResolveJsonKey(PropertyInfo prop, ConfigAttribute? cfg)
        {
            if (!string.IsNullOrEmpty(cfg?.Key)) return cfg!.Key!;
            if (prop.GetCustomAttribute<ConfigKeyAttribute>() is { } ck) return ck.Key;
            if (prop.GetCustomAttribute<JsonPropertyNameAttribute>() is { } jp) return jp.Name;
            return _jsonOptions.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;
        }

        private static string ResolveXmlElementName(PropertyInfo prop, ConfigAttribute? cfg)
        {
            if (!string.IsNullOrEmpty(cfg?.Key)) return cfg!.Key!;
            if (prop.GetCustomAttribute<XmlElementAttribute>() is { } xe && !string.IsNullOrEmpty(xe.ElementName))
                return xe.ElementName;
            return prop.Name;
        }

        private T CreateWithDefaults()
        {
            var instance = new T();
            ApplyConfigDefaultAttributes(instance);
            return instance;
        }

        private void ApplyConfigDefaultAttributes(T instance)
        {
            foreach (var meta in _propMetas)
            {
                if (!meta.HasConfigDefault || meta.ConfigDefaultValue == null || !meta.CanWrite) continue;
                var current = meta.Property.GetValue(instance);
                var typeDefault = meta.Property.PropertyType.IsValueType
                    ? Activator.CreateInstance(meta.Property.PropertyType) : null;

                if (Equals(current, typeDefault))
                    meta.Property.SetValue(instance, Convert.ChangeType(meta.ConfigDefaultValue, meta.Property.PropertyType));
            }
        }

        private void ApplyDefaultForNullProperties(T instance)
        {
            foreach (var meta in _propMetas)
            {
                if (!meta.CanWrite) continue;
                var current = meta.Property.GetValue(instance);
                if (current != null) continue;

                var pt = meta.Property.PropertyType;
                if (pt.IsValueType)
                    meta.Property.SetValue(instance, Activator.CreateInstance(pt));
                else if (pt == typeof(string))
                    meta.Property.SetValue(instance, string.Empty);
                else if (pt.IsGenericType && typeof(IList).IsAssignableFrom(pt))
                    meta.Property.SetValue(instance, Activator.CreateInstance(pt));
            }
        }

        private void ValidateRequired(T instance)
        {
            foreach (var meta in _propMetas)
            {
                if (!meta.IsRequired) continue;
                var value = meta.Property.GetValue(instance);
                if (value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
                    throw new ConfigValidationException(
                        $"配置必填字段 '{meta.Property.Name}' 缺失或为空。文件: {_filePath}");
            }
        }

        private T CloneConfig(T source)
        {
            var json = JsonSerializer.Serialize(source, _jsonOptions);
            return JsonSerializer.Deserialize<T>(json, _jsonOptions)!;
        }

        private void TryBackupCorruptedFile()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var backup = _filePath + $".corrupted_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Move(_filePath, backup);
            }
            catch { }
        }

        private void RaiseChanged(ConfigChangedEventArgs<T> e)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { ConfigChanged?.Invoke(this, e); } catch { }
            });
        }

        private sealed class CustomDateTimeConverter : JsonConverter<DateTime>
        {
            private const string Format = "yyyy-MM-dd HH:mm:ss";

            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var s = reader.GetString();
                if (string.IsNullOrEmpty(s)) return default;
                if (DateTime.TryParseExact(s, Format, null, System.Globalization.DateTimeStyles.None, out var dt))
                    return dt;
                return DateTime.Parse(s);
            }

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString(Format));
            }
        }
    }

    /// <summary>
    /// 配置工厂 — 从类特性自动识别格式，零代码加载/保存配置。
    /// 用法：[JsonConfig("config/app.json")] public class AppConfig { ... }
    /// </summary>
    public static class ConfigFactory
    {
        private static readonly ConcurrentDictionary<Type, object> _helpers = new();

        public static T Load<T>() where T : class, new()
        {
            var type = typeof(T);
            if (_helpers.TryGetValue(type, out var existing) && existing is ConfigHelper<T> h)
                return h.Value;

            var attr = GetConfigAttribute(type);
            var helper = CreateHelper<T>(attr);
            _helpers[type] = helper;
            return helper.Value;
        }

        public static void Save<T>(T config) where T : class, new()
        {
            if (_helpers.TryGetValue(typeof(T), out var h) && h is ConfigHelper<T> helper)
                helper.Save();
            else
            {
                var attr = GetConfigAttribute(typeof(T));
                var tempHelper = CreateHelper<T>(attr);
                tempHelper.SaveCopy(config ?? tempHelper.Value);
            }
        }

        public static ConfigHelper<T>? GetHelper<T>() where T : class, new()
            => _helpers.TryGetValue(typeof(T), out var h) ? h as ConfigHelper<T> : null;

        public static ConfigHelper<T> Register<T>(string filePath, ConfigFormat format = ConfigFormat.Json)
            where T : class, new()
        {
            var helper = new ConfigHelper<T>(filePath, format);
            _helpers[typeof(T)] = helper;
            return helper;
        }

        public static void Generate<T>() where T : class, new()
        {
            var attr = GetConfigAttribute(typeof(T));
            var tempHelper = CreateHelper<T>(attr);
            if (!File.Exists(tempHelper.FilePath))
                tempHelper.Save();
        }

        private static (string filePath, ConfigFormat format) GetConfigAttribute(Type type)
        {
            var jsonAttr = type.GetCustomAttribute<JsonConfigAttribute>();
            if (jsonAttr != null) return (jsonAttr.FilePath, ConfigFormat.Json);

            var xmlAttr = type.GetCustomAttribute<XmlConfigAttribute>();
            if (xmlAttr != null) return (xmlAttr.FilePath, ConfigFormat.Xml);

            throw new InvalidOperationException(
                $"类型 '{type.Name}' 未标注 [JsonConfig] 或 [XmlConfig] 特性");
        }

        private static ConfigHelper<T> CreateHelper<T>((string path, ConfigFormat format) attr) where T : class, new()
            => CreateHelper<T>(attr.path, attr.format);

        private static ConfigHelper<T> CreateHelper<T>(string filePath, ConfigFormat format) where T : class, new()
            => new ConfigHelper<T>(filePath, format, autoLoad: true);
    }

    /// <summary>配置管理器 — 全局注册</summary>
    public static class ConfigManager
    {
        private static readonly ConcurrentDictionary<Type, object> _configs = new();
        private static readonly ConcurrentDictionary<Type, object> _helpersDict = new();

        public static TConfig Register<TConfig>(string filePath, JsonSerializerOptions? jsonOptions = null)
            where TConfig : class, new()
        {
            var helper = new ConfigHelper<TConfig>(filePath, ConfigFormat.Json, jsonOptions);
            _helpersDict[typeof(TConfig)] = helper;
            _configs[typeof(TConfig)] = helper.Value;
            return helper.Value;
        }

        public static ConfigHelper<TConfig>? GetHelper<TConfig>() where TConfig : class, new()
            => _helpersDict.TryGetValue(typeof(TConfig), out var h) ? h as ConfigHelper<TConfig> : null;

        public static TConfig? Get<TConfig>() where TConfig : class, new()
            => _configs.TryGetValue(typeof(TConfig), out var c) ? c as TConfig : null;

        public static TConfig GetOrRegister<TConfig>(string filePath) where TConfig : class, new()
        {
            if (_configs.TryGetValue(typeof(TConfig), out var c)) return (TConfig)c;
            return Register<TConfig>(filePath);
        }

        public static void Unregister<TConfig>() where TConfig : class, new()
        {
            if (_helpersDict.TryRemove(typeof(TConfig), out var h) && h is IDisposable d) d.Dispose();
            _configs.TryRemove(typeof(TConfig), out _);
        }
    }

    /// <summary>
    /// 配置实体类基类。继承后可通过 T.Current 获取配置实例。
    /// 子类必须标记 [JsonConfig] 或 [XmlConfig] 特性。
    /// </summary>
    public abstract class ConfigBase<T> where T : ConfigBase<T>, new()
    {
        public static T Current => ConfigFactory.Load<T>();
        public static ConfigHelper<T>? Helper => ConfigFactory.GetHelper<T>();
    }

    /// <summary>直接从 JSON/XML 文件查找字段值，无需定义实体类</summary>
    public static class ConfigRawReader
    {
        public static object? GetJsonValue(string filePath, string path)
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            return GetJsonValueFromString(json, path);
        }

        public static object? GetJsonValueFromString(string json, string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                var segments = ParsePath(path);
                var element = doc.RootElement;

                foreach (var seg in segments)
                {
                    if (seg.IsIndex)
                    {
                        if (element.ValueKind != JsonValueKind.Array) return null;
                        var arr = element.EnumerateArray().ToArray();
                        if (seg.Index < 0 || seg.Index >= arr.Length) return null;
                        element = arr[seg.Index];
                    }
                    else
                    {
                        if (element.ValueKind != JsonValueKind.Object) return null;
                        if (!element.TryGetProperty(seg.Name, out var prop))
                        {
                            var found = false;
                            foreach (var p in element.EnumerateObject())
                            {
                                if (string.Equals(p.Name, seg.Name, StringComparison.OrdinalIgnoreCase))
                                { element = p.Value; found = true; break; }
                            }
                            if (!found) return null;
                            continue;
                        }
                        element = prop;
                    }
                }

                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt32(out var i) ? (object)i : element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => element
                };
            }
            catch { return null; }
        }

        public static string? GetXmlValue(string filePath, string xpath)
        {
            if (!File.Exists(filePath)) return null;
            var xml = File.ReadAllText(filePath);
            return GetXmlValueFromString(xml, xpath);
        }

        public static string? GetXmlValueFromString(string xml, string xpath)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var node = doc.SelectSingleNode(xpath);
                return node?.InnerText;
            }
            catch { return null; }
        }

        private struct PathSegment
        {
            public string Name;
            public int Index;
            public bool IsIndex;
            public PathSegment(string name) { Name = name; Index = -1; IsIndex = false; }
            public PathSegment(int index) { Name = ""; Index = index; IsIndex = true; }
        }

        private static PathSegment[] ParsePath(string path)
        {
            var parts = path.Split('.');
            var segments = new List<PathSegment>();
            foreach (var part in parts)
            {
                var bracketStart = part.IndexOf('[');
                if (bracketStart >= 0)
                {
                    var name = part.Substring(0, bracketStart);
                    if (!string.IsNullOrEmpty(name)) segments.Add(new PathSegment(name));
                    var bracketEnd = part.IndexOf(']', bracketStart);
                    if (bracketEnd > bracketStart)
                    {
                        var indexStr = part.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
                        if (int.TryParse(indexStr, out var idx)) segments.Add(new PathSegment(idx));
                    }
                }
                else segments.Add(new PathSegment(part));
            }
            return segments.ToArray();
        }
    }

    /// <summary>将实体类序列化为带注释的 JSON（JSONC 格式）</summary>
    internal static class JsonCommentWriter
    {
        public static string Serialize<T>(T instance, ConfigHelper<T> helper) where T : class, new()
        {
            var sb = new StringBuilder();
            var metas = helper.GetPropMetas().ToList();
            WriteObject(sb, instance, metas, 0);
            return sb.ToString();
        }

        private static void WriteObject(StringBuilder sb, object? instance, List<PropMeta> metas, int depth)
        {
            if (instance == null) { sb.Append("null"); return; }

            sb.AppendLine("{");
            var indent = new string(' ', (depth + 1) * 2);

            for (int i = 0; i < metas.Count; i++)
            {
                var meta = metas[i];
                var value = meta.Property.GetValue(instance);
                WriteComments(sb, meta, value, indent);
                sb.Append(indent);
                sb.Append('"').Append(EscapeJson(meta.JsonKey)).Append("\": ");
                WriteValue(sb, value, meta, depth + 1);
                if (i < metas.Count - 1) sb.Append(',');
                sb.AppendLine();
            }

            sb.Append(new string(' ', depth * 2)).Append('}');
        }

        private static void WriteComments(StringBuilder sb, PropMeta meta, object? value, string indent)
        {
            var comments = new List<string>();
            if (!string.IsNullOrEmpty(meta.Description)) comments.Add(meta.Description!);
            if (meta.HasConfigDefault && meta.ConfigDefaultValue != null) comments.Add($"[默认值: {meta.ConfigDefaultValue}]");
            if (meta.MaxStringLength.HasValue) comments.Add($"[最大长度: {meta.MaxStringLength}]");
            if (value is IList list && !(value is string)) comments.Add($"[列表项数: {list.Count}]");

            foreach (var c in comments) sb.Append(indent).Append("// ").AppendLine(c);
        }

        private static void WriteValue(StringBuilder sb, object? value, PropMeta meta, int depth)
        {
            if (value == null) { sb.Append("null"); return; }

            var converter = meta.GetConverter();
            if (converter != null) { sb.Append('"').Append(EscapeJson(converter.ConvertTo(value))).Append('"'); return; }

            var type = value.GetType();

            if (value is string s)
            {
                if (meta.MaxStringLength.HasValue && s.Length > meta.MaxStringLength.Value)
                    s = s.Substring(0, meta.MaxStringLength.Value);
                sb.Append('"').Append(EscapeJson(s)).Append('"');
            }
            else if (type.IsPrimitive || value is decimal)
                sb.Append(JsonSerializer.Serialize(value));
            else if (type.IsEnum)
                sb.Append('"').Append(value.ToString()).Append('"');
            else if (value is DateTime dt)
                sb.Append('"').Append(dt.ToString("yyyy-MM-dd HH:mm:ss")).Append('"');
            else if (value is IList list)
                WriteListValue(sb, list, depth);
            else if (value is IDictionary dict)
                WriteDictionaryValue(sb, dict, depth);
            else
            {
                var nestedMetas = BuildNestedMetas(type);
                if (nestedMetas.Count > 0) WriteObject(sb, value, nestedMetas, depth);
                else sb.Append(JsonSerializer.Serialize(value));
            }
        }

        private static void WriteListValue(StringBuilder sb, IList list, int depth)
        {
            if (list.Count == 0) { sb.Append("[]"); return; }

            sb.AppendLine("[");
            var indent = new string(' ', (depth + 1) * 2);
            var itemType = list.GetType().GetGenericArguments().FirstOrDefault();

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                sb.Append(indent);
                if (item == null) sb.Append("null");
                else if (item is string s) sb.Append('"').Append(EscapeJson(s)).Append('"');
                else if (item.GetType().IsPrimitive || item is decimal) sb.Append(JsonSerializer.Serialize(item));
                else if (itemType != null)
                {
                    var nestedMetas = BuildNestedMetas(itemType);
                    if (nestedMetas.Count > 0) WriteObject(sb, item, nestedMetas, depth + 1);
                    else sb.Append(JsonSerializer.Serialize(item));
                }
                else sb.Append(JsonSerializer.Serialize(item));

                if (i < list.Count - 1) sb.Append(',');
                sb.AppendLine();
            }

            sb.Append(new string(' ', depth * 2)).Append(']');
        }

        private static void WriteDictionaryValue(StringBuilder sb, IDictionary dict, int depth)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }

            sb.AppendLine("{");
            var indent = new string(' ', (depth + 1) * 2);
            var keys = new ArrayList(dict.Keys);

            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i]?.ToString() ?? "null";
                var val = dict[keys[i]!];
                sb.Append(indent).Append('"').Append(EscapeJson(key)).Append("\": ");
                if (val is string sv) sb.Append('"').Append(EscapeJson(sv)).Append('"');
                else sb.Append(JsonSerializer.Serialize(val));
                if (i < keys.Count - 1) sb.Append(',');
                sb.AppendLine();
            }

            sb.Append(new string(' ', depth * 2)).Append('}');
        }

        private static List<PropMeta> BuildNestedMetas(Type type)
        {
            var list = new List<PropMeta>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;

                var cfg = prop.GetCustomAttribute<ConfigAttribute>();
                if (cfg?.Ignore == true || (cfg == null && prop.GetCustomAttribute<ConfigIgnoreAttribute>() != null))
                    continue;

                list.Add(new PropMeta
                {
                    Property = prop,
                    JsonKey = cfg?.Key
                        ?? prop.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                        ?? prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                        ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name),
                    HasConfigDefault = cfg?.Default != null
                        || (cfg == null && prop.GetCustomAttribute<ConfigDefaultAttribute>() != null),
                    ConfigDefaultValue = cfg?.Default
                        ?? prop.GetCustomAttribute<ConfigDefaultAttribute>()?.Value,
                    Description = cfg?.Desc ?? prop.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    MaxStringLength = cfg?.StringLength > 0 ? cfg.StringLength
                        : prop.GetCustomAttribute<ConfigStringLengthAttribute>()?.MaxLength,
                    ConverterType = cfg?.Converter
                        ?? prop.GetCustomAttribute<ConfigConverterAttribute>()?.ConverterType,
                    ConverterArgs = cfg?.ConverterArgs,
                });
            }
            return list;
        }

        private static string EscapeJson(string s)
        {
            int escapeCount = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] is '\\' or '"' or '\n' or '\r' or '\t') escapeCount++;
            }
            if (escapeCount == 0) return s;

            var sb = new StringBuilder(s.Length + escapeCount);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>将实体类序列化为带注释的 XML</summary>
    internal static class XmlCommentWriter
    {
        public static string Serialize<T>(T instance, ConfigHelper<T> helper) where T : class, new()
        {
            var rootAttr = typeof(T).GetCustomAttribute<XmlConfigAttribute>();
            var rootName = rootAttr?.RootName ?? typeof(T).Name;

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = false
            };

            using var sw = new StringWriterWithEncoding(Encoding.UTF8);
            using var writer = XmlWriter.Create(sw, settings);

            writer.WriteStartDocument();
            writer.WriteStartElement(rootName);

            var metas = helper.GetPropMetas().ToList();
            WriteObject(writer, instance, metas);

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();

            return sw.ToString();
        }

        private static void WriteObject(XmlWriter writer, object? instance, List<PropMeta> metas)
        {
            if (instance == null) return;
            foreach (var meta in metas)
            {
                var value = meta.Property.GetValue(instance);
                WriteXmlComments(writer, meta, value);
                writer.WriteStartElement(meta.XmlElementName);
                if (value == null) writer.WriteString("");
                else WriteXmlValue(writer, value, meta);
                writer.WriteEndElement();
            }
        }

        private static void WriteXmlComments(XmlWriter writer, PropMeta meta, object? value)
        {
            var comments = new List<string>();
            if (!string.IsNullOrEmpty(meta.Description)) comments.Add(meta.Description!);
            if (meta.HasConfigDefault && meta.ConfigDefaultValue != null) comments.Add($"默认值: {meta.ConfigDefaultValue}");
            if (meta.MaxStringLength.HasValue) comments.Add($"最大长度: {meta.MaxStringLength}");
            if (value is IList list && !(value is string)) comments.Add($"列表项数: {list.Count}");
            foreach (var c in comments) writer.WriteComment($" {c} ");
        }

        private static void WriteXmlValue(XmlWriter writer, object value, PropMeta meta)
        {
            var converter = meta.GetConverter();
            if (converter != null) { writer.WriteString(converter.ConvertTo(value)); return; }

            var type = value.GetType();
            if (value is string s)
            {
                if (meta.MaxStringLength.HasValue && s.Length > meta.MaxStringLength.Value)
                    s = s.Substring(0, meta.MaxStringLength.Value);
                writer.WriteString(s);
            }
            else if (type.IsPrimitive || value is decimal || type.IsEnum)
                writer.WriteString(value.ToString());
            else if (value is DateTime dt)
                writer.WriteString(dt.ToString("yyyy-MM-dd HH:mm:ss"));
            else if (value is IList list)
                WriteXmlList(writer, list);
            else if (value is IDictionary dict)
                WriteXmlDictionary(writer, dict);
            else
            {
                var nestedMetas = BuildNestedMetas(type);
                if (nestedMetas.Count > 0) WriteObject(writer, value, nestedMetas);
                else writer.WriteString(value.ToString() ?? "");
            }
        }

        private static void WriteXmlList(XmlWriter writer, IList list)
        {
            var itemType = list.GetType().GetGenericArguments().FirstOrDefault();
            var itemName = itemType?.Name ?? "Item";
            foreach (var item in list)
            {
                writer.WriteStartElement(itemName);
                if (item == null) writer.WriteString("");
                else if (item is string s) writer.WriteString(s);
                else if (item.GetType().IsPrimitive || item is decimal) writer.WriteString(item.ToString()!);
                else if (itemType != null)
                {
                    var nested = BuildNestedMetas(itemType);
                    if (nested.Count > 0) WriteObject(writer, item, nested);
                    else writer.WriteString(item.ToString() ?? "");
                }
                else writer.WriteString(item.ToString() ?? "");
                writer.WriteEndElement();
            }
        }

        private static void WriteXmlDictionary(XmlWriter writer, IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                writer.WriteStartElement("Item");
                writer.WriteAttributeString("key", entry.Key?.ToString() ?? "");
                writer.WriteString(entry.Value?.ToString() ?? "");
                writer.WriteEndElement();
            }
        }

        private static List<PropMeta> BuildNestedMetas(Type type)
        {
            var list = new List<PropMeta>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetCustomAttribute<XmlIgnoreAttribute>() != null) continue;
                var cfg = prop.GetCustomAttribute<ConfigAttribute>();
                if (cfg?.Ignore == true || (cfg == null && prop.GetCustomAttribute<ConfigIgnoreAttribute>() != null))
                    continue;

                list.Add(new PropMeta
                {
                    Property = prop,
                    XmlElementName = cfg?.Key
                        ?? prop.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                        ?? prop.GetCustomAttribute<XmlElementAttribute>()?.ElementName
                        ?? prop.Name,
                    Description = cfg?.Desc ?? prop.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    MaxStringLength = cfg?.StringLength > 0 ? cfg.StringLength
                        : prop.GetCustomAttribute<ConfigStringLengthAttribute>()?.MaxLength,
                    ConverterType = cfg?.Converter
                        ?? prop.GetCustomAttribute<ConfigConverterAttribute>()?.ConverterType,
                    ConverterArgs = cfg?.ConverterArgs,
                });
            }
            return list;
        }

        private class StringWriterWithEncoding(Encoding encoding) : StringWriter
        {
            public override Encoding Encoding => encoding;
        }
    }

    /// <summary>XML 反序列化兼容辅助</summary>
    public static class XmlConfigHelper
    {
        public static XmlSerializer CreateSerializer<T>()
        {
            var overrides = new XmlAttributeOverrides();
            var type = typeof(T);
            ApplyOverrides(type, overrides);
            return new XmlSerializer(type, overrides);
        }

        private static void ApplyOverrides(Type type, XmlAttributeOverrides overrides, HashSet<Type>? visited = null)
        {
            visited ??= new HashSet<Type>();
            if (!visited.Add(type)) return;

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;
                if (prop.GetCustomAttribute<XmlIgnoreAttribute>() != null) continue;
                var cfg = prop.GetCustomAttribute<ConfigAttribute>();
                if (cfg?.Ignore == true) continue;

                var attrs = new XmlAttributes();
                var elementName = cfg?.Key
                    ?? prop.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                    ?? prop.Name;
                attrs.XmlElements.Add(new XmlElementAttribute(elementName));
                overrides.Add(type, prop.Name, attrs);

                var pt = prop.PropertyType;
                if (pt.IsGenericType && typeof(IList).IsAssignableFrom(pt))
                {
                    var itemType = pt.GetGenericArguments().FirstOrDefault();
                    if (itemType != null && itemType.IsClass && itemType != typeof(string))
                        ApplyOverrides(itemType, overrides, visited);
                }
                else if (pt.IsClass && pt != typeof(string) && pt != typeof(object))
                {
                    ApplyOverrides(pt, overrides, visited);
                }
            }
        }
    }
}
