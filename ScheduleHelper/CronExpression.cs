using System;
using System.Collections.Generic;
using System.Linq;

namespace VassasCo.Utility
{
    /// <summary>
    /// CRON 表达式解析器。支持 5 段格式：分 时 日 月 周。特殊字符：* , - / ? L
    /// </summary>
    public class CronExpression
    {
        private readonly int[] _minutes;
        private readonly int[] _hours;
        private readonly int[] _daysOfMonth;
        private readonly int[] _months;
        private readonly int[] _daysOfWeek;

        private const int Any = -1;
        private const int LastDay = 99;
        private const int LastWeekday = 98;

        /// <summary>解析 CRON 表达式。示例: "0 9 * * 1-5" (工作日早9点)</summary>
        public CronExpression(string expression)
        {
            var parts = (expression ?? throw new ArgumentNullException(nameof(expression)))
                .Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 5)
                throw new ArgumentException($"CRON 表达式必须包含 5 个字段，当前: {parts.Length}", nameof(expression));

            _minutes = ParseField(parts[0], 0, 59);
            _hours = ParseField(parts[1], 0, 23);
            _daysOfMonth = ParseField(parts[2], 1, 31);
            _months = ParseField(parts[3], 1, 12);
            _daysOfWeek = ParseField(parts[4], 0, 7);
        }

        /// <summary>判断指定时间是否匹配表达式</summary>
        public bool Matches(DateTime time)
        {
            if (!_months.Contains(Any) && !_months.Contains(time.Month)) return false;

            var dow = (int)time.DayOfWeek;
            var hasDomConstraint = !_daysOfMonth.Contains(Any);
            var hasDowConstraint = !_daysOfWeek.Contains(Any);

            if (hasDomConstraint && hasDowConstraint)
            {
                if (!MatchesDayOfMonth(time) && !_daysOfWeek.Contains(dow)) return false;
            }
            else if (hasDomConstraint)
            {
                if (!MatchesDayOfMonth(time)) return false;
            }
            else if (hasDowConstraint)
            {
                if (!_daysOfWeek.Contains(dow)) return false;
            }

            if (!_hours.Contains(Any) && !_hours.Contains(time.Hour)) return false;
            if (!_minutes.Contains(Any) && !_minutes.Contains(time.Minute)) return false;

            return true;
        }

        /// <summary>获取下一个匹配时间（距离 now 最近的未来时间）</summary>
        public DateTime GetNext(DateTime now)
        {
            var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
            var limit = now.AddYears(10);

            while (next <= limit)
            {
                if (Matches(next)) return next;
                next = next.AddMinutes(1);
            }

            throw new InvalidOperationException($"CRON 表达式在未来 10 年内无匹配: {this}");
        }

        private bool MatchesDayOfMonth(DateTime time)
        {
            if (_daysOfMonth.Contains(LastDay)) return time.Day == DateTime.DaysInMonth(time.Year, time.Month);
            if (_daysOfMonth.Contains(LastWeekday)) return time.Day == LastWeekdayOfMonth(time.Year, time.Month);
            return _daysOfMonth.Contains(time.Day);
        }

        private static int LastWeekdayOfMonth(int year, int month)
        {
            var lastDay = DateTime.DaysInMonth(year, month);
            var date = new DateTime(year, month, lastDay);
            while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                date = date.AddDays(-1);
            return date.Day;
        }

        private static int[] ParseField(string field, int min, int max)
        {
            if (field == "*" || field == "?") return new[] { Any };

            var values = new HashSet<int>();

            foreach (var part in field.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed == "L") { values.Add(LastDay); continue; }
                if (trimmed == "LW") { values.Add(LastWeekday); continue; }

                int stepIndex = trimmed.IndexOf('/');
                int step = 1;
                if (stepIndex > 0)
                {
                    step = int.Parse(trimmed.Substring(stepIndex + 1));
                    trimmed = trimmed.Substring(0, stepIndex);
                }

                int rangeIndex = trimmed.IndexOf('-');
                if (rangeIndex > 0)
                {
                    var start = int.Parse(trimmed.Substring(0, rangeIndex));
                    var end = int.Parse(trimmed.Substring(rangeIndex + 1));
                    for (var v = start; v <= end; v += step)
                        if (v >= min && v <= max) values.Add(v);
                }
                else
                {
                    var v = int.Parse(trimmed);
                    if (v >= min && v <= max) values.Add(v);
                    else if (v == 7) values.Add(0);
                }
            }

            return values.Count > 0 ? values.ToArray() : new[] { Any };
        }

        /// <inheritdoc />
        public override string ToString()
            => $"{string.Join(",", _minutes)} {string.Join(",", _hours)} " +
               $"{string.Join(",", _daysOfMonth)} {string.Join(",", _months)} {string.Join(",", _daysOfWeek)}";
    }
}
