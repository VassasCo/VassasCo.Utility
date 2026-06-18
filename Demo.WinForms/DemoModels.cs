using System;
using System.Collections.Generic;
using VassasCo.Utility;

namespace WinFormsDemo
{
    public class DemoAppConfig
    {
        [ExcelDisplay("应用名称")]
        public string AppName { get; set; } = "MyApp";
        public string Version { get; set; } = "1.0.0";
        public int MaxConnections { get; set; } = 10;
        public double TimeoutSeconds { get; set; } = 30.0;
        public bool EnableLogging { get; set; } = true;
        public string Description { get; set; } = "";
        public string ApiKey { get; set; } = "";
        [ExcelDisplay("日志级别")]
        public string LogLevel { get; set; } = "Info";
    }

    public class LogDemoEntry
    {
        public DateTime Time { get; set; }
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public override string ToString() => $"[{Time:HH:mm:ss}] [{Type}] {Message}";
    }

    public class DemoOrder
    {
        [ExcelDisplay("订单编号")]
        public long Id { get; set; }
        [ExcelDisplay("客户名称")]
        public string Customer { get; set; } = "";
        [ExcelDisplay("订单金额")]
        public decimal Amount { get; set; }
        [ExcelDisplay("下单日期")]
        public DateTime OrderDate { get; set; }
        [ExcelDisplay("客户信息")]
        public DemoCustomer? CustomerInfo { get; set; }
        [ExcelDisplay("订单明细")]
        public List<DemoOrderItem> Items { get; set; } = new();
    }

    public class DemoCustomer
    {
        [ExcelDisplay("姓名")]
        public string Name { get; set; } = "";
        [ExcelDisplay("城市")]
        public string City { get; set; } = "";
        [ExcelDisplay("电话")]
        public string Phone { get; set; } = "";
    }

    public class DemoOrderItem
    {
        [ExcelDisplay("产品名称")]
        public string ProductName { get; set; } = "";
        [ExcelDisplay("数量")]
        public int Quantity { get; set; }
        [ExcelDisplay("单价")]
        public decimal UnitPrice { get; set; }
    }

    public class ScheduleTaskInfo
    {
        public string Name { get; set; } = "";
        public string CronExpression { get; set; } = "";
        public DateTime NextRun { get; set; }
        public bool IsRunning { get; set; }
        public override string ToString() => $"[{(IsRunning ? "Running" : "Paused")}] {Name} | Next: {NextRun:HH:mm:ss} | Cron: {CronExpression}";
    }
}
