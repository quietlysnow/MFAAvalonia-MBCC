using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MFAAvalonia.Helper;

/// <summary>
/// 程序启动时后台刷新「活动提醒」数据：拉起 MBCCtools 的 agent/wiki_activity.py 抓一次 BWIKI。
/// 抓取与解析都在 MBCCtools 侧（AGENTS.md §2 的分工），GUI 只负责触发，实测约 1-5 秒，不阻塞启动。
/// 没有这一步的话，只有跑任务（MaaFW 拉起 agent）时数据才会更新，纯开 GUI 看提醒就是旧的。
/// </summary>
public static class ActivityRefreshLauncher
{
    /// <summary>数据这么新就不再启动采集，别为一次启动多打 BWIKI 两下。</summary>
    private static readonly TimeSpan FreshEnough = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 拉起一次采集。返回后台进程（没跑就返回 null，比如脚本不存在或数据还新鲜），
    /// 调用方可以等它退出再读数据，见 ActivityReminder。
    /// </summary>
    public static Process? Launch()
    {
        try
        {
            var script = Path.Combine(AppPaths.DataRoot, "agent", "wiki_activity.py");
            if (!File.Exists(script))
                return null;

            if (IsRecordFresh())
            {
                LoggerHelper.Info("活动数据 30 分钟内已更新过，跳过启动时的活动采集");
                return null;
            }

            var python = ReadAgentInterpreter() ?? "python";
            // 发布包的 child_exec 是包内相对路径（./agent/python/python.exe）：相对路径交给
            // Process.Start 会按父进程 CWD 解析，这里自己按数据根展开（与 AgentHelper 同口径）
            if (python.Contains('/') || python.Contains('\\'))
                python = Path.GetFullPath(python, AppPaths.DataRoot);
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{script}\"",
                WorkingDirectory = AppPaths.DataRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc == null)
                return null;
            // 脚本日志走 stderr（会进 agent 自己的日志链路之外），这里只吞不存；管道不满就不卡子进程
            proc.OutputDataReceived += (_, _) => { };
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            LoggerHelper.Info($"已拉起活动数据采集（{python} agent/wiki_activity.py，后台运行约 1-5 秒）");
            return proc;
        }
        catch (Exception e)
        {
            // python 缺失/被安全软件拦截等一律静默：界面打开时还有上一次的数据
            LoggerHelper.Info($"启动时的活动数据采集没跑起来（不影响使用）：{e.Message}");
            return null;
        }
    }

    /// <summary>record/活动.json 的 collected_at 距现在不到 FreshEnough 就算新鲜。</summary>
    private static bool IsRecordFresh()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataRoot, "record", "活动.json");
            if (!File.Exists(path))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("collected_at", out var at)
                && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), out var time))
                return DateTimeOffset.Now - time < FreshEnough;
        }
        catch
        {
            // 文件坏/读不动就当作旧数据，照常启动一次采集
        }
        return false;
    }

    /// <summary>解释器取 interface.json 的 agent.child_exec：MBCCtools 怎么拉 agent，这里就怎么拉采集脚本。</summary>
    private static string? ReadAgentInterpreter()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataRoot, "interface.json");
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("agent", out var agent)
                && agent.TryGetProperty("child_exec", out var exec)
                && exec.ValueKind == JsonValueKind.String)
                return exec.GetString();
        }
        catch
        {
            // 读不到配置就用 PATH 上的 python
        }
        return null;
    }
}
