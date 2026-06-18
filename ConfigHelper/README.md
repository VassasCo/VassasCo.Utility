# ConfigHelper

零代码实体类与 JSON/XML 配置文件双向映射工具。

## 文件说明

| 文件 | 内容 |
|------|------|
| `ConfigAttributes.cs` | 特性定义：[JsonConfig]、[XmlConfig]、[Config]、[ConfigIgnore] 等 |
| `ConfigCore.cs` | 核心逻辑：ConfigHelper<T>、ConfigFactory、ConfigBase<T>、ConfigRawReader、JsonCommentWriter、XmlCommentWriter、XmlConfigHelper |
| `ConfigConverters.cs` | 内置转换器：HexInt、TimeSpan、DateTime、BoolFormat 等 |

## 快速开始

### 1. 定义配置实体类

```csharp
[JsonConfig("config/app.json")]
public class AppConfig : ConfigBase<AppConfig>
{
    [Config(Desc = "应用名称", Default = "MyApp", Required = true)]
    public string AppName { get; set; } = "";

    [Config(Desc = "端口号", Default = 8080)]
    public int Port { get; set; }

    [Config(Desc = "服务器列表")]
    public List<ServerInfo> Servers { get; set; } = new();
}

public class ServerInfo
{
    [Config(Desc = "服务器IP")]
    public string Ip { get; set; } = "";
    public int Port { get; set; }
}
```

### 2. 加载/保存

```csharp
// 方式 1：ConfigFactory 入口
var cfg = ConfigFactory.Load<AppConfig>();
ConfigFactory.Save(cfg);

// 方式 2：ConfigBase<T> 基类
var cfg = AppConfig.Current;
```

### 3. 手动创建实例

```csharp
var helper = new ConfigHelper<MyConfig>("config/my.json", ConfigFormat.Json);
var cfg = helper.Value;
helper.Save();
```

### 4. 热重载

```csharp
var helper = ConfigFactory.GetHelper<AppConfig>();
helper.EnableHotReload();  // 配置文件外部修改后自动重载
```

### 5. 事件监听

```csharp
helper.ConfigChanged += (s, e) =>
{
    Console.WriteLine($"配置变更: {e.ChangeType}");
};
helper.ConfigSaving += (s, e) =>
{
    if (!Validate(e.Config)) e.Cancel = true; // 可取消保存
};
```

## 特性说明

### 类级特性

| 特性 | 说明 |
|------|------|
| `[JsonConfig("path")]` | 标记为 JSON 配置 |
| `[XmlConfig("path")]` | 标记为 XML 配置 |

### 属性特性

| 参数 | 类型 | 说明 |
|------|------|------|
| `Desc` | string | 属性描述，写入注释 |
| `Key` | string | 自定义 JSON/XML 键名 |
| `Default` | object | 默认值 |
| `Ignore` | bool | 忽略此属性 |
| `Required` | bool | 必填校验 |
| `StringLength` | int | 字符串最大长度 |
| `Converter` | Type | 自定义转换器 |

### 内置转换器

- `HexIntConverter` - 十六进制
- `TimeSpanStringConverter` - 时间跨度
- `DateTimeStringConverter` - 日期时间
- `BoolFormatConverter("是","否")` - 布尔格式化
- `NumberFormatConverter("F2")` - 数字格式化
- `VersionConverter` - 版本号
- `TypeNameConverter` - 类型名
- `EnumIntConverter` - 枚举存数字

## XML 配置

```csharp
[XmlConfig("config/settings.xml", RootName = "Settings")]
public class XmlSettings : ConfigBase<XmlSettings>
{
    [Config(Desc = "服务名", Key = "ServiceName")]
    public string Name { get; set; } = "";
}
```

## ConfigRawReader — 无需实体类直接读值

```csharp
var ip = ConfigRawReader.GetJsonValue("config.json", "device.ip");
var name = ConfigRawReader.GetXmlValue("config.xml", "/AppSettings/ServiceName");
```

路径支持：`"servers[0].name"`、`"nested.deep.key"`
