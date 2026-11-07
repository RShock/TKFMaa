using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MaaFramework.Binding.Buffers;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.UserControls.Dashboard;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Each source file is compiled separately by MFA. Keep the scheduler and its action together.
public sealed class TkfmDispatchSchedule : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmDispatchSchedule";
    private const string ServiceKey = "TKFMaa.Dispatch.Schedule.Service";
    private static readonly object ServiceLock = new object();

    public TkfmDispatchSchedule() { Service(); }

    private static DispatchClock Service()
    {
        lock (ServiceLock)
        {
            var clock = AppDomain.CurrentDomain.GetData(ServiceKey) as DispatchClock;
            if (clock != null) return clock;
            (AppDomain.CurrentDomain.GetData(ServiceKey) as IDisposable)?.Dispose();
            clock = new DispatchClock();
            AppDomain.CurrentDomain.SetData(ServiceKey, clock);
            return clock;
        }
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        MaaProcessor processor = null;
        DispatchClock clock = null;
        JObject job = null;
        bool timed = false;
        try
        {
            processor = MaaProcessor.Processors.ToArray()
                .FirstOrDefault(p => context.Tasker.Equals(p.MaaTasker));
            if (processor == null) return false;

            clock = Service();
            var mode = ParseObject(args.ActionParam, "动作参数");
            if ((string)mode["mode"] == "countdown")
                return CaptureCountdown(context, processor, clock);
            clock.ClearCountdown(processor);

            var existing = clock.Read(processor);
            timed = (string)existing?["state"] == "queued";
            JObject config;
            JObject overrides;
            if (timed)
            {
                job = existing;
                clock.Change(processor, (string)job["id"], j => j["state"] = "running");
                overrides = (JObject)job["overrides"].DeepClone();
                config = (JObject)job["config"].DeepClone();
                var login = LoginOverrides(processor);
                processor.AddLog("[定时派遣] 到点执行：重新启动游戏，识别关闭启动广告后进入派遣。", changeColor: false);
                if (!Succeeded(context, "TkfmRecoveryRestartGame", login))
                    throw new InvalidOperationException("游戏启动或登录失败，保留现场，不自动重复预约");
            }
            else
            {
                if (!context.GetNodeData("DailyDispatchScheduleConfig", out var raw))
                    throw new InvalidOperationException("缺少派遣定时配置节点");
                config = ParseObject(raw, "派遣定时配置")["attach"] as JObject ?? new JObject();
                if ((bool?)config["enabled"] != true) config["close_game"] = false;
                overrides = new JObject();
                // Automatic countdown mode always reads the current dispatch page;
                // the disabled manual clock must never decide the initial run time.
                if ((bool?)config["enabled"] == true && (bool?)config["auto_countdown"] != true)
                {
                    overrides = Snapshot(context);
                    clock.Arm(processor, config, overrides);
                    return !context.IsCancellationRequested;
                }
            }
            // The shared main-page exit uses StopTask for top-level tasks. Inside this
            // wrapper it must end the child naturally so success cleanup can still run.
            overrides["CommonStopOnMain"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray()
            };
            bool success = Succeeded(context, "DailyDispatchRunNow", overrides);
            if (success && !context.IsCancellationRequested && (bool?)config["close_game"] == true)
            {
                processor.AddLog("[派遣] 派遣成功，按选项关闭游戏。", changeColor: false);
                success = Succeeded(context, "DailyDispatchCloseGame", LoginOverrides(processor));
                if (success) AppDomain.CurrentDomain.SetData("TKFMaa.GamePrepared." + processor.InstanceId, null);
            }
            bool completed = success && !context.IsCancellationRequested;
            var countdown = clock.TakeCountdown(processor);
            if (timed)
                clock.Change(processor, (string)job["id"], j =>
                {
                    j["state"] = completed ? "succeeded" : "failed";
                    j["result"] = completed ? "派遣执行完成" : "派遣失败或被停止，保留现场";
                    if (completed && countdown != null) j["next_due"] = countdown;
                });
            else if (completed && countdown != null)
            {
                try { clock.ArmCountdown(processor, config, Snapshot(context), countdown); }
                catch (Exception ex) { processor.AddLog("[智能派遣] 本轮派遣已完成，但预约未保存：" + ex.Message, "OrangeRed"); }
            }
            return completed;
        }
        catch (Exception ex)
        {
            if (processor != null)
                processor.AddLog("[定时派遣] " + ex.Message, "OrangeRed");
            else
                LoggerHelper.Error("TKFMaa 定时派遣：" + ex);
            if (timed && job != null && clock != null)
            {
                try
                {
                    clock.Change(processor, (string)job["id"], j =>
                    {
                        j["state"] = "failed";
                        j["result"] = ex.Message;
                    });
                }
                catch (Exception updateError)
                {
                    LoggerHelper.Error("TKFMaa 定时派遣状态更新失败：" + updateError.Message);
                }
            }
            return false;
        }
    }

    private static JObject ParseObject(string raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
            return new JObject();
        try
        {
            return JObject.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(label + "不是有效 JSON 对象：" + ex.Message, ex);
        }
    }

    private static JObject Snapshot(IMaaContext context)
    {
        var snapshot = new JObject();
        foreach (var node in context.Tasker.Resource.NodeList.Where(n => n.StartsWith("DailyDispatch", StringComparison.Ordinal)
            || n.StartsWith("__DailyDispatch", StringComparison.Ordinal)))
            if (context.GetNodeData(node, out var data)) snapshot[node] = JObject.Parse(data);
        return snapshot;
    }

    private static bool CaptureCountdown(IMaaContext context, MaaProcessor processor, DispatchClock clock)
    {
        clock.ClearCountdown(processor);
        if (context.IsCancellationRequested) return false;
        if (!context.GetNodeData("DailyDispatchScheduleConfig", out var raw)
            || (bool?)JObject.Parse(raw)["attach"]?["auto_countdown"] != true) return true;
        try
        {
            using (var image = new MaaImageBuffer())
            {
                // The all-busy recognition just captured this frame. Read all slots from
                // that same normalized device image before navigation leaves the page.
                if (!context.Tasker.Controller.GetCachedImage(image))
                    throw new InvalidOperationException("无法取得派遣总览截图");
                var seconds = new List<int>();
                for (int slot = 1; slot <= 3; slot++)
                {
                    if (context.IsCancellationRequested) return false;
                    using (var result = context.RunRecognition("DailyDispatchCountdownSlot" + slot, image))
                    {
                        var detail = result == null ? new JObject() : JObject.Parse(result.Detail);
                        var candidates = new List<string>();
                        if (detail["best"]?["text"] != null) candidates.Add((string)detail["best"]["text"]);
                        else if (detail["filtered"] is JArray filtered)
                            candidates.AddRange(filtered.OfType<JObject>().Select(item => (string)item["text"] ?? ""));
                        processor.AddLog("[智能派遣] 第" + slot + "队倒计时 OCR："
                            + (candidates.Count == 0 ? "未读到" : string.Join(" / ", candidates)), changeColor: false);
                        var values = new HashSet<int>();
                        foreach (string text in candidates)
                        {
                            var match = Regex.Match(Regex.Replace(text.Replace('：', ':'), @"\s+", ""),
                                @"^(\d{2}):([0-5]\d):([0-5]\d)$");
                            if (match.Success) values.Add(int.Parse(match.Groups[1].Value) * 3600
                                + int.Parse(match.Groups[2].Value) * 60 + int.Parse(match.Groups[3].Value));
                        }
                        seconds.Add(result?.Hit == true && values.Count == 1 ? values.Single() : -1);
                    }
                }
                if (seconds.Any(value => value < 0))
                    throw new InvalidOperationException("三队倒计时未全部完整识别");
                int longest = seconds.Max();
                var due = DateTimeOffset.Now.AddSeconds(longest + 5);
                clock.RecordCountdown(processor, due.ToString("O"));
                processor.AddLog("[智能派遣] 三队倒计时均已读取，最长 " + TimeSpan.FromSeconds(longest).ToString()
                    + "；本轮成功结束后预约 " + due.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（含5秒余量）。", changeColor: false);
            }
        }
        catch (Exception ex)
        {
            processor.AddLog("[智能派遣] 未创建后续预约：" + ex.Message + "。本轮仍按原流程结束。", "OrangeRed");
        }
        return !context.IsCancellationRequested;
    }

    private static bool Succeeded(IMaaContext context, string entry, JObject overrides) =>
        !context.IsCancellationRequested
        && context.RunTask(entry, overrides.ToString(Formatting.None))?.Status == MaaJobStatus.Succeeded
        && !context.IsCancellationRequested;

    private static JObject LoginOverrides(MaaProcessor processor)
    {
        string resource = processor.ViewModel?.CurrentResource;
        string package = resource == "官服" ? "com.pinkcore.tkfm"
            : resource == "erolab服务器" ? "com.pinkcore.tkfm.erolabs"
            : throw new InvalidOperationException("资源类型对应的服务器配置无效：" + resource);
        var overrides = new JObject();
        foreach (string node in new[] { "TkfmRecoveryRestartGame", "DailyDispatchCloseGame" })
            overrides[node] = new JObject
            {
                ["action"] = new JObject { ["type"] = "StopApp", ["param"] = new JObject { ["package"] = package } }
            };
        overrides["GameLoginLaunch"] = new JObject
        {
            ["action"] = new JObject { ["type"] = "StartApp", ["param"] = new JObject { ["package"] = package } }
        };
        overrides["GameMainReady"] = new JObject
        {
            ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray()
        };
        return overrides;
    }
}

