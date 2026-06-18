using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VassasCo.Utility
{
    /// <summary>
    /// 节假日日历辅助类。
    /// 支持从 JSON 文件加载节假日配置，提供 IsHoliday / IsWorkday 查询。
    /// 假日名称完全由用户自定义，不内置任何特定国家或地区的节假日数据。
    /// 
    /// JSON 格式：
    /// {
    ///   "holidays": [{ "date": "2025-01-01", "name": "元旦" }],
    ///   "workdays": ["2025-01-26"]
    /// }
    /// </summary>
    public class HolidayCalendar
    {
        private readonly Dictionary<DateTime, string> _holidays = new();
        private readonly HashSet<DateTime> _workdays = [];

        /// <summary>获取所有假日及其名称</summary>
        public IReadOnlyDictionary<DateTime, string> Holidays => _holidays;

        /// <summary>获取所有调休工作日</summary>
        public IReadOnlyCollection<DateTime> Workdays => _workdays;

        /// <summary>添加一个假日。同一日期重复添加会覆盖之前的名称。</summary>
        public HolidayCalendar AddHoliday(DateTime date, string name)
        {
            _holidays[date.Date] = name;
            return this;
        }

        /// <summary>批量添加假日</summary>
        public HolidayCalendar AddHolidays(IEnumerable<(DateTime Date, string Name)> holidays)
        {
            foreach (var (date, name) in holidays) _holidays[date.Date] = name;
            return this;
        }

        /// <summary>添加一个调休工作日。重复添加自动去重。</summary>
        public HolidayCalendar AddWorkday(DateTime date)
        {
            _workdays.Add(date.Date);
            return this;
        }

        /// <summary>批量添加调休工作日</summary>
        public HolidayCalendar AddWorkdays(IEnumerable<DateTime> dates)
        {
            foreach (var date in dates) _workdays.Add(date.Date);
            return this;
        }

        /// <summary>判断指定日期是否为假日</summary>
        public bool IsHoliday(DateTime date) => _holidays.ContainsKey(date.Date);

        /// <summary>
        /// 判断指定日期是否为工作日。
        /// 优先级：调休日(workdays) → 假日(holidays) → 周末 → 普通工作日。
        /// 同一日期同时存在于 workdays 和 holidays 时，workday 优先（调休规则）。
        /// </summary>
        public bool IsWorkday(DateTime date)
        {
            var day = date.Date;
            if (_workdays.Contains(day)) return true;
            if (_holidays.ContainsKey(day)) return false;
            if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                return false;
            return true;
        }

        /// <summary>获取指定日期的假日名称，非假日返回 null</summary>
        public string? GetHolidayName(DateTime date)
            => _holidays.TryGetValue(date.Date, out var name) ? name : null;

        /// <summary>
        /// 获取所有同时存在于 holidays 和 workdays 中的冲突日期。
        /// 冲突日期在 IsWorkday 判定中会优先被视为工作日。
        /// </summary>
        public IReadOnlyCollection<DateTime> GetConflicts()
        {
            var conflicts = new List<DateTime>();
            foreach (var date in _workdays)
            {
                if (_holidays.ContainsKey(date)) conflicts.Add(date);
            }
            return conflicts;
        }

        /// <summary>生成可直接用于 ScheduleHelper.SetCalendarFilter 的过滤器委托</summary>
        public Func<DateTime, bool> ToWorkdayFilter() => dt => IsWorkday(dt);

        /// <summary>
        /// 从 JSON 文件加载节假日配置。
        /// 抛出 FileNotFoundException / FormatException / ArgumentException 描述具体错误。
        /// </summary>
        public static HolidayCalendar Load(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"节假日配置文件不存在: {filePath}");

            string json;
            try { json = File.ReadAllText(filePath, Encoding.UTF8); }
            catch (Exception ex) { throw new FormatException($"无法读取节假日配置文件: {filePath}", ex); }

            System.Text.Json.JsonDocument doc;
            try { doc = System.Text.Json.JsonDocument.Parse(json); }
            catch (Exception ex)
            {
                throw new FormatException(
                    $"JSON 格式错误，无法解析: {filePath}\n错误: {ex.Message}\n" +
                    "请确保 JSON 结构为: {\"holidays\":[{\"date\":\"2025-01-01\",\"name\":\"元旦\"}], \"workdays\":[\"2025-01-26\"]}",
                    ex);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new ArgumentException($"JSON 顶层必须是对象，当前类型: {root.ValueKind}");

                var calendar = new HolidayCalendar();
                var parseErrors = new List<string>();

                if (root.TryGetProperty("holidays", out var holidaysElement))
                {
                    if (holidaysElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                        throw new ArgumentException("\"holidays\" 字段必须是数组");

                    var index = 0;
                    foreach (var item in holidaysElement.EnumerateArray())
                    {
                        index++;
                        if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                        {
                            parseErrors.Add($"holidays[{index}]: 不是有效对象，已跳过");
                            continue;
                        }

                        if (!item.TryGetProperty("date", out var dateProp) ||
                            dateProp.ValueKind != System.Text.Json.JsonValueKind.String)
                        {
                            parseErrors.Add($"holidays[{index}]: 缺少 \"date\" 字段或格式不正确，已跳过");
                            continue;
                        }

                        var dateStr = dateProp.GetString();
                        if (string.IsNullOrWhiteSpace(dateStr) || !DateTime.TryParse(dateStr, out var date))
                        {
                            parseErrors.Add($"holidays[{index}]: 日期 \"{dateStr}\" 无法解析，已跳过");
                            continue;
                        }

                        var name = string.Empty;
                        if (item.TryGetProperty("name", out var nameProp) &&
                            nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            name = nameProp.GetString() ?? string.Empty;

                        var dateKey = date.Date;
                        if (calendar._holidays.ContainsKey(dateKey))
                            parseErrors.Add(
                                $"holidays[{index}]: 日期 {date:yyyy-MM-dd} 重复，" +
                                $"后添加的名称 \"{name}\" 将覆盖之前的 \"{calendar._holidays[dateKey]}\"");

                        calendar.AddHoliday(date, name);
                    }
                }

                if (root.TryGetProperty("workdays", out var workdaysElement))
                {
                    if (workdaysElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                        throw new ArgumentException("\"workdays\" 字段必须是数组");

                    var index = 0;
                    foreach (var item in workdaysElement.EnumerateArray())
                    {
                        index++;
                        var dateStr = item.GetString();
                        if (string.IsNullOrWhiteSpace(dateStr) || !DateTime.TryParse(dateStr, out var date))
                        {
                            parseErrors.Add($"workdays[{index}]: 日期 \"{dateStr}\" 无法解析，已跳过");
                            continue;
                        }
                        calendar.AddWorkday(date);
                    }
                }

                foreach (var conflictDate in calendar.GetConflicts())
                {
                    var holidayName = calendar._holidays[conflictDate];
                    parseErrors.Add(
                        $"注意: {conflictDate:yyyy-MM-dd} (\"{holidayName}\") " +
                        "同时出现在 holidays 和 workdays 中，将按调休规则视为工作日");
                }

                if (parseErrors.Count > 0)
                {
                    var msg = $"节假日配置文件解析警告（共 {parseErrors.Count} 条）:\n" +
                              string.Join("\n", parseErrors);
                    System.Diagnostics.Debug.WriteLine(msg);
                }

                return calendar;
            }
        }

        /// <summary>安全加载节假日配置，不抛异常</summary>
        /// <param name="filePath">JSON 文件路径</param>
        /// <param name="calendar">成功时返回 HolidayCalendar，失败为 null</param>
        /// <param name="error">失败时的错误描述</param>
        /// <returns>是否加载成功</returns>
        public static bool TryLoad(string filePath, out HolidayCalendar? calendar, out string? error)
        {
            try
            {
                calendar = Load(filePath);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                calendar = null;
                error = ex.Message;
                return false;
            }
        }
    }
}
