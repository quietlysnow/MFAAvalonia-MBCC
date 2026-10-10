# MFAAvalonia 定制分支（fork）— Agent 规则

本文件是给 AI 编码助手的项目约束。本仓库是 [MaaXYZ/MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia) 的 fork，专为《无期迷途》自动化项目 **MBCCtools** 定制，目前有「抽卡记录」「活动提醒」两个自建侧栏页。

**动手前先读 §1：这个改动该落在哪个仓库。**

## 1. 两个工作区的分工（最重要）

| 要做的事 | 放哪 |
| --- | --- |
| 新增/修改界面页面、侧栏项、样式、通知呈现 | **本工作区**（`G:/MFAAvalonia工作区`） |
| pipeline 节点、Custom 识别器与动作、模板图、OCR 模型、资源包 | MBCCtools 工作区（`G:/MBCCtools工作区`） |
| `agent/` 里的联网采集与落档（`record/`） | MBCCtools 工作区 |
| 任务清单/文案/选项（`interface.json`）、公告 md | MBCCtools 工作区 |

判断口径：**碰游戏画面或联网采集 → MBCCtools；碰 Avalonia 控件/页面 → 本工作区。**
MFAAvalonia 没有"资源侧声明自定义页面"的机制，任何界面都必须硬编码在本仓库（页面注册点见 §3），所以界面需求没有第二个选择。

两仓库靠**文件契约**连接（§2），不要跨进程直接调用，也不要让本仓库的页面去抓网络。

想"不动 GUI 就发布内容"时走 MBCCtools 的 `resource/Announcement/*.md`：MFAAvalonia 原生展示公告，不需要改 fork。

## 2. 数据契约（本仓库只读文件，MBCCtools 写）

| 文件 | 写入方 | 读取方 |
| --- | --- | --- |
| `record/抽卡记录/<类型>.jsonl`（旧的扁平 `record/*.jsonl` 也读） | agent 的 `GachaRecordPage` 采集 | `GachaRecordViewModel` |
| `record/活动.json` | `agent/wiki_activity.py`（抓 BWIKI 首页「游戏日历」） | `ActivityViewModel` |
| `resource/Announcement/*.md` | 人 / 资源更新 | MFAAvalonia 原生公告窗 |