internal sealed class DispatchClock : IDisposable
{
    private const string Setting = "TkfmDispatchReservation";
    private readonly object sync = new object();
    private readonly Dictionary<string, string> countdowns = new Dictionary<string, string>();

    internal void ClearCountdown(MaaProcessor processor) { lock (sync) countdowns.Remove(processor.InstanceId); }
    internal void RecordCountdown(MaaProcessor processor, string due) { lock (sync) countdowns[processor.InstanceId] = due; }
    internal string TakeCountdown(MaaProcessor processor)
    {
        lock (sync)
        {
            countdowns.TryGetValue(processor.InstanceId, out var due);
            countdowns.Remove(processor.InstanceId);
            return due;
        }
    }

    internal void ArmCountdown(MaaProcessor processor, JObject config, JObject overrides, string due)
    {
        ArmAt(processor, config, overrides, DateTimeOffset.Parse(due), true);
    }
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> observed = new HashSet<string>();
    private readonly HashSet<string> working = new HashSet<string>();
    private readonly HashSet<string> busyLogged = new HashSet<string>();
    private readonly Dictionary<string, CancellationTokenSource> operations = new Dictionary<string, CancellationTokenSource>();
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    private bool disposed;
    private readonly Dictionary<Control, List<IDisposable>> optionBindings = new Dictionary<Control, List<IDisposable>>();

