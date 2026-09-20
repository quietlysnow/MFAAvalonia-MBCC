<!-- markdownlint-disable MD033 MD041 -->

> **本仓库是一个 fork。** 这是 [MaaXYZ/MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia)（MaaFramework 的通用桌面界面）的定制分支，专为《无期迷途》自动化项目 **MBCCtools** 而改。下方保留上游原始说明。

## 本 fork 用途

在 MFAAvalonia 里新增了一个独立的侧栏页面「**抽卡记录**」，用来查看 MBCCtools 采集到的游戏抽卡（招募）历史。

- **数据来源**：读取项目根目录下 `record/*.jsonl`（由 MBCCtools 的「抽卡记录」任务 + AgentServer 的 `GachaRecordPage` 识别器采集落档）。每行含 `时间 / 卡池名 / 稀有度(狂·危·普) / 角色名`。
- **页面形态**：左侧卡池导航栏（各池抽数、点击筛选）+ 三个页签：
  - **概览**：稀有度占比条、总抽数 / 平均出货抽数 / 距上次狂·危 等指标、角色获得次数卡片墙（狂/危/普角色均加载 `resource/base/image/头像/<稀有度级>/<名>.png` 真实头像，缺图退回首字色块）。
  - **表格**：完整记录表（按稀有度整行染色、分页、排序、筛选、搜索）。
  - **统计**：分卡池汇总表。
- **改动范围**：新增 `ViewModels/Pages/GachaRecordViewModel.cs`、`Views/Pages/GachaRecordView.axaml(.cs)`；小改 4 个上游文件（`App.axaml`、`App.axaml.cs`、`Helper/Instances.cs`、`Views/Mobile/RootViewContent.axaml`）。

## 构建与部署（本 fork）

- 需要 **.NET 10 SDK**（`MFAAvalonia.csproj` 目标 `net10.0`）。
- 编译产物：`dotnet publish MFAAvalonia.Desktop/MFAAvalonia.Desktop.csproj -c Release -r win-x64 --self-contained false -o publish`。
- 部署：把产物覆盖进 **MBCCtools 项目根目录**（`MFAAvalonia.exe` 须与 `interface.json`、`record/` 同目录，页面才读得到数据）。覆盖前先关闭正在运行的 `MFAAvalonia.exe`。

## 跟进上游

- 远程：`origin` = 上游 `MaaXYZ/MFAAvalonia`（只 fetch）；定制改动在分支 `feature/gacha-record`。
- 更新循环：`git fetch origin` → `git checkout feature/gacha-record` → `git merge origin/master`（冲突集中在上述 4 个文件，多为“双方都保留”）→ 重新 publish 部署。
- ⚠ 每次跟进后核对 `MFAAvalonia.csproj` 的 `Maa.Framework.Runtimes` 版本，与 MBCCtools 侧 `pip install MaaFw==<对应版本>` 保持一致，否则抽卡记录的 Custom 节点会协议不匹配连不上。

---

<div align="center">
  <img alt="MFAAvalonia" src="./docs/images/mfa-logo_512x512.png" width="192" height="192" />

# MFAAvalonia

MaaFramework 的跨平台通用桌面界面

