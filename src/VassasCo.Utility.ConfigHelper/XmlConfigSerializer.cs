using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace VassasCo.Utility.ConfigHelper
{
    /// <summary>
    /// XML 自定义读写器（支持注释）。写与读共用同一套 <see cref="PropMeta"/>，严格对称。
    /// </summary>
    internal static class XmlConfigSerializer
    {
        public static string Serialize(object? instance, Type type, ConfigMetadataProvider metadata, string? rootName)
        {
            var root = !string.IsNullOrEmpty(rootName) ? rootName! : type.Name;

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
            writer.WriteStartElement(root);

            var metas = metadata.GetMetas(type);
            WriteObject(writer, instance, metas, metadata);

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();

            return sw.ToString();
        }

        public static object? Deserialize(string xml, Type type, ConfigMetadataProvider metadata, HashSet<string>? present)
        {
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Ignore
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element)
                return Activator.CreateInstance(type);
            return ReadObject(reader, type, metadata, present);
        }

        // ── 写 ──

        private static void WriteObject(XmlWriter writer, object? instance, List<PropMeta> metas, ConfigMetadataProvider metadata)
        {
            if (instance == null) return;

            foreach (var meta in metas)
            {
                var value = meta.Property.GetValue(instance);
                WriteComments(writer, meta, value);
                writer.WriteStartElement(meta.XmlElementName);
                WritePropertyValue(writer, value, meta, metadata);
                writer.WriteEndElement();
            }
        }

        private static void WriteComments(XmlWriter writer, PropMeta meta, object? value)
        {
            var comments = new List<string>();
            if (!string.IsNullOrEmpty(meta.Description)) comments.Add(meta.Description!);
            if (meta.HasConfigDefault && meta.ConfigDefaultValue != null) comments.Add("默认值: " + meta.ConfigDefaultValue);
            if (meta.MaxStringLength.HasValue) comments.Add("最大长度: " + meta.MaxStringLength);
            if (value is IList list && !(value is string)) comments.Add("列表项数: " + list.Count);
            foreach (var c in comments) writer.WriteComment(" " + c + " ");
        }

        private static void WritePropertyValue(XmlWriter writer, object? value, PropMeta meta, ConfigMetadataProvider metadata)
        {
            if (value == null) { writer.WriteString(""); return; }

            var converter = meta.GetConverter();
            if (converter != null) { writer.WriteString(converter.ConvertTo(value)); return; }

            var type = value.GetType();

            if (value is string s)
            {
                if (meta.MaxStringLength.HasValue && s.Length > meta.MaxStringLength.Value)
                    s = s.Substring(0, meta.MaxStringLength.Value);
                writer.WriteString(s);
            }
            else if (type.IsEnum)
            {
                writer.WriteString(ConfigValueCodec.SerializeEnum(value, meta.MapType));
            }
            else if (ConfigValueCodec.IsScalarType(type))
            {
                writer.WriteString(ConfigValueCodec.SerializeScalar(value));
            }
            else if (value is IDictionary dict)
            {
                WriteDictionary(writer, dict, meta, metadata);
            }
            else if (value is IEnumerable en)
            {
                WriteList(writer, en, meta, metadata);
            }
            else
            {
                var nested = metadata.GetMetas(type);
                if (nested.Count > 0) WriteObject(writer, value, nested, metadata);
                else writer.WriteString(value.ToString() ?? "");
            }
        }

        private static void WriteList(XmlWriter writer, IEnumerable items, PropMeta meta, ConfigMetadataProvider metadata)
        {
            var itemType = meta.ItemType;
            foreach (var item in items)
            {
                writer.WriteStartElement(meta.XmlItemName);
                if (item == null) writer.WriteString("");
                else WriteItem(writer, item, itemType, metadata, meta.MapType);
                writer.WriteEndElement();
            }
        }

        private static void WriteItem(XmlWriter writer, object item, Type? itemType, ConfigMetadataProvider metadata, Type? enumMapType = null)
        {
            var type = item.GetType();
            if (type.IsEnum)
            {
                writer.WriteString(ConfigValueCodec.SerializeEnum(item, enumMapType));
                return;
            }
            if (ConfigValueCodec.IsScalarType(type))
            {
                writer.WriteString(ConfigValueCodec.SerializeScalar(item));
                return;
            }

            if (itemType != null)
            {
                var nested = metadata.GetMetas(itemType);
                if (nested.Count > 0) { WriteObject(writer, item, nested, metadata); return; }
            }

            writer.WriteString(item.ToString() ?? "");
        }

        private static void WriteDictionary(XmlWriter writer, IDictionary dict, PropMeta meta, ConfigMetadataProvider metadata)
        {
            foreach (DictionaryEntry entry in dict)
            {
                writer.WriteStartElement(meta.XmlItemName);
                writer.WriteAttributeString("key", entry.Key?.ToString() ?? "");
                if (entry.Value == null) writer.WriteString("");
                else WriteItem(writer, entry.Value, GetDictionaryValueType(meta), metadata, meta.MapType);
                writer.WriteEndElement();
            }
        }

        private static Type? GetDictionaryValueType(PropMeta meta)
        {
            if (meta.Property != null && ConfigTypeUtil.TryGetDictionaryTypes(meta.Property.PropertyType, out _, out var vt))
                return vt;
            return null;
        }

        // ── 读 ──

        private static object ReadObject(XmlReader reader, Type type, ConfigMetadataProvider metadata, HashSet<string>? present)
        {
            var instance = Activator.CreateInstance(type)!;
            var metas = metadata.GetMetas(type);

            bool isEmpty = reader.IsEmptyElement;
            reader.ReadStartElement();
            if (isEmpty) return instance;

            while (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.Name;
                var meta = FindMeta(metas, name);
                if (meta == null) { reader.Skip(); continue; }

                present?.Add(ConfigTypeUtil.PresentKey(type, meta.Property.Name));

                if (meta.IsCollection)
                {
                    var value = ReadCollection(reader, meta, metadata);
                    if (meta.CanWrite) meta.Property.SetValue(instance, value);
                }
                else if (meta.CanWrite)
                {
                    var value = ReadValue(reader, meta.Property.PropertyType, meta, metadata);
                    meta.Property.SetValue(instance, ConfigValueCodec.NormalizeForType(value, meta.Property.PropertyType));
                }
                else
                {
                    reader.Skip();
                }
            }

            reader.ReadEndElement();
            return instance;
        }

        private static object? ReadValue(XmlReader reader, Type targetType, PropMeta? meta, ConfigMetadataProvider metadata)
        {
            var converter = meta?.GetConverter();
            if (converter != null)
                return converter.ConvertFrom(ReadElementText(reader));

            if (reader.IsEmptyElement)
            {
                reader.ReadStartElement();
                return ConfigValueCodec.NormalizeForType(null, targetType);
            }

            targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (targetType.IsEnum)
            {
                var text = ReadElementText(reader);
                return ConfigValueCodec.TryDeserializeEnum(text, targetType, meta?.MapType, out var enumVal)
                    ? enumVal : Activator.CreateInstance(targetType);
            }

            if (ConfigValueCodec.IsScalarType(targetType))
                return ConfigValueCodec.DeserializeScalar(ReadElementText(reader), targetType);

            return ReadObject(reader, targetType, metadata, null);
        }

        private static object ReadCollection(XmlReader reader, PropMeta meta, ConfigMetadataProvider metadata)
        {
            if (meta.IsDictionary)
            {
                ConfigTypeUtil.TryGetDictionaryTypes(meta.Property.PropertyType, out var keyType, out var valueType);
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;

                bool empty = reader.IsEmptyElement;
                reader.ReadStartElement();
                if (!empty)
                {
                    while (reader.NodeType == XmlNodeType.Element)
                    {
                        var keyText = reader.GetAttribute("key") ?? "";
                        var key = ConfigValueCodec.DeserializeScalar(keyText, keyType);
                        var val = ReadValue(reader, valueType, null, metadata);
                        if (key != null) dict[key] = val;
                    }
                    reader.ReadEndElement();
                }
                return dict;
            }
            else
            {
                var itemType = meta.ItemType ?? typeof(object);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;

                bool empty = reader.IsEmptyElement;
                reader.ReadStartElement();
                if (!empty)
                {
                    while (reader.NodeType == XmlNodeType.Element)
                    {
                        var item = ReadValue(reader, itemType, null, metadata);
                        list.Add(item);
                    }
                    reader.ReadEndElement();
                }
                return ConfigTypeUtil.ConvertList(list, meta.Property.PropertyType, itemType);
            }
        }

        private static string ReadElementText(XmlReader reader)
        {
            reader.ReadStartElement();
            var text = reader.ReadContentAsString();
            reader.ReadEndElement();
            return text;
        }

        private static PropMeta? FindMeta(List<PropMeta> metas, string elementName)
        {
            foreach (var m in metas)
                if (string.Equals(m.XmlElementName, elementName, StringComparison.Ordinal)) return m;
            foreach (var m in metas)
                if (string.Equals(m.XmlElementName, elementName, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        private sealed class StringWriterWithEncoding : StringWriter
        {
            private readonly Encoding _encoding;
            public StringWriterWithEncoding(Encoding encoding) { _encoding = encoding; }
            public override Encoding Encoding => _encoding;
        }
    }
}
