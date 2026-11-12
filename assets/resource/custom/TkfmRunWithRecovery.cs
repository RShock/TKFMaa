using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Wraps a complete module, never individual screen transitions or native event callbacks.
public sealed class TkfmRunWithRecovery : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmRunWithRecovery";
    internal const string EnabledKey = "TkfmRestartFailedStep";
    internal const string FailureBehaviorKey = "TkfmFailureBehavior";
    internal const string RestartBehavior = "restart";
    internal const string PauseBehavior = "pause";
    private static readonly ConcurrentDictionary<IMaaTasker, byte> Running =
        new ConcurrentDictionary<IMaaTasker, byte>();

    public TkfmRunWithRecovery()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.GameOptionsMigration";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmGameOptionsMigration());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        MaaProcessor processor = null;
        bool registered = false;
        bool continueAfterFailure = false;
        string failureBehavior = RestartBehavior;
        try
        {
            processor = MaaProcessor.Processors.ToArray().FirstOrDefault(p => context.Tasker.Equals(p.MaaTasker));
            if (processor == null)
                throw new InvalidOperationException("无法定位所属实例，拒绝执行任务");
            if (!Running.TryAdd(context.Tasker, 0))
                return FinishFailure(processor, "异常恢复入口发生递归，拒绝再次进入。", false);
            registered = true;

            // Snapshot policy before starting; settings are disabled while a queue is running.
            failureBehavior = GetFailureBehavior(processor);
            bool restartOnFailure = failureBehavior == RestartBehavior;
            continueAfterFailure = processor.InstanceConfiguration.GetValue(
                ConfigurationKeys.ContinueRunningWhenError, false);
            var param = JObject.Parse(args.ActionParam);
            string entry = (string)param["entry"];
            string label = (string)param["label"] ?? args.NodeName;
            string[] successAnyHit = (param["success_any_hit"] as JArray)?.Values<string>()
                .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray() ?? Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(entry) || entry == args.NodeName
                || !context.GetNodeData(entry, out var unused))
                throw new InvalidOperationException("任务主体不存在或指向恢复入口自身：" + entry);

            processor.AddLog("[异常恢复] " + label + "：开始执行。", changeColor: false);
            // Install error branches on this module's context, before preparation and retries.
            // JumpBack retains the existing pipeline stack and business bookkeeping.
            foreach (string popup in new[] { "TkfmRecoveryAdvertisement", "TkfmRecoveryAnnouncement",
                "TkfmRecoveryEventSignIn", "TkfmRecoveryBackButton", "GameLoginBackFallback",
                "GameLoginExistingGiftFallback" })
                if (context.GetHitCount(popup, out var popupCount) && popupCount > 0 && !context.ClearHitCount(popup))
                    throw new InvalidOperationException("无法初始化弹窗恢复额度：" + popup);
            if (!context.OverridePipeline(PopupRecoveryOverride(context).ToString(Formatting.None)))
                throw new InvalidOperationException("无法安装通用弹窗恢复分支");
            if (EnsureGameRunning(context, processor)
                && RunAttempt(context, processor, entry, successAnyHit)) return !Cancelled(context, processor);
            if (Cancelled(context, processor)) return false;
            if (!restartOnFailure)
                return PauseForFailure(processor, label + "执行失败。", continueAfterFailure);

            // The single restart allowance is consumed before stop/start/login, including failures there.
            processor.AddLog("[异常恢复] " + label
                + "首次异常：结束游戏进程，重新启动并登录，再从当前步骤入口重做（仅一次）。", "OrangeRed");
            string retryOverride = RetryOverride(context, entry).ToString(Formatting.None);
            ResetNavigationHits(context);
            if (Cancelled(context, processor)) return false;

            if (args.NodeName != "GameLoginStart")
            {
                string loginOverride = LoginOverride(context, processor).ToString(Formatting.None);
                if (!RunOnce(context, processor, "TkfmRecoveryRestartGame", loginOverride))
                {
                    if (Cancelled(context, processor)) return false;
                    return FinishFailure(processor, label + "的重启或登录失败，不再重启。", continueAfterFailure);
                }
                MarkGamePrepared(processor);
            }
            // The login module's own attempt already stops and starts the game. Do not restart it twice.
            if (Cancelled(context, processor)) return false;
            if (RunAttempt(context, processor, entry, successAnyHit, retryOverride))
            {
                if (Cancelled(context, processor)) return false;
                processor.AddLog("[异常恢复] " + label + "重试成功。", changeColor: false);
                return true;
            }
            if (Cancelled(context, processor)) return false;
            return FinishFailure(processor, label + "重试后仍异常，一次重启额度已用尽。", continueAfterFailure);
        }
        catch (Exception ex)
        {
            if (processor == null)
            {
                LoggerHelper.Error("TKFMaa 异常恢复：" + ex.Message);
                return false;
            }
            if (Cancelled(context, processor)) return false;
            string reason = "恢复调度失败：" + ex.Message + "；不递归重启。";
            return failureBehavior == PauseBehavior
                ? PauseForFailure(processor, reason, continueAfterFailure)
                : FinishFailure(processor, reason, continueAfterFailure);
        }
        finally
        {
            if (registered) Running.TryRemove(context.Tasker, out var unused);
        }
    }

    private static string GetFailureBehavior(MaaProcessor processor)
    {
        string configured = processor.InstanceConfiguration.GetValue(FailureBehaviorKey, string.Empty);
        if (configured.Equals(RestartBehavior, StringComparison.OrdinalIgnoreCase)
            || configured.Equals(PauseBehavior, StringComparison.OrdinalIgnoreCase))
            return configured.ToLowerInvariant();

        string migrated = processor.InstanceConfiguration.GetValue(EnabledKey, true)
            ? RestartBehavior : PauseBehavior;
        processor.InstanceConfiguration.SetValue(FailureBehaviorKey, migrated);
        return migrated;
    }

    private static bool PauseForFailure(MaaProcessor processor, string reason, bool continueAfterFailure)
    {
        processor.AddLog("[故障暂停] " + reason + " 不重启，等待人工处理。", "OrangeRed");
        try
        {
            object? service = AppDomain.CurrentDomain.GetData("TKFMaa.TaskPause.Service");
            var method = service?.GetType().GetMethod("PauseForFailure",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (service != null && method != null)
            {
                object? result = method.Invoke(service, new object[] { processor, reason });
                if (result is bool paused && paused) return false;
            }
        }
        catch (TargetInvocationException ex)
        {
            processor.AddLog("[故障暂停] 暂停服务异常：" + (ex.InnerException?.Message ?? ex.Message), "OrangeRed");
        }
        catch (Exception ex)
        {
            processor.AddLog("[故障暂停] 暂停服务异常：" + ex.Message, "OrangeRed");
        }
        return FinishFailure(processor, reason + " 暂停服务不可用。", continueAfterFailure);
    }

    private static bool Cancelled(IMaaContext context, MaaProcessor processor) =>
        context.IsCancellationRequested || processor.CancellationTokenSource?.IsCancellationRequested == true;

    private static bool RunOnce(IMaaContext context, MaaProcessor processor, string entry, string overrides = "{}")
    {
        if (Cancelled(context, processor)) return false;
        try
        {
            // RunTask clones pipeline overrides while sharing hit counts/anchors. Explicitly reset only
            // navigation counts on retry; do not erase consumption limits or the purchased-stamina anchor.
            var detail = context.RunTask(entry, overrides);
            return !Cancelled(context, processor) && detail?.Status == MaaJobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            if (!Cancelled(context, processor))
                processor.AddLog("[异常恢复] " + entry + "执行异常：" + ex.Message, "OrangeRed");
            return false;
        }
    }

    private static bool RunAttempt(IMaaContext context, MaaProcessor processor, string entry,
        IReadOnlyList<string> successAnyHit, string overrides = "{}")
    {
        if (successAnyHit.Count == 0) return RunOnce(context, processor, entry, overrides);

        var before = new ulong[successAnyHit.Count];
        for (int i = 0; i < successAnyHit.Count; i++)
            if (context.GetHitCount(successAnyHit[i], out var count)) before[i] = count;

        if (!RunOnce(context, processor, entry, overrides)) return false;
        for (int i = 0; i < successAnyHit.Count; i++)
            if (context.GetHitCount(successAnyHit[i], out var count) && count > before[i]) return true;

        processor.AddLog("[异常恢复] " + entry + "返回成功，但未命中完成节点："
            + string.Join(" / ", successAnyHit) + "。安全返回主页不等于任务完成。", "OrangeRed");
        return false;
    }

    private static void ResetNavigationHits(IMaaContext context)
    {
        if (!context.GetNodeData("TkfmRecoveryConfig", out var raw))
            throw new InvalidOperationException("缺少公共恢复配置");
        var config = JObject.Parse(raw)["attach"];
        var preserve = new HashSet<string>((config?["preserve_hits"] as JArray ?? new JArray()).Values<string>());
        foreach (var name in context.Tasker.Resource.NodeList)
        {
            if (preserve.Contains(name)) continue;
            if (context.GetHitCount(name, out var count) && count > 0 && !context.ClearHitCount(name))
                throw new InvalidOperationException("无法重置导航次数：" + name);
        }
    }

    private static JObject PopupRecoveryOverride(IMaaContext context)
    {
        var overrides = new JObject();
        foreach (string name in context.Tasker.Resource.NodeList)
        {
            // Recovery branches must not recursively recover themselves. Pure recognition
            // helpers have no outgoing flow and are not task failure boundaries.
            if (name.StartsWith("TkfmRecovery", StringComparison.Ordinal)
                || name == "TkfmTaskFailure" || !context.GetNodeData(name, out var raw)) continue;
            var node = JObject.Parse(raw);
            if ((string)node["action"]?["param"]?["custom_action"] == "TkfmRunWithRecovery") continue;
            var next = NodeList(node["next"]);
            var original = NodeList(node["on_error"]);
            if (next.Count == 0 && original.Count == 0) continue;
            // A normal page can have the same arrow as an advertisement. Recognize the
            // complete back button first so it uses navigation recovery and its truthful log.
            var errors = new JArray("[JumpBack]TkfmRecoveryAnnouncement", "[JumpBack]TkfmRecoveryEventSignIn",
                "[JumpBack]TkfmRecoveryBackButton", "[JumpBack]TkfmRecoveryAdvertisement");
            foreach (var item in original) errors.Add(item.DeepClone());
            if (original.Count == 0) errors.Add("TkfmRecoveryUnhandled");
            overrides[name] = new JObject { ["on_error"] = errors };
        }
        // Recovery may finish a login subflow, but must never stop its parent task.
        // Only the recognized home page completes a login subflow; no advertisement is required.
        overrides["GameMainReady"] = new JObject
        {
            ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray()
        };
        return overrides;
    }

    private static JArray NodeList(JToken? value) => value is JArray array
        ? (JArray)array.DeepClone() : value == null || value.Type == JTokenType.Null
            ? new JArray() : new JArray(value.DeepClone());

    private static JObject RetryOverride(IMaaContext context, string entry)
    {
        var overrides = new JObject();
        if (entry == "AutoBattleStartAttempt")
        {
            // This legacy entry requires a manually selected activity. Returning to the home page
            // after restart must not masquerade as successfully rebuilding that activity entry.
            overrides[entry] = new JObject
            {
                ["next"] = new JArray("RegularActivityStageListDecision"), ["timeout"] = 10000
            };
            return overrides;
        }
        // Recruitment uses per-attempt anchors shared by recovery subtasks. Navigation hit
        // counts are no longer the quota, and must never be converted back into batch counts.
        return overrides;
    }

    private static JObject LoginOverride(IMaaContext context, MaaProcessor processor)
    {
        string resource = processor.ViewModel?.CurrentResource;
        string package = resource == "官服" ? "com.pinkcore.tkfm"
            : resource == "erolab服务器" ? "com.pinkcore.tkfm.erolabs"
            : throw new InvalidOperationException("无法确定资源类型对应的游戏包名：" + resource);
        return new JObject
        {
            ["TkfmRecoveryRestartGame"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "StopApp", ["param"] = new JObject { ["package"] = package } }
            },
            ["GameLoginLaunch"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "StartApp", ["param"] = new JObject { ["package"] = package } }
            },
            // StopTask propagates to the parent context and would prevent rerunning the failed module.
            // Finish only this login subtask after recognizing Expedition without a known obstruction.
            ["GameMainReady"] = new JObject { ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray() }
        };
    }

    private static bool EnsureGameRunning(IMaaContext context, MaaProcessor processor)
    {
        var source = processor.CancellationTokenSource
            ?? throw new InvalidOperationException("缺少当前队列取消令牌");
        if (ReferenceEquals(AppDomain.CurrentDomain.GetData("TKFMaa.GamePrepared." + processor.InstanceId), source)) return true;
        try
        {
            var overrides = LoginOverride(context, processor);
            string package = (string)overrides["GameLoginLaunch"]["action"]["param"]["package"];
            // Use the same active configuration as MFA's controller. The persisted value
            // may be an AdbDeviceInfo in memory, so GetValue<JObject> can return an empty default.
            var device = processor.Config.AdbDevice;
            string adb = device.AdbPath, serial = device.AdbSerial;
            if (string.IsNullOrWhiteSpace(adb) || string.IsNullOrWhiteSpace(serial))
                throw new InvalidOperationException("所选 ADB 设备配置不完整："
                    + (string.IsNullOrWhiteSpace(adb) ? "ADB 路径缺失；" : "")
                    + (string.IsNullOrWhiteSpace(serial) ? "设备地址缺失；" : "")
                    + "请重新选择并连接设备");
            // A shell always returns success after emitting a marker, so a transport failure
            // cannot be confused with the ordinary pidof exit status for a stopped game.
            string output = AdbOutput(adb, serial,
                "pidof " + package + "; echo TKFM_PID_CHECK_DONE", source.Token);
            if (!output.Contains("TKFM_PID_CHECK_DONE"))
                throw new InvalidOperationException("ADB 未返回完整游戏进程状态");
            bool running = Regex.IsMatch(output.Replace("TKFM_PID_CHECK_DONE", "").Trim(), @"^\d+(\s+\d+)*$");
            if (!running)
            {
                processor.AddLog("[自动启动] 所选游戏未运行，启动并完成登录。", changeColor: false);
                if (!RunAttempt(context, processor, "GameLoginLaunch",
                    new[] { "GameMainReady" }, overrides.ToString(Formatting.None))) return false;
            }
            else
            {
                // StartApp brings an existing Android task to the foreground without stopping it.
                string activity = AdbOutput(adb, serial, "dumpsys activity activities", source.Token);
                bool foreground = activity.Split('\n').Any(line =>
                    (line.Contains("mResumedActivity") || line.Contains("topResumedActivity"))
                    && Regex.IsMatch(line, @"\b" + Regex.Escape(package) + @"/"));
                if (!foreground)
                {
                    processor.AddLog("[自动启动] 游戏已运行，切回所选游戏。", changeColor: false);
                    var bringForward = (JObject)overrides.DeepClone();
                    bringForward["GameLoginLaunch"]["next"] = new JArray();
                    if (!RunOnce(context, processor, "GameLoginLaunch", bringForward.ToString(Formatting.None))) return false;
                }
                // Let the selected task recognize its current page. Delayed advertisements
                // and other interruptions are handled by the installed recovery branches.
                processor.AddLog("[自动启动] 游戏已运行，直接执行当前任务；广告、公告或返回按钮交由异常恢复处理。",
                    changeColor: false);
            }
            if (Cancelled(context, processor)) return false;
            MarkGamePrepared(processor);
            return true;
        }
        catch (Exception ex)
        {
            if (!Cancelled(context, processor)) processor.AddLog("[自动启动] " + ex.Message, "OrangeRed");
            return false;
        }
    }

    private static void MarkGamePrepared(MaaProcessor processor)
    {
        if (processor.CancellationTokenSource != null)
            AppDomain.CurrentDomain.SetData("TKFMaa.GamePrepared." + processor.InstanceId, processor.CancellationTokenSource);
    }

    private static string AdbOutput(string adb, string serial, string shell, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(adb)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-s", serial, "shell", shell }) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 ADB 状态查询"))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            if (Task.WhenAny(exit, Task.Delay(10000, cancellation)).GetAwaiter().GetResult() != exit)
            {
                try { process.Kill(); } catch { }
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException("ADB 状态查询超时");
            }
            exit.GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException("ADB 状态查询失败：" + error.GetAwaiter().GetResult().Trim());
            return output.GetAwaiter().GetResult();
        }
    }

    private static bool FinishFailure(MaaProcessor processor, string reason, bool continueAfterFailure)
    {
        processor.AddLog("[异常恢复] " + reason
            + (continueAfterFailure ? " 当前步骤记为失败，继续下一步骤。" : " 停止全部任务。"), "OrangeRed");
        if (!continueAfterFailure)
        {
            // Clear synchronously before returning failure, including on the first run of an old config
            // whose frontend task objects may already have captured ContinueOnError=true.
            processor.TaskQueue.Clear();
        }
        return false; // Never turn a skipped or failed module into a successful result.
    }
}

