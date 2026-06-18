using System;

namespace VassasCo.Utility
{
    /// <summary>
    /// CRON 表达式建造者 — 流式 API 和预设快捷方式。
    /// 示例：
    ///   CronBuilder.EveryDayAt(9, 0)        → "0 9 * * *"
    ///   CronBuilder.EveryWeekdayAt(10, 0)   → "0 10 * * 1-5"
    ///   CronBuilder.Create().AtMinute(0).AtHour(8,12,16).EveryDay().Build() → "0 8,12,16 * * *"
    /// </summary>
    public static class CronBuilder
    {
        /// <summary>创建流式建造器</summary>
        public static CronFluentBuilder Create() => new CronFluentBuilder();

        public static string EveryMinute() => "* * * * *";
        public static string EveryMinutes(int minutes) => $"*/{minutes} * * * *";
        public static string Every5Minutes() => "*/5 * * * *";
        public static string Every10Minutes() => "*/10 * * * *";
        public static string Every15Minutes() => "*/15 * * * *";
        public static string Every30Minutes() => "*/30 * * * *";
        public static string EveryHour() => "0 * * * *";
        public static string EveryHours(int hours) => $"0 */{hours} * * *";
        public static string EveryDayAt(int hour, int minute = 0) => $"{minute} {hour} * * *";
        public static string EveryMondayAt(int hour, int minute = 0) => $"{minute} {hour} * * 1";
        public static string EveryTuesdayAt(int hour, int minute = 0) => $"{minute} {hour} * * 2";
        public static string EveryWednesdayAt(int hour, int minute = 0) => $"{minute} {hour} * * 3";
        public static string EveryThursdayAt(int hour, int minute = 0) => $"{minute} {hour} * * 4";
        public static string EveryFridayAt(int hour, int minute = 0) => $"{minute} {hour} * * 5";
        public static string EverySaturdayAt(int hour, int minute = 0) => $"{minute} {hour} * * 6";
        public static string EverySundayAt(int hour, int minute = 0) => $"{minute} {hour} * * 0";
        public static string EveryWeekdayAt(int hour, int minute = 0) => $"{minute} {hour} * * 1-5";
        public static string EveryWeekendAt(int hour, int minute = 0) => $"{minute} {hour} * * 6,0";
        public static string Monthly(int day, int hour, int minute = 0) => $"{minute} {hour} {day} * *";
        public static string LastDayOfMonth(int hour, int minute = 0) => $"{minute} {hour} L * ?";
        public static string Yearly(int month, int day, int hour, int minute = 0)
            => $"{minute} {hour} {day} {month} *";

        /// <summary>流式 CRON 表达式建造器</summary>
        public class CronFluentBuilder
        {
            private string _minute = "*";
            private string _hour = "*";
            private string _dayOfMonth = "*";
            private string _month = "*";
            private string _dayOfWeek = "*";

            public CronFluentBuilder AtMinute(params int[] minutes) { _minute = JoinValues(minutes); return this; }
            public CronFluentBuilder EveryMinutes(int minutes) { _minute = $"*/{minutes}"; return this; }
            public CronFluentBuilder AtMinuteRange(int from, int to) { _minute = $"{from}-{to}"; return this; }
            public CronFluentBuilder AtHour(params int[] hours) { _hour = JoinValues(hours); return this; }
            public CronFluentBuilder EveryHours(int hours) { _hour = $"*/{hours}"; return this; }
            public CronFluentBuilder AtHourRange(int from, int to) { _hour = $"{from}-{to}"; return this; }

            public CronFluentBuilder EveryDay() { _dayOfMonth = "*"; _dayOfWeek = "*"; return this; }
            public CronFluentBuilder OnDaysOfMonth(params int[] days) { _dayOfMonth = JoinValues(days); _dayOfWeek = "?"; return this; }
            public CronFluentBuilder OnLastDayOfMonth() { _dayOfMonth = "L"; _dayOfWeek = "?"; return this; }

            public CronFluentBuilder OnDaysOfWeek(params DayOfWeek[] days)
            {
                var values = new int[days.Length];
                for (var i = 0; i < days.Length; i++) values[i] = (int)days[i];
                _dayOfWeek = JoinValues(values);
                _dayOfMonth = "?";
                return this;
            }

            public CronFluentBuilder OnWeekdays() { _dayOfWeek = "1-5"; _dayOfMonth = "?"; return this; }
            public CronFluentBuilder OnWeekends() { _dayOfWeek = "6,0"; _dayOfMonth = "?"; return this; }
            public CronFluentBuilder InMonths(params int[] months) { _month = JoinValues(months); return this; }

            /// <summary>构建 CRON 表达式字符串</summary>
            public string Build() => $"{_minute} {_hour} {_dayOfMonth} {_month} {_dayOfWeek}";

            private static string JoinValues(params int[] values) => string.Join(",", values);
        }
    }
}