- 路径基准：MFA 的 `AppPaths.DataRoot`（= exe 所在目录 = MBCCtools 项目根）。页面里用 `Path.Combine(AppPaths.DataRoot, "record", ...)`，不要写绝对路径。
- 写入约定：agent 先写临时文件再 `os.replace`（页面随时可能正在读）；带上 `collected_at` 采集时间，页面显示"多久前"，让数据陈旧可见；单个数据源失败不要丢整份文件，失败原因记进 `errors`。
- 页面只读文件、不联网：**抓取/解析规则变了（wiki 改模板等）只需更新 MBCCtools 的 agent，不用重发 GUI**。把解析塞进 C# 会让每次改版都要走 publish → 部署 → 用户重下。
- 抽卡记录的时间口径（2026-10-10）：单个 jsonl 内部是「`index` 升序 = 游戏列表顺序 = 最新在前」，但**跨文件不是全局时间序**（页面按文件 mtime 拼接读取，各追踪类型一个文件）。凡是要按时间呈现的地方自己排 `cells[0]`，且必须**稳定排序**——同一秒内可能有多条（十连/连抽共享时间戳），先后只能靠行序定。「狂级出金记录」就是这么排的，别退回成"行序反转"（那样卡片会按追踪类型分组、最新一次出金被埋在中段）。时间戳有三种脏形态（`… 01:03:57` / 丢分隔符 `…0201:03:57` / 分隔符被 OCR 认成点 `….01:03:57`），统一由 `GachaRecordViewModel.TimeFix` 归一化后再比字符串。
- 触发约定（2026-09-29 起）：GUI 启动时 `Helper/ActivityRefreshLauncher` 会用 `interface.json` 的 `agent.child_exec` 后台拉起一次 `agent/wiki_activity.py`（实测约 1-5 秒；`record/活动.json` 的 `collected_at` 距今 <30 分钟则跳过）。之前只有跑任务（MaaFW 拉起 agent）才会采集，纯开 GUI 看提醒永远是旧数据。GUI 只触发、不解析，抓取规则仍只在 MBCCtools 改。
- 抽卡页顶栏「更新记录」= 重读 `record/` 下所有 `*.jsonl`（递归，含子目录）并重算全部页签，旁边回显"更新于 HH:mm:ss"；它**不触发采集**（采集仍靠跑「抽卡记录」任务）。读取用 `FileShare.ReadWrite`，因为点刷新常发生在采集任务正追加同一个文件时。
- 活动临期口径（2026-10-10）：剩余 **<3 天**一律标红（`ActivityPalette.Alert`，`ActivityRow.IsAlerting`；活动名靠 `Classes.alert` 用 x:Static 取同一个红，状态/倒计时/左侧色条走 `StatusBrush`），<24 小时只是状态文字变"即将结束"、颜色不再区分。<3 天口径同时决定开机播报的内容。
- 活动卡片视觉（2026-10-10 用户指定，参考图见 `bin/ref-activity-progress-bars.png`）：**进度条就是卡片主体**——一条 `Border`（底槽 `SukiPrimaryColor5` + 圆角 + `ClipToBounds`）里塞填充色块与文字；填充宽度用 `ScaleTransform.ScaleX={Binding ElapsedRatio}`（`RenderTransformOrigin="0%,0%"`）从左边拉伸，**不用去量控件宽度**；填充色是 `ActivityPalette.FillOf(state)` 的同色淡色（28% 透明），明暗主题都压得住前景文字。条上是活动名（左）+「距离活动结束还有 X 天 Y 小时」（右，状态色），第二行才是 `RangeText` + 类型标签 + 状态文字。`ElapsedRatio = 已过时长 / 总时长`（日精度按当天结束算）；wiki 没给 `start` 时是 0，条留空槽，别假装有进度。卡片/弹窗共用 `CountdownText` 一句文案（口径"距离活动结束还有…"，`RemainingText` 已删）。
- 开机播报（2026-10-10）：`Helper/ActivityReminder` 在窗口就绪 + 等启动采集落档（最多 20 秒）后，把 3 天内结束的活动送到**主页右上角**——它用**自己的一套 `ISukiToastManager` + `SukiToastHost`**（`RootView.axaml` / `RootViewMobile.axaml` 里 `Position="TopRight"`，`Margin="0,44,0,0"` 让开标题栏），因为全局那条 toast 停在右下角，动它等于把所有提示一起挪走。**一个活动一条弹窗**（标题=活动名，内容="剩余 X（即将/临近结束）"），不合并成一条列表；最多 5 条（与 host 的 `MaxToasts` 对齐——超了 host 会先把最老的一条顶掉，而最老的正好是最紧急的），再多就只在末条加一句"另有 N 个…"。一次启动只播一次（`ActivityViewModel._reminded`）；进活动页时 `NotifyEndingSoon()` 兜底再试一次（启动时没数据的场景）。启动路径没弹时日志会写清原因（"进行中的活动里没有 3 天内结束的" / "没弹出来：…"），排查别只看有没有弹窗。

## 3. 本仓库的改动面（跟进上游的成本）

新增一个页面要动 3 处，照抄现有页面即可：

1. `MFAAvalonia/App.axaml.cs`：`.AddView<XView, XViewModel>(services)`
2. `MFAAvalonia/Helper/Instances.cs`：加两个字段 `private static XView _xView; private static XViewModel _xViewModel;`（`LazyStaticGenerator` 按"私有静态 + `_` 前缀"自动生成 `Instances.XView`，无需特性）
3. `MFAAvalonia/Views/Mobile/RootViewContent.axaml`：加一项 `<suki:SukiSideMenuItem Header=... Icon=... PageContent="{extensions:ServiceProvider ServiceType={x:Type pages:XView}}" />`

约束：

- **侧栏首项（Home）必须保持 `InstanceContainerView`**，它是"主页 = 任务"。不要为了调试或截图把默认页改成别的。
- **不要动上游文件的既有逻辑**，只做上面这类插入式改动；样式尽量写在页面自己的 `<UserControl.Styles>` 里，避免再往 `App.axaml` 加 `ResourceInclude`。**唯一的例外是 `.github/workflows/Release.yml`**（2026-10-10 用户指定）：构建矩阵只留 Windows（`win-x64` / `win-arm64`）与 macOS（`osx-x64` / `osx-arm64`），linux 条目和 Android job 已删。跟进上游时保留本 fork 的版本，别把 linux / Android 加回来；macOS 也不能删——它的包要在 macOS 上构建并做 adhoc 签名，本地 Windows 出不了。
- 图标用 `FluentIcons.Common.Icon` 的成员名，**这个版本里没有的名字会编译报错**（`CalendarLtr` 就不存在，日历类可用 `CalendarClock`）。
- `Helper/IconHelper.cs` 的 `WindowIcon` **必须**把 `.ico` 原始字节交给框架（`new WindowIcon(new MemoryStream(bytes))`）。Avalonia 的 `LoadIcon(IBitmapImpl)` 会先把位图重新编码成 PNG，`Win32Icon` 拿不到 ICO 目录就只能"按位图原尺寸建 HICON"，结果是 512px 位图被 Windows 缩到标题栏的 16px —— 图标发糊。**别为了"简化"改回传 Bitmap**（2026-10-10；核对方法见 MBCCtools `AGENTS.md` §13.3，它会打印窗口真正设上去的图标尺寸）。
- 页面文案中文；与既有页面保持同一套设计语言（`Border.Card`、`TextBlock.SectionTitle`、`TextBlock.Hint`）。颜色令牌：抽卡页在 `Assets/Style/GachaTheme.axaml`；活动页的状态色集中在 `ActivityPalette`（C# 单一来源，XAML 只绑 `StatusBrush`）。

