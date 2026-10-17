# PageShot — 页面离屏渲染

把 MFAAvalonia 的页面画成 PNG，**全程不创建系统窗口**：不抢前台、不占任务栏、不动鼠标。

验证界面时用它，不要为了截图去启动 GUI。只有要看真实窗口外壳（阴影、圆角、toast 弹出位置）才回到 `bin/shot-pid.ps1`。

## 用法

在仓库根目录执行：

```bash
tools/PageShot/pageshot.sh                    # 抽卡页 → out/ps-gacha.png
tools/PageShot/pageshot.sh activity           # 活动页 → out/ps-activity.png
tools/PageShot/pageshot.sh share              # 导出晒卡的分享卡 → out/ps-share.png
tools/PageShot/pageshot.sh activity dark      # 深色主题 → out/ps-activity-dark.png
tools/PageShot/pageshot.sh gacha light 2      # 2 倍分辨率
tools/PageShot/pageshot.sh -- --width 800 --height 600
tools/PageShot/pageshot.sh -- --help          # PageShot 的全部参数
```

位置参数简写是「页面 [主题] [缩放]」。带 `--` 的参数原样透传给 PageShot。

`share` 不是页面，而是应用里「导出晒卡」的分享卡（`GachaShareCard`）：固定深色的设计稿、高度按内容自适应，
走的是和点按钮同一条渲染路径（挂进无头窗口让模板/样式落地 → 反复量排 → `RenderTargetBitmap`），
所以出图就是用户拿到的那张。建议 `share 1.5`（750 逻辑宽 × 1.5 ≈ 1125px）。

数据目录默认 `G:/MBCCtools工作区`（真实数据），用 `--data-root` 覆盖。
出图默认落在本目录的 `out/`（已 gitignore）。

## 它是什么

用 Avalonia 的 headless 平台（`UseHeadlessDrawing = false` + `UseSkia()`）建一个不映射到操作系统的窗口，页面照常走 `Loaded`、绑定、布局，再 `RenderTargetBitmap.Render(page)` 画出来。

跑的是真实的页面类、真实的 `App.axaml` 资源、真实的数据文件——和 GUI 里看到的一样，只是没有窗口。

## 两个必须照做的点

1. **页面自己不带背景。** 真实窗口里那层渐变底色是 `SukiWindow` 模板里的 `SukiMainHost` 画的。宿主要把页面套进 `SukiMainHost` 并把 `BackgroundStyle` 设成 `SukiBackgroundStyle.GradientSoft`（与 `RootView.axaml` 一致），否则卡片之间的空隙是透明的、导出的图放到聊天软件里会是黑框。
2. **`ItemsControl` 的条目容器是延迟生成的。** 绑定落到列表之后才建容器，只量一次布局会抓到「列表里有数据但一个条目都没建出来」的空壳。要 `Measure`/`Arrange`/`UpdateLayout` 跑几轮，每轮之间再跑一遍 Dispatcher 队列。

## 加新页面

在 `Program.cs` 的 `PageShotApp.CreatePage` 里补一行即可（照抄 `gacha` / `activity`）。

## 不在发布包里

这是 fork 自用的开发工具，`MFAAvalonia.Desktop` 不引用它，发布产物里不会有它。
