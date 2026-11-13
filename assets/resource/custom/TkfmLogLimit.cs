using System;
using System.Linq;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;

// Keep the native log view, collection binding and scrolling. Only cap row count.
public sealed class TkfmLogLimit : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmLogLimit";

    public TkfmLogLimit()
    {
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.LogLimit.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmLogLimitService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmLogLimitService : IDisposable
{
    private const int Limit = 100;
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    private bool disposed;
    private bool errorReported;

    internal TkfmLogLimitService()
    {
        timer.Tick += Trim;
        timer.Start();
        Trim(null, EventArgs.Empty);
    }

    private void Trim(object sender, EventArgs args)
    {
        if (disposed) return;
        foreach (var processor in MaaProcessor.Processors.ToArray())
        {
            try
            {
                int excess = processor.LogItemViewModels.Count - Limit;
                // The native disposable collection owns removed rows. Do not
                // mutate it from CollectionChanged or issue any scroll requests.
                if (excess > 0) processor.LogItemViewModels.RemoveRange(0, excess);
            }
            catch (Exception ex)
            {
                if (errorReported) continue;
                errorReported = true;
                LoggerHelper.Warning("TKFMaa 日志条数限制未完成：" + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Trim;
    }
}
