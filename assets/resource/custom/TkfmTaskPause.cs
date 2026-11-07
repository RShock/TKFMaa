using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;

// UI and callback gate only. MaaFramework has no native tasker pause API; blocking
// the synchronous Node.*.Starting callback freezes the pipeline at a recoverable boundary.
public sealed class TkfmTaskPause : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmTaskPause";

    public TkfmTaskPause()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.TaskPause.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmTaskPauseService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmTaskPauseService : IDisposable
{
    private readonly ConcurrentDictionary<MaaProcessor, PauseState> states =
        new ConcurrentDictionary<MaaProcessor, PauseState>();
    private readonly List<Button> buttons = new List<Button>();
    private readonly Dictionary<Button, PauseState> stopButtons = new Dictionary<Button, PauseState>();
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
    private bool disposed;

    internal TkfmTaskPauseService()
    {
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (disposed) return;
        try
        {
            foreach (var processor in MaaProcessor.Processors.ToArray())
            {
                var state = EnsureState(processor);
                state.Attach(processor.MaaTasker);
                if (processor.ViewModel?.IsRunning != true || processor.MaaTasker?.IsStopping == true)
                    state.Resume(false);
            }

            foreach (var removed in states.Keys.Where(p => !MaaProcessor.Processors.Contains(p)).ToArray())
            {
                states[removed].Dispose();
                states.TryRemove(removed, out _);
            }

            var root = Instances.TopLevel;
            if (root != null)
            {
                foreach (var view in root.GetVisualDescendants().OfType<TaskQueueView>().ToArray())
                {
                    if (!(view.DataContext is TaskQueueViewModel vm)
                        || !states.TryGetValue(vm.Processor, out var state))
                        continue;
                    EnsureButtons(view, state);
                }
            }

            foreach (var button in buttons.ToArray())
            {
                if (!(button.Tag is PauseState state) || !states.ContainsKey(state.Processor)) continue;
                UpdateButton(button, state);
            }

            foreach (var button in stopButtons.Keys.Where(button => !button.IsAttachedToVisualTree()).ToArray())
            {
                button.RemoveHandler(InputElement.PointerPressedEvent, stopButtons[button].ReleaseForStop);
                stopButtons.Remove(button);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("TKFMaa 暂停任务扩展刷新失败：" + ex.Message);
        }
    }

    internal bool PauseForFailure(MaaProcessor processor, string reason)
    {
        if (disposed || !states.TryGetValue(processor, out var state)) return false;
        state.Attach(processor.MaaTasker);
        return state.PauseForFailure(reason);
    }

    private PauseState EnsureState(MaaProcessor processor) =>
        states.GetOrAdd(processor, candidate => new PauseState(candidate));

    private void EnsureButtons(TaskQueueView view, PauseState state)
    {
        foreach (var borderName in new[] { "TaskToggleBorder", "TaskToggleBorder1" })
        {
            var toggle = view.FindControl<Border>(borderName);
            if (toggle == null) continue;
            var parent = EnsureHorizontalParent(toggle);
            if (parent != null)
                EnsureButton(parent, parent.Children.IndexOf(toggle), state, 30);
        }

        foreach (var toggle in view.GetVisualDescendants().OfType<Button>()
                     .Where(button => button.Classes.Contains("PlayStopButton")).ToArray())
        {
            AttachStopRelease(toggle, state);
            if (!(toggle.Parent is StackPanel parent)) continue;
            int index = parent.Children.IndexOf(toggle);
            EnsureButton(parent, index, state, 28);
        }
    }

    private void AttachStopRelease(Button button, PauseState state)
    {
        if (stopButtons.TryGetValue(button, out var existing))
        {
            if (ReferenceEquals(existing, state)) return;
            button.RemoveHandler(InputElement.PointerPressedEvent, existing.ReleaseForStop);
            stopButtons.Remove(button);
        }

        // Tunnel before the button command so a paused callback cannot keep native StopTask blocked.
        button.AddHandler(InputElement.PointerPressedEvent, state.ReleaseForStop,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        stopButtons.Add(button, state);
    }

    private static StackPanel? EnsureHorizontalParent(Control child)
    {
        if (child.Parent is StackPanel panel) return panel;
        if (!(child.Parent is Grid grid)) return null;

        panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Grid.SetColumn(panel, Grid.GetColumn(child));
        Grid.SetRow(panel, Grid.GetRow(child));
        Grid.SetColumnSpan(panel, Grid.GetColumnSpan(child));
        Grid.SetRowSpan(panel, Grid.GetRowSpan(child));
        grid.Children.Remove(child);
        panel.Children.Add(child);
        grid.Children.Add(panel);
        return panel;
    }

    private void EnsureButton(StackPanel parent, int index, PauseState state, double height)
    {
        var existing = parent.Children.OfType<Button>().FirstOrDefault(button => button.Name == "TkfmTaskPauseButton");
        if (existing != null && ReferenceEquals(existing.Tag, state)) return;
        if (existing != null)
        {
            buttons.Remove(existing);
            parent.Children.Remove(existing);
        }

        var button = new Button
        {
            Name = "TkfmTaskPauseButton",
            Tag = state,
            Height = height,
            MinWidth = 78,
            Padding = new Thickness(9, 0),
            Margin = new Thickness(0, 0, 5, 0),
            FontSize = 12,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, __) => Toggle(state);
        parent.Children.Insert(Math.Max(0, Math.Min(index, parent.Children.Count)), button);
        buttons.Add(button);
        UpdateButton(button, state);
    }

    private static void Toggle(PauseState state)
    {
        if (state.ViewModel?.IsRunning != true) return;
        if (state.IsPaused) state.Resume(true);
        else state.Pause();
    }

    private static void UpdateButton(Button button, PauseState state)
    {
        bool running = state.ViewModel?.IsRunning == true;
        button.IsVisible = running;
        button.IsEnabled = running;
        button.Content = state.IsPaused ? "继续任务" : "暂停任务";
        ToolTip.SetTip(button, state.IsPaused
            ? "解除节点边界闸门，从当前状态机继续执行。"
            : "在下一个识别、动作或等待节点开始前暂停，不停止、不重启当前任务。");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Refresh;
        foreach (var state in states.Values) state.Dispose();
        states.Clear();
        foreach (var button in buttons)
            if (button.Parent is Panel parent) parent.Children.Remove(button);
        buttons.Clear();
        foreach (var pair in stopButtons)
            pair.Key.RemoveHandler(InputElement.PointerPressedEvent, pair.Value.ReleaseForStop);
        stopButtons.Clear();
    }

    private sealed class PauseState : IDisposable
    {
        internal readonly MaaProcessor Processor;
        internal readonly TaskQueueViewModel? ViewModel;
        internal MaaTasker? Tasker;
        private EventHandler<MaaCallbackEventArgs>? handler;
        private readonly ManualResetEventSlim gate = new ManualResetEventSlim(true);
        private int paused;

        internal PauseState(MaaProcessor processor)
        {
            Processor = processor;
            ViewModel = processor.ViewModel;
        }

        internal bool IsPaused => Volatile.Read(ref paused) != 0;

        internal void Attach(MaaTasker? tasker)
        {
            if (ReferenceEquals(Tasker, tasker)) return;
            Detach();
            Tasker = tasker;
            if (tasker == null) return;
            handler = OnCallback;
            tasker.Callback += handler;
        }

        internal void Pause()
        {
            if (IsPaused || ViewModel?.IsRunning != true) return;
            gate.Reset();
            Volatile.Write(ref paused, 1);
            Processor.AddLog("[暂停任务] 已冻结在下一个识别、动作或等待节点开始前；不停止任务，不重启游戏。", changeColor: false);
        }

        internal bool PauseForFailure(string reason)
        {
            if (ViewModel?.IsRunning != true || Processor.MaaTasker?.IsStopping == true) return false;
            if (!IsPaused)
            {
                gate.Reset();
                Volatile.Write(ref paused, 1);
            }
            Processor.AddLog("[故障暂停] " + reason
                + " 已冻结在下一个节点边界；修复设备或游戏状态后点击“继续任务”。"
                + "本步骤保持失败状态。", "OrangeRed");
            try
            {
                while (IsPaused)
                {
                    if (ViewModel?.IsRunning != true || Processor.MaaTasker?.IsStopping == true
                        || Processor.CancellationTokenSource?.IsCancellationRequested == true)
                    {
                        Resume(false);
                        break;
                    }
                    gate.Wait(100);
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            return true;
        }

        internal void ReleaseForStop(object? sender, PointerPressedEventArgs args) => Resume(false);

        internal void Resume(bool log)
        {
            bool wasPaused = IsPaused;
            Volatile.Write(ref paused, 0);
            gate.Set();
            if (log && wasPaused)
                Processor.AddLog("[继续任务] 已从当前节点边界恢复执行。", changeColor: false);
        }

        private void OnCallback(object? sender, MaaCallbackEventArgs args)
        {
            if (!IsPaused || !IsPauseBoundary(args.Message)) return;
            try
            {
                gate.Wait();
            }
            catch (ObjectDisposedException)
            {
                // Service disposal releases the pipeline without propagating a task error.
            }
        }

        private static bool IsPauseBoundary(string message) =>
            message.StartsWith("Node.", StringComparison.Ordinal)
            && message.EndsWith(".Starting", StringComparison.Ordinal);

        private void Detach()
        {
            if (Tasker != null && handler != null) Tasker.Callback -= handler;
            Tasker = null;
            handler = null;
        }

        public void Dispose()
        {
            Resume(false);
            Detach();
            gate.Dispose();
        }
    }
}