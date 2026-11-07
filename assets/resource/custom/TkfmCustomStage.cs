using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.Views.UserControls.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

// MFA compiles each custom source separately; keep this feature self-contained.
public sealed class TkfmCustomStage : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmCustomStage";
    internal const string ConfigKey = "TkfmCustomStageProfilesV1";
    private const string ServiceKey = "TKFMaa.CustomStage.UI";

    public TkfmCustomStage()
    {
        Dispatcher.UIThread.Post(() =>
        {
            (AppDomain.CurrentDomain.GetData(ServiceKey) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(ServiceKey, new CustomStageUI());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        var processor = MaaProcessor.Processors.ToArray().FirstOrDefault(p => context.Tasker.Equals(p.MaaTasker));
        if (processor == null) return false;
        try
        {
            if (context.IsCancellationRequested) return false;
            if ((string)JObject.Parse(string.IsNullOrWhiteSpace(args.ActionParam) ? "{}" : args.ActionParam)["mode"] == "direct")
                return RunDirectBattle(context, processor);
            var config = ReadConfig(processor);
            if (!context.GetNodeData("CustomStageConfig", out var raw))
                throw new InvalidOperationException("缺少自定义关卡配置节点");
            var node = JObject.Parse(raw);
            string requestedId = ((string)node["attach"]?["profile_id"] ?? "").Trim();
            string requested = ((string)node["attach"]?["profile_name"] ?? "").Trim();
            bool usedInstanceDefault = false;
            if (requestedId.Length == 0 && requested.Length == 0)
            {
                // Older/saved task instances may not carry the UI-selected profile override.
                // The custom action only runs for the custom case, so the instance's last
                // saved selection is a safe fallback and avoids failing before navigation.
                requestedId = ((string)config["selected"] ?? "").Trim();
                if (requestedId.Length == 0)
                    throw new InvalidOperationException("请在消耗体力的关卡下拉框中选择一份自定义关卡");
                usedInstanceDefault = true;
            }
            var profiles = (config["profiles"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
            var matches = requestedId.Length > 0
                ? profiles.Where(p => (string)p["id"] == requestedId).ToArray()
                : profiles.Where(p => string.Equals((string)p["name"], requested, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(requested.Length == 0
                    ? "所选自定义关卡未配置或已删除，请在消耗体力中重新选择关卡"
                    : "找不到唯一的配置“" + requested + "”，请检查配置名");
            var profile = matches[0];
            if (usedInstanceDefault)
                processor.AddLog("[自定义关卡] 当前步骤未保存关卡ID，已回退到实例最后选择的配置“"
                    + (string)profile["name"] + "”。", changeColor: false);
            string resource = ResourceRoot(processor);
            string entry = TemplatePath(resource, (string)profile["entry"]);
            string stage = TemplatePath(resource, (string)profile["stage"]);
            string page = TemplatePath(resource, (string)profile["page"]);
            int cost = (int?)profile["cost"] ?? 0;
            if (cost < 1 || cost > 999) throw new InvalidOperationException("请填写关卡每次消耗的体力（1-999）");
            var overrides = new JObject
            {
                ["DailyStaminaPresetRouteStart"] = new JObject { ["next"] = new JArray("CustomStageActivityRouter") },
                ["CustomStageEntryImage"] = ImageRecognition(entry, new JArray(0, 160, 720, 1000)),
                ["CustomStageTargetImage"] = ImageRecognition(stage, new JArray(0, 160, 720, 1000)),
                ["CustomStageListPage"] = ImageRecognition(page, new JArray(50, 60, 600, 80)),
                ["DailyStaminaReturnMainFromStageList"] = new JObject
                {
                    ["recognition"] = new JObject
                    {
                        ["type"] = "And", ["param"] = new JObject { ["all_of"] = new JArray("CustomStageTargetImage") }
                    }
                },
                // Finish this nested task naturally; StopTask would also stop the wrapper.
                ["CommonStopOnMain"] = new JObject
                {
                    ["action"] = new JObject { ["type"] = "DoNothing" }, ["next"] = new JArray()
                }
            };
            foreach (string name in new[] { "DailyStaminaPresetStaminaEnough", "DailyStaminaPresetStaminaLow" })
            {
                if (!context.GetNodeData(name, out var data)) throw new InvalidOperationException("缺少体力判断节点");
                var staminaNode = JObject.Parse(data);
                staminaNode["recognition"]["param"]["custom_recognition_param"]["cost"] = cost;
                overrides[name] = staminaNode;
            }
            processor.AddLog("[自定义关卡] 使用配置“" + (string)profile["name"]
                + "”：无状态识别出征、活动入口和关卡截图；关卡列表最多下滚6屏。", changeColor: false);
            var result = context.RunTask("CustomStageWithRecovery", overrides.ToString(Formatting.None));
            return !context.IsCancellationRequested && result?.Status == MaaJobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            processor.AddLog("[自定义关卡] " + ex.Message + "。未能完成关卡任务。", "OrangeRed");
            // Configuration errors are caught before recovery, so missing images never restart the game.
            if (!processor.InstanceConfiguration.GetValue(ConfigurationKeys.ContinueRunningWhenError, false)) processor.TaskQueue.Clear();
            return false;
        }
    }

    private static bool RunDirectBattle(IMaaContext context, MaaProcessor processor)
    {
        try
        {
            var overrides = new JObject
            {
                ["DailyStaminaTrainingResumeStart"] = new JObject { ["enabled"] = true },
                ["DailyStaminaTrainingFinishWhenLow"] = new JObject { ["enabled"] = true },
                ["DailyStaminaStageDetailInitialDecision"] = new JObject { ["next"] = new JArray("CustomStageDirectInitial") },
                ["DailyStaminaUsePotionPopup"] = new JObject
                {
                    ["next"] = new JArray("[JumpBack]CommonWaitLoading", "DailyStaminaCloseStaminaPurchasePopupAfterRefill", "CustomStageDirectInitial")
                },
                ["DailyStaminaCloseStaminaPurchasePopupAfterRefill"] = new JObject
                {
                    ["next"] = new JArray("[JumpBack]CommonWaitLoading", "CustomStageDirectInitial")
                },
                ["DailyStaminaCurrencyPurchasedState"] = new JObject
                {
                    ["next"] = new JArray("[JumpBack]CommonWaitLoading", "DailyStaminaCloseStaminaPurchasePopupAfterRefill", "CustomStageDirectInitial")
                }
            };
            foreach (string name in new[] { "DailyStaminaRefillWithPotionWhenLow", "DailyStaminaRefillWithCurrencyWhenLow" })
                overrides[name] = new JObject
                {
                    // Keep enabled and consumption limits from the task's explicit options.
                    ["recognition"] = new JObject
                    {
                        ["type"] = "And", ["param"] = new JObject
                        {
                            ["all_of"] = new JArray("DailyStaminaPresetStaminaLow", "__DailyStaminaTrainingStartButton"), ["box_index"] = 1
                        }
                    },
                    ["action"] = new JObject { ["param"] = new JObject { ["target"] = true } },
                    ["post_wait_freezes"] = new JObject { ["time"] = 300, ["target"] = new JArray(270, 1090, 180, 85), ["timeout"] = 1000 }
                };
            processor.AddLog("[自定义关卡] 已识别开始页，直接战斗；战斗后复用再次挑战和体力不足返回。", changeColor: false);
            var result = context.RunTask("CustomStageDirectInitial", overrides.ToString(Formatting.None));
            return !context.IsCancellationRequested && result?.Status == MaaJobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            processor.AddLog("[自定义关卡] 直接战斗失败：" + ex.Message, "OrangeRed");
            return false;
        }
    }

    private static JObject ImageRecognition(string path, JArray roi) => new JObject
    {
        ["enabled"] = true,
        ["recognition"] = new JObject
        {
            ["type"] = "TemplateMatch", ["param"] = new JObject
            {
                ["template"] = new JArray(path), ["roi"] = roi, ["threshold"] = 0.85
            }
        }
    };

    internal static bool Busy(MaaProcessor processor) => processor.IsClosed || processor.IsConnecting
        || processor.IsTaskRunActive || processor.ViewModel?.IsRunning == true || processor.TaskQueue.Count > 0;

    internal static JObject ReadConfig(MaaProcessor processor)
    {
        var config = (JObject)processor.InstanceConfiguration.GetValue(ConfigKey, new JObject()).DeepClone();
        if (config.HasValues && (int?)config["version"] != 1)
            throw new InvalidOperationException("配置版本不受支持，请使用对应版本的助手");
        return config;
    }

    internal static string ResourceRoot(MaaProcessor processor)
    {
        var resource = processor.ViewModel?.CurrentResources.FirstOrDefault(r => r.Name == processor.ViewModel.CurrentResource);
        var paths = resource?.ResolvedPath ?? resource?.Path;
        var path = paths?.LastOrDefault(p => File.Exists(Path.Combine(p, "custom", "TkfmCustomStage.cs")));
        if (path == null) throw new InvalidOperationException("找不到当前资源的自定义关卡目录，请先加载本资源");
        return Path.GetFullPath(path);
    }

    internal static string TemplatePath(string resource, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("入口或关卡截图尚未设置，请重新截取并保存配置");
        string folder = Path.GetFullPath(Path.Combine(resource, "image", "UserStages")) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(resource, "image", relative));
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("配置截图缺失，请重新截取并保存配置");
        using (var bitmap = SKBitmap.Decode(path))
            if (bitmap == null || bitmap.Width < 16 || bitmap.Height < 16 || bitmap.Width > 720 || bitmap.Height > 1280)
                throw new InvalidOperationException("配置截图损坏或大小无效，请重新截取");
        return relative.Replace('\\', '/');
    }

    internal static byte[] Crop(byte[] png, int x, int y, int width, int height)
    {
        using (var source = SKBitmap.Decode(png))
        using (var target = new SKBitmap())
        {
            if (source == null || !source.ExtractSubset(target, new SKRectI(x, y, x + width, y + height)))
                throw new InvalidOperationException("裁剪区域无效");
            using (var image = SKImage.FromBitmap(target))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100)) return data.ToArray();
        }
    }
}

// Only the stamina select row is extended. The native task and its other options stay in place.
internal sealed class CustomStageChoice
{
    internal string Label, ProfileId;
    internal int Index;
    internal bool Add;
    public override string ToString() => Label;
}

internal sealed class CustomStageSelectRow
{
    internal CustomStageUI Owner;
    internal MaaProcessor Processor;
    internal DragItemViewModel Task;
    internal ComboBox Combo;
    internal Button Manage;
    internal string Signature;
    internal bool Updating;
}

internal sealed class CustomStageUI : IDisposable
{
    internal const string CaseName = "__TKFM_CUSTOM_STAGE__";
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<TaskQueueViewModel, Action<DragItemViewModel, bool>> subscriptions =
        new Dictionary<TaskQueueViewModel, Action<DragItemViewModel, bool>>();
    private readonly Dictionary<TaskQueueViewModel, DragItemViewModel> active = new Dictionary<TaskQueueViewModel, DragItemViewModel>();
    private Window editor;
    private bool disposed;
    internal CustomStageUI() { timer.Tick += Tick; timer.Start(); Tick(null, EventArgs.Empty); }

    internal static void Persist(MaaProcessor processor)
    {
        processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems,
            processor.ViewModel.TaskItemViewModels.Where(t => !t.IsResourceOptionItem).Select(t => t.InterfaceItem).ToList());
    }

    private static MaaInterface.MaaInterfaceSelectOption StageOption(DragItemViewModel task) =>
        task.InterfaceItem?.Option?.FirstOrDefault(o => o.Name == "体力消耗关卡");

    private const string ProfileDataKey = "custom_stage_profile_id";

    private static string SelectedId(DragItemViewModel task)
    {
        var option = StageOption(task);
        if (option?.Data != null && option.Data.TryGetValue(ProfileDataKey, out var saved)
            && !string.IsNullOrWhiteSpace(saved)) return saved.Trim();
        if (task.InterfaceItem?.PipelineOverride == null
            || !task.InterfaceItem.PipelineOverride.TryGetValue("CustomStageConfig", out var config)) return "";
        return (string)config["attach"]?["profile_id"] ?? "";
    }

    private static void Select(MaaProcessor processor, DragItemViewModel task, CustomStageChoice choice)
    {
        var option = StageOption(task);
        if (option == null || TkfmCustomStage.Busy(processor)) return;
        option.Index = choice.Index;
        option.Data ??= new Dictionary<string, string>();
        if (string.IsNullOrEmpty(choice.ProfileId)) option.Data.Remove(ProfileDataKey);
        else option.Data[ProfileDataKey] = choice.ProfileId;
        task.InterfaceItem.PipelineOverride ??= new Dictionary<string, JToken>();
        if (string.IsNullOrEmpty(choice.ProfileId)) task.InterfaceItem.PipelineOverride.Remove("CustomStageConfig");
        else task.InterfaceItem.PipelineOverride["CustomStageConfig"] = new JObject
        {
            ["attach"] = new JObject { ["profile_id"] = choice.ProfileId }
        };
        Persist(processor);
    }

    private static void Migrate(MaaProcessor processor, int customIndex)
    {
        bool changed = false;
        foreach (var task in processor.ViewModel.TaskItemViewModels.Where(t => t.InterfaceItem?.Entry == "CustomStageStart"))
        {
            var config = TkfmCustomStage.ReadConfig(processor);
            var old = task.InterfaceItem.Option?.FirstOrDefault(o => o.Name == "自定义关卡配置名");
            string oldName = old?.Data != null && old.Data.TryGetValue("配置名", out var text) ? (text ?? "").Trim() : "";
            var profiles = (config["profiles"] as JArray ?? new JArray()).OfType<JObject>();
            var profile = oldName.Length == 0 ? profiles.FirstOrDefault(p => (string)p["id"] == (string)config["selected"])
                : profiles.FirstOrDefault(p => string.Equals((string)p["name"], oldName, StringComparison.OrdinalIgnoreCase));
            task.InterfaceItem.Entry = "DailyStaminaStart";
            task.InterfaceItem.Name = "消耗体力";
            task.Name = "消耗体力";
            task.InterfaceItem.Option ??= new List<MaaInterface.MaaInterfaceSelectOption>();
            task.InterfaceItem.Option.RemoveAll(o => o.Name == "自定义关卡配置名");
            var staminaOption = new MaaInterface.MaaInterfaceSelectOption
            {
                Name = "体力消耗关卡", Index = customIndex,
                Data = new Dictionary<string, string> { [ProfileDataKey] = (string)profile?["id"] ?? "__MISSING_PROFILE__" }
            };
            task.InterfaceItem.Option.Insert(0, staminaOption);
            task.InterfaceItem.PipelineOverride ??= new Dictionary<string, JToken>();
            task.InterfaceItem.PipelineOverride["CustomStageConfig"] = new JObject
            {
                ["attach"] = new JObject { ["profile_id"] = (string)profile?["id"] ?? "__MISSING_PROFILE__" }
            };
            changed = true;
        }
        if (changed) Persist(processor);
    }

    private static void NormalizeStaminaChoices(MaaProcessor processor, int customIndex)
    {
        // Only one built-in remains. Resolve custom selections by their stable ID,
        // never by an index from the retired 19-item activity list.
        bool changed = false;
        foreach (var task in processor.ViewModel.TaskItemViewModels.Where(t => t.InterfaceItem?.Entry == "DailyStaminaStart"))
        {
            var option = StageOption(task);
            if (option == null) continue;
            int expected = string.IsNullOrEmpty(SelectedId(task)) ? 0 : customIndex;
            if (option.Index == expected) continue;
            option.Index = expected;
            changed = true;
        }
        if (changed) Persist(processor);
    }

    private void Tick(object sender, EventArgs args)
    {
        if (disposed || Instances.TopLevel == null || MaaProcessor.Interface?.Option == null
            || !MaaProcessor.Interface.Option.TryGetValue("体力消耗关卡", out var definition) || definition.Cases == null) return;
        int customIndex = definition.Cases.FindIndex(c => c.Name == CaseName);
        if (customIndex < 0) return;
        try
        {
            // Remove the previous global placements if this source is reloaded in the same frontend.
            foreach (var old in Instances.TopLevel.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Name == "TkfmCustomStageSettings").ToArray())
                (old.Parent as Panel)?.Children.Remove(old);
            foreach (var old in Instances.TopLevel.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Name == "TkfmCustomStageSettingsCard").ToArray())
                (old.Parent as Panel)?.Children.Remove(old);

            foreach (var processor in MaaProcessor.Processors.ToArray())
            {
                var vm = processor.ViewModel;
                if (vm == null || TkfmCustomStage.Busy(processor)) continue;
                Migrate(processor, customIndex);
                NormalizeStaminaChoices(processor, customIndex);
                if (subscriptions.ContainsKey(vm)) continue;
                Action<DragItemViewModel, bool> handler = (task, visible) =>
                {
                    if (visible) active[vm] = task;
                    else if (active.TryGetValue(vm, out var selected) && ReferenceEquals(selected, task)) active.Remove(vm);
                    Dispatcher.UIThread.Post(() => Tick(null, EventArgs.Empty));
                };
                subscriptions[vm] = handler; vm.SetOptionRequested += handler;
                var opened = vm.TaskItemViewModels.LastOrDefault(t => t.EnableSetting);
                if (opened != null) active[vm] = opened;
            }
            foreach (var view in Instances.TopLevel.GetVisualDescendants().OfType<TaskQueueView>())
            {
                var vm = view.DataContext as TaskQueueViewModel;
                if (vm == null) continue;
                foreach (var combo in view.GetVisualDescendants().OfType<ComboBox>().ToArray())
                {
                    var row = combo.Tag as CustomStageSelectRow;
                    if (row != null)
                    {
                        // Existing rows belong to the task that created their panel, including hidden caches.
                        Refresh(row, definition, customIndex);
                        continue;
                    }
                    if (!combo.IsVisible || combo.GetVisualAncestors().OfType<Control>().Any(parent => !parent.IsVisible)) continue;
                    if (!ReferenceEquals(combo.ItemsSource, definition.Cases)
                        || !active.TryGetValue(vm, out var task) || task.InterfaceItem?.Entry != "DailyStaminaStart") continue;
                    if (TkfmCustomStage.Busy(vm.Processor)) continue;
                    Install(combo, vm.Processor, task, definition, customIndex);
                }
            }
        }
        catch (Exception ex) { LoggerHelper.Error("TKFMaa 消耗体力关卡选择：" + ex.Message); }
    }

    private void Install(ComboBox original, MaaProcessor processor, DragItemViewModel task,
        MaaInterface.MaaInterfaceOption definition, int customIndex)
    {
        if (!(original.Parent is Grid grid) || !(grid.Parent is StackPanel wrapper)) return;
        var combo = new ComboBox
        {
            MinWidth = original.MinWidth, Margin = original.Margin,
            HorizontalAlignment = original.HorizontalAlignment, VerticalAlignment = original.VerticalAlignment,
            MaxHeight = original.MaxHeight
        };
        foreach (var name in original.Classes) combo.Classes.Add(name);
        Grid.SetColumn(combo, Grid.GetColumn(original)); Grid.SetRow(combo, Grid.GetRow(original));
        var row = new CustomStageSelectRow { Owner = this, Processor = processor, Task = task, Combo = combo };
        combo.Tag = row;
        int index = grid.Children.IndexOf(original); grid.Children.RemoveAt(index); grid.Children.Insert(index, combo);
        var manage = new Button { Content = "管理自定义关卡", Margin = new Thickness(10, 4, 10, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        row.Manage = manage; wrapper.Children.Add(manage);
        manage.Click += (_, __) => row.Owner.Open(row, false);
        combo.SelectionChanged += (_, __) =>
        {
            if (row.Updating || row.Owner.disposed || TkfmCustomStage.Busy(processor)) return;
            var choice = combo.SelectedItem as CustomStageChoice;
            if (choice == null) return;
            if (choice.Add)
            {
                row.Signature = null; row.Owner.Refresh(row, definition, customIndex); row.Owner.Open(row, true); return;
            }
            Select(processor, task, choice); row.Signature = null;
            row.Owner.Refresh(row, definition, customIndex);
        };
        Refresh(row, definition, customIndex);
    }

    private void Refresh(CustomStageSelectRow row, MaaInterface.MaaInterfaceOption definition, int customIndex)
    {
        row.Owner = this;
        bool busy = TkfmCustomStage.Busy(row.Processor);
        row.Combo.IsEnabled = row.Manage.IsEnabled = !busy;
        if (busy) return;
        var config = TkfmCustomStage.ReadConfig(row.Processor);
        var option = StageOption(row.Task);
        if (option == null) return;
        string id = SelectedId(row.Task);
        string signature = config.ToString(Formatting.None) + "|" + option.Index + "|" + id;
        if (row.Signature == signature) return;
        var choices = definition.Cases.Select((item, index) => new CustomStageChoice
        {
            Label = string.IsNullOrEmpty(item.DisplayName) ? item.Name : item.DisplayName, Index = index
        }).Where(c => c.Index != customIndex).ToList();
        foreach (var profile in (config["profiles"] as JArray ?? new JArray()).OfType<JObject>())
            choices.Add(new CustomStageChoice
            {
                Label = (string)profile["name"] + "（自定义）", ProfileId = (string)profile["id"], Index = customIndex
            });
        CustomStageChoice selected;
        if (option.Index == customIndex)
        {
            selected = choices.FirstOrDefault(c => c.ProfileId == id && !string.IsNullOrEmpty(id));
            if (selected == null)
            {
                selected = new CustomStageChoice { Label = "自定义关卡已删除或未配置，请重新选择", ProfileId = id, Index = customIndex };
                choices.Add(selected);
            }
        }
        else selected = choices.FirstOrDefault(c => c.Index == (option.Index ?? 0) && string.IsNullOrEmpty(c.ProfileId));
        choices.Add(new CustomStageChoice { Label = "＋ 添加自定义关卡…", Add = true });
        row.Updating = true;
        try { row.Combo.ItemsSource = choices; row.Combo.SelectedItem = selected; row.Signature = signature; }
        finally { row.Updating = false; }
    }

    private async void Open(CustomStageSelectRow row, bool add)
    {
        if (disposed || TkfmCustomStage.Busy(row.Processor)) return;
        if (editor != null) { editor.Activate(); return; }
        try
        {
            var window = new CustomStageEditor(row.Processor, SelectedId(row.Task), add);
            window.Saved += id =>
            {
                if (disposed || string.IsNullOrEmpty(id) || TkfmCustomStage.Busy(row.Processor)) return;
                var cases = MaaProcessor.Interface.Option["体力消耗关卡"].Cases;
                int index = cases.FindIndex(c => c.Name == CaseName);
                if (index < 0) return;
                Select(row.Processor, row.Task, new CustomStageChoice { ProfileId = id, Index = index });
                row.Signature = null; Refresh(row, MaaProcessor.Interface.Option["体力消耗关卡"], index);
            };
            editor = window;
            await window.ShowDialog((Window)Instances.TopLevel);
        }
        catch (Exception ex) { row.Processor.AddLog("[自定义关卡] 无法打开设置：" + ex.Message, "OrangeRed"); }
        finally { editor = null; }
    }

    public void Dispose()
    {
        disposed = true; timer.Stop(); timer.Tick -= Tick;
        foreach (var pair in subscriptions) pair.Key.SetOptionRequested -= pair.Value;
        subscriptions.Clear(); active.Clear(); editor?.Close();
    }
}

internal sealed class CustomStageDraft
{
    internal JObject Data;
    internal byte[] Entry, Stage, Page;
    internal CustomStageDraft(JObject data) { Data = data; }
    public override string ToString() => (string)Data["name"] ?? "未命名";
}

internal sealed class CustomStageEditor : Window
{
    private readonly MaaProcessor processor;
    private readonly string resource;
    private readonly List<CustomStageDraft> drafts = new List<CustomStageDraft>();
    private readonly ComboBox choices = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly TextBox name = new TextBox { Watermark = "给配置取一个名字（最多64字）", MaxLength = 64 };
    private readonly TextBox cost = new TextBox { Watermark = "每次消耗体力：1-999", MaxLength = 3 };
    private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly Image entryPreview = new Image { Height = 100, Stretch = Stretch.Uniform };
    private readonly Image stagePreview = new Image { Height = 100, Stretch = Stretch.Uniform };
    private readonly StackPanel form = new StackPanel { Spacing = 12 };
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private CustomStageDraft current;
    private string confirmDelete;
    private bool loading;
    private bool closed;
    private Window capture;

    internal event Action<string> Saved;

    internal CustomStageEditor(MaaProcessor processor, string selectedId = "", bool add = false)
    {
        this.processor = processor;
        resource = TkfmCustomStage.ResourceRoot(processor);
        Title = "自定义关卡设置";
        Width = 640; Height = 790; MinWidth = 440; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var config = TkfmCustomStage.ReadConfig(processor);
        foreach (var item in (config["profiles"] as JArray ?? new JArray()).OfType<JObject>())
            drafts.Add(new CustomStageDraft((JObject)item.DeepClone()));
        form.Children.Add(new TextBlock { Text = "命名配置", FontSize = 20 });
        form.Children.Add(new TextBlock { Text = "保存后会选入当前消耗体力步骤。切换配置保留本窗口中的编辑；关闭窗口会放弃尚未保存的修改。",
            TextWrapping = TextWrapping.Wrap });
        form.Children.Add(choices);
        var tools = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        tools.Children.Add(Button("新增", () => Add(false)));
        tools.Children.Add(Button("复制", () => Add(true)));
        tools.Children.Add(Button("删除", Delete));
        form.Children.Add(tools);
        form.Children.Add(new TextBlock { Text = "配置名称" });
        form.Children.Add(name);
        form.Children.Add(new TextBlock { Text = "每次消耗体力（用于体力不足判断）" });
        form.Children.Add(cost);
        form.Children.Add(Button("设置活动入口截图", async () => await Capture(false)));
        form.Children.Add(entryPreview);
        form.Children.Add(Button("设置关卡截图", async () => await Capture(true)));
        form.Children.Add(stagePreview);
        form.Children.Add(new TextBlock
        {
            Text = "先在消耗体力的“活动页签”下拉框选择“限时活动”或“每日活动”。入口须位于所选页签，并进入纵向关卡列表；两张用户截图之间不自动处理额外分类或地图路线。请框选包含名称的稳定区域，避开倒计时、体力数值和动画。",
            TextWrapping = TextWrapping.Wrap
        });
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(20) };
        var scroll = new ScrollViewer { Content = form };
        body.Children.Add(scroll);
        Grid.SetRow(status, 1); status.Margin = new Thickness(0, 10); body.Children.Add(status);
        var save = Button("保存并使用此关卡", Save);
        Grid.SetRow(save, 2); body.Children.Add(save);
        Content = body;
        choices.SelectionChanged += (_, __) =>
        {
            if (loading) return;
            Remember(); confirmDelete = null;
            current = choices.SelectedItem as CustomStageDraft;
            Load();
        };
        timer.Tick += (_, __) =>
        {
            bool busy = TkfmCustomStage.Busy(processor);
            form.IsEnabled = !busy; save.IsEnabled = !busy;
            if (busy) status.Text = "任务正在运行或设备正在连接，暂时不能编辑或截图。";
        };
        timer.Start();
        Closed += (_, __) =>
        {
            closed = true; timer.Stop(); capture?.Close();
            SetPreview(entryPreview, null); SetPreview(stagePreview, null);
        };
        current = drafts.FirstOrDefault(d => (string)d.Data["id"] == selectedId)
            ?? drafts.FirstOrDefault(d => (string)d.Data["id"] == (string)config["selected"]) ?? drafts.FirstOrDefault();
        if (add || current == null) Add(false); else RefreshChoices();
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, __) => action();
        return button;
    }

    private void Remember()
    {
        if (current == null || loading) return;
        current.Data["name"] = (name.Text ?? "").Trim();
        current.Data["cost_text"] = cost.Text ?? "";
    }

    private void RefreshChoices()
    {
        loading = true;
        choices.ItemsSource = null;
        choices.ItemsSource = drafts.ToArray();
        choices.SelectedItem = current;
        loading = false;
        Load();
    }

    private void Load()
    {
        loading = true;
        name.Text = (string)current?.Data["name"] ?? "";
        cost.Text = (string)current?.Data["cost_text"] ?? ((int?)current?.Data["cost"] ?? 50).ToString();
        name.IsEnabled = cost.IsEnabled = current != null;
        try
        {
            SetPreview(entryPreview, ImageBytes(current, false));
            SetPreview(stagePreview, ImageBytes(current, true));
            status.Text = current == null ? "没有配置。点击新增开始设置。" : "编辑后点击“保存并使用此关卡”生效。";
        }
        catch (Exception ex) { status.Text = "截图无法读取，请重新设置：" + ex.Message; }
        finally { loading = false; }
    }

    private byte[] ImageBytes(CustomStageDraft draft, bool stage)
    {
        if (draft == null) return null;
        byte[] bytes = stage ? draft.Stage : draft.Entry;
        if (bytes != null) return bytes;
        string relative = (string)draft.Data[stage ? "stage" : "entry"];
        return string.IsNullOrWhiteSpace(relative) ? null
            : File.ReadAllBytes(Path.Combine(resource, "image", TkfmCustomStage.TemplatePath(resource, relative)));
    }

    private static void SetPreview(Image view, byte[] png)
    {
        var old = view.Source as IDisposable;
        if (png == null) view.Source = null;
        else using (var stream = new MemoryStream(png)) view.Source = new Bitmap(stream);
        old?.Dispose();
    }

    private void Add(bool copy)
    {
        if (TkfmCustomStage.Busy(processor)) return;
        Remember();
        var data = copy && current != null ? (JObject)current.Data.DeepClone() : new JObject { ["cost"] = 50 };
        string stem = copy && current != null ? ((string)current.Data["name"] ?? "配置") + " 副本" : "新配置";
        stem = stem.Substring(0, Math.Min(stem.Length, 54));
        string candidate = stem; int suffix = 2;
        while (drafts.Any(d => string.Equals((string)d.Data["name"], candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = stem + " " + suffix++;
        data["id"] = Guid.NewGuid().ToString("N"); data["name"] = candidate;
        var added = new CustomStageDraft(data);
        if (copy && current != null) { added.Entry = current.Entry; added.Stage = current.Stage; added.Page = current.Page; }
        current = added; drafts.Add(added); confirmDelete = null; RefreshChoices();
    }

    private void Delete()
    {
        if (TkfmCustomStage.Busy(processor) || current == null) return;
        if (confirmDelete != (string)current.Data["id"])
        {
            confirmDelete = (string)current.Data["id"];
            status.Text = "再次点击“删除”移除“" + current + "”。点击保存后生效；关闭窗口可放弃删除。";
            return;
        }
        drafts.Remove(current); current = drafts.FirstOrDefault(); confirmDelete = null; RefreshChoices();
    }

    private async Task Capture(bool stage)
    {
        if (closed) return;
        if (current == null)
        {
            status.Text = "请先新增并选择一份自定义关卡配置，再设置截图。";
            return;
        }
        if (TkfmCustomStage.Busy(processor))
        {
            status.Text = "任务正在运行或设备正在连接，暂时不能截图。请停止任务后重试。";
            return;
        }
        if (capture != null)
        {
            capture.Activate();
            status.Text = "截图窗口已经打开；如果被游戏窗口遮挡，请从任务栏切回助手。";
            return;
        }
        var selected = current;
        try
        {
            Remember();
            var window = new CustomStageCapture(processor, stage);
            capture = window;
            var result = await window.ShowDialog<CustomStageSelection>(this);
            if (closed || result == null || TkfmCustomStage.Busy(processor)) return;
            if (stage) { selected.Stage = result.Template; selected.Page = result.Page; }
            else selected.Entry = result.Template;
            Load(); status.Text = "已截取" + (stage ? "关卡" : "活动入口") + "，点击保存后生效。";
        }
        catch (Exception ex)
        {
            string message = "截图失败：" + ex.Message;
            if (!closed) status.Text = message;
            processor.AddLog("[自定义关卡] " + message, "OrangeRed");
        }
        finally { capture = null; }
    }

    private void Save()
    {
        if (closed || TkfmCustomStage.Busy(processor)) return;
        Remember();
        try
        {
            if (!string.Equals(resource, TkfmCustomStage.ResourceRoot(processor), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("资源已切换，请关闭设置后重新打开");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var draft in drafts)
            {
                string label = ((string)draft.Data["name"] ?? "").Trim();
                if (label.Length == 0 || label.Length > 64 || label.Any(char.IsControl) || !names.Add(label))
                    throw new InvalidOperationException("每份配置必须有唯一名称（1-64字），请检查重复或空名称");
                if (!int.TryParse((string)draft.Data["cost_text"] ?? ((int?)draft.Data["cost"] ?? 50).ToString(), out var value)
                    || value < 1 || value > 999)
                    throw new InvalidOperationException("“" + label + "”的体力消耗须为1-999的整数");
                draft.Data["name"] = label; draft.Data["cost"] = value;
                if (draft.Entry == null) TkfmCustomStage.TemplatePath(resource, (string)draft.Data["entry"]);
                if (draft.Stage == null) TkfmCustomStage.TemplatePath(resource, (string)draft.Data["stage"]);
                if (draft.Page == null) TkfmCustomStage.TemplatePath(resource, (string)draft.Data["page"]);
            }
            var saved = new JArray();
            foreach (var draft in drafts)
            {
                var item = (JObject)draft.Data.DeepClone(); item.Remove("cost_text");
                // Immutable filenames prevent the native template cache from reusing replaced crops.
                string revision = "UserStages/" + Guid.NewGuid().ToString("N");
                foreach (var pair in new[] { (Key: "entry", Bytes: draft.Entry), (Key: "stage", Bytes: draft.Stage), (Key: "page", Bytes: draft.Page) })
                {
                    if (pair.Bytes == null) continue;
                    string relative = revision + "/" + pair.Key + ".png";
                    string path = Path.Combine(resource, "image", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, pair.Bytes);
                    item[pair.Key] = relative;
                }
                saved.Add(item);
            }
            processor.InstanceConfiguration.SetValue(TkfmCustomStage.ConfigKey, new JObject
            {
                ["version"] = 1, ["selected"] = (string)current?.Data["id"] ?? "", ["profiles"] = saved
            });
            for (int i = 0; i < drafts.Count; i++)
            {
                drafts[i].Data = (JObject)saved[i].DeepClone();
                drafts[i].Entry = drafts[i].Stage = drafts[i].Page = null;
            }
            RefreshChoices();
            status.Text = current == null ? "已保存：没有自定义关卡。" : "已保存，当前消耗体力关卡：“" + current + "”。可关闭窗口继续设置其他选项。";
            Saved?.Invoke((string)current?.Data["id"] ?? "");
            processor.AddLog("[自定义关卡] 已保存" + drafts.Count + "份配置；默认：" + (current?.ToString() ?? "无"), changeColor: false);
        }
        catch (Exception ex) { status.Text = "未保存：" + ex.Message; }
    }
}

internal sealed class CustomStageSelection
{
    internal byte[] Template, Page;
}

internal sealed class CustomStageCapture : Window
{
    private readonly MaaProcessor processor;
    private readonly bool stage;
    private readonly Canvas canvas = new Canvas { Width = 720, Height = 1280, Background = Brushes.Black };
    private readonly Image image = new Image { Width = 720, Height = 1280, Stretch = Stretch.Fill };
    private readonly Rectangle selection = new Rectangle
    {
        Stroke = Brushes.DeepSkyBlue, StrokeThickness = 3,
        Fill = new SolidColorBrush(Color.FromArgb(45, 0, 160, 255)), IsVisible = false, IsHitTestVisible = false
    };
    private readonly TextBlock hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly Button freeze = new Button { Content = "截取", IsEnabled = false };
    private readonly Button confirm = new Button { Content = "使用选区", IsEnabled = false };
    private readonly Button refresh = new Button { Content = "刷新 / 重新截取" };
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private byte[] frame;
    private Point start;
    private bool frozen, dragging, fetching, closed;
    private int left, top, width, height;

    internal CustomStageCapture(MaaProcessor processor, bool stage)
    {
        this.processor = processor; this.stage = stage;
        Title = stage ? "设置关卡截图 · 实时画面" : "设置活动入口截图 · 实时画面";
        Width = 540; Height = 850; MinWidth = 390; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        hint.Text = stage
            ? "本窗口复用助手右侧实时画面。请在游戏中进入目标活动的关卡列表，找到要打的关卡；点击“截取”后拖框选择该关卡的名称或稳定图案。"
            : "本窗口复用助手右侧实时画面。请先在任务中选好“活动页签”，再在游戏中切到对应页签的活动封面列表；点击“截取”后拖框选择要打的活动。";
        canvas.Children.Add(image); canvas.Children.Add(selection);
        canvas.PointerPressed += (_, e) =>
        {
            if (!frozen || !e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
            start = Clamp(e.GetPosition(canvas)); dragging = true;
            e.Pointer.Capture(canvas); UpdateSelection(start); e.Handled = true;
        };
        canvas.PointerMoved += (_, e) => { if (dragging) UpdateSelection(Clamp(e.GetPosition(canvas))); };
        canvas.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            UpdateSelection(Clamp(e.GetPosition(canvas))); dragging = false; e.Pointer.Capture(null);
            confirm.IsEnabled = width >= 16 && height >= 16 && top >= 160 && top + height <= 1160;
            message.Text = confirm.IsEnabled
                ? "选区：" + width + " × " + height + "，可重新拖框或点击“使用选区”。"
                : "请框选至少16×16像素的活动或关卡，避开顶部菜单和底部导航；可先在游戏中滚动到列表中部再截取。";
        };
        canvas.PointerCaptureLost += (_, __) => dragging = false;
        freeze.Click += (_, __) =>
        {
            if (frame == null || fetching || closed) return;
            frozen = true; timer.Stop(); freeze.IsEnabled = false;
            hint.Text = stage ? "拖框选择要打的关卡，建议包含完整关卡名，避开星级、倒计时和动画。"
                : "拖框选择要打的活动，建议包含稳定标题或独特图案，避开倒计时和动画。";
            message.Text = "画面已冻结。按住鼠标左键并拖动，框选需要识别的图片。";
        };
        refresh.Click += async (_, __) =>
        {
            if (closed || fetching) return;
            frozen = false; dragging = false; selection.IsVisible = false; confirm.IsEnabled = false;
            await RefreshFrame(); if (!closed && !frozen && frame != null) timer.Start();
        };
        confirm.Click += (_, __) =>
        {
            if (closed || !frozen || !confirm.IsEnabled || TkfmCustomStage.Busy(processor)) return;
            try
            {
                EnsureLiveView();
                var result = new CustomStageSelection { Template = TkfmCustomStage.Crop(frame, left, top, width, height) };
                // Capture a stable, fixed header separately from the user's moving list target.
                if (stage) result.Page = TkfmCustomStage.Crop(frame, 50, 60, 600, 80);
                Close(result);
            }
            catch (Exception ex) { message.Text = "无法使用选区：" + ex.Message; }
        };
        var buttons = new WrapPanel();
        foreach (var button in new[] { freeze, refresh, confirm }) { button.Margin = new Thickness(0, 0, 8, 4); buttons.Children.Add(button); }
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(16) };
        grid.Children.Add(hint);
        var viewer = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new Thickness(0, 10) };
        Grid.SetRow(viewer, 1); grid.Children.Add(viewer);
        Grid.SetRow(message, 2); message.Margin = new Thickness(0, 5); grid.Children.Add(message);
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Content = grid;
        timer.Tick += async (_, __) => await RefreshFrame();
        Opened += async (_, __) =>
        {
            if (Screens.Primary != null) MaxHeight = Math.Max(500, Screens.Primary.WorkingArea.Height / Screens.Primary.Scaling - 60);
            await RefreshFrame(); if (!closed && frame != null) timer.Start();
        };
        Closed += (_, __) =>
        {
            closed = true; timer.Stop(); cancellation.Cancel();
            var old = image.Source as IDisposable; image.Source = null; old?.Dispose();
            frame = null;
        };
    }

    private static Point Clamp(Point point) => new Point(Math.Clamp(point.X, 0, 720), Math.Clamp(point.Y, 0, 1280));
    private void UpdateSelection(Point end)
    {
        left = (int)Math.Floor(Math.Min(start.X, end.X)); top = (int)Math.Floor(Math.Min(start.Y, end.Y));
        width = Math.Min(720 - left, (int)Math.Ceiling(Math.Max(start.X, end.X)) - left);
        height = Math.Min(1280 - top, (int)Math.Ceiling(Math.Max(start.Y, end.Y)) - top);
        Canvas.SetLeft(selection, left); Canvas.SetTop(selection, top);
        selection.Width = width; selection.Height = height; selection.IsVisible = true;
        confirm.IsEnabled = false;
    }

    private void EnsureLiveView()
    {
        if (processor.IsClosed) throw new InvalidOperationException("当前实例已关闭，请重新打开助手");
        if (processor.ViewModel?.IsConnected != true)
            throw new InvalidOperationException("右侧实时画面尚未连接，请等待连接成功后重试");
    }

    private async Task RefreshFrame()
    {
        if (fetching || frozen || closed) return;
        fetching = true; freeze.IsEnabled = false;
        try
        {
            EnsureLiveView();
            byte[] png = await Task.Run(() => ReadLiveView(processor), cancellation.Token);
            byte[] normalized = await Task.Run(() => Normalize(png), cancellation.Token);
            if (closed || frozen) return;
            EnsureLiveView();
            Bitmap bitmap;
            using (var stream = new MemoryStream(normalized)) bitmap = new Bitmap(stream);
            var old = image.Source as IDisposable; image.Source = bitmap; old?.Dispose(); frame = normalized;
            message.Text = "正在复用助手右侧实时画面（约每1.2秒刷新）。在游戏窗口操作到目标页面后点击“截取”。";
            freeze.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            timer.Stop(); frame = null; freeze.IsEnabled = false;
            if (!closed) message.Text = "无法读取右侧实时画面：" + ex.Message + "。确认右侧画面正常后点击“刷新 / 重新截取”。";
        }
        finally { fetching = false; }
    }

    private static byte[] Normalize(byte[] png)
    {
        using (var source = SKBitmap.Decode(png))
        {
            if (source == null) throw new InvalidOperationException("实时画面未返回有效的 PNG 图像");
            if (source.Width >= source.Height || Math.Abs((double)source.Width / source.Height - 9.0 / 16.0) > 0.008)
                throw new InvalidOperationException("需要9:16竖屏设备画面，请将模拟器设置为720×1280或1080×1920");
            if (source.Width == 720 && source.Height == 1280) return png;
            using (var target = new SKBitmap(720, 1280))
            {
                using (var drawing = new SKCanvas(target))
                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
                    drawing.DrawBitmap(source, new SKRect(0, 0, 720, 1280), paint);
                using (var image = SKImage.FromBitmap(target))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100)) return data.ToArray();
            }
        }
    }

    private static byte[] ReadLiveView(MaaProcessor processor)
    {
        // This is the same cached frame path used by the right-side live view.
        // Never launch a second ADB process from the settings window.
        using (var bitmap = processor.GetLiveViewCached())
        {
            if (bitmap == null)
                throw new InvalidOperationException("当前没有可用的实时画面缓存帧");
            using (var output = new MemoryStream())
            {
                bitmap.Save(output);
                return output.ToArray();
            }
        }
    }
}
