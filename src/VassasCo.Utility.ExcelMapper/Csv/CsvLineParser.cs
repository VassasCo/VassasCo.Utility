// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VassasCo.Utility.Internals
{
    /// <summary>
    /// RFC 4180 词法解析：支持引号包裹、引号内双引号转义、引号内换行、自定义分隔符。
    /// 基于 TextReader 逐字符前向读取，不依赖 string.Split（无法正确处理引号内分隔符）。
    /// </summary>
    internal sealed class CsvLineParser
    {
        private readonly TextReader _reader;
        private readonly char _delimiter;
        private int _position; // 已从源读取的字符序号（错误定位用）

        public CsvLineParser(TextReader reader, char delimiter)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _delimiter = delimiter;
        }

        /// <summary>读取下一条逻辑记录；流结束返回 null</summary>
        public List<string>? ReadRecord()
        {
            var fields = new List<string>();
            var builder = new StringBuilder();
            bool hasContent = false;

            while (true)
            {
                int ch = _reader.Read();
                if (ch < 0)
                {
                    if (!hasContent)
                        return null;

                    fields.Add(builder.ToString());
                    return fields;
                }

                _position++;
                hasContent = true;
                char c = (char)ch;

                if (c == '"')
                {
                    if (!ReadQuotedField(builder))
                    {
                        // 引号未闭合到流尾：按已读内容作为字段（宽容策略）
                        fields.Add(builder.ToString());
                        return fields;
                    }
                    ContinueAfterQuote(fields, builder);
                    return fields;
                }

                if (c == _delimiter)
                {
                    fields.Add(builder.ToString());
                    builder.Clear();
                    continue;
                }

                if (c == '\r')
                {
                    int next = _reader.Peek();
                    if (next == '\n')
                        _reader.Read();
                    fields.Add(builder.ToString());
                    return fields;
                }

                if (c == '\n')
                {
                    fields.Add(builder.ToString());
                    return fields;
                }

                builder.Append(c);
            }
        }

        /// <summary>读取引号内字段内容（进入时引号已消费）；返回 false 表示流尾引号未闭合</summary>
        private bool ReadQuotedField(StringBuilder builder)
        {
            while (true)
            {
                int ch = _reader.Read();
                if (ch < 0)
                    return false;

                _position++;
                char c = (char)ch;

                if (c == '"')
                {
                    int next = _reader.Peek();
                    if (next == '"')
                    {
                        _reader.Read(); // 消费第二个引号 → 字面引号
                        _position++;
                        builder.Append('"');
                        continue;
                    }
                    return true;
                }

                builder.Append(c);
            }
        }

        /// <summary>引号字段结束后，必须紧跟分隔符或行结束（容忍尾部空白）</summary>
        private void ContinueAfterQuote(List<string> fields, StringBuilder builder)
        {
            while (true)
            {
                int ch = _reader.Peek();
                if (ch < 0)
                {
                    fields.Add(builder.ToString());
                    return;
                }

                char c = (char)ch;
                if (c == _delimiter)
                {
                    _reader.Read();
                    _position++;
                    fields.Add(builder.ToString());
                    builder.Clear();

                    // 继续读下一个字段
                    ReadNextFieldInto(fields, builder);
                    return;
                }

                if (c == '\r' || c == '\n')
                {
                    _reader.Read();
                    _position++;
                    if (c == '\r' && _reader.Peek() == '\n')
                    {
                        _reader.Read();
                        _position++;
                    }
                    fields.Add(builder.ToString());
                    return;
                }

                // 非标准：引号后出现裸字符（如 a"b），宽容追加
                _reader.Read();
                _position++;
                builder.Append(c);
            }
        }

        private void ReadNextFieldInto(List<string> fields, StringBuilder builder)
        {
            while (true)
            {
                int ch = _reader.Read();
                if (ch < 0)
                {
                    fields.Add(builder.ToString());
                    return;
                }

                _position++;
                char c = (char)ch;

                if (c == '"')
                {
                    if (ReadQuotedField(builder))
                    {
                        ContinueAfterQuote(fields, builder);
                        return;
                    }
                    fields.Add(builder.ToString());
                    return;
                }

                if (c == _delimiter)
                {
                    fields.Add(builder.ToString());
                    builder.Clear();
                    continue;
                }

                if (c == '\r')
                {
                    if (_reader.Peek() == '\n')
                        _reader.Read();
                    fields.Add(builder.ToString());
                    return;
                }

                if (c == '\n')
                {
                    fields.Add(builder.ToString());
                    return;
                }

                builder.Append(c);
            }
        }
    }
}
