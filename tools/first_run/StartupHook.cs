using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// .NET calls this before MFA reads appsettings.json or creates configuration objects.
internal static class StartupHook
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string title, uint flags);

    private static bool WasShown(string marker)
    {
        if (!File.Exists(marker)) return false;
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(marker));
            return state.RootElement.GetProperty("status").GetString() != "starting";
        }
        catch { return true; }
    }

    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = AppContext.BaseDirectory;
        string marker = Path.Combine(root, ".tkfm-first-run-migration.json");
        try
        {
            FileStream guard;
            try
            {
                guard = new FileStream(Path.Combine(root, ".tkfm-first-run-migration.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { Environment.Exit(0); return; }
            using (guard)
            {
                // An importer can outlive a forcibly closed frontend. Never let a
                // second frontend load half-replaced settings during that window.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var importGuard = new FileStream(Path.Combine(root, ".tkfm-first-run-import.lock"),
                            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        break;
                    }
                    catch (IOException) when (attempt < 100) { Thread.Sleep(100); }
                    catch (IOException)
                    {
                        MessageBoxW(IntPtr.Zero, "旧版配置仍在导入，请等待迁移完成后再打开助手。", "TKFMaaR", 0x30);
                        Environment.Exit(0);
                        return;
                    }
                }
                if (WasShown(marker)) return;
                string script = Path.Combine(root, "migration", "first_run.ps1");
                if (!File.Exists(script)) throw new FileNotFoundException("缺少首次启动迁移向导", script);
                // Separate from config/: importing old settings cannot restore the prompt.
                using (var state = new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.Read))
                    JsonSerializer.Serialize(state, new { version = 1, status = "starting", at = DateTimeOffset.Now });
                string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PowerShell", "7", "pwsh.exe");
                string configuredShell = Environment.GetEnvironmentVariable("TKFM_FIRST_RUN_PWSH") ?? "";
                string? pathShell = (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator).Select(folder => Path.Combine(folder, "pwsh.exe")).FirstOrDefault(File.Exists);
                string shell = File.Exists(configuredShell) ? configuredShell : File.Exists(pwsh) ? pwsh : pathShell ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(shell)
                {
                    WorkingDirectory = root, UseShellExecute = false,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                foreach (string arg in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", script,
                    "-InstallRoot", root.TrimEnd(Path.DirectorySeparatorChar), "-CurrentProcessId", Environment.ProcessId.ToString() })
                    start.ArgumentList.Add(arg);
                using var wizard = Process.Start(start) ?? throw new IOException("无法打开迁移向导");
                var output = wizard.StandardOutput.ReadToEndAsync();
                var error = wizard.StandardError.ReadToEndAsync();
                wizard.WaitForExit();
                if (wizard.ExitCode != 0)
                {
                    string log = Path.Combine(root, "migration", "startup-error.log");
                    File.WriteAllText(log, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
                    throw new IOException("迁移向导未正常结束，错误详情：" + log);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, "旧版配置迁移未完成：" + ex.Message +
                "\n助手将继续启动。旧版目录保留，可按升级说明手动复制配置和截图。", "TKFMaaR", 0x30);
        }
    }
}