## 4. 布局约定（这个壳里踩过的坑）

- ~~靠右对齐的内容会被推到卡片右缘外看不见~~ **已平反（2026-09-29）**：当年"被裁"其实是截图伪影——旧 `bin/shot-pid.ps1` 从 DPI 未感知的 PowerShell 调 `MoveWindow/GetWindowRect`，坐标被系统按本机 125% 缩放虚拟化，`PrintWindow` 只截到实际渲染面（1500 DIP 窗口 = 1875×1175 物理像素）的左上 80%，靠右内容一直渲染得好好的。Grid 星号列+Auto 列、`DockPanel` 右停靠、`HorizontalAlignment=Right` 都实测可用（活动卡片右列即如此）；行内横排 `StackPanel` 依旧是保底方案。
- 列表结构用 `ScrollViewer > StackPanel > ItemsControl`；**不要**用 `Panel` 叠层放列表与空态提示（空态与列表互斥显示即可）。
- 横向 `StackPanel` 不约束子元素宽度，别指望里面长文本被 `TextTrimming` 裁掉；长文本要放在会给出宽度的容器里（如 Grid 的 `*` 列）。
- 提醒/统计这类"随现在时刻变化"的文案，用 `DispatcherTimer` 在页面 `Loaded` 起、`Unloaded` 停（见 `ActivityView.axaml.cs`）。
- **窗口的 `Opened` 事件抓不住**（2026-10-10 实测）：`App.OnFrameworkInitializationCompleted` 里到 `TrayIconManager.InitializeTrayIcon(...)` 那行时窗口已经 `IsVisible=True`、`Loaded` 也已触发（SukiWindow 显示得比这段代码早），`window.Opened` 挂在后面永远不会回调。想在"程序起来时"做一次事，用 `if (window.IsLoaded) 立刻做; else window.Loaded += ...`，不要只挂 `Opened`。
- 开机时的 toast 想抓进截图得卡时间：窗口约 5-8 秒才出来、toast 只停 10 秒，`bin/shot-pid.ps1` 自己内部还有 ~2.5 秒的 `MoveWindow`/前台化等待，所以从 `Start-Process` 到截图建议 5-8 秒（见本次验证图 `bin/ref-activity-reminder-toasts.png`、`bin/ref-activity-red-3days.png`）。

## 5. 构建与部署

```bash
# 需要 .NET 10 SDK。PATH 上的 dotnet 可能是 9.x，用用户级 SDK：
"$USERPROFILE/.dotnet/dotnet.exe" publish MFAAvalonia.Desktop/MFAAvalonia.Desktop.csproj \
    -c Release -r win-x64 --self-contained false -o publish
```

部署到 **MBCCtools 项目根目录**（`MFAAvalonia.exe` 必须与 `interface.json`、`record/` 同目录），**覆盖前先关闭正在运行的 `MFAAvalonia.exe`**。

⚠ 两个坑，别踩：

1. **只覆盖构建产物**（`MFAAvalonia.exe/.dll`、`MFAAvalonia.deps.json`、`MFAAvalonia.runtimeconfig.json`、`libs/`、`runtimes/`、`MaaAgentBinary/`、`libloader.dll`）。不要把整个 `publish/` 目录 `cp` 过去 —— 那里面可能混着 `config/`、`record/`、`resource/` 的旧副本，会覆盖用户配置与抽卡记录。
2. **安装目录根下不要残留旧的 `MFAAvalonia.Core.dll`**：原生依赖探测顺序是「应用目录 → `libs/`」，根目录那份会**遮蔽** `libs/` 里刚发布的新版本，表现为"改了代码界面没变"（`MFAAvalonia` 项目的 `AssemblyName` 就是 `MFAAvalonia.Core`，发布时被 NetBeauty 挪进 `libs/`）。发新版前确认它只存在于 `libs/` 下。

## 6. 验证（没有单测、没有 CI）

