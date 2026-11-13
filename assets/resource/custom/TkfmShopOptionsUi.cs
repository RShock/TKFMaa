using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Helper;
using MFAAvalonia.Views.Pages;

// Add group dividers without replacing the native option controls or bindings.
public sealed class TkfmShopOptionsUi : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmShopOptionsUi";

    public TkfmShopOptionsUi()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.ShopOptionsUi.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmShopOptionsUiService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmShopOptionsUiService : IDisposable
{
    private const string DividerName = "TkfmShopGroupDivider";
    private static readonly HashSet<string> GroupLabels = new HashSet<string>
    {
        "交易所 · 常用道具", "交易所 · 材料", "协会商店 · 属性石",
        "协会商店 · 调教道具", "协会商店 · 其他"
    };
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<WeakReference<Separator>> dividers = new List<WeakReference<Separator>>();
    private bool disposed;
    private bool errorReported;

    internal TkfmShopOptionsUiService()
    {
        timer.Tick += Refresh;
        timer.Start();
        Refresh(null, EventArgs.Empty);
    }

    private void Refresh(object sender, EventArgs args)
    {
        if (disposed || Instances.TopLevel == null) return;
        try
        {
            dividers.RemoveAll(reference => !reference.TryGetTarget(out var divider) || divider.Parent == null);
            foreach (var view in Instances.TopLevel.GetVisualDescendants().OfType<TaskQueueView>())
            foreach (var heading in view.GetVisualDescendants().OfType<TextBlock>()
                         .Where(text => text.IsVisible && GroupLabels.Contains(text.Text ?? "")).ToArray())
            {
                Control child = heading;
                while (child.Parent is Control parent && parent != view)
                {
                    if (parent is StackPanel panel && panel.Orientation == Orientation.Vertical)
                    {
                        int index = panel.Children.IndexOf(child);
                        if (index < 0 || (index > 0 && panel.Children[index - 1].Name == DividerName)) break;
                        var divider = new Separator
                        {
                            Name = DividerName, Margin = new Thickness(0, 12, 0, 8),
                            HorizontalAlignment = HorizontalAlignment.Stretch, Opacity = 0.4
                        };
                        panel.Children.Insert(index, divider);
                        dividers.Add(new WeakReference<Separator>(divider));
                        break;
                    }
                    child = parent;
                }
            }
        }
        catch (Exception ex)
        {
            if (errorReported) return;
            errorReported = true;
            LoggerHelper.Warning("TKFMaa 商城分组分隔线未加载：" + ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Refresh;
        foreach (var reference in dividers)
            if (reference.TryGetTarget(out var divider)) (divider.Parent as Panel)?.Children.Remove(divider);
        dividers.Clear();
    }
}