    internal DispatchClock()
    {
        Dispatcher.UIThread.Post(() => { if (!disposed) { timer.Tick += Tick; timer.Start(); } });
    }

    internal JObject Read(MaaProcessor processor)
    {
        lock (sync)
        {
            string raw = processor.InstanceConfiguration.GetValue(Setting, "");
            return string.IsNullOrWhiteSpace(raw) ? null : JObject.Parse(raw);
        }
    }

    private void Save(MaaProcessor processor, JObject job) =>
        processor.InstanceConfiguration.SetValue(Setting, job.ToString(Formatting.None));

    internal void Change(MaaProcessor processor, string id, Action<JObject> change)
    {
        lock (sync)
        {
            var current = Read(processor);
            if (current == null || (string)current["id"] != id) return;
            change(current);
            Save(processor, current);
        }
    }

    internal void Arm(MaaProcessor processor, JObject config, JObject overrides)
    {
        string text = ((string)config["run_at"] ?? "").Trim();
        DateTime due;
        if (DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            due = DateTime.Today.Add(time.TimeOfDay);
            if (due <= DateTime.Now) due = due.AddDays(1);
        }
        else if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out due)
            || due <= DateTime.Now)
            throw new InvalidOperationException("预约时间须为 HH:mm 或未来的 yyyy-MM-dd HH:mm");
        ArmAt(processor, config, overrides, new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Local)), false);
    }

    private void ArmAt(MaaProcessor processor, JObject config, JObject overrides, DateTimeOffset due, bool countdown)
    {
        var job = new JObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["session"] = session, ["state"] = "pending",
            ["due"] = due.ToString("O"), ["from_countdown"] = countdown,
            ["config"] = config.DeepClone(), ["overrides"] = overrides.DeepClone(),
            ["handled"] = false, ["wait_closed"] = false
        };
        var device = processor.InstanceConfiguration.GetValue("AdbDevice", new JObject());
        job["serial"] = (string)device["AdbSerial"] ?? "";
        job["adb"] = (string)device["AdbPath"] ?? "";
        if ((bool?)config["manage_emulator"] == true)
        {
            var metadata = JObject.Parse((string)device["Config"] ?? "{}")["extras"]?["mumu"];
            int? index = (int?)metadata?["index"];
            string path = (string)metadata?["path"];
            if ((bool?)metadata?["enable"] != true || index == null || index < 0 || string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("所选设备缺少 MuMu 安装路径或实例编号，无法安全管理模拟器");
            string manager = Path.Combine(path, "nx_main", "MuMuManager.exe");
            if (!File.Exists(manager) || !File.Exists((string)job["adb"]) || string.IsNullOrWhiteSpace((string)job["serial"]))
                throw new InvalidOperationException("MuMu 管理程序、ADB 或设备地址缺失，请重新选择 MuMu 设备");
            job["manager"] = manager;
            job["vm_index"] = index.Value;
        }
        lock (sync)
        {
            var old = Read(processor);
            if (new[] { "launching", "queued", "running" }.Contains((string)old?["state"]))
                throw new InvalidOperationException("已有定时派遣正在启动或执行，请先停止当前任务");
            if ((bool?)old?["policy_saved"] == true
                || ((string)old?["state"] == "succeeded" && (bool?)old?["handled"] != true))
                throw new InvalidOperationException("上一轮正在收尾，请稍后重新预约");
            Save(processor, job);
        }
        processor.AddLog((countdown ? "[智能派遣] 已根据最长倒计时预约：" : "[定时派遣] 已预约一次：")
            + due.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            + "（电脑本地时间）。助手须保持运行，可在日志标题栏取消预约。", changeColor: false);
    }

    private static bool Busy(MaaProcessor processor) =>
        (bool?)processor.GetType().GetProperty("IsTaskRunActive")?.GetValue(processor) == true
        || processor.ViewModel?.IsRunning == true || processor.ViewModel?.ToggleEnable == false
        || processor.MaaTasker?.IsRunning == true;

    private static bool SharedBusy(MaaProcessor owner, JObject job) => MaaProcessor.Processors.ToArray().Any(p =>
        !ReferenceEquals(p, owner) && Busy(p)
        && (string)p.InstanceConfiguration.GetValue("AdbDevice", new JObject())["AdbSerial"] == (string)job["serial"]);

    private void Tick(object sender, EventArgs args)
    {
        if (disposed) return;
        foreach (var processor in MaaProcessor.Processors.ToArray())
        {
            try
            {
                if (!Busy(processor)) MigrateCloseGameOption(processor);
                var job = Read(processor);
                if (job == null) continue;
                string id = (string)job["id"];
                if (observed.Add(processor.InstanceId + ":" + id) && (string)job["session"] != session)
                {
                    string state = (string)job["state"];
                    if (state == "pending" && DateTimeOffset.Parse((string)job["due"]) <= DateTimeOffset.Now)
                    {
                        Change(processor, id, j => { j["state"] = "missed"; j["handled"] = true; });
                        processor.AddLog("[定时派遣] 预约时间已在助手离线期间错过，已失效；请重新预约，不会补跑。", "OrangeRed");
                    }
                    else if (new[] { "launching", "queued", "running" }.Contains(state))
                    {
                        Change(processor, id, j => { j["state"] = "failed"; j["result"] = "上次执行被中断，不自动重试"; });
                        processor.AddLog("[定时派遣] 上次执行被中断，已停止预约，请检查派遣状态。", "OrangeRed");
                    }
                    Change(processor, id, j => j["session"] = session);
                    job = Read(processor);
                }
                if (working.Contains(processor.InstanceId)) continue;
                if ((string)job["state"] == "pending")
                {
                    bool due = DateTimeOffset.Parse((string)job["due"]) <= DateTimeOffset.Now;
                    bool shutdown = (bool?)job["config"]?["manage_emulator"] == true
                        && (bool?)job["config"]?["close_wait"] == true && (bool?)job["wait_closed"] != true;
                    if (!due && !shutdown) continue;
                    if (Busy(processor) || SharedBusy(processor, job))
                    {
                        if (due && busyLogged.Add(id)) processor.AddLog("[定时派遣] 已到预约时间，等待当前设备任务空闲。", changeColor: false);
                        continue;
                    }
                    working.Add(processor.InstanceId);
                    _ = due ? Launch(processor, job) : CloseWhileWaiting(processor, job);
                }
                else if (new[] { "queued", "running" }.Contains((string)job["state"]) && !Busy(processor))
                {
                    Change(processor, id, j => { j["state"] = "failed"; j["result"] = "连接失败或队列被停止，未完成派遣"; });
                    processor.AddLog("[定时派遣] 连接失败或队列被停止，预约已结束，保留现场。", "OrangeRed");
                }
                else if ((bool?)job["handled"] != true && new[] { "succeeded", "failed", "cancelled" }.Contains((string)job["state"]) && !Busy(processor))
                {
                    working.Add(processor.InstanceId);
                    _ = Finish(processor, job);
                }
            }
            catch (Exception ex) { LoggerHelper.Error("TKFMaa 派遣调度：" + ex.Message); }
        }
        InstallCancelButton();
        InstallOptionBindings();
    }

    private static void MigrateCloseGameOption(MaaProcessor processor)
    {
        bool changed = false;
        if (processor.ViewModel == null) return;
        foreach (var task in processor.ViewModel.TaskItemViewModels.Where(t => t.InterfaceItem?.Entry == "DailyDispatchStart"))
        {
            var options = task.InterfaceItem.Option;
            var legacy = options?.FirstOrDefault(o => o.Name == "派遣成功后关闭游戏");
            var scheduled = options?.FirstOrDefault(o => o.Name == "定时派遣");
            if (legacy == null || scheduled == null) continue;
            scheduled.SubOptions ??= new List<MaaInterface.MaaInterfaceSelectOption>();
            scheduled.SubOptions.RemoveAll(o => o.Name == legacy.Name);
            scheduled.SubOptions.Add(legacy);
            options.Remove(legacy);
            changed = true;
        }
        if (changed)
            processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems,
                processor.ViewModel.TaskItemViewModels.Where(t => !t.IsResourceOptionItem).Select(t => t.InterfaceItem).ToList());
    }

    private void InstallOptionBindings()
    {
        var root = Instances.TopLevel;
        if (root == null) return;
        foreach (var view in root.GetVisualDescendants().OfType<TaskQueueView>())
        {
            // The explicit input header identifies only the dispatch clock, so other
            // task inputs and the native daily scheduler keep their normal bindings.
            foreach (var title in view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "派遣预约时间").ToArray())
            {
                var header = title.Parent as StackPanel;
                var container = header?.Parent as StackPanel;
                if (container == null || optionBindings.ContainsKey(container)) continue;
                var automatic = container.GetVisualAncestors().OfType<Control>()
                    .Select(parent => parent.GetVisualDescendants().OfType<ToggleButton>()
                        .FirstOrDefault(toggle => (toggle.Tag as string) == "自动根据倒计时再后续执行派遣"))
                    .FirstOrDefault(toggle => toggle != null);
                if (automatic == null) continue;
                optionBindings[container] = new List<IDisposable>
                {
                    container.Bind(Control.IsEnabledProperty, new Binding("IsChecked")
                    {
                        Source = automatic, Converter = new FuncValueConverter<bool?, bool>(value => value != true)
                    }),
                    container.Bind(Visual.OpacityProperty, new Binding("IsChecked")
                    {
                        Source = automatic, Converter = new FuncValueConverter<bool?, double>(value => value == true ? 0.45 : 1.0)
                    })
                };
            }
        }
    }

    private async Task CloseWhileWaiting(MaaProcessor processor, JObject job)
    {
        try
        {
            processor.SetTasker();
            await Command((string)job["manager"], new[] { "control", "-v", (string)job["vm_index"], "shutdown" }, CancellationToken.None);
            Change(processor, (string)job["id"], j => j["wait_closed"] = true);
            processor.AddLog("[定时派遣] 当前队列已空闲，已关闭指定 MuMu 实例，等待预约时间。", changeColor: false);
        }
        catch (Exception ex) { Fail(processor, job, "预约后的模拟器关闭失败：" + ex.Message); }
        finally { working.Remove(processor.InstanceId); }
    }

    private async Task Launch(MaaProcessor processor, JObject job)
    {
        string id = (string)job["id"];
        var cancellation = new CancellationTokenSource();
        operations[processor.InstanceId] = cancellation;
        try
        {
            // Persist the claim before starting anything. An interrupted run must never be replayed.
            Change(processor, id, j => j["state"] = "launching");
            if ((bool?)job["config"]?["manage_emulator"] == true)
            {
                processor.AddLog("[定时派遣] 正在启动 MuMu 实例 " + (string)job["vm_index"] + "，等待原 ADB 设备就绪。", changeColor: false);
                await Command((string)job["manager"], new[] { "control", "-v", (string)job["vm_index"], "launch" }, cancellation.Token);
                await WaitForDevice(job, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (disposed || (string)Read(processor)?["id"] != id || (string)Read(processor)?["state"] != "launching") return;
            if (Busy(processor) || SharedBusy(processor, job))
                throw new InvalidOperationException("启动模拟器期间设备被其他任务占用，请重新预约");
            var tasks = processor.ViewModel?.TaskItemViewModels.Where(t => !t.IsResourceOptionItem
                && t.InterfaceItem?.Entry == "DailyDispatchStart").Take(1).ToList();
            if (tasks == null || tasks.Count != 1) throw new InvalidOperationException("当前实例缺少每日派遣任务");
            var device = processor.InstanceConfiguration.GetValue("AdbDevice", new JObject());
            if ((string)device["AdbSerial"] != (string)job["serial"])
                throw new InvalidOperationException("预约后所选 ADB 设备已改变，请重新预约");
            // Discard controller/Agent handles held before the emulator was powered off.
            processor.SetTasker();
            Change(processor, id, j =>
            {
                j["auto_detect"] = processor.InstanceConfiguration.GetValue("AutoDetectOnConnectionFailed", true);
                j["before_task"] = processor.InstanceConfiguration.GetValue("BeforeTask", "None");
                j["after_task"] = processor.InstanceConfiguration.GetValue("AfterTask", "None");
                j["dispatch_checked"] = tasks[0].IsChecked;
                j["policy_saved"] = true;
                j["state"] = "queued";
            });
            processor.InstanceConfiguration.SetValue("AutoDetectOnConnectionFailed", false);
            processor.InstanceConfiguration.SetValue("BeforeTask", "None");
            processor.InstanceConfiguration.SetValue("AfterTask", "None");
            processor.AddLog("[定时派遣] 启动独立派遣队列，使用预约时保存的地点和时长；不运行其他日常。", changeColor: false);
            // MFA versions differ in whether Start exposes ignoreCheckedState. The explicit
            // one-item queue works on both; restore the user's checkbox after this run.
            tasks[0].IsChecked = true;
            processor.Start(tasks);
        }
        catch (OperationCanceledException) { Fail(processor, job, "预约已取消或调度器已关闭"); }
        catch (Exception ex) { Fail(processor, job, ex.Message); }
        finally
        {
            operations.Remove(processor.InstanceId);
            cancellation.Dispose();
            working.Remove(processor.InstanceId);
        }
    }

    private async Task Finish(MaaProcessor processor, JObject job)
    {
        try
        {
            if ((bool?)job["policy_saved"] == true)
            {
                processor.InstanceConfiguration.SetValue("AutoDetectOnConnectionFailed", (bool?)job["auto_detect"] ?? true);
                processor.InstanceConfiguration.SetValue("BeforeTask", (string)job["before_task"] ?? "None");
                processor.InstanceConfiguration.SetValue("AfterTask", (string)job["after_task"] ?? "None");
                var item = processor.ViewModel?.TaskItemViewModels.FirstOrDefault(t => t.InterfaceItem?.Entry == "DailyDispatchStart");
                if (item != null) item.IsChecked = (bool?)job["dispatch_checked"] ?? true;
                Change(processor, (string)job["id"], j => j["policy_saved"] = false);
            }
            if ((string)job["state"] == "succeeded" && (bool?)job["config"]?["manage_emulator"] == true
                && (bool?)job["config"]?["close_emulator"] == true)
            {
                if (SharedBusy(processor, job)) return;
                processor.SetTasker();
                await Command((string)job["manager"], new[] { "control", "-v", (string)job["vm_index"], "shutdown" }, CancellationToken.None);
                processor.AddLog("[定时派遣] 派遣成功，已关闭指定 MuMu 实例。", changeColor: false);
            }
            Change(processor, (string)job["id"], j => j["handled"] = true);
            if ((string)job["state"] == "succeeded" && job["next_due"] != null)
                ArmCountdown(processor, (JObject)job["config"], (JObject)job["overrides"], (string)job["next_due"]);
            else
                processor.AddLog((string)job["state"] == "succeeded"
                    ? "[定时派遣] 本次预约已完成，未登记后续预约。"
                    : "[定时派遣] 本次预约已结束，不会自动重试。", changeColor: false);
        }
        catch (Exception ex)
        {
            Change(processor, (string)job["id"], j => j["handled"] = true);
            processor.AddLog("[定时派遣] 收尾失败：" + ex.Message + "；不重复执行派遣。", "OrangeRed");
        }
        finally { working.Remove(processor.InstanceId); }
    }

    private void Fail(MaaProcessor processor, JObject job, string reason)
    {
        if ((string)Read(processor)?["state"] == "cancelled") return;
        Change(processor, (string)job["id"], j => { j["state"] = "failed"; j["result"] = reason; });
        processor.AddLog("[定时派遣] " + reason + "。本次预约停止，不自动重试。", "OrangeRed");
    }

    private static async Task WaitForDevice(JObject job, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (((string)job["serial"]).Contains(":"))
                    await Command((string)job["adb"], new[] { "connect", (string)job["serial"] }, cancellation);
                string output = await Command((string)job["adb"], new[] { "-s", (string)job["serial"], "shell", "getprop", "sys.boot_completed" }, cancellation);
                if (output.Trim() == "1") return;
            }
            catch (InvalidOperationException) { /* The original device may still be booting. */ }
            await Task.Delay(2000, cancellation);
        }
        throw new InvalidOperationException("MuMu 启动后原 ADB 设备在三分钟内未就绪，不切换其他设备");
    }

    private static async Task<string> Command(string executable, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动管理命令"))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(15000, cancellation)) != exit)
            {
                try { process.Kill(); } catch { }
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException("管理命令超过15秒未返回");
            }
            await exit;
            if (process.ExitCode != 0) throw new InvalidOperationException("管理命令失败：" + (await error).Trim());
            return await output;
        }
    }

    private void InstallCancelButton()
    {
        var root = Instances.TopLevel;
        if (root == null) return;
        foreach (var view in root.GetVisualDescendants().OfType<TaskQueueView>())
        {
            var vm = view.DataContext as TaskQueueViewModel;
            if (vm == null) continue;
            var header = view.FindControl<DashboardCard>("LogCard")?.HeaderActions as StackPanel;
            if (header == null) continue;
            var job = Read(vm.Processor);
            var old = header.Children.OfType<Button>().FirstOrDefault(b => b.Name == "TkfmCancelDispatchReservation");
            bool visible = job != null && new[] { "pending", "launching" }.Contains((string)job["state"]);
            string caption = visible ? "取消定时派遣 " + DateTimeOffset.Parse((string)job["due"]).ToLocalTime().ToString("MM-dd HH:mm") : "";
            if (old != null && visible && ReferenceEquals(old.Tag, this) && (string)old.Content == caption) continue;
            if (old != null) header.Children.Remove(old);
            if (!visible) continue;
            var button = new Button
            {
                Name = "TkfmCancelDispatchReservation", Content = caption, Tag = this,
                Padding = new Thickness(8, 3), MinHeight = 28
            };
            button.Click += (_, __) =>
            {
                var current = Read(vm.Processor);
                if (current == null || !new[] { "pending", "launching" }.Contains((string)current["state"])) return;
                if (operations.TryGetValue(vm.Processor.InstanceId, out var operation)) operation.Cancel();
                Change(vm.Processor, (string)current["id"], j => j["state"] = "cancelled");
                vm.Processor.AddLog("[定时派遣] 已取消本次预约。", changeColor: false);
            };
            header.Children.Insert(0, button);
        }
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var operation in operations.Values) operation.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            timer.Stop(); timer.Tick -= Tick;
            foreach (var pair in optionBindings)
            {
                foreach (var binding in pair.Value) binding.Dispose();
                pair.Key.IsEnabled = true;
                pair.Key.Opacity = 1;
            }
            optionBindings.Clear();
        });
    }
}
