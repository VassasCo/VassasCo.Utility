using System;
using System.IO;
using VassasCo.Utility.ConfigHelper;
using Xunit;

namespace VassasCo.Utility.ConfigHelper.Tests
{
    public sealed class ConfigHelperTests : IDisposable
    {
        private readonly string _dir;

        public ConfigHelperTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vassasco-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string FilePath(string name) => Path.Combine(_dir, name);

        [Fact]
        public void Save_And_Load_RoundTrip()
        {
            var file = FilePath("app.json");
            var helper = new ConfigHelper<AppConfig>(file);

            helper.Value.Name = "TestApp";
            helper.Value.Port = 1234;
            helper.Value.RequiredField = "req";
            helper.Value.Servers.Add("a");
            helper.Value.Servers.Add("b");
            helper.Save();

            var reloaded = new ConfigHelper<AppConfig>(file);
            Assert.Equal("TestApp", reloaded.Value.Name);
            Assert.Equal(1234, reloaded.Value.Port);
            Assert.Equal(2, reloaded.Value.Servers.Count);
            Assert.Equal("a", reloaded.Value.Servers[0]);
            Assert.Equal("b", reloaded.Value.Servers[1]);
        }

        [Fact]
        public void Load_CreatesFile_WithDefaults_WhenMissing()
        {
            var file = FilePath("missing.json");
            var helper = new ConfigHelper<AppConfig>(file);

            Assert.True(helper.FileExists);
            Assert.Equal("MyApp", helper.Value.Name);
            Assert.Equal(8080, helper.Value.Port);
        }

        [Fact]
        public void Save_RequiredFieldMissing_Throws()
        {
            var file = FilePath("app.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.Name = "x";

            Assert.Throws<ConfigValidationException>(() => helper.Save());
        }

        [Fact]
        public void Alias_IsUsed_ForScalarProperty()
        {
            var file = FilePath("alias.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.RequiredField = "req";
            helper.Value.Title = "MyTitle";
            helper.Save();

            var json = File.ReadAllText(file);
            Assert.Contains("app_title", json);
            Assert.DoesNotContain("\"title\"", json);
        }

        [Fact]
        public void Converter_IsApplied_OnWriteAndRead()
        {
            var file = FilePath("converter.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.RequiredField = "req";
            helper.Value.Shout = "hello";
            helper.Save();

            var json = File.ReadAllText(file);
            Assert.Contains("HELLO", json);

            var reloaded = new ConfigHelper<AppConfig>(file);
            Assert.Equal("hello", reloaded.Value.Shout);
        }

        [Fact]
        public void Enum_MapType_StoredAsNumber()
        {
            var file = FilePath("enum.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.RequiredField = "req";
            helper.Value.Level = LogLevel.Error;
            helper.Save();

            var json = File.ReadAllText(file);
            Assert.Contains("\"level\": 2", json);

            var reloaded = new ConfigHelper<AppConfig>(file);
            Assert.Equal(LogLevel.Error, reloaded.Value.Level);
        }

        [Fact]
        public void Nested_Object_And_Dictionary_RoundTrip()
        {
            var file = FilePath("nested.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.RequiredField = "req";
            helper.Value.Server = new ServerInfo { Host = "localhost", Port = 3306 };
            helper.Value.Limits["max"] = 100;
            helper.Save();

            var reloaded = new ConfigHelper<AppConfig>(file);
            Assert.NotNull(reloaded.Value.Server);
            Assert.Equal("localhost", reloaded.Value.Server!.Host);
            Assert.Equal(3306, reloaded.Value.Server.Port);
            Assert.Equal(100, reloaded.Value.Limits["max"]);
        }

        [Fact]
        public void Ignore_Property_IsNotSerialized()
        {
            var file = FilePath("ignore.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.RequiredField = "req";
            helper.Value.Ignored = "secret";
            helper.Save();

            var json = File.ReadAllText(file);
            Assert.DoesNotContain("secret", json);
        }

        [Fact]
        public void ConfigChanged_Event_Fires_OnSave()
        {
            var file = FilePath("event.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.RequiredField = "req";

            ConfigChangeType? changeType = null;
            helper.ConfigChanged += (_, e) => changeType = e.ChangeType;

            helper.Save();

            Assert.Equal(ConfigChangeType.Saved, changeType);
        }

        [Fact]
        public void ConfigSaving_Cancel_PreventsWrite()
        {
            var file = FilePath("cancel.json");
            var helper = new ConfigHelper<AppConfig>(file);
            helper.Value.RequiredField = "req";
            helper.Value.Name = "before";
            helper.Save();

            helper.Value.Name = "after";
            helper.ConfigSaving += (_, e) => e.Cancel = true;
            helper.Save();

            var reloaded = new ConfigHelper<AppConfig>(file);
            Assert.Equal("before", reloaded.Value.Name);
        }

        [Fact]
        public void Xml_RoundTrip()
        {
            var file = FilePath("app.xml");
            var helper = new ConfigHelper<XmlAppConfig>(file, ConfigFormat.Xml);
            helper.Value.Name = "XmlName";
            helper.Value.Port = 9999;
            helper.Value.Tags.Add("t1");
            helper.Save();

            var reloaded = new ConfigHelper<XmlAppConfig>(file, ConfigFormat.Xml);
            Assert.Equal("XmlName", reloaded.Value.Name);
            Assert.Equal(9999, reloaded.Value.Port);
            Assert.Single(reloaded.Value.Tags);
            Assert.Equal("t1", reloaded.Value.Tags[0]);
        }

        [Fact]
        public void ConfigBuilder_Fluent_Mapping()
        {
            var file = FilePath("builder.json");
            var helper = ConfigBuilder.For<PlainConfig>(file)
                .Property(x => x.Title, c => c.Alias("title_alias"))
                .Build();

            helper.Value.Title = "hello";
            helper.Value.Count = 5;
            helper.Save();

            var json = File.ReadAllText(file);
            Assert.Contains("title_alias", json);

            var reloaded = ConfigBuilder.For<PlainConfig>(file)
                .Property(x => x.Title, c => c.Alias("title_alias"))
                .Build();
            Assert.Equal("hello", reloaded.Value.Title);
            Assert.Equal(5, reloaded.Value.Count);
        }

        [Fact]
        public void ConfigFactory_Register_And_GetHelper()
        {
            var file = FilePath("factory.json");
            var helper = ConfigFactory.Register<FactoryConfig>(file);

            Assert.NotNull(helper);
            Assert.True(helper.FileExists);
            Assert.Same(helper, ConfigFactory.GetHelper<FactoryConfig>());
        }
    }
}
