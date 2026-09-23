# VassasCo.Utility.ConfigHelper

零代码实体类与 JSON/XML 配置文件双向映射工具（桌面端/服务端通用）。  
读写均走**自研解析器**并**共用同一份元数据（`PropMeta`）**，从架构上保证读写严格一致。

[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 目录

- [功能总览](#功能总览)
- [目标框架与依赖](#目标框架与依赖)
- [快速开始](#快速开始)
- [侵入式：特性配置](#侵入式特性配置)
- [非侵入式：Fluent 配置](#非侵入式fluent-配置)
- [别名与命名](#别名与命名)
- [枚举映射 MapType](#枚举映射-maptype)
- [集合、字典与嵌套对象](#集合字典与嵌套对象)
- [默认值与校验](#默认值与校验)
- [自定义转换器](#自定义转换器)
- [热重载与事件](#热重载与事件)
- [错误处理与损坏文件兜底](#错误处理与损坏文件兜底)
- [安全策略](#安全策略)
- [配置参考](#配置参考)
- [API 速查](#api-速查)
- [异常类型](#异常类型)
- [测试](#测试)

---

## 功能总览

| 类别 | 能力 |
|------|------|
| **零代码映射** | 实体类标注特性即可自动读写 JSON/XML |
| **读写严格对称** | 自研读写器共用同一份 `PropMeta` 元数据 |
| **双格式** | JSON（支持注释 JSONC）与 XML（支持属性注释） |
| **属性别名** | `Alias` 自定义字段名；`JsonAlias` JSON 专用键名 |
| **枚举映射** | `MapType` 按 string/byte/int/long 等任意类型存储枚举 |
| **非侵入式** | 实体类零特性，Fluent 外部映射 |
| **集合/字典/嵌套** | 自动递归展开 |
| **默认值与校验** | 默认值、必填、字符串长度限制 |
| **自定义转换器** | 内置 + 自定义 `IConfigConverter` |
| **热重载** | `FileSystemWatcher` 监听外部修改 |
| **原子保存** | 临时文件 + 覆盖，避免进程中断写坏配置 |
| **损坏兜底** | 仅文件语法损坏才备份重建 |

---

## 目标框架与依赖

| 目标框架 | 依赖 |
|----------|------|
| netstandard2.0 | System.Text.Json 8.0.4 |
| net6.0 | 无（仅 BCL） |
| net8.0 | 无（仅 BCL） |
| net9.0 | 无（仅 BCL） |
| net10.0 | 无（仅 BCL） |

---

## 快速开始

定义配置实体类：

```csharp
using System.Collections.Generic;
using VassasCo.Utility.ConfigHelper;

[JsonConfig("config/app.json")]
public class AppConfig : ConfigBase<AppConfig>
{
    [Config(Desc = "应用名称", Default = "MyApp", Required = true)]
    public string AppName { get; set; } = "";

    [Config(Desc = "端口号", Default = 8080)]
    public int Port { get; set; }

    [Config(Desc = "服务器列表", Alias = "Server")]
    public List<ServerInfo> Servers { get; set; } = new();
}

public class ServerInfo
{
    [Config(Desc = "服务器 IP")]
    public string Ip { get; set; } = "";

    public int Port { get; set; }
}
```

加载 / 保存：

```csharp
// 方式 1：ConfigBase<T> 基类
var cfg = AppConfig.Current;       // 数据实例
cfg.Port = 9090;
AppConfig.Helper?.Save();          // 通过管理器保存

// 方式 2：ConfigFactory 入口
var cfg = ConfigFactory.Load<AppConfig>();
ConfigFactory.Save(cfg);           // 尊重传入实例

// 方式 3：手动创建实例
var helper = new ConfigHelper<AppConfig>("config/app.json", ConfigFormat.Json);
var value = helper.Value;
helper.Save();
```

首次运行若文件不存在，会自动写入带默认值与注释的配置文件。

---

## 侵入式：特性配置

在实体类上贴特性，调用方零配置：

```csharp
public enum LogLevel : byte { Off, Info, Warn, Error }

[JsonConfig("config/app.json")]
public class AppConfig : ConfigBase<AppConfig>
{
    [Config(Desc = "应用名称", Default = "MyApp", Required = true)]
    public string AppName { get; set; } = "";

    [Config(Desc = "端口号", Default = 8080)]
    public int Port { get; set; }

    [Config(MapType = typeof(byte))]   // 存数字（与枚举底层类型一致）
    public LogLevel Level { get; set; }

    [Config(Ignore = true)]
    public string Password { get; set; } = "";
}
```

### 类级特性

| 特性 | 说明 |
|------|------|
| `[JsonConfig("path")]` | 标记为 JSON 配置，指定文件路径 |
| `[XmlConfig("path", RootName = "Settings")]` | 标记为 XML 配置，可指定根元素名（默认取类型名） |

### 属性特性 `[Config]`

| 参数 | 类型 | 说明 |
|------|------|------|
| `Alias` | string | 属性别名（见[别名与命名](#别名与命名)） |
| `JsonAlias` | string | JSON 专用键名，优先级高于 `Alias` |
| `MapType` | Type | 枚举属性的映射类型（`string` / 整数类型） |
| `Desc` | string | 描述，写入注释 |
| `Default` | object | 默认值 |
| `Ignore` | bool | 忽略该属性（不读写） |
| `Required` | bool | 必填校验 |
| `StringLength` | int | 字符串最大长度（仅 `string`） |
| `Converter` | Type | 自定义转换器类型（实现 `IConfigConverter`） |
| `ConverterArgs` | object[] | 转换器构造参数 |

> 已废弃但保留兼容：`Key`（等同 `Alias`），以及旧特性 `[ConfigKey]`、`[ConfigDefault]`、`[ConfigIgnore]`、`[ConfigRequired]`、`[ConfigStringLength]`、`[ConfigConverter]`。新代码请统一使用 `[Config]`。

---

## 非侵入式：Fluent 配置

实体类**不加任何特性**，通过 `ConfigBuilder` 从外部映射。

实体类（零特性）：

```csharp
using System.Collections.Generic;

public class PlainConfig
{
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public string Secret { get; set; } = "";                 // 将被 Ignore
    public string Level { get; set; } = "";                  // 将被 Converter
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, int> Counters { get; set; } = new();
}

// 自定义转换器
public class UpperCaseConverter : IConfigConverter
{
    public string ConvertTo(object? value) => (value as string)?.ToUpperInvariant() ?? "";
    public object? ConvertFrom(string? configValue) => configValue?.ToLowerInvariant();
}
```

映射与构建：

```csharp
using VassasCo.Utility.ConfigHelper;

var helper = ConfigBuilder.For<PlainConfig>("config/plain.json", ConfigFormat.Json)
    .Property(x => x.Name, p => p
        .Alias("app_name")          // 标量：JSON 键名 / XML 元素名
        .Description("应用名称")     // 写注释
        .Default("demo")            // 默认值
        .Required(true)             // 必填
        .StringLength(20))          // 字符串最大长度
    .Property(x => x.Port, p => p
        .Alias("listen_port")
        .Default(8080))
    .Property(x => x.Secret, p => p.Ignore())                 // 忽略
    .Property(x => x.Level, p => p
        .Alias("log_level")
        .Converter(typeof(UpperCaseConverter)))               // 转换器
    .Property(x => x.Tags, p => p
        .Alias("tag")               // 集合：XML 项名
        .JsonAlias("labels"))       // 集合：JSON 键名
    .Property(x => x.Counters, p => p
        .Alias("counter")           // 字典：XML 项名
        .JsonAlias("metrics"))      // 字典：JSON 键名
    .Build();

var cfg = helper.Value;
helper.Save();
```

### PropertyConfigurator 链式方法

| 方法 | 说明 |
|------|------|
| `Alias(string)` | 属性别名（标量作用于键名/元素名，集合作用于 XML 项名） |
| `JsonAlias(string)` | JSON 专用键名 |
| `Description(string)` | 描述，写注释 |
| `Default(object)` | 默认值 |
| `Ignore(bool)` | 忽略该属性 |
| `Required(bool)` | 必填校验 |
| `StringLength(int)` | 字符串最大长度 |
| `Converter(Type, params object?[])` | 自定义转换器 |
| `MapType(Type)` | 枚举映射类型 |

---

## 别名与命名

### `Alias`

| 属性类型 | Alias 作用 |
|----------|-----------|
| 普通标量 / 嵌套对象 | JSON 键名 + XML 元素名 |
| 集合（`List<T>` / 数组） | **XML 列表项元素名**（容器名始终为属性名） |
| 字典 | 与集合相同，作用于 XML 字典项元素名 |

### `JsonAlias`

`JsonAlias` 是 JSON 专用键名，优先级高于 `Alias`，只影响 JSON、不影响 XML：

| 属性类型 | JsonAlias 作用 |
|----------|---------------|
| 普通标量 / 嵌套对象 | 覆盖 JSON 键名（XML 仍用 `Alias` 或属性名） |
| 集合 / 字典 | 覆盖 JSON 键名（`Alias` 仍作用于 XML 项名） |

### 默认回退

- 未设置 `Alias` / `JsonAlias` 的普通属性：JSON 用属性名转 camelCase，XML 用属性名。
- 未设置 `Alias` 的集合属性：XML 项名默认取元素类型友好名（`string`、`int`、`ServerInfo`）。
- 未设置 `JsonAlias` 的集合属性：JSON 键名默认用属性名转 camelCase。

---

## 枚举映射 `MapType`

枚举属性默认以**枚举名（字符串）**存储。通过 `MapType` 可指定映射类型：

```csharp
public enum LogLevel : byte { Off, Info, Warn, Error }

public class AppConfig : ConfigBase<AppConfig>
{
    [Config(MapType = typeof(string))]  // 存 "Info"（默认，可省略）
    public LogLevel A { get; set; }

    [Config(MapType = typeof(byte))]    // 存数字 1（与枚举底层类型一致）
    public LogLevel B { get; set; }

    [Config(MapType = typeof(int))]     // 存数字 1（int 32 位）
    public LogLevel C { get; set; }
}
```

- `MapType = typeof(string)` 或省略：存枚举名，例如 `"Info"`。
- `MapType = typeof(byte)` / `typeof(int)` / `typeof(long)` 等任意整数类型：存对应数字，例如 `1`。
- 仅对枚举属性生效；写数字时按 `MapType` 指定类型转换，读时按该类型解析后 `Enum.ToObject` 转回，`byte` / `ushort` / `long` / `ulong` 等底层类型都不会溢出或丢精度。

Fluent 等价写法：`.Property(x => x.B, p => p.MapType(typeof(byte)))`。

---

## 集合、字典与嵌套对象

### 列表 / 数组

```csharp
[Config(Alias = "Server")]           // XML 项名（JSON 键名由 JsonAlias 控制）
public List<ServerInfo> Servers { get; set; } = new();
```

JSON 输出：

```jsonc
"servers": [
  { "ip": "127.0.0.1", "port": 80 }
]
```

XML 输出（容器名 = 属性名 `Servers`，项名 = `Server`）：

```xml
<Servers>
  <Server>
    <Ip>127.0.0.1</Ip>
    <Port>80</Port>
  </Server>
</Servers>
```

### 字典

```csharp
[Config(Alias = "counter")]          // XML 项名（默认 "Item"）
public Dictionary<string, int> Counters { get; set; } = new();
```

JSON 输出：

```jsonc
"counters": {
  "x": 1,
  "y": 2
}
```

XML 输出（键存为 `key` 属性，值为文本节点）：

```xml
<Counters>
  <counter key="x">1</counter>
  <counter key="y">2</counter>
</Counters>
```

### 嵌套对象

复杂类型属性会递归展开其公共可读写属性，支持任意层级嵌套。

---

## 默认值与校验

### 默认值 `Default`

- 文件不存在时，用默认值生成初始文件。
- 文件存在但缺少某字段时，自动补填默认值（仅填缺失字段）。
- 仅标量类型支持默认值；复杂类型（class / List / Dictionary）不能设置 `Default`。

### 必填校验 `Required`

```csharp
[Config(Required = true)]
public string Token { get; set; } = "";
```

加载时若字段缺失或为 `null` / 空字符串，抛出 `ConfigValidationException`。

### 字符串长度 `StringLength`

```csharp
[Config(StringLength = 50)]
public string Name { get; set; } = "";
```

写入时超长部分被截断；仅适用于 `string` 类型。

### 忽略 `Ignore`

```csharp
[Config(Ignore = true)]
public string Password { get; set; } = "";
```

被忽略的属性不参与读写。

---

## 自定义转换器

### 接口 `IConfigConverter`

```csharp
public interface IConfigConverter
{
    // 实体值 → 配置字符串（写入时）
    string ConvertTo(object? value);

    // 配置字符串 → 实体值（读取时）
    object? ConvertFrom(string? configValue);
}
```

### 内置转换器

| 转换器 | 说明 | 示例 |
|--------|------|------|
| `HexIntConverter` | 十六进制整数 | `255` → `"0xFF"` |
| `TimeSpanStringConverter` | 时间跨度字符串 | `01:30:00` |
| `DateTimeStringConverter` | 日期时间字符串 | `2026-09-07 12:00:00` |
| `BoolFormatConverter("是","否")` | 布尔格式化 | `true` → `"是"`（默认 `"1"/"0"`） |
| `NumberFormatConverter("F2")` | 数字格式化 | `3.14159` → `"3.14"` |
| `VersionConverter` | 版本号 | `1.2.3.4` |
| `TypeNameConverter` | 类型全名 | 存 `AssemblyQualifiedName` |
| `EnumIntConverter` | 枚举整数（**已由 `MapType` 取代**） | `1` |

### 自定义转换器示例

```csharp
public class UpperCaseConverter : IConfigConverter
{
    public string ConvertTo(object? value) => (value as string)?.ToUpperInvariant() ?? "";
    public object? ConvertFrom(string? configValue) => configValue?.ToLowerInvariant();
}

public class AppConfig : ConfigBase<AppConfig>
{
    [Config(Converter = typeof(UpperCaseConverter))]
    public string Level { get; set; } = "";
}
```

带构造参数的转换器：

```csharp
[Config(Converter = typeof(BoolFormatConverter), ConverterArgs = new object[] { "是", "否" })]
public bool Enabled { get; set; }
```

---

## 热重载与事件

### 热重载

```csharp
var helper = ConfigFactory.GetHelper<AppConfig>();
helper?.EnableHotReload();    // 文件被外部修改后自动重载
helper?.DisableHotReload();   // 停用
```

- 通过 `FileSystemWatcher` 监听文件变化。
- 仅当文件时间戳晚于上次写入时才触发，且带 500ms 去抖，忽略 `.tmp` 临时文件。
- 重载时自动跳过内容未变化的情况。

### 变更事件 `ConfigChanged`

```csharp
helper.ConfigChanged += (s, e) =>
{
    // e.ChangeType：Initial / Saved / HotReload / Reloaded
    // e.OldConfig / e.NewConfig：变更前后实例
    Console.WriteLine($"变更类型: {e.ChangeType}");
};
```

### 保存前拦截 `ConfigSaving`

```csharp
helper.ConfigSaving += (s, e) =>
{
    if (!Validate(e.Config)) e.Cancel = true;   // 置 true 取消保存
    else e.Config = modified;                    // 保存前替换实例
};
```

---

## 错误处理与损坏文件兜底

### 损坏文件判定

仅当反序列化抛出以下异常时，才判定为文件损坏：

- `System.Text.Json.JsonException`（JSON 语法错误）
- `System.Xml.XmlException`（XML 语法错误）

损坏时自动把原文件备份为 `xxx.corrupted_yyyyMMdd_HHmmss`，再用默认值重建。

### 其它异常

- `ConfigValidationException`：必填字段缺失、特性配置非法等。
- 类型转换失败等其它异常：**直接抛出，不备份、不覆盖**原文件，避免库自身问题静默销毁用户数据。

### 不抛异常的加载

```csharp
if (helper.TryLoad(out var cfg))
{
    // 成功
}
else
{
    Console.WriteLine(helper.LastError?.Message);
}
```

---

## 安全策略

| 策略 | 说明 |
|------|------|
| **原子保存** | 先写 `.tmp` 临时文件再覆盖目标，避免进程中断写坏配置 |
| **损坏文件兜底** | 仅当文件本身语法损坏（`JsonException`/`XmlException`）才备份为 `.corrupted_时间戳` 并重建 |
| **非损坏异常不覆盖** | 必填缺失、类型转换失败等异常直接抛出，不备份、不覆盖原文件 |
| **热重载防抖** | 忽略 `.tmp` 临时文件、时间戳晚于上次写入才触发、500ms 去抖 |
| **线程安全** | `ReaderWriterLockSlim`（支持递归）保护配置实例读写 |
| **路径解析** | 相对路径基于程序集目录解析，绝对路径原样返回 |

---

## 配置参考

### 类级特性

| 特性 | 说明 |
|------|------|
| `[JsonConfig("path")]` | 标记为 JSON 配置，指定文件路径 |
| `[XmlConfig("path", RootName = "Settings")]` | 标记为 XML 配置，可指定根元素名 |

### `[Config]` 属性参数

| 参数 | 类型 | 说明 |
|------|------|------|
| `Alias` | string | 属性别名 |
| `JsonAlias` | string | JSON 专用键名 |
| `MapType` | Type | 枚举映射类型 |
| `Desc` | string | 描述（写注释） |
| `Default` | object | 默认值 |
| `Ignore` | bool | 忽略该属性 |
| `Required` | bool | 必填校验 |
| `StringLength` | int | 字符串最大长度 |
| `Converter` | Type | 自定义转换器类型 |
| `ConverterArgs` | object[] | 转换器构造参数 |

### 支持的标量类型

| 分类 | 类型 |
|------|------|
| 文本 | `string`、`char` |
| 布尔 | `bool` |
| 整数 | `byte`、`sbyte`、`short`、`ushort`、`int`、`uint`、`long`、`ulong` |
| 浮点 | `float`、`double`、`decimal` |
| 时间/标识 | `DateTime`、`DateTimeOffset`、`TimeSpan`、`Guid` |
| 枚举 | 任意 `enum`（默认存枚举名，可用 `MapType` 改为数字） |

其余类型会被视为**复杂类型**，递归展开其公共可读写属性。

---

## API 速查

### ConfigHelper\<T\>（引擎）

```csharp
public class ConfigHelper<T> : IDisposable where T : class, new()
```

| 成员 | 说明 |
|------|------|
| `Value` | 当前配置实例 |
| `FilePath` | 配置文件绝对路径 |
| `FileExists` | 文件是否存在 |
| `LastError` | 最近一次 `Try*` 失败异常 |
| `Properties` | 顶层属性元数据（只读） |
| `Load()` | 加载（文件不存在则写默认值） |
| `Reload()` | 手动重载 |
| `TryLoad(out T)` / `TryReload(out T)` | 不抛异常的加载 / 重载 |
| `Save()` | 保存当前实例 |
| `SaveCopy(T)` | 保存指定实例，不改变内存 `Value` |
| `ExportToJson()` | 导出为 JSON 字符串（与 Save 一致） |
| `EnableHotReload()` / `DisableHotReload()` | 启用 / 停用热重载 |
| `ConfigChanged` | 配置变更事件 |
| `ConfigSaving` | 保存前拦截事件 |
| `Dispose()` | 释放资源 |

构造：

```csharp
new ConfigHelper<T>(filePath, ConfigFormat.Json, jsonOptions?, autoLoad: true)
```

### ConfigFactory（静态门面）

| 方法 | 说明 |
|------|------|
| `Load<T>()` | 根据类特性自动识别格式并加载（缓存 Helper） |
| `Save<T>(config)` | 保存指定实例（尊重传入实例） |
| `GetHelper<T>()` | 获取已缓存的 `ConfigHelper<T>`（未加载返回 null） |
| `Register<T>(path, format)` | 手动注册并缓存 Helper |
| `Generate<T>()` | 若文件不存在则生成默认文件 |

### ConfigBuilder / ConfigBuilder\<T\>

```csharp
ConfigBuilder.For<T>(filePath, ConfigFormat.Json)
    .UseJsonOptions(options)                 // 可选：自定义 JSON 选项
    .Property(x => x.Prop, p => p.Alias("..."))
    .Build();                                // 返回 ConfigHelper<T>
```

### ConfigBase\<T\>

```csharp
public abstract class ConfigBase<T> where T : ConfigBase<T>, new()
{
    public static T Current => ConfigFactory.Load<T>();          // 数据实例
    public static ConfigHelper<T>? Helper => ConfigFactory.GetHelper<T>();  // 管理器
}
```

---

## 异常类型

| 异常 | 说明 |
|------|------|
| `ConfigValidationException` | 特性配置非法或必填校验失败时抛出 |

```csharp
try
{
    var cfg = ConfigFactory.Load<AppConfig>();
}
catch (ConfigValidationException ex)
{
    Console.WriteLine($"配置校验失败：{ex.Message}");
}
```

---

## 测试

```bash
cd VassasCo.Utility
dotnet test
```

> 当前 ConfigHelper 未包含独立测试项目，随解决方案统一执行测试即可。

---

## 许可证

MIT
