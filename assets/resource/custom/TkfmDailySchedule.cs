using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;

// UI only. Actual scheduling, persistence and startup use MFA's native TimerModel.
public sealed class TkfmDailySchedule : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmDailySchedule";

    public TkfmDailySchedule()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.DailySchedule.UI";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmDailyScheduleUI());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmDailyScheduleUI : IDisposable
{
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Button> buttons = new List<Button>();
    private readonly Dictionary<string, Window> editors = new Dictionary<string, Window>();
    private bool disposed;

    internal TkfmDailyScheduleUI()
    {
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    private void Refresh(object sender, EventArgs args)
    {
        if (disposed || Instances.TopLevel == null) return;
        foreach (var view in Instances.TopLevel.GetVisualDescendants().OfType<TaskQueueView>())
        {
            if (!(view.DataContext is TaskQueueViewModel vm)) continue;
            foreach (string name in new[] { "TaskToggleBorder", "TaskToggleBorder1" })
            {
                var start = view.FindControl<Border>(name);
                if (start == null) continue;
                StackPanel parent = start.Parent as StackPanel;
                if (parent == null && start.Parent is Grid grid)
                {
                    parent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    Grid.SetColumn(parent, Grid.GetColumn(start));
                    Grid.SetRow(parent, Grid.GetRow(start));
                    Grid.SetColumnSpan(parent, Grid.GetColumnSpan(start));
                    Grid.SetRowSpan(parent, Grid.GetRowSpan(start));
                    grid.Children.Remove(start);
                    parent.Children.Add(start);
                    grid.Children.Add(parent);
                }
                if (parent == null) continue;
                var old = parent.Children.OfType<Button>().FirstOrDefault(b => b.Name == "TkfmDailyScheduleButton");
                if (old != null && ReferenceEquals(old.Tag, vm)) continue;
                if (old != null) parent.Children.Remove(old);
                var button = new Button
                {
                    Name = "TkfmDailyScheduleButton", Content = "定时任务", Tag = vm,
                    Height = 30, Padding = new Thickness(9, 2), Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(button, "设置当前配置每天自动运行的时间；执行当时勾选的任务和选项。");
                button.Click += (_, __) => Open(vm, TopLevel.GetTopLevel(view) as Window);
                parent.Children.Insert(parent.Children.IndexOf(start) + 1, button);
                buttons.Add(button);
            }
        }
    }

    private void Open(TaskQueueViewModel vm, Window owner)
    {
        string id = vm.Processor.InstanceId;
        if (editors.TryGetValue(id, out var existing)) { existing.Activate(); return; }
        var editor = new TkfmDailyScheduleEditor(vm);
        editors[id] = editor;
        editor.Closed += (_, __) => editors.Remove(id);
        if (owner == null) editor.Show();
        else _ = editor.ShowDialog(owner);
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        timer.Tick -= Refresh;
        foreach (var editor in editors.Values.ToArray()) editor.Close();
        foreach (var button in buttons)
            if (button.Parent is Panel parent) parent.Children.Remove(button);
        buttons.Clear();
    }
}

internal sealed class TkfmDailyScheduleEditor : Window
{
    private readonly TaskQueueViewModel vm;
    private readonly CheckBox enabled = new CheckBox { Content = "每天定时执行当前配置" };
    private readonly TextBox times = new TextBox
    {
        AcceptsReturn = true, MinHeight = 110, Watermark = "09:00\n21:30", MaxLength = 200
    };
    private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };

    internal TkfmDailyScheduleEditor(TaskQueueViewModel viewModel)
    {
        vm = viewModel;
        Title = "定时任务 · " + vm.Processor.InstanceId;
        Width = 540;
        Height = 530;
        MinWidth = 440;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var model = TimerModel.Instance;
        model.RefreshInstanceList();
        var active = Matching(model).Where(t => t.IsOn).Select(t => t.Time.ToString(@"hh\:mm")).Distinct().ToArray();
        enabled.IsChecked = active.Length > 0;
        times.Text = active.Length > 0 ? string.Join(Environment.NewLine, active)
            : vm.Processor.InstanceConfiguration.GetValue("TkfmDailyRunTimes", "09:00");
        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, __) => Save();
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = vm.Processor.InstanceId, FontSize = 18, FontWeight = FontWeight.Bold });
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock
        {
            Text = "每天执行时间（电脑本地时间，HH:mm，每行一个，也可用逗号分隔）",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(times);
        panel.Children.Add(new TextBlock
        {
            Text = "到点执行本配置当时勾选的全部任务及选项。助手须运行、电脑须唤醒；离线错过的时间不补跑。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "复用前端原生定时器。启用后按各定时器选定的配置执行，关闭“全局启动全部配置”和“强制定时启动”；本配置正在运行时跳过当次触发。其他配置已有时间点保留。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(status);
        panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel };
    }

    private IEnumerable<TimerModel.TimerProperties> Matching(TimerModel model) => model.Timers.Where(t =>
        t.TimerConfig == vm.Processor.InstanceId && t.TimerAction == TimerActionType.StartTask
        && t.ScheduleConfig.ScheduleType == TimerScheduleType.Daily);

    private void Save()
    {
        var parsed = new SortedSet<TimeSpan>();
        foreach (string raw in (times.Text ?? "").Split(new[] { '\r', '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string text = raw.Trim();
            if (text.Length == 0) continue;
            if (!DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                status.Text = "时间格式不正确：" + text + "。请填写 09:00 这样的 HH:mm。";
                return;
            }
            parsed.Add(value.TimeOfDay);
        }
        bool turnOn = enabled.IsChecked == true;
        if (turnOn && parsed.Count == 0) { status.Text = "请至少填写一个每天执行的时间。"; return; }
        try
        {
            var model = TimerModel.Instance;
            model.RefreshInstanceList();
            var slots = Matching(model).ToList();
            var triggered = slots.Where(t => t.LastTriggered.HasValue).GroupBy(t => t.Time)
                .ToDictionary(group => group.Key, group => group.Max(t => t.LastTriggered.Value));
            // Disable before editing so the minute timer cannot trigger a half-edited slot.
            foreach (var timer in slots) timer.IsOn = false;
            if (turnOn)
            {
                // Preserve existing explicit targets. Native global-start and forced-start
                // modes override per-instance selection and must be off for this editor.
                model.CustomConfig = true;
                model.ForceScheduledStart = false;
                GlobalConfiguration.SetValue(ConfigurationKeys.GlobalStartEnabled, bool.FalseString);
                int index = 0;
                foreach (var time in parsed)
                {
                    TimerModel.TimerProperties timer;
                    if (index < slots.Count) timer = slots[index];
                    else
                    {
                        model.AddTimer();
                        timer = model.Timers.Last();
                        slots.Add(timer);
                    }
                    timer.IsOn = false;
                    timer.TimerConfig = vm.Processor.InstanceId;
                    timer.TimerAction = TimerActionType.StartTask;
                    timer.ScheduleConfig = new TimerScheduleConfig { ScheduleType = TimerScheduleType.Daily };
                    timer.Time = time;
                    // Preserve de-duplication by time, including when times change order.
                    timer.LastTriggered = triggered.TryGetValue(time, out var last) ? last : (DateTime?)null;
                    timer.IsOn = true;
                    index++;
                }
            }
            string text = string.Join(Environment.NewLine, parsed.Select(t => t.ToString(@"hh\:mm")));
            vm.Processor.InstanceConfiguration.SetValue("TkfmDailyRunTimes", text);
            vm.Processor.AddLog(turnOn ? "[定时任务] 已为配置“" + vm.Processor.InstanceId + "”设置每天 "
                + text.Replace(Environment.NewLine, "、") + " 自动运行。" : "[定时任务] 已关闭本配置的每日自动运行。", changeColor: false);
            Close();
        }
        catch (Exception ex) { status.Text = "定时设置未完整保存，请检查原生定时设置：" + ex.Message; }
    }
}
