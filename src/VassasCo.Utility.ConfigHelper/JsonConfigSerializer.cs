using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>
    /// JSON 自定义读写器（JSONC：支持注释）。写与读共用同一套 <see cref="PropMeta"/>，严格对称。
    /// </summary>
    internal static class JsonConfigSerializer
    {
        public static string Serialize(object? instance, Type type, ConfigMetadataProvider metadata)
        {
            var sb = new StringBuilder();
            var metas = metadata.GetMetas(type);
            WriteObject(sb, instance, metas, 0, metadata);
            return sb.ToString();
        }

        public static object? Deserialize(string json, Type type, ConfigMetadataProvider metadata, HashSet<string>? present)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            return ReadObject(doc.RootElement, type, metadata, present);
        }

        // ── 写 ──

        private static void WriteObject(StringBuilder sb, object? instance, List<PropMeta> metas, int depth, ConfigMetadataProvider metadata)
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
                sb.Append('"').Append(Escape(meta.JsonKey)).Append("\": ");
                WritePropertyValue(sb, value, meta, depth + 1, metadata);
                if (i < metas.Count - 1) sb.Append(',');
                sb.AppendLine();
            }

            sb.Append(new string(' ', depth * 2)).Append('}');
        }

        private static void WriteComments(StringBuilder sb, PropMeta meta, object? value, string indent)
        {
            var comments = new List<string>();
            if (!string.IsNullOrEmpty(meta.Description)) comments.Add(meta.Description!);
            if (meta.HasConfigDefault && meta.ConfigDefaultValue != null) comments.Add("[默认值: " + meta.ConfigDefaultValue + "]");
            if (meta.MaxStringLength.HasValue) comments.Add("[最大长度: " + meta.MaxStringLength + "]");
            if (value is IList list && !(value is string)) comments.Add("[列表项数: " + list.Count + "]");

            foreach (var c in comments) sb.Append(indent).Append("// ").AppendLine(c);
        }

        private static void WritePropertyValue(StringBuilder sb, object? value, PropMeta meta, int depth, ConfigMetadataProvider metadata)
        {
            if (value == null) { sb.Append("null"); return; }

            var converter = meta.GetConverter();
            if (converter != null) { sb.Append('"').Append(Escape(converter.ConvertTo(value))).Append('"'); return; }

            if (value is string s && meta.MaxStringLength.HasValue && s.Length > meta.MaxStringLength.Value)
                value = s.Substring(0, meta.MaxStringLength.Value);

            WriteCore(sb, value, depth, metadata, meta.MapType);
        }

        private static void WriteCore(StringBuilder sb, object? value, int depth, ConfigMetadataProvider metadata, Type? enumMapType = null)
        {
            if (value == null) { sb.Append("null"); return; }
            var type = value.GetType();

            if (value is string s) sb.Append('"').Append(Escape(s)).Append('"');
            else if (value is char c) sb.Append('"').Append(Escape(c.ToString())).Append('"');
            else if (value is bool b) sb.Append(b ? "true" : "false");
            else if (ConfigValueCodec.IsIntegral(type)) sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
            else if (value is float f) sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
            else if (value is double d) sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            else if (value is decimal m) sb.Append(m.ToString(CultureInfo.InvariantCulture));
            else if (type.IsEnum)
            {
                var enumText = ConfigValueCodec.SerializeEnum(value, enumMapType);
                if (enumMapType != null && enumMapType != typeof(string))
                    sb.Append(enumText);
                else
                    sb.Append('"').Append(Escape(enumText)).Append('"');
            }
            else if (value is DateTime dt) sb.Append('"').Append(dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('"');
            else if (value is DateTimeOffset dto) sb.Append('"').Append(dto.ToString("O", CultureInfo.InvariantCulture)).Append('"');
            else if (value is Guid) sb.Append('"').Append(value.ToString()).Append('"');
            else if (value is TimeSpan ts) sb.Append('"').Append(ts.ToString()).Append('"');
            else if (value is IDictionary dict) WriteDictionary(sb, dict, depth, metadata, enumMapType);
            else if (value is IEnumerable en) WriteArray(sb, en, depth, metadata, enumMapType);
            else
            {
                var nested = metadata.GetMetas(type);
                if (nested.Count > 0) WriteObject(sb, value, nested, depth, metadata);
                else sb.Append('"').Append(Escape(value.ToString() ?? "")).Append('"');
            }
        }

        private static void WriteArray(StringBuilder sb, IEnumerable items, int depth, ConfigMetadataProvider metadata, Type? enumMapType = null)
        {
            var list = new List<object?>();
            foreach (var it in items) list.Add(it);

            if (list.Count == 0) { sb.Append("[]"); return; }

            sb.AppendLine("[");
            var indent = new string(' ', (depth + 1) * 2);
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append(indent);
                WriteCore(sb, list[i], depth + 1, metadata, enumMapType);
                if (i < list.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append(new string(' ', depth * 2)).Append(']');
        }

        private static void WriteDictionary(StringBuilder sb, IDictionary dict, int depth, ConfigMetadataProvider metadata, Type? enumMapType = null)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }

            sb.AppendLine("{");
            var indent = new string(' ', (depth + 1) * 2);
            var keys = new ArrayList(dict.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i]?.ToString() ?? "null";
                var val = dict[keys[i]!];
                sb.Append(indent).Append('"').Append(Escape(key)).Append("\": ");
                WriteCore(sb, val, depth + 1, metadata, enumMapType);
                if (i < keys.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append(new string(' ', depth * 2)).Append('}');
        }

        // ── 读 ──

        private static object? ReadObject(JsonElement el, Type type, ConfigMetadataProvider metadata, HashSet<string>? present)
        {
            var instance = Activator.CreateInstance(type)!;
            var metas = metadata.GetMetas(type);

            if (el.ValueKind != JsonValueKind.Object) return instance;

            foreach (var meta in metas)
            {
                if (!TryGetProperty(el, meta.JsonKey, out var propEl)) continue;

                present?.Add(ConfigTypeUtil.PresentKey(type, meta.Property.Name));
                if (!meta.CanWrite) continue;

                var value = ReadValue(propEl, meta.Property.PropertyType, meta, metadata);
                meta.Property.SetValue(instance, ConfigValueCodec.NormalizeForType(value, meta.Property.PropertyType));
            }

            return instance;
        }

        private static object? ReadValue(JsonElement el, Type targetType, PropMeta? meta, ConfigMetadataProvider metadata)
        {
            var converter = meta?.GetConverter();
            if (converter != null)
                return converter.ConvertFrom(el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText());

            if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
                return null;

            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (targetType == typeof(string)) return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            if (targetType == typeof(char)) { var s = el.GetString(); return string.IsNullOrEmpty(s) ? '\0' : s[0]; }
            if (targetType == typeof(bool)) return el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False ? el.GetBoolean() : ConfigValueCodec.DeserializeScalar(el.GetString(), typeof(bool));

            if (targetType == typeof(int)) return el.TryGetInt32(out var i) ? i : (int)(ConfigValueCodec.DeserializeScalar(el.GetString(), typeof(int)) ?? 0);
            if (targetType == typeof(long)) return el.TryGetInt64(out var l) ? l : (long)(ConfigValueCodec.DeserializeScalar(el.GetString(), typeof(long)) ?? 0L);
            if (targetType == typeof(short)) return el.TryGetInt32(out var sh) ? (short)sh : (short)(ConfigValueCodec.DeserializeScalar(el.GetString(), typeof(short)) ?? 0);
            if (targetType == typeof(byte)) return el.TryGetByte(out var by) ? by : (byte)0;
            if (targetType == typeof(sbyte)) return el.TryGetSByte(out var sb) ? sb : (sbyte)0;
            if (targetType == typeof(ushort)) return el.TryGetUInt16(out var us) ? us : (ushort)0;
            if (targetType == typeof(uint)) return el.TryGetUInt32(out var ui) ? ui : 0U;
            if (targetType == typeof(ulong)) return el.TryGetUInt64(out var ul) ? ul : 0UL;
            if (targetType == typeof(float)) return el.TryGetSingle(out var f) ? f : 0f;
            if (targetType == typeof(double)) return el.TryGetDouble(out var d) ? d : 0d;
            if (targetType == typeof(decimal)) return el.TryGetDecimal(out var m) ? m : 0m;

            if (targetType.IsEnum)
            {
                var mapType = meta?.MapType;
                if (el.ValueKind == JsonValueKind.Number)
                    return ConfigValueCodec.TryDeserializeEnum(el.GetRawText(), targetType, mapType, out var enumNum)
                        ? enumNum : Activator.CreateInstance(targetType);
                var name = el.GetString();
                if (name == null) return Activator.CreateInstance(targetType);
                return ConfigValueCodec.TryDeserializeEnum(name, targetType, mapType, out var enumVal)
                    ? enumVal : Activator.CreateInstance(targetType);
            }

            if (targetType == typeof(DateTime) || targetType == typeof(DateTimeOffset)
                || targetType == typeof(Guid) || targetType == typeof(TimeSpan))
                return ConfigValueCodec.DeserializeScalar(el.GetString(), targetType);

            if (typeof(IDictionary).IsAssignableFrom(targetType))
                return ReadDictionary(el, targetType, metadata);

            if (ConfigTypeUtil.IsCollectionType(targetType, out _, out _))
                return ReadArray(el, targetType, metadata);

            return ReadObject(el, targetType, metadata, null);
        }

        private static object ReadArray(JsonElement el, Type collectionType, ConfigMetadataProvider metadata)
        {
            var itemType = ConfigTypeUtil.GetElementType(collectionType) ?? typeof(object);
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;

            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    list.Add(ReadValue(item, itemType, null, metadata));
            }

            return ConfigTypeUtil.ConvertList(list, collectionType, itemType);
        }

        private static object ReadDictionary(JsonElement el, Type dictType, ConfigMetadataProvider metadata)
        {
            ConfigTypeUtil.TryGetDictionaryTypes(dictType, out var keyType, out var valueType);
            var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;

            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in el.EnumerateObject())
                {
                    var key = ConfigValueCodec.DeserializeScalar(p.Name, keyType);
                    var val = ReadValue(p.Value, valueType, null, metadata);
                    if (key != null) dict[key] = val;
                }
            }

            return dict;
        }

        private static bool TryGetProperty(JsonElement el, string name, out JsonElement value)
        {
            if (el.TryGetProperty(name, out value)) return true;
            foreach (var p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
