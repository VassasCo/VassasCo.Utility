# VassasCo.Utility

面向 .NET 桌面端 / 服务端的通用工具库集合，包含配置管理、Excel 导入导出、日志三个独立组件。

[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## 目录

- [项目概览](#项目概览)
- [解决方案结构](#解决方案结构)
- [构建](#构建)
- [测试](#测试)
- [许可证](#许可证)

---

## 项目概览

| 项目 | 说明 | 目标框架 | 文档 |
|------|------|----------|------|
| **VassasCo.Utility.ConfigHelper** | 实体类与 JSON/XML 配置文件双向映射，自研解析器，读写严格对称 | netstandard2.0 / net6.0 / net8.0 / net9.0 / net10.0 | [README](src/VassasCo.Utility.ConfigHelper/README.md) |
| **VassasCo.Utility.ExcelMapper** | Excel / CSV 导入导出，支持 ClosedXML 与 OpenXml 流式引擎 | netstandard2.0 / net6.0 / net8.0 | [README](src/VassasCo.Utility.ExcelMapper/README.md) |
| **VassasCo.Utility.LogHelper** | 结构化日志，支持文件滚动与日志事件 | netstandard2.0 / net6.0 / net8.0 | [README](src/VassasCo.Utility.LogHelper/README.md) |

---

## 解决方案结构

```
VassasCo.Utility.slnx
├── build/                                 # 共享构建文件（IsExternalInit.cs）
├── src/
│   ├── VassasCo.Utility.ConfigHelper/      # 配置管理库
│   ├── VassasCo.Utility.ExcelMapper/       # Excel 导入导出库
│   └── VassasCo.Utility.LogHelper/         # 日志库
└── tests/
    ├── VassasCo.Utility.ConfigHelper.Tests/
    ├── VassasCo.Utility.ExcelMapper.Tests/
    └── VassasCo.Utility.LogHelper.Tests/
```

---

## 构建

在解决方案根目录执行：

```bash
dotnet build VassasCo.Utility.slnx
```

## 测试

```bash
dotnet test VassasCo.Utility.slnx
```

---

## 许可证

[MIT](LICENSE)
