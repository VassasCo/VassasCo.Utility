using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Serialization;

namespace VassasCo.Utility
{
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

        private sealed class StringWriterWithEncoding(Encoding encoding) : StringWriter
        {
            public override Encoding Encoding => encoding;
        }
    }

    /// <summary>XML 反序列化兼容辅助 — 为嵌套类生成正确的 XmlAttributeOverrides</summary>
    public static class XmlConfigHelper
    {
        /// <summary>创建带 XmlElement 重写的 XmlSerializer</summary>
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