[![License](https://img.shields.io/github/license/MaaXYZ/MFAAvalonia?style=flat-square&color=4a90d9)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-blueviolet?style=flat-square)](https://github.com/MaaXYZ/MFAAvalonia)
[![Commit Activity](https://img.shields.io/github/commit-activity/m/MaaXYZ/MFAAvalonia?style=flat-square&color=00d4aa)](https://github.com/MaaXYZ/MFAAvalonia/commits)
[![Stars](https://img.shields.io/github/stars/MaaXYZ/MFAAvalonia?style=flat-square&color=ffca28)](https://github.com/MaaXYZ/MFAAvalonia/stargazers)
[![Mirror酱](https://img.shields.io/badge/Mirror%E9%85%B1-%239af3f6?style=flat-square&logo=countingworkspro&logoColor=4f46e5)](https://mirrorchyan.com/zh/projects?rid=MFAAvalonia&source=mfaagh-badge)

[English](./README_en.md) | **简体中文**

</div>

## 项目简介

MFAAvalonia 是基于 [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) 和 [SukiUI](https://github.com/kikipoulet/SukiUI) 构建的 [MaaFramework](https://github.com/MaaXYZ/MaaFramework) 通用 GUI。资源开发者通过 Project Interface V2 描述任务、选项、控制器和界面文本，用户即可在桌面端配置并运行自动化任务。

MFAAvalonia 本身不包含具体业务资源。通常应将它集成到基于 MaaFramework 的资源项目中使用。

## 主要功能

### 任务与资源

- 支持 Project Interface V2 的任务、选项、预设、国际化、`import` 与 `pretask` 等能力。
- 支持输入框、选择器、开关等任务选项，以及可折叠的 Markdown 预设说明。
- 每条任务显示运行中、成功、失败、已取消或未执行状态，并在执行期间及完成后显示耗时。
- 支持资源公告与 Markdown 内容展示。

### 实例与自动化

- 使用标签页管理多个独立实例，可搜索、重命名、复制实例 ID，并导入或导出实例配置。
- 支持定时执行、应用启动时批量执行、全局热键和命令行自动启动。
- 同一路径下已运行的 MFAAvalonia 可接收新的命令行请求，无需重复启动一个进程。

### 控制器与平台

- 提供 Windows x64/arm64、Linux x64/arm64 和 macOS x64/arm64 发布目标。
- 支持 MaaFramework 的 ADB、Win32 和 PlayCover 控制器；实际可用控制器取决于系统、资源配置和打包内容。
- 支持亮色、暗色主题以及可由资源定义的多语言文本。

### 更新、通知与诊断

- 可通过 GitHub 或 Mirror酱检查并安装程序和资源更新。
- 支持将符合条件的本地资源压缩包拖入窗口，经确认后更新；任务运行期间不会执行拖放更新。
- 支持钉钉、飞书、Telegram、Discord、SMTP、WxPusher、QMsg、OneBot、Server酱和自定义 Webhook 等外部通知方式。
- 支持可配置的任务面板布局，详见[自定义布局](./docs/zh/自定义布局.md)。
- 资源可在 PI 中配置 Sentry 遥测；只有资源提供遥测配置且用户未关闭“帮助改进软件”时才会发送诊断信息。

## 界面预览

<p align="center">
  <img alt="MFAAvalonia 界面预览" src="./docs/images/preview.png" width="100%" />
</p>

## 运行要求

| 项目 | 要求 |
|:---|:---|
| 运行时 | [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。官方发布包默认为非自包含构建 |
| 系统 | 与所下载包匹配的 Windows、Linux 或 macOS 系统及 CPU 架构 |
| 资源 | 包含有效 `interface.json` 和 MaaFramework 资源文件的资源项目 |

不同资源可能有额外的模拟器、ADB、应用程序或系统权限要求，请以对应资源项目的说明为准。

## 快速开始

### 使用项目模板（推荐）

[MaaPracticeBoilerplate](https://github.com/MaaXYZ/MaaPracticeBoilerplate) 已提供 MFAAvalonia 集成流程。开发资源项目前，请先阅读项目模板的[开发说明](https://github.com/MaaXYZ/MaaPracticeBoilerplate/blob/main/docs/zh_cn/develop/how_to_develop.md)和 [Project Interface V2 协议](https://github.com/MaaXYZ/MaaFramework/blob/main/docs/zh_cn/3.3-ProjectInterfaceV2%E5%8D%8F%E8%AE%AE.md)。

MFAAvalonia 面向资源发布后的配置与运行。开发和排查 Pipeline 时，请使用 MaaFramework 提供的调试工具。

### 手动集成

1. 从 [Releases](https://github.com/MaaXYZ/MFAAvalonia/releases) 下载与目标系统及架构匹配的版本并解压。
2. 按 Project Interface V2 的目录约定放置 `interface.json`、资源文件和所需组件。
3. 安装 .NET 10 Runtime，然后启动对应平台的 MFAAvalonia 可执行文件。

手动打包时，MaaFramework 原生库、资源目录和 `interface.json` 必须彼此匹配。一般情况下应优先使用项目模板生成发布产物。

如需提供首次启动的预设配置，可在发布包的 `config` 目录放置 `config.template.json`。仅当目录中不存在其它配置 JSON（包括 `config/instances` 下的实例配置）时，该文件才会转换为 `config.json`；更新已有安装时不会覆盖或新增用户配置。

模板还可使用 `PresetSettings` 字典为不同 preset 指定独立配置：

```json
{
  "UI.LiveView.EnableLiveView": false,
  "PresetSettings": {
    "preset_a": {
      "UI.LiveView.EnableLiveView": true
    },
    "preset_b": {
      "UI.LiveView.EnableLiveView": false
    }
  }
}
```

`PresetSettings` 仅用于首次初始化 preset，转换完成后不会作为普通配置项写入 `config.json`。

## 启动参数

MFAAvalonia 支持通过命令行选择实例并执行任务。实例可以使用名称或实例 ID 指定；实例 ID 可在实例标签页的右键菜单中复制。

```text
MFAAvalonia.exe [参数]
```

| 参数 | 作用 |
|:---|:---|
| `-h`, `--help` | 显示命令行帮助并退出 |
| `-c <实例>`, `-i <实例>`, `--instance <实例>` | 按实例名称或实例 ID 激活目标实例；名称匹配不区分大小写，实例 ID 优先匹配 |
| `--autostart` | 自动执行目标实例中当前配置并勾选的任务；未指定实例时使用当前激活的实例 |
| `-q`, `--quit-after-run` | 本次命令行自动启动的任务完成后退出 MFAAvalonia；仅与 `--autostart` 配合时有效 |
| `-f`, `--forceStart` | 目标实例已运行时，先停止当前任务再重新启动；仅与 `--autostart` 和实例参数同时使用时有效 |

### 常用示例

```powershell
# 查看帮助
.\MFAAvalonia.exe --help

# 切换到指定实例
.\MFAAvalonia.exe --instance "日常任务"
.\MFAAvalonia.exe -i 1a2b3c4d

# 自动执行指定实例
.\MFAAvalonia.exe --autostart -i "日常任务"

# 执行完成后退出
.\MFAAvalonia.exe --autostart -i 1a2b3c4d -q

# 若实例正在运行，则停止后重新执行
.\MFAAvalonia.exe --autostart -i "日常任务" --forceStart
.\MFAAvalonia.exe --autostart -c 1a2b3c4d -f
```

### 参数组合规则

- 仅指定实例参数时，只切换到对应实例，不自动执行任务。
- `--autostart` 指向正在运行的实例时，默认跳过本次启动，不重复添加任务。
- 同时使用 `--autostart`、实例参数和 `-f` 时，会等待现有任务停止后重新启动。
- `-q` 只跟踪本次命令启动的任务；与 `-f` 配合时，停止旧任务不会被视为本次执行完成。

## 资源开发文档

- [Project Interface V2 协议](https://github.com/MaaXYZ/MaaFramework/blob/main/docs/zh_cn/3.3-ProjectInterfaceV2%E5%8D%8F%E8%AE%AE.md)
- [MaaPracticeBoilerplate](https://github.com/MaaXYZ/MaaPracticeBoilerplate)
- [Android 资源项目接入](./docs/zh/Android资源项目构建.md)
- [自定义布局](./docs/zh/自定义布局.md)
- [外部通知配置](./docs/zh/外部通知.md)

旧版 `Advanced` 配置已废弃。现有资源应迁移到 Project Interface V2，不应继续依赖旧字段。

### MFAAvalonia 扩展字段

MFAAvalonia 在 Project Interface V2 的 `task` 项中额外支持以下字段：

| 字段 | 类型 | 默认值 | 说明 |
|:---|:---|:---|:---|
| `repeatable` | `boolean` | `false` | 是否在任务设置中显示重复次数控件 |
| `repeat_count` | `integer` | `1` | 初始重复次数；`-1` 表示持续执行，直到用户停止任务。仅在 `repeatable` 为 `true` 时生效 |

```jsonc
{
  "task": [
    {
      "name": "重复任务",
      "entry": "TaskEntry",
      "repeatable": true,
      "repeat_count": 1
    }
  ]
}
```

用户在界面中修改的重复次数会随实例配置保存。

### 公告系统

将 `.md` 文件放入 `resource/announcement/` 目录即可作为资源公告显示。程序或资源更新的 Release Notes 会单独下载，并显示在更新提示中。

### 自定义图标

将 `logo.ico` 放置在程序根目录的 `Assets` 文件夹中，即可替换 MFAAvalonia 的窗口和托盘图标。

## 从源码构建

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 后执行：

```powershell
dotnet restore
dotnet build MFAAvalonia.sln -c Debug
dotnet publish MFAAvalonia.Desktop/MFAAvalonia.Desktop.csproj -c Release -r win-x64
```

将 `win-x64` 替换为项目支持的其他 RID，即可构建对应平台。运行仍需要有效的资源目录和 `interface.json`。

## 开源许可

本项目基于 [GPL-3.0 License](./LICENSE) 开源。

## 致谢

MFAAvalonia 使用了 [MaaFramework](https://github.com/MaaXYZ/MaaFramework)、[MaaFramework.Binding.CSharp](https://github.com/MaaXYZ/MaaFramework.Binding.CSharp)、[Avalonia](https://github.com/AvaloniaUI/Avalonia)、[SukiUI](https://github.com/kikipoulet/SukiUI)、[Serilog](https://github.com/serilog/serilog)、[Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)、[Mirror酱](https://github.com/MirrorChyan/docs) 等开源项目与服务。

感谢所有为 MFAAvalonia 做出贡献的开发者。

<a href="https://github.com/MaaXYZ/MFAAvalonia/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=MaaXYZ/MFAAvalonia&max=1000" alt="Contributors" />
</a>

<div align="center">

**如果这个项目对你有帮助，请给我们一个 ⭐ Star！**

[![Star History Chart](https://star-history.dera.page/svg?repos=MaaXYZ/MFAAvalonia&type=Date)](https://star-history.dera.page/#MaaXYZ/MFAAvalonia&Date)

</div>
