using System;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace DshWebManager
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Join all args: a control action may contain a space (e.g. "backend wsl")
            // and PowerShell-style launchers split it into separate arguments.
            string action = args.Length > 0 ? String.Join(" ", args).ToLowerInvariant() : "open";
            AppPaths.EnsureDirectories();
            FileLog.Info("dsh web manager starting, action=" + action + ", exe=" + AppPaths.ExePath);

            // GitHub requires TLS 1.2+; .NET Framework may default to older protocols.
            try { System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12; }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                FileLog.Error("Unhandled: " + (e.ExceptionObject == null ? "null" : e.ExceptionObject.ToString()));
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                FileLog.Error(e.Exception == null ? "Thread exception (null)" : e.Exception.ToString());
            };

            string sid = WindowsIdentity.GetCurrent() == null ? "default" : (WindowsIdentity.GetCurrent().User == null ? "default" : WindowsIdentity.GetCurrent().User.Value.Replace('-', '_'));
            string mutexName = @"Local\DshWebManager-" + sid + AppPaths.InstanceSuffix;
            bool createdNew;
            using (Mutex mutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Forward the action to the running primary instance and exit.
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        if (ControlProtocol.TrySendAction(action)) return 0;
                        Thread.Sleep(150);
                    }
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                ManagerConfig config = ManagerConfig.Load();

                // Primary-instance control actions. A control action normally gets
                // forwarded to an already-running instance; if none is running (we
                // just acquired the mutex) the action must not turn into a plain
                // tray start (that used to leave a manager that could never be
                // stopped by 'exit'). "exit" with no instance means: nothing owned
                // by us is running, just terminate. Other control actions are
                // already persisted in config and take effect on this start.
                if (action.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    FileLog.Info("No running instance to exit; terminating immediately.");
                    return 0;
                }

                ManagerService service = new ManagerService(config);
                using (ControlServer server = new ControlServer(service))
                {
                    server.Start();
                    using (TrayFrontend tray = new TrayFrontend(service))
                    {
                        service.Initialize(action);
                        Application.Run(tray);
                    }
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}