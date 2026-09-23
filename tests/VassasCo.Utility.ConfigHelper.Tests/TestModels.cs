using System.Collections.Generic;
using VassasCo.Utility.ConfigHelper;

namespace VassasCo.Utility.ConfigHelper.Tests
{
    public enum LogLevel { Debug = 0, Info = 1, Error = 2 }

    /// <summary>测试用转换器：写入时转大写，读取时转小写。</summary>
    public sealed class UpperConverter : IConfigConverter
    {
        public string ConvertTo(object? value) => value?.ToString()?.ToUpperInvariant() ?? string.Empty;
        public object? ConvertFrom(string? configValue) => configValue?.ToLowerInvariant();
    }

    public sealed class ServerInfo
    {
        public string? Host { get; set; }
        public int Port { get; set; }
    }

    [JsonConfig("app.json")]
    public sealed class AppConfig
    {
        [Config(Desc = "应用名称", Default = "MyApp")]
        public string Name { get; set; } = string.Empty;

        [Config(Alias = "app_title")]
        public string Title { get; set; } = string.Empty;

        [Config(Default = 8080)]
        public int Port { get; set; }

        [Config(Required = true)]
        public string? RequiredField { get; set; }

        [Config(Converter = typeof(UpperConverter))]
        public string? Shout { get; set; }

        [Config(MapType = typeof(int))]
        public LogLevel Level { get; set; }

        public List<string> Servers { get; set; } = new();

        public Dictionary<string, int> Limits { get; set; } = new();

        [Config(Ignore = true)]
        public string? Ignored { get; set; }

        public ServerInfo? Server { get; set; }
    }

    [XmlConfig("app.xml", RootName = "AppConfig")]
    public sealed class XmlAppConfig
    {
        [Config(Default = "XmlApp")]
        public string Name { get; set; } = string.Empty;

        public int Port { get; set; }

        public List<string> Tags { get; set; } = new();
    }

    /// <summary>无任何特性的普通类，用于 Fluent 映射测试。</summary>
    public sealed class PlainConfig
    {
        public string Title { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>专用于 ConfigFactory 测试的独立类型。</summary>
    public sealed class FactoryConfig
    {
        public string Name { get; set; } = string.Empty;
    }
}
