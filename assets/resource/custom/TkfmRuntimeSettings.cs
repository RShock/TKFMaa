using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.UserControls.Settings;

// Open configuration inputs without changing the tasker's running state or queued parameters.
public sealed class TkfmRuntimeSettings : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmRuntimeSettings";

    public TkfmRuntimeSettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.RuntimeSettings.UI";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmRuntimeSettingsUI());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmRuntimeSettingsUI : IDisposable
{
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<Control, IDisposable> enabled = new Dictionary<Control, IDisposable>();
    private readonly Dictionary<StackPanel, TextBlock> notices = new Dictionary<StackPanel, TextBlock>();
    private bool disposed;
    private bool errorReported;

    internal TkfmRuntimeSettingsUI()
    {
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    private static bool IsInput(Control control) => control is TextBox || control is ComboBox
        || control is NumericUpDown || control is AutoCompleteBox || control is ToggleButton
        || control is ToggleSwitch || control is TimePicker || control is DatePicker;

    private void Unlock(Control root, HashSet<Control> current)
    {
        foreach (var input in root.GetVisualDescendants().OfType<Control>().Where(IsInput))
        {
            // Do not override a ComboBox/TextBox template's own internal buttons or editors.
            if (input.GetVisualAncestors().TakeWhile(parent => !ReferenceEquals(parent, root))
                .OfType<Control>().Any(IsInput)) continue;
            current.Add(input);
            if (enabled.ContainsKey(input)) continue;
            // Keep the native Idle binding underneath. Disposing the override restores it.
            // Only leaf inputs are opened: parent option conditions (e.g. automatic dispatch)
            // still disable their children through Avalonia's inherited effective state.
            var lease = input.SetValue(Control.IsEnabledProperty, true, BindingPriority.Animation);
            if (lease != null) enabled[input] = lease;
        }
    }

    private void Notice(StackPanel panel, string text, bool visible, HashSet<StackPanel> current)
    {
        current.Add(panel);
        if (!notices.TryGetValue(panel, out var notice) || notice.Parent != panel)
        {
            notice = new TextBlock
            {
                Name = "TkfmRuntimeSettingsNotice", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(10, 4, 10, 10), Opacity = 0.72, FontSize = 12
            };
            panel.Children.Insert(0, notice);
            notices[panel] = notice;
        }
        if (notice.Text != text) notice.Text = text;
        notice.IsVisible = visible;
    }

    private void Refresh(object sender, EventArgs args)
    {
        if (disposed || Instances.TopLevel == null) return;
        try
        {
            var current = new HashSet<Control>();
            var currentPanels = new HashSet<StackPanel>();
            foreach (var view in Instances.TopLevel.GetVisualDescendants().OfType<TaskQueueView>())
            {
                if (!(view.DataContext is TaskQueueViewModel vm) || vm.Processor.IsClosed
                    || vm.Processor.IsConnecting) continue;
                foreach (string name in new[] { "CommonOptionSettings", "AdvancedOptionSettings" })
                {
                    var panel = view.FindControl<StackPanel>(name);
                    if (panel == null) continue;
                    Unlock(panel, current);
                    Notice(panel, "运行中可以修改。任务选项在启动队列时已加载，修改后下次启动队列生效。",
                        vm.IsRunning || vm.Processor.IsTaskRunActive, currentPanels);
                }
            }
            foreach (var view in Instances.TopLevel.GetVisualDescendants().OfType<GameSettingsUserControl>())
            {
                var processor = MaaProcessorManager.Instance.Current;
                if (processor == null || processor.IsClosed || processor.IsConnecting) continue;
                Unlock(view, current);
                if (view.Content is StackPanel panel)
                    Notice(panel, "可在运行中保存设置。故障处理方式在每个步骤开始时读取；当前步骤沿用开始时的设置。",
                        true, currentPanels);
            }
            foreach (var item in enabled.Where(item => !current.Contains(item.Key)).ToArray())
            {
                item.Value.Dispose();
                enabled.Remove(item.Key);
            }
            foreach (var item in notices.Where(item => !currentPanels.Contains(item.Key)).ToArray())
            {
                item.Key.Children.Remove(item.Value);
                notices.Remove(item.Key);
            }
        }
        catch (Exception ex)
        {
            if (errorReported) return;
            errorReported = true;
            LoggerHelper.Warning("TKFMaa 运行中配置编辑未完整加载：" + ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Refresh;
        foreach (var lease in enabled.Values) lease.Dispose();
        enabled.Clear();
        foreach (var item in notices) item.Key.Children.Remove(item.Value);
        notices.Clear();
    }
}