- 视觉核对：`powershell -ExecutionPolicy Bypass -File bin/shot-pid.ps1 -Id <pid> -Out x.png`。按 **PID** 抓（可能同时开着多个实例，按进程名会抓错），内部用 `PrintWindow`，不要求窗口在前台。**脚本已 DPI 感知（2026-09-29 修复）**：本机显示缩放 125%，DPI 未感知的截图只会截到窗口左边 80%，会把靠右内容误判为"被裁"；另写截图代码务必先 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`，并注意脚本里的 `-Width/-Height` 现在是物理像素（默认 1875×1175 = 1500×940 DIP）。
- ⚠ **合成点击不可靠，别把"点一下再看"当验证手段**：这台机器上 `SetCursorPos` 的实际落点会被别的东西带偏、`SetForegroundWindow` 常被系统拒。`shot-pid.ps1 -ClickX -ClickY -PostClick`（向 WndProc 直接投递鼠标消息）实测可用，但属于侥幸路径；要看某个侧栏页，宁可临时加一个环境变量开关让启动即选中该项（验完删掉）。（2026-10-10 实测：`-ClickX` 直接用截图里的 x；`-ClickY` 要比目标行在截图里的 y 大 44，差的是标题栏高度，不补就会点到上一行。那类临时开关（如本次用过的 `MFA_START_PAGE`，验完已从代码里删掉）偶发失效：启动慢时 1.5 秒内侧栏还没就绪，补一次点击即可。）
- 数据先脱离 GUI 验：数据文件用 `python -c` 读出来核对字段与排序，比截图快得多。
- `publish/` 那份副本可以直接跑起来当沙箱验界面（它自带 `config/`、`interface.json`、`resource/`），但注意两件事：①它是独立数据目录，所以要么往 `publish/record/` 灌测试数据、要么它自己的 `record/` 就是旧数据；②它会照自己那份 config 自动执行「启动前」任务并连真实模拟器（会碰到正在跑的游戏画面）——只想看界面，先把 `publish/config/instances/default.json` 里 `AdbDevice.AdbPath/AdbSerial` 清空，验完还原。
- 拿 GUI 里的真实数据验「3 天内结束」很别扭（活动日历上通常没有这么近的）：按 §2 的契约在 `publish/record/活动.json` 造几条临期活动最快，验完删掉即可。
- 涉及 MBCCtools 的改动，本仓库无法验证，交给用户实机跑；MBCCtools 侧的自检清单见其 `AGENTS.md` §8。

## 7. 跟进上游

- 远程：`origin` = 上游 `MaaXYZ/MFAAvalonia`（只 fetch）；`myfork` = 自己的公开 fork `quietlysnow/MFAAvalonia-MBCC`（默认分支就是 `feature/gacha-record`）；定制改动在 `feature/gacha-record` 分支。
- 更新循环（2026-10-10 起自动化，两条路都以"合并提交只有一份"为前提）：`git fetch origin` → `git checkout feature/gacha-record` → `git merge origin/master` → 重新 publish 部署。冲突集中在 §3 那 3 个注册点文件，多为"双方都保留"。
  - **云端** `.github/workflows/upstream-sync.yml`：定时（北京时间 09:05 / 14:05 / 20:05，按上游活跃时段 09:00–次日 01:00 排的点）检测上游 `master`，能干净合并就自动合并并推回 `myfork`；有冲突则分支原样不动，改开一个带 `upstream-sync` 标签的 issue 等人处理。定时任务只在**默认分支**上跑，所以这个文件必须留在 `feature/gacha-record`；公开仓库连续 60 天无活动时 GitHub 会停掉定时任务，去 Actions 页点一次 Run workflow 即可恢复。
  - **本地** `tools/sync-upstream.sh`：默认「fetch 上游 + 对齐 `myfork` + 合并」，`--check` 只报告落后多少（落后退出码 1，可拿去做通知/定时），`--push` 推回 `myfork`，`--build`/`--deploy` 顺带 publish 及部署到 MBCCtools 根目录（内置 §5 那两个坑的防护：只覆盖构建产物、拦住根目录残留的 `MFAAvalonia.Core.dll`、程序在跑就拒绝部署）。它先快进到 `myfork` 的合并结果再补合，避免云端和本地各合一份而分叉；本地合完**建议带 `--push`**。
  - 同步（`upstream-sync.yml`）只合代码、不构建：合并后仍要在本机跑一次 `--deploy`（或 `--build` 后手动按 §5 部署）。
  - 发版线（2026-10-10 起）：推 `v*` tag 触发 `Release.yml`（fork 侧只保留 Windows / macOS 各 x64+arm64，见 §3），云端打包四个包并创建/更新对应 Release。**发版一律走云端 workflow，不要在本地 publish 后手发**（2026-10-10 用户定），两边对同一个 tag 各发一次会互相覆盖。
- ⚠ 每次跟进后核对 `MFAAvalonia.csproj` 的 `Maa.Framework.Runtimes` 版本，与 MBCCtools 侧 `pip install MaaFw==<对应版本>` 保持一致，否则 Custom 节点协议不匹配连不上。
