using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MaaFramework.Binding.Notification;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.UserControls.Dashboard;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Loaded by MFAAvalonia's existing custom-action compiler. Never runs a game action.
public sealed class TkfmDetailedLog : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmDetailedLog";
    public TkfmDetailedLog()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.DetailedLog.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            var service = new DetailedLogService();
            AppDomain.CurrentDomain.SetData(key, service);
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class DetailedLogService : IDisposable
{
    private const string Setting = "TkfmDetailedLogEnabled";
    private const int MaxVisibleLogs = 100;
    private readonly Dictionary<MaaProcessor, InstanceLog> instances = new Dictionary<MaaProcessor, InstanceLog>();
    private readonly ConcurrentQueue<LogEvent> queue = new ConcurrentQueue<LogEvent>();
    private readonly object drainLock = new object();
    private readonly DispatcherTimer uiTimer;
    private readonly Timer writerTimer;
    private volatile bool disposed;

    internal DetailedLogService()
    {
        uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        uiTimer.Tick += Refresh;
        uiTimer.Start();
        writerTimer = new Timer(_ => Drain(), null, 250, 250);
        Refresh(null, EventArgs.Empty);
        LoggerHelper.Info("TKFMaa 详细日志扩展已初始化；等待日志卡及任务连接。");
    }

    internal void SetEnabled(MaaProcessor processor, bool enabled)
    {
        if (disposed) return;
        var state = EnsureState(processor);
        state.Enabled = enabled;
        processor.InstanceConfiguration.SetValue(Setting, enabled);
        state.ShowDetails();
        queue.Enqueue(new LogEvent(state, "DetailedLog.Mode", new JObject
        {
            ["enabled"] = enabled
        }.ToString(Formatting.None)));
        processor.AddLog(enabled
            ? "详细模式已开启：显示最近100条记录；完整详细记录保存于 logs/detailed/。"
            : "详细模式已关闭：显示最近100条普通日志，完整详细记录继续保存于 logs/detailed/。", changeColor: false);
    }

    private InstanceLog EnsureState(MaaProcessor processor)
    {
        if (!instances.TryGetValue(processor, out var state))
        {
            state = new InstanceLog(processor);
            instances.Add(processor, state);
        }
        var current = processor.MaaTasker;
        if (!ReferenceEquals(state.Tasker, current))
        {
            state.DetachTasker();
            state.Tasker = current;
            var captured = state;
            state.Handler = (_, e) =>
            {
                // No native queries, file IO, parsing or UI calls inside a native callback.
                if (!disposed)
                    queue.Enqueue(new LogEvent(captured, e.Message, e.Details));
            };
            if (current != null) current.Callback += state.Handler;
        }
        return state;
    }

    private void Refresh(object sender, EventArgs args)
    {
        if (disposed) return;
        try
        {
            foreach (var processor in MaaProcessor.Processors.ToArray())
            {
                var state = EnsureState(processor);
                bool configured = processor.InstanceConfiguration.GetValue(Setting, false);
                if (state.Enabled != configured)
                {
                    state.Enabled = configured;
                    state.ShowDetails();
                }
            }
            foreach (var removed in instances.Keys.Where(p => !MaaProcessor.Processors.Contains(p)).ToArray())
            {
                instances[removed].Dispose();
                instances.Remove(removed);
            }
            foreach (var state in instances.Values) state.FlushDisplay();
            var root = Instances.TopLevel;
            if (root == null) return;
            foreach (var view in root.GetVisualDescendants().OfType<TaskQueueView>().ToArray())
            {
                var vm = view.DataContext as TaskQueueViewModel;
                if (vm == null || !instances.TryGetValue(vm.Processor, out var state)) continue;
                var card = view.FindControl<DashboardCard>("LogCard");
                var header = card?.HeaderActions as StackPanel;
                if (card == null || header == null || !state.AttachCard(card)) continue;
                state.ShowDetails();
                // The switch now lives in the instance's developer settings. Remove the
                // previous title-bar toggle when a reloaded extension finds an old layout.
                var existing = header.Children.OfType<ToggleButton>().FirstOrDefault(b => b.Name == "TkfmDetailedLogToggle");
                if (existing != null) header.Children.Remove(existing);
            }
        }
        catch (Exception ex) { LoggerHelper.Error("TKFMaa 详细日志界面加载失败：" + ex.Message); }
    }

    private void Drain()
    {
        lock (drainLock)
        {
            // Batch file writes; capture every event independently of detailed-view visibility.
            var batches = new Dictionary<string, StringBuilder>();
            var owners = new Dictionary<string, InstanceLog>();
            int count = 0;
            while (count++ < 2000 && queue.TryDequeue(out var e))
            {
                try
                {
                    var data = JObject.Parse(string.IsNullOrWhiteSpace(e.Details) ? "{}" : e.Details);
                    var filename = Path.Combine(AppContext.BaseDirectory, "logs", "detailed",
                        e.Time.ToString("yyyyMMdd") + "-" + SafeId(e.State.Processor.InstanceId) + ".jsonl");
                    if (!batches.ContainsKey(filename)) batches[filename] = new StringBuilder();
                    owners[filename] = e.State;
                    batches[filename].AppendLine(new JObject
                    {
                        ["time"] = e.Time.ToString("O"), ["instance"] = e.State.Processor.InstanceId,
                        ["event"] = e.Message, ["data"] = data
                    }.ToString(Formatting.None));
                    if (e.Epoch == Volatile.Read(ref e.State.Epoch))
                    {
                        if (e.State.DescribeEpoch != e.Epoch) e.State.ResetDescription(e.Epoch);
                        Describe(e, data);
                    }
                }
                catch (Exception ex) { LoggerHelper.Error("TKFMaa 详细事件处理失败：" + ex.Message); }
            }
            foreach (var pair in batches)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Key));
                    File.AppendAllText(pair.Key, pair.Value.ToString(), new UTF8Encoding(false));
                    owners[pair.Key].WriteErrorReported = false;
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error("TKFMaa 详细日志保存失败：" + ex.Message);
                    var owner = owners[pair.Key];
                    if (!owner.WriteErrorReported)
                    {
                        owner.WriteErrorReported = true;
                        owner.Processor.AddLog("详细日志文件保存失败：" + ex.Message, "OrangeRed");
                    }
                }
            }
        }
    }

    private static string SafeId(string value) => new string(value.Select(c =>
        char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
    private static string Compact(JToken value) => value == null || value.Type == JTokenType.Null
        ? "未提供" : value.ToString(Formatting.None);

    private static string Recognition(JToken reco)
    {
        if (reco == null) return "识别信息未提供";
        var scores = reco.SelectTokens("$..score").Select(Compact).Distinct().Take(8).ToArray();
        // Composite recognitions contain an array rather than a detail object.
        var texts = reco.SelectTokens("$..best.text").Select(t => (string)t).Where(t => t != null).Take(4);
        var reasons = reco.SelectTokens("$..reason").Select(t => (string)t).Distinct().Take(3).Select(r => r switch
        {
            "no_targets_selected" => "未勾选商品，未扫描",
            "no_target_visible" => "已扫描，当前未命中可购买目标",
            "sold_out" => "已售罄",
            "detail_name_unreadable" => "无法读取详情复核名称",
            "already_handled" => "本轮已处理",
            _ => r
        }).ToArray();
        var targets = reco.SelectTokens("$..targets").FirstOrDefault();
        var decisions = reco.SelectTokens("$..decision_log").Select(t => (string)t)
            .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();
        return "识别=" + (string)reco["algorithm"] + " 分数=" + (scores.Length == 0 ? "未提供" : string.Join("/", scores))
            + " 区域=" + Compact(reco["box"]) + " 文字=" + string.Join("/", texts)
            + (reasons.Length == 0 ? "" : " 原因=" + string.Join("/", reasons))
            + (targets == null ? "" : " 已选商品=" + Compact(targets))
            + (decisions.Length == 0 ? "" : " 征才记录=" + string.Join(" | ", decisions));
    }

    private static void Describe(LogEvent e, JObject data)
    {
        var state = e.State;
        string task = (string)data["task_id"] ?? "未知";
        string node = (string)data["name"] ?? (string)data["entry"] ?? "未知";
        var reco = data["reco_details"] as JObject;
        var action = data["action_details"] as JObject;
        if (e.Message == "Tasker.Task.Starting") state.TaskNames[task] = node;
        int dot = e.Message.LastIndexOf('.');
        string phase = dot < 0 ? e.Message : e.Message.Substring(0, dot);
        string timingKey = task + ":" + phase + ":" + node;
        if (e.Message.EndsWith(".Starting"))
        {
            state.Starts[timingKey] = e.Time;
            return; // Raw start notifications remain in JSONL.
        }
        string elapsed = state.Starts.TryGetValue(timingKey, out var began)
            ? " 耗时=" + (e.Time - began).TotalMilliseconds.ToString("F0") + "ms" : "";
        state.Starts.Remove(timingKey);
        string prefix = "[详细 " + e.Time.ToString("HH:mm:ss.fff") + "] 任务=" + task
            + (state.TaskNames.TryGetValue(task, out var entry) ? "(" + entry + ")" : "") + elapsed + " ";
        if (e.Message.StartsWith("Node.Recognition."))
        {
            node = (string)reco?["name"] ?? node;
            bool success = e.Message.EndsWith(".Succeeded");
            if (success && state.PendingClick.TryGetValue(task, out var previous) && previous != node
                && (string)reco?["algorithm"] != "DirectHit" && reco?["box"]?.Type == JTokenType.Array)
            {
                state.Append(prefix + "点击 " + previous + " 后，后续节点=" + node + " 已命中；"
                    + Recognition(reco));
                state.PendingClick.Remove(task);
            }
            var decision = reco?.SelectTokens("$..decision_log").Select(t => (string)t)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (success && decision != null) state.RecruitPlans[task + ":" + node] = decision;
            state.Append(prefix + "节点=" + node + (success ? " 识别命中；" : " 未命中，继续等待；")
                + Recognition(reco));
        }
        else if (e.Message.StartsWith("Node.Action."))
        {
            node = (string)action?["name"] ?? node;
            string kind = (string)action?["action"] ?? "未知";
            bool success = e.Message.EndsWith(".Succeeded");
            var detail = action?["detail"];
            state.Append(prefix + "节点=" + node + " 动作=" + kind
                + " 实际坐标=" + Compact((detail as JObject)?["point"]) + " 命中区域=" + Compact(action?["box"])
                + " 动作参数=" + Compact(detail) + (success ? " 执行成功" : " 执行失败")
                + (kind == "Click" && success ? "；等待后续识别确认页面" : "")
                + (state.RecruitPlans.TryGetValue(task + ":" + node, out var plan) ? "；本次选择依据=" + plan : ""));
            if (success && kind == "Click") state.PendingClick[task] = node;
        }
        else if (e.Message.StartsWith("Node.PipelineNode."))
        {
            // Top-level name may refer to the parent entry. Use the actual recognition/action node.
            node = (string)reco?["name"] ?? (string)action?["name"] ?? node;
            if (e.Message.EndsWith(".Failed"))
            {
                state.Append(prefix + "流程失败/超时：" + node
                    + "；未确认点击后的目标页面。详情=" + Compact(data), "OrangeRed");
                state.PendingClick.Remove(task);
                return;
            }
            if (node == "DailyShopOpenDailyExchange")
                state.Append(prefix + "已识别并点击【每日兑换】，等待【交易所】确认。");
            else if (node == "DailyShopOpenExchangeTab")
                state.Append(prefix + "已识别【交易所】，商城页面流转已确认。");
        }
        else if (e.Message.StartsWith("Tasker.Task."))
        {
            state.Append(prefix + "任务状态=" + e.Message + " 入口=" + node);
            state.PendingClick.Remove(task);
            state.TaskNames.Remove(task);
            foreach (var key in state.RecruitPlans.Keys.Where(k => k.StartsWith(task + ":")).ToArray())
                state.RecruitPlans.Remove(key);
            foreach (var key in state.Starts.Keys.Where(k => k.StartsWith(task + ":")).ToArray())
                state.Starts.Remove(key);
        }
    }

    public void Dispose()
    {
        disposed = true;
        uiTimer.Stop();
        writerTimer.Dispose();
        foreach (var state in instances.Values) state.DetachTasker();
        Drain();
        foreach (var state in instances.Values) state.Dispose();
        instances.Clear();
    }

    private sealed class InstanceLog : IDisposable
    {
        internal readonly MaaProcessor Processor;
        internal volatile bool Enabled;
        internal MaaTasker Tasker;
        internal EventHandler<MaaCallbackEventArgs> Handler;
        internal readonly Dictionary<string, string> PendingClick = new Dictionary<string, string>();
        internal readonly Dictionary<string, string> TaskNames = new Dictionary<string, string>();
        internal readonly Dictionary<string, string> RecruitPlans = new Dictionary<string, string>();
        internal readonly Dictionary<string, DateTimeOffset> Starts = new Dictionary<string, DateTimeOffset>();
        internal readonly ObservableCollection<LogItemViewModel> History = new ObservableCollection<LogItemViewModel>();
        private readonly Queue<DisplayLine> display = new Queue<DisplayLine>();
        private readonly object displayLock = new object();
        internal int Epoch;
        internal int DescribeEpoch;
        internal bool WriteErrorReported;
        private DashboardCard card;
        private object originalContent;
        private ListBox logView;
        private bool updatePending;
        private bool scrollPending;
        private bool released;

        internal InstanceLog(MaaProcessor processor)
        {
            Processor = processor;
            Enabled = processor.InstanceConfiguration.GetValue(Setting, false);
            TrimOrdinary();
            foreach (var item in processor.LogItemViewModels) CopyOrdinary(item);
            processor.LogItemViewModels.CollectionChanged += OrdinaryChanged;
        }

        private void OrdinaryChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            // Do not mutate the source inside CollectionChanged: other subscribers may
            // still be processing the notification, and the frontend owns these rows.
            if (args.Action == NotifyCollectionChangedAction.Reset && Processor.LogItemViewModels.Count == 0)
            {
                Interlocked.Increment(ref Epoch);
                ClearHistory();
            }
            else if (args.NewItems != null)
                foreach (LogItemViewModel item in args.NewItems) CopyOrdinary(item);
            ScheduleUpdate();
        }

        private void CopyOrdinary(LogItemViewModel item)
        {
            // Clone rows: the frontend may dispose its original models when trimming them.
            AddHistory(new LogItemViewModel((item.ShowTime ? "[" + item.Time + "] " : "") + item.Content,
                item.Color, "Regular", "HH':'mm':'ss", showTime: false, changeColor: false)
            { BackgroundColor = item.BackgroundColor, UseMarkdown = item.UseMarkdown });
        }

        internal bool AttachCard(DashboardCard target)
        {
            if (ReferenceEquals(card, target)) return true;
            var originalItems = target.GetVisualDescendants().OfType<ItemsControl>()
                .FirstOrDefault(c => c.ItemTemplate != null);
            if (originalItems == null) return false;
            RestoreCard();
            card = target;
            originalContent = target.Content;
            // Both modes use a virtualized list with our own follow policy. The
            // original ScrollViewer's automatic scrolling cannot interrupt browsing.
            logView = new ListBox
            {
                ItemTemplate = originalItems.ItemTemplate,
                AutoScrollToSelectedItem = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Margin = new Thickness(15, 8, 10, 5)
            };
            logView.AddHandler(ScrollViewer.ScrollChangedEvent, LogScrollChanged);
            card.PointerExited += PointerLeft;
            return true;
        }

        internal void ShowDetails()
        {
            if (card == null || logView == null) return;
            System.Collections.IEnumerable source = Enabled ? (System.Collections.IEnumerable)History
                : Processor.LogItemViewModels;
            bool changed = !ReferenceEquals(logView.ItemsSource, source)
                || !ReferenceEquals(card.Content, logView);
            if (changed)
            {
                logView.ItemsSource = source;
                card.Content = logView;
                FollowLatest();
            }
        }

        private void AddHistory(LogItemViewModel item)
        {
            while (History.Count >= MaxVisibleLogs)
            {
                var oldest = History[0];
                History.RemoveAt(0);
                if ((object)oldest is IDisposable disposable) disposable.Dispose();
            }
            History.Add(item);
        }

        private void TrimOrdinary()
        {
            int excess = Processor.LogItemViewModels.Count - MaxVisibleLogs;
            // DisposableObservableCollection releases its own removed rows.
            if (excess > 0) Processor.LogItemViewModels.RemoveRange(0, excess);
        }

        private void ScheduleUpdate()
        {
            if (released || updatePending) return;
            updatePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                updatePending = false;
                if (released) return;
                TrimOrdinary();
                FollowLatest();
            });
        }

        private void PointerLeft(object sender, PointerEventArgs args) => FollowLatest();

        private void LogScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            // Wrapped text and markdown may change height after ScrollIntoView.
            // Follow the final extent too, without reacting to manual offset changes.
            if (args.ExtentDelta.Y != 0 || args.ViewportDelta.Y != 0) FollowLatest();
        }

        private void FollowLatest()
        {
            if (released || scrollPending || card == null || card.IsPointerOver || logView == null) return;
            scrollPending = true;
            // Coalesce a batch into one scroll, after the list has processed its changes.
            Dispatcher.UIThread.Post(() =>
            {
                scrollPending = false;
                if (released || card == null || card.IsPointerOver || logView == null
                    || !ReferenceEquals(card.Content, logView)) return;
                var latest = Enabled ? History.LastOrDefault() : Processor.LogItemViewModels.LastOrDefault();
                if (latest != null) logView.ScrollIntoView(latest);
                logView.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
            }, DispatcherPriority.Loaded);
        }

        internal void Append(string content, string color = "")
        {
            lock (displayLock)
            {
                if (released) return;
                while (display.Count >= MaxVisibleLogs) display.Dequeue();
                display.Enqueue(new DisplayLine(content, color, DescribeEpoch));
            }
        }

        internal void FlushDisplay()
        {
            DisplayLine[] rows;
            lock (displayLock)
            {
                rows = display.ToArray();
                display.Clear();
            }
            bool changed = false;
            foreach (var row in rows)
            {
                if (row.Epoch != Volatile.Read(ref Epoch)) continue;
                AddHistory(new LogItemViewModel(row.Content,
                    row.Color == "OrangeRed" ? Brushes.OrangeRed : Brushes.Black,
                    "Regular", "HH':'mm':'ss", showTime: false, changeColor: row.Color != "OrangeRed"));
                changed = true;
            }
            if (changed && Enabled) FollowLatest();
        }

        internal void ResetDescription(int epoch)
        {
            PendingClick.Clear();
            TaskNames.Clear();
            RecruitPlans.Clear();
            Starts.Clear();
            DescribeEpoch = epoch;
        }

        internal void DetachTasker()
        {
            if (Tasker != null && Handler != null) Tasker.Callback -= Handler;
            Tasker = null;
            Handler = null;
        }

        private void RestoreCard()
        {
            if (card != null)
            {
                card.PointerExited -= PointerLeft;
                if (ReferenceEquals(card.Content, logView)) card.Content = originalContent;
            }
            if (logView != null)
            {
                logView.RemoveHandler(ScrollViewer.ScrollChangedEvent, LogScrollChanged);
                logView.ItemsSource = null;
            }
            logView = null;
            card = null;
            originalContent = null;
        }

        private void ClearHistory()
        {
            lock (displayLock) display.Clear();
            var previous = History.ToArray();
            History.Clear();
            foreach (var item in previous)
                if ((object)item is IDisposable disposable) disposable.Dispose();
        }

        public void Dispose()
        {
            lock (displayLock) released = true;
            DetachTasker();
            Processor.LogItemViewModels.CollectionChanged -= OrdinaryChanged;
            RestoreCard();
            ClearHistory();
        }
    }

    private sealed class DisplayLine
    {
        internal readonly string Content;
        internal readonly string Color;
        internal readonly int Epoch;
        internal DisplayLine(string content, string color, int epoch)
        { Content = content; Color = color; Epoch = epoch; }
    }

    private sealed class LogEvent
    {
        internal readonly InstanceLog State;
        internal readonly string Message;
        internal readonly string Details;
        internal readonly int Epoch;
        internal readonly DateTimeOffset Time = DateTimeOffset.Now;
        internal LogEvent(InstanceLog state, string message, string details)
        { State = state; Message = message; Details = details; Epoch = Volatile.Read(ref state.Epoch); }
    }
}
