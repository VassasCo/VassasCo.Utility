namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>
    /// 配置实体类基类。继承后可通过 T.Current 获取配置实例。
    /// 子类必须标记 [JsonConfig] 或 [XmlConfig] 特性。
    /// </summary>
    public abstract class ConfigBase<T> where T : ConfigBase<T>, new()
    {
        public static T Current => ConfigFactory.Load<T>();

        public static ConfigHelper<T>? Helper
        {
            get
            {
                // 先确保已加载，避免未访问 Current 时 Helper 为 null 导致 ?.Save() 静默失败。
                ConfigFactory.Load<T>();
                return ConfigFactory.GetHelper<T>();
            }
        }
    }
}