// Retire saved startup rows and lift the old nested quota into the task's direct option.
// Run only when idle; selecting a resource invalidates the frontend's native tasker.
internal sealed class TkfmGameOptionsMigration : IDisposable
{
    private const string MigrationKey = "TkfmGameOptionsV2";
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<MaaProcessor, JArray> snapshots = new Dictionary<MaaProcessor, JArray>();

    internal TkfmGameOptionsMigration()
    {
        foreach (var processor in MaaProcessor.Processors.ToArray())
            snapshots[processor] = (JArray)processor.InstanceConfiguration.GetValue("TaskItems", new JArray()).DeepClone();
        timer.Tick += Tick;
        timer.Start();
    }

    private void Tick(object sender, EventArgs args)
    {
        foreach (var processor in MaaProcessor.Processors.ToArray())
        {
            if (processor.IsClosed || processor.IsConnecting || processor.IsTaskRunActive
                || processor.ViewModel == null || processor.ViewModel.IsRunning || processor.TaskQueue.Count > 0) continue;
            if (processor.InstanceConfiguration.GetValue(MigrationKey, false)) continue;
            try
            {
                var tasks = snapshots.TryGetValue(processor, out var saved) ? saved
                    : processor.InstanceConfiguration.GetValue("TaskItems", new JArray());
                var startup = tasks.OfType<JObject>().FirstOrDefault(t => (string)t["entry"] == "GameLoginStart");
                var server = (startup?["option"] as JArray)?.OfType<JObject>()
                    .FirstOrDefault(o => (string)o["name"] == "选择服务器");
                // Once a new resource is selected explicitly, never replace it with a legacy index.
                string resource = processor.ViewModel.CurrentResource;
                if (resource == "官服" && (int?)server?["index"] == 1)
                {
                    processor.InstanceConfiguration.SetValue(MigrationKey, true);
                    processor.ViewModel.CurrentResource = "erolab服务器";
                    // Finish row migration after resource reload has replaced the task objects.
                    processor.InstanceConfiguration.SetValue(MigrationKey, false);
                    snapshots[processor] = tasks;
                    continue;
                }
                var rows = processor.ViewModel.TaskItemViewModels;
                foreach (var row in rows.Where(t => t.InterfaceItem?.Entry == "GameLoginStart").ToArray()) rows.Remove(row);
                int recruitmentIndex = 0;
                var oldRecruitments = tasks.OfType<JObject>().Where(t => (string)t["entry"] == "DailyRecruitmentStart").ToArray();
                foreach (var row in rows.Where(t => t.InterfaceItem?.Entry == "DailyRecruitmentStart"))
                {
                    var previous = oldRecruitments.ElementAtOrDefault(recruitmentIndex++);
                    var oldCount = previous?.Descendants().OfType<JObject>()
                        .FirstOrDefault(o => (string)o["name"] == "全境征才循环次数");
                    var options = row.InterfaceItem.Option ??= new List<MaaInterface.MaaInterfaceSelectOption>();
                    options.RemoveAll(o => o.Name == "全境征才立即招募");
                    var count = options.FirstOrDefault(o => o.Name == "全境征才循环次数");
                    if (count == null)
                    {
                        count = new MaaInterface.MaaInterfaceSelectOption { Name = "全境征才循环次数" };
                        options.Add(count);
                    }
                    count.Data ??= new Dictionary<string, string>();
                    string value = (string)oldCount?["data"]?["次数"];
                    if (int.TryParse(value, out var total) && total >= 1 && total <= 100) count.Data["次数"] = value;
                    else if (!count.Data.ContainsKey("次数")) count.Data["次数"] = "4";
                    // Old task-level overrides must not resurrect either retired switch.
                    row.InterfaceItem.PipelineOverride?.Remove("DailyRecruitmentAfterSlots");
                    row.InterfaceItem.PipelineOverride?.Remove("DailyRecruitmentCollectResults");
                    row.InterfaceItem.PipelineOverride?.Remove("DailyRecruitmentCycleGate");
                }
                processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems,
                    rows.Where(t => !t.IsResourceOptionItem).Select(t => t.InterfaceItem).ToList());
                var current = processor.InstanceConfiguration.GetValue("CurrentTasks", new List<string>());
                current.RemoveAll(t => t.EndsWith("<|||>GameLoginStart", StringComparison.Ordinal));
                processor.InstanceConfiguration.SetValue("CurrentTasks", current);
                processor.InstanceConfiguration.SetValue(MigrationKey, true);
                snapshots.Remove(processor);
            }
            catch (Exception ex) { LoggerHelper.Warning("TKFMaa 配置迁移暂未完成：" + ex.Message); }
        }
    }

    public void Dispose() { timer.Stop(); timer.Tick -= Tick; snapshots.Clear(); }
}
