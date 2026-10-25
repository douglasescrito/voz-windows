using System;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace VozWindows;

public static class Program
{
    public const string ShowUiEventName = "Local\\PROJETOS_VozWindows_ShowUI";
    public const string CursorLoopEventName = "Local\\PROJETOS_VozWindows_CursorLoop";
    public const string LegacyCursorLoopEventName = "Local\\CursorLoop-ShowUI-Douglas-2026";

    private static EventWaitHandle OpenOrCreateSharedEvent(string name)
    {
        try
        {
            var sec = new EventWaitHandleSecurity();
            sec.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));
            return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, sec);
        }
        catch
        {
            try
            {
                return EventWaitHandle.OpenExisting(name);
            }
            catch
            {
                return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            }
        }
    }

    private static Mutex OpenOrCreateSharedMutex(string name, out bool createdNew)
    {
        try
        {
            var sec = new MutexSecurity();
            sec.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.FullControl,
                AccessControlType.Allow));
            return MutexAcl.Create(true, name, out createdNew, sec);
        }
        catch
        {
            try
            {
                var existing = Mutex.OpenExisting(name);
                createdNew = false;
                return existing;
            }
            catch
            {
                return new Mutex(true, name, out createdNew);
            }
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--import-clipboard")
        {
            string clip = (System.Windows.Clipboard.GetText() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clip) || (!clip.StartsWith("AQ.") && !clip.StartsWith("AIza")))
            {
                Console.Error.WriteLine("Copie uma chave válida (iniciada com AQ. ou AIza) antes de importar.");
                return 1;
            }
            bool ok = NativeInterop.WriteApiKeyToVault(clip);
            Console.WriteLine(ok
                ? $"Chave salva com segurança em {NativeInterop.CredentialTarget} (tamanho={clip.Length})."
                : "Falha ao gravar no Cofre do Windows.");
            return ok ? 0 : 2;
        }

        bool wantCursorLoop = args.Any(a => string.Equals(a, "--cursorloop", StringComparison.OrdinalIgnoreCase));

        using var showUiEvent = OpenOrCreateSharedEvent(ShowUiEventName);
        using var cursorLoopEvent = OpenOrCreateSharedEvent(CursorLoopEventName);
        using var legacyLoopEvent = OpenOrCreateSharedEvent(LegacyCursorLoopEventName);

        string? currentExe = Environment.ProcessPath;
        bool isPortableRun = string.IsNullOrEmpty(currentExe) || !currentExe.Contains("PROJETOS", StringComparison.OrdinalIgnoreCase);

        if (isPortableRun)
        {
            // Em modo portatil, encerrar qualquer instancia anterior do VozWindows para que a nova versao substitua na hora
            try
            {
                int myPid = Environment.ProcessId;
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("VozWindows"))
                {
                    if (proc.Id != myPid)
                    {
                        try { proc.Kill(); proc.WaitForExit(800); } catch { }
                    }
                }
            }
            catch { }
        }

        using var mutex = OpenOrCreateSharedMutex("Local\\PROJETOS_VozWindows_Singleton", out bool createdNew);
        if (!createdNew && !isPortableRun)
        {
            if (wantCursorLoop)
            {
                cursorLoopEvent.Set();
                return 0;
            }
            if (args.Length == 0)
            {
                showUiEvent.Set();
                return 0;
            }
        }

        // Stop any legacy standalone CursorLoop process so it doesn't double-boost the cursor or lock Ctrl+Alt+0..3
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("CursorLoop"))
            {
                try { proc.Kill(); } catch { }
            }
        }
        catch { }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
        };

        var win = new MainWindow(
            showUiEvent,
            cursorLoopEvent,
            legacyLoopEvent,
            openCursorLoopOnStart: wantCursorLoop);
        win.Closed += (_, _) => app.Shutdown();

        if (args.Length >= 3 && args[0] == "--verify-demo")
        {
            string wavPath = args[1];
            string screenshotPath = args[2];
            bool exitAfter = args.Length >= 4 && args[3] == "--exit-after";

            win.Loaded += async (_, _) =>
            {
                try
                {
                    await win.RunAutomatedLiveProofAsync(wavPath, screenshotPath);
                }
                finally
                {
                    if (exitAfter)
                        win.Close();
                }
            };
            app.Run(win);
            return 0;
        }

        win.InitializeTrayOnlyStartup(openCursorLoopOnStart: wantCursorLoop);
        app.Run();
        return 0;
    }
}
