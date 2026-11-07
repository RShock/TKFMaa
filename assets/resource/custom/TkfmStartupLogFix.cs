using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Avalonia.Threading;
using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.ViewModels.Pages;

// Loaded by MFAAvalonia's existing custom-action compiler. Never runs a game action.
public sealed class TkfmStartupLogFix : IMaaCustomAction
{
    public string Name { get; set; } = "TkfmStartupLogFix";

    public TkfmStartupLogFix()
    {
        // Consume the auto-connect onlyStart completion synchronously. Waiting for
        // the UI dispatcher can race with the short startup connection task.
        TkfmStartupLogFixService.SuppressCurrentStartupCompletion();
        Dispatcher.UIThread.Post(() =>
        {
            const string key = "TKFMaa.StartupLogFix.Service";
            (AppDomain.CurrentDomain.GetData(key) as IDisposable)?.Dispose();
            AppDomain.CurrentDomain.SetData(key, new TkfmStartupLogFixService());
        });
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext => true;
}

internal sealed class TkfmStartupLogFixService : IDisposable
{
    // EmulatorExtras can return no frame briefly while the emulator finishes its
    // first native capture setup. Keep the normal warning for failures that persist
    // beyond this short connection grace period.
    private static readonly TimeSpan LiveViewGrace = TimeSpan.FromSeconds(2.5);

    private static readonly FieldInfo? CompletionHandledField = typeof(MaaProcessor).GetField(
        "_stopCompletionMessageHandled", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? LiveViewNoImageLoggedField = typeof(TaskQueueViewModel).GetField(
        "_liveViewNoImageLogged", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Dictionary<MaaProcessor, ProcessorState> processors = new Dictionary<MaaProcessor, ProcessorState>();
    private readonly DispatcherTimer timer;
    private bool disposed;

    internal TkfmStartupLogFixService()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += Refresh;
        Refresh(null, EventArgs.Empty);
        timer.Start();
    }

    internal static void SuppressCurrentStartupCompletion()
    {
        try
        {
            // This extension is instantiated by the same onlyStart resource-loading pass
            // that previously ended with a false success message. The next user StartTask
            // resets this flag in MFAAvalonia before running a real queue.
            foreach (var processor in MaaProcessor.Processors.ToArray())
                SetIntField(processor, CompletionHandledField, 1);
        }
        catch
        {
            // Keep startup connection behavior unchanged if the frontend field is absent.
        }
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (disposed) return;

        try
        {
            foreach (var processor in MaaProcessor.Processors.ToArray())
            {
                if (!processors.TryGetValue(processor, out var state))
                {
                    state = new ProcessorState();
                    processors.Add(processor, state);
                }

                var viewModel = processor.ViewModel;
                if (!ReferenceEquals(state.ViewModel, viewModel))
                {
                    if (state.ViewModel != null)
                        state.ViewModel.PropertyChanged -= state.PropertyChangedHandler;

                    state.ViewModel = viewModel;
                    if (viewModel != null)
                    {
                        state.PropertyChangedHandler = (_, eventArgs) =>
                        {
                            if (eventArgs.PropertyName == nameof(TaskQueueViewModel.IsConnected)
                                && viewModel.IsConnected)
                                BeginLiveViewGrace(state);
                        };
                        viewModel.PropertyChanged += state.PropertyChangedHandler;
                    }
                }

                if (viewModel?.IsConnected == true)
                {
                    if (!state.WasConnected)
                    {
                        state.WasConnected = true;
                        BeginLiveViewGrace(state);
                    }

                    if (state.GraceUntil > DateTime.UtcNow)
                        SetBoolField(viewModel, LiveViewNoImageLoggedField, true);
                    else if (state.GraceActive)
                    {
                        state.GraceActive = false;
                        SetBoolField(viewModel, LiveViewNoImageLoggedField, false);
                    }
                }
                else
                {
                    state.WasConnected = false;
                }
            }

            foreach (var removed in processors.Keys.Where(processor => !MaaProcessor.Processors.Contains(processor)).ToArray())
            {
                processors[removed].Dispose();
                processors.Remove(removed);
            }
        }
        catch
        {
            // A version mismatch must never interfere with the frontend connection flow.
        }
    }

    private static void BeginLiveViewGrace(ProcessorState state)
    {
        state.GraceActive = true;
        state.GraceUntil = DateTime.UtcNow + LiveViewGrace;
        SetBoolField(state.ViewModel, LiveViewNoImageLoggedField, true);
    }

    private static void SetBoolField(object? target, FieldInfo? field, bool value)
    {
        if (target == null || field == null || field.FieldType != typeof(bool)) return;
        try { field.SetValue(target, value); }
        catch { }
    }

    private static void SetIntField(object? target, FieldInfo? field, int value)
    {
        if (target == null || field == null || field.FieldType != typeof(int)) return;
        try { field.SetValue(target, value); }
        catch { }
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        foreach (var state in processors.Values) state.Dispose();
        processors.Clear();
    }

    private sealed class ProcessorState : IDisposable
    {
        internal TaskQueueViewModel? ViewModel;
        internal PropertyChangedEventHandler? PropertyChangedHandler;
        internal bool WasConnected;
        internal bool GraceActive;
        internal DateTime GraceUntil;

        public void Dispose()
        {
            if (ViewModel != null && PropertyChangedHandler != null)
                ViewModel.PropertyChanged -= PropertyChangedHandler;
            ViewModel = null;
            PropertyChangedHandler = null;
        }
    }
}
