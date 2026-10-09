<!-- markdownlint-disable MD033 MD041 -->

<div align="center">

<img alt="MFAAvalonia" src="./docs/images/mfa-logo_512x512.png" width="160" height="160" />

# MFAAvalonia-MBCC

《无期迷途》自动化项目 **MBCCtools** 的定制 GUI

[![上游](https://img.shields.io/badge/上游-MaaXYZ%2FMFAAvalonia-4a90d9?style=flat-square&logo=github)](https://github.com/MaaXYZ/MFAAvalonia)
[![配套项目](https://img.shields.io/badge/配套项目-quietlysnow%2FMBCCtools-ffca28?style=flat-square&logo=github)](https://github.com/quietlysnow/MBCCtools)
[![License](https://img.shields.io/github/license/quietlysnow/MFAAvalonia-MBCC?style=flat-square)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)

</div>

> 🧠 **纯 vibecoding 项目 · 作者完全不会写代码**
>
> 本仓库所有代码均由 AI 编写，作者只负责描述需求、验收界面效果和整理文档。
>
> 代码风格由 AI 决定，不保证规范，也可能藏着没测出来的 bug。欢迎提 Issue 反馈，作者会转达给 AI 处理。

<!-- 以后想补界面截图：把两个页面的截图放进 docs/images/，再在这里插一张 <img> 即可 -->

## 这是什么

[MaaXYZ/MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia)（MaaFramework 的通用桌面 GUI）的一个 **fork**，为《无期迷途》自动化项目 [MBCCtools](https://github.com/quietlysnow/MBCCtools) 定制：在上游界面之外新增两个自建侧栏页面「抽卡记录」「活动提醒」。除此之外的一切均来自上游。

> ⚠ 本 GUI 需与 MBCCtools **配套使用**：`MFAAvalonia.exe` 必须和 `interface.json`、`record/` 放在同一目录，否则页面读不到数据；单独运行也没有任务资源。

## ✨ 本分支新增的功能

### 抽卡记录

查看 MBCCtools 采集到的抽卡（招募）历史。左侧是卡池导航栏（各池抽数、点击筛选），右侧三个页签：

| 页签 | 内容 |
|:---|:---|
| **概览** | 稀有度占比条、总抽数 / 平均出货抽数 / 距上次狂·危 等指标、角色获得次数卡片墙（狂 / 危 / 普角色均挂 `resource/base/image/头像/` 下的真实头像，缺图退回首字色块） |
| **表格** | 完整记录表，按稀有度整行染色，支持排序、筛选、搜索、分页 |
| **统计** | 分卡池汇总表 |

- **数据来源**：项目根目录 `record/抽卡记录/<类型>.jsonl`（旧版扁平的 `record/*.jsonl` 也兼容），由 MBCCtools 的「抽卡记录」任务识别采集落档，每行含 `时间 / 卡池名 / 稀有度(狂·危·普) / 角色名`。
- **刷新**：顶栏右侧「更新记录」重读全部 jsonl 并刷新页签（采集任务正往文件里追加时也能读）；想采集新记录，请运行 MBCCtools 的「抽卡记录」任务。

### 活动提醒

盯住当前活动还剩多久结束：

- **顶部摘要**：几个进行中、几个 24 小时内结束，以及数据采集时间（附"多久前"）。
- **活动卡片**：按"离结束还有多久"排序，状态色 🔴 24 小时内结束 · 🟠 72 小时内 · 🟢 进行中 · 🔵 未开始 · ⚪ 已结束；剩余时长每分钟自动重算。
- **通知**：进入页面时若确有活动 24 小时内结束，弹一条通知（每次启动只弹一次）。
- **数据来源**：项目根目录 `record/活动.json`，由 MBCCtools 的 `agent/wiki_activity.py` 抓 BWIKI 首页「游戏日历」落档，每项含 `名称 / 类型(活动·密令) / 开始 / 结束 / 链接`；GUI 启动时若数据已陈旧，会先在后台补采一次。

### 两个页面的共同约定

页面只读文件、不联网，采集与解析都在 MBCCtools 侧：

```mermaid
flowchart LR
    MBCC["MBCCtools 的任务 / agent<br/>负责采集与落档"] -->|写入| REC["record/ 目录<br/>抽卡记录 jsonl、活动.json"]
    REC -->|只读，页面不联网| GUI["本仓库的两个页面<br/>抽卡记录 / 活动提醒"]
```

好处是：抓取规则或游戏改版时只需更新 MBCCtools，不用重新发布 GUI。

## 📦 相关仓库

| 仓库 | 角色 |
|:---|:---|
| [MaaXYZ/MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia) | **上游**，本仓库的基础；除新增页面外的代码都来自它 |
| [quietlysnow/MFAAvalonia-MBCC](https://github.com/quietlysnow/MFAAvalonia-MBCC) | **本仓库**；定制改动都在 `feature/gacha-record` 分支（也是默认分支） |
| [quietlysnow/MBCCtools](https://github.com/quietlysnow/MBCCtools) | **配套资源项目**（《无期迷途》小助手本体），页面数据由它采集 |
| [MaaXYZ/MaaFramework](https://github.com/MaaXYZ/MaaFramework) | 驱动整个自动化的框架 |

## 🚀 使用与构建

- **运行要求**：Windows + [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（发布包为非自包含构建）。
- **使用方式**：把它当作 MBCCtools 的 GUI —— 构建产物覆盖到 MBCCtools 目录即可（`MFAAvalonia.exe` 与 `interface.json`、`record/` 同目录）。

构建（需要 .NET 10 SDK）：

```bash
dotnet publish MFAAvalonia.Desktop/MFAAvalonia.Desktop.csproj -c Release -r win-x64 --self-contained false -o publish
```

部署时**只覆盖构建产物**（`MFAAvalonia.exe` / `MFAAvalonia.dll`、`MFAAvalonia.deps.json`、`MFAAvalonia.runtimeconfig.json`、`libs/`、`runtimes/`、`MaaAgentBinary/`、`libloader.dll`），覆盖前先关闭正在运行的 `MFAAvalonia.exe`：

- ⚠ 不要整个 `publish/` 目录拷过去：里面可能混着旧的 `config/`、`record/`、`resource/` 副本，会覆盖用户配置与抽卡记录。
- ⚠ 安装目录根下不要残留旧的 `MFAAvalonia.Core.dll`（它只应存在于 `libs/` 里），否则会遮蔽刚发布的新版本，表现为"改了代码但界面没变"。

## 🔄 跟进上游

上游更新会定期合并进 `feature/gacha-record`（云端定时 workflow + 本地 `tools/sync-upstream.sh`），冲突一般只出现在页面注册用的那几个文件里。合并后注意：`MFAAvalonia.csproj` 里的 `Maa.Framework.Runtimes` 版本需与 MBCCtools 侧 `pip install MaaFw==<对应版本>` 保持一致，否则 Custom 节点协议不匹配会连不上。

## 📄 开源许可

基于 [GPL-3.0 License](./LICENSE) 开源（继承上游），本 fork 的全部改动同样以 GPL-3.0 发布。

## 🙏 致谢

感谢上游 [MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia)，以及它背后的 [MaaFramework](https://github.com/MaaXYZ/MaaFramework)、[Avalonia](https://github.com/AvaloniaUI/Avalonia)、[SukiUI](https://github.com/kikipoulet/SukiUI) 等开源项目。
