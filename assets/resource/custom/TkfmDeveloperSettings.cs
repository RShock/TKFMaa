using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using MFAAvalonia.ViewModels.UsersControls.Settings;
using MFAAvalonia.Views.UserControls.Settings;

// UI only. The frontend compiles each custom file independently, so cross-service
// calls use the shared AppDomain slots and reflection rather than dynamic types.
public sealed class TkfmDeveloperSettings : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmDeveloperSettings";

    public TkfmDeveloperSettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.DeveloperSettings.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmDeveloperSettingsService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmDeveloperSettingsService : IDisposable
{
    internal const string DetailedLogSetting = "TkfmDetailedLogEnabled";
    internal const string FailureBehaviorSetting = "TkfmFailureBehavior";
    internal const string RestartBehavior = "restart";
    internal const string PauseBehavior = "pause";
    private const string LegacyRestartSetting = "TkfmRestartFailedStep";
    private const string DetailedLogServiceKey = "TKFMaa.DetailedLog.Service";

    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<MaaProcessor> initialized = new HashSet<MaaProcessor>();
    private readonly object uiOwner = new object();
    private bool disposed;

    internal TkfmDeveloperSettingsService()
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
                if (initialized.Add(processor) && !processor.InstanceConfiguration.TryGetValue<bool>(
                        ConfigurationKeys.ContinueRunningWhenError, out var unused))
                    processor.InstanceConfiguration.SetValue(ConfigurationKeys.ContinueRunningWhenError, false);
                GetFailureBehavior(processor);
            }
            ForceGithubConfiguration();

            var processorNow = MaaProcessorManager.Instance.Current;
            var root = Instances.TopLevel;
            if (processorNow == null || root == null) return;
            foreach (var view in root.GetVisualDescendants().OfType<GameSettingsUserControl>())
                Install(view, processorNow);
            foreach (var view in root.GetVisualDescendants().OfType<VersionUpdateSettingsUserControl>())
                ApplyGithubOnly(view);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("TKFMaa 开发者选单刷新失败：" + ex.Message);
        }
    }

    private static void ForceGithubConfiguration()
    {
        // This fork is maintained locally. Never let upstream updates replace it.
        foreach (string setting in new[] {
                     ConfigurationKeys.EnableCheckVersion,
                     ConfigurationKeys.EnableAutoUpdateResource,
                     ConfigurationKeys.EnableAutoUpdateMFA })
            if (ConfigurationManager.Current.GetValue(setting, true))
                ConfigurationManager.Current.SetValue(setting, false);
        if (ConfigurationManager.Current.GetValue(ConfigurationKeys.DownloadSourceIndex, 1) != 0)
            ConfigurationManager.Current.SetValue(ConfigurationKeys.DownloadSourceIndex, 0);
        string encryptedCdk = ConfigurationManager.Current.GetValue(
            ConfigurationKeys.DownloadCDK, string.Empty);
        if (!string.IsNullOrWhiteSpace(encryptedCdk))
        {
            string plainCdk;
            try { plainCdk = SimpleEncryptionHelper.Decrypt(encryptedCdk); }
            catch { plainCdk = encryptedCdk; }
            if (!string.IsNullOrEmpty(plainCdk))
                ConfigurationManager.Current.SetValue(ConfigurationKeys.DownloadCDK,
                    SimpleEncryptionHelper.Encrypt(string.Empty));
        }
    }

    private void Install(GameSettingsUserControl view, MaaProcessor processor)
    {
        if (!(view.Content is StackPanel root)) return;
        foreach (var stale in root.Children.OfType<Border>()
                     .Where(border => border.Name == "TkfmRecoverySettings").ToArray())
            root.Children.Remove(stale);

        var old = root.Children.OfType<Border>()
            .FirstOrDefault(border => border.Name == "TkfmDeveloperSettings");
        if (old != null && ReferenceEquals(old.Tag, uiOwner))
        {
            var existing = old.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(combo => combo.Name == "TkfmFailureBehavior");
            if (existing != null) existing.IsEnabled = processor.ViewModel?.IsRunning != true;
            return;
        }
        if (old != null) root.Children.Remove(old);

        // Keep the existing failure-continuation setting native and instance-local,
        // but name it precisely because it now runs after the one allowed restart.
        var nativeSwitch = root.GetVisualDescendants().OfType<ToggleSwitch>()
            .FirstOrDefault(toggle => toggle.Classes.Contains("Switch"));
        if (nativeSwitch?.Parent is DockPanel continueRow)
        {
            foreach (var caption in continueRow.GetVisualDescendants().OfType<TextBlock>()
                         .Where(text => text.Text?.Contains("任务运行失败后", StringComparison.Ordinal) == true))
                caption.Text = "重试后仍异常时继续下一步骤";
            Instances.GameSettingsUserControlModel.ContinueRunningWhenError =
                processor.InstanceConfiguration.GetValue(ConfigurationKeys.ContinueRunningWhenError, false);
        }

        var detailedLog = new ToggleSwitch
        {
            Name = "TkfmDeveloperDetailedLog",
            Content = "开发者详细日志",
            IsChecked = processor.InstanceConfiguration.GetValue(DetailedLogSetting, false)
        };
        ToolTip.SetTip(detailedLog,
            "显示最近100条普通及详细记录，完整详细记录持续保存于 logs/detailed/；鼠标停留日志区域时暂停自动滚动，移开后跟随最新记录。仅影响当前实例。");
        detailedLog.IsCheckedChanged += (_, __) =>
        {
            bool enabled = detailedLog.IsChecked == true;
            processor.InstanceConfiguration.SetValue(DetailedLogSetting, enabled);
            if (!TryInvokeService(DetailedLogServiceKey, "SetEnabled", processor, enabled))
                processor.AddLog("[开发者设置] 详细日志开关已保存，重新连接后生效。", changeColor: false);
        };

        var behavior = new ComboBox
        {
            Name = "TkfmFailureBehavior",
            ItemsSource = new[] { "重启", "暂停" },
            SelectedIndex = GetFailureBehavior(processor) == RestartBehavior ? 0 : 1,
            Width = 215,
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = processor.ViewModel?.IsRunning != true
        };
        ToolTip.SetTip(behavior,
            "重启：首次失败最多重启游戏一次并重做当前步骤。暂停：失败后冻结任务，等待人工处理后继续。");
        behavior.SelectionChanged += (_, __) =>
        {
            if (behavior.SelectedIndex < 0) return;
            string value = behavior.SelectedIndex == 0 ? RestartBehavior : PauseBehavior;
            processor.InstanceConfiguration.SetValue(FailureBehaviorSetting, value);
            // Keep the previous boolean key synchronized for configurations opened by older builds.
            processor.InstanceConfiguration.SetValue(LegacyRestartSetting, value == RestartBehavior);
            processor.AddLog("[开发者设置] 遇到故障时行为已设置为"
                + (value == RestartBehavior ? "“重启”。" : "“暂停”。"), changeColor: false);
        };

        var behaviorRow = CreateRow("遇到故障时行为", behavior);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "开发者选单",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(detailedLog);
        panel.Children.Add(behaviorRow);
        panel.Children.Add(new TextBlock
        {
            Text = "仅适用于当前实例。“重启”会消耗一次游戏重启额度；“暂停”冻结在当前节点边界，"
                + "点击“继续任务”后本步骤仍记为失败。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        root.Children.Insert(0, new Border
        {
            Name = "TkfmDeveloperSettings",
            Tag = uiOwner,
            Padding = new Thickness(25),
            Child = panel
        });
    }

    private static DockPanel CreateRow(string label, Control control)
    {
        var row = new DockPanel { MinHeight = Math.Max(36, control.MinHeight) };
        DockPanel.SetDock(control, Dock.Right);
        control.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(control);
        row.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = label, FontSize = 16, FontWeight = FontWeight.SemiBold } }
        });
        return row;
    }

    private static string GetFailureBehavior(MaaProcessor processor)
    {
        string configured = processor.InstanceConfiguration.GetValue(FailureBehaviorSetting, string.Empty);
        if (configured.Equals(RestartBehavior, StringComparison.OrdinalIgnoreCase)
            || configured.Equals(PauseBehavior, StringComparison.OrdinalIgnoreCase))
            return configured.ToLowerInvariant();

        // Migrate the old restart toggle once. Missing settings preserve the historic default.
        string migrated = processor.InstanceConfiguration.GetValue(LegacyRestartSetting, true)
            ? RestartBehavior : PauseBehavior;
        processor.InstanceConfiguration.SetValue(FailureBehaviorSetting, migrated);
        return migrated;
    }

    private static void ApplyGithubOnly(VersionUpdateSettingsUserControl view)
    {
        if (!(view.DataContext is VersionUpdateSettingsUserControlModel model)) return;
        model.EnableCheckVersion = false;
        model.EnableAutoUpdateResource = false;
        model.EnableAutoUpdateMFA = false;
        foreach (var toggle in view.GetVisualDescendants().OfType<ToggleSwitch>())
        {
            toggle.IsEnabled = false;
            ToolTip.SetTip(toggle, "本地魔改版本已关闭自动检查和自动更新，请手动管理版本。");
        }
        if (model.DownloadSourceIndex != 0) model.DownloadSourceIndex = 0;
        if (!string.IsNullOrEmpty(model.CdkPassword)) model.CdkPassword = string.Empty;

        foreach (var combo in view.GetVisualDescendants().OfType<ComboBox>().ToArray())
        {
            if (!IsDownloadSourceCombo(combo)) continue;
            combo.IsVisible = false;
            if (combo.Parent is DockPanel row)
            {
                var caption = row.Children.OfType<StackPanel>()
                    .SelectMany(panel => panel.Children.OfType<TextBlock>()).FirstOrDefault();
                if (caption != null) caption.Text = "资源下载源：GitHub";
            }
        }

        foreach (var label in view.GetVisualDescendants().OfType<TextBlock>()
                     .Where(text => text.Text == "CDK").ToArray())
        {
            var mirrorGrid = label.GetVisualAncestors().OfType<Grid>().FirstOrDefault();
            if (mirrorGrid != null) mirrorGrid.IsVisible = false;
        }
    }

    private static bool IsDownloadSourceCombo(ComboBox combo)
    {
        if (!(combo.ItemsSource is IEnumerable items)) return false;
        return items.Cast<object>().Any(item =>
            string.Equals(item.GetType().GetProperty("Name")?.GetValue(item) as string,
                "GitHub", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryInvokeService(string key, string methodName, params object[] args)
    {
        try
        {
            object? service = AppDomain.CurrentDomain.GetData(key);
            var method = service?.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (service == null || method == null) return false;
            method.Invoke(service, args);
            return true;
        }
        catch (TargetInvocationException ex)
        {
            LoggerHelper.Error("TKFMaa 开发者设置调用服务失败：" + (ex.InnerException?.Message ?? ex.Message));
            return false;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("TKFMaa 开发者设置调用服务失败：" + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Refresh;
        var root = Instances.TopLevel;
        if (root == null) return;
        foreach (var border in root.GetVisualDescendants().OfType<Border>()
                     .Where(item => item.Name == "TkfmDeveloperSettings"
                         && ReferenceEquals(item.Tag, uiOwner)).ToArray())
            (border.Parent as Panel)?.Children.Remove(border);
    }
}
