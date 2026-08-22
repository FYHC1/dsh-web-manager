using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace DshWebManager
{
    /// <summary>
    /// Named-pipe control channel. A second invocation of the EXE forwards its action
    /// (open/tray/exit) to the primary instance and exits.
    /// </summary>
    public static class ControlProtocol
    {
        public static string PipeName
        {
            get
            {
                string sid = WindowsIdentity.GetCurrent().User == null
                    ? "default"
                    : WindowsIdentity.GetCurrent().User.Value.Replace('-', '_');
                return @"\\.\pipe\dsh-web-manager-" + sid + AppPaths.InstanceSuffix;
            }
        }

        /// <summary>Called by a second instance: send the action to the primary, return success.</summary>
        public static bool TrySendAction(string action)
        {
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName.Substring(9), PipeDirection.Out))
                {
                    client.Connect(1500);
                    byte[] data = Encoding.UTF8.GetBytes(action + "\n");
                    client.Write(data, 0, data.Length);
                    client.Flush();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Hosts the pipe server on a background thread; actions are marshaled to the UI thread.</summary>
    public sealed class ControlServer : IDisposable
    {
        private readonly ManagerService _service;
        private volatile bool _running;

        public ControlServer(ManagerService service)
        {
            _service = service;
        }

        public void Start()
        {
            _running = true;
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "control-server";
            t.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    using (NamedPipeServerStream server = new NamedPipeServerStream(
                        ControlProtocol.PipeName.Substring(9), PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        server.WaitForConnection();
                        using (StreamReader reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string line = reader.ReadLine();
                            if (!String.IsNullOrEmpty(line)) Handle(line.Trim());
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Error("ControlServer loop: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }

        private void Handle(string action)
        {
            // Marshal to the WinForms UI thread if one exists.
            try
            {
                System.Windows.Forms.Control control = System.Windows.Forms.Form.ActiveForm;
                if (control != null && control.InvokeRequired)
                {
                    control.BeginInvoke(new Action<string>(Handle), action);
                    return;
                }
            }
            catch { }

            try
            {
                if (action.Equals("open", StringComparison.OrdinalIgnoreCase))
                    _service.OpenWindow();
                else if (action.Equals("open windows", StringComparison.OrdinalIgnoreCase))
                    _service.OpenBackendWindow("windows");
                else if (action.Equals("open wsl", StringComparison.OrdinalIgnoreCase))
                    _service.OpenBackendWindow("wsl");
                else if (action.Equals("tray", StringComparison.OrdinalIgnoreCase))
                    { /* keep tray only: no-op */ }
                else if (action.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    _service.Exit(false);
                else if (action.Equals("restart", StringComparison.OrdinalIgnoreCase))
                    _service.Restart();
                else if (action.Equals("updatemanager", StringComparison.OrdinalIgnoreCase))
                    _service.ApplyManagerUpdate();
                else if (action.Equals("checkmanagerupdate", StringComparison.OrdinalIgnoreCase))
                    _service.CheckForManagerUpdate();
                else if (action.Equals("updateplugin", StringComparison.OrdinalIgnoreCase))
                    _service.UpdatePluginBundle();
                else if (action.StartsWith("backend ", StringComparison.OrdinalIgnoreCase))
                    _service.SetBackend(action.Substring("backend ".Length).Trim());
                else if (action.StartsWith("wslmode ", StringComparison.OrdinalIgnoreCase))
                    _service.SetWslMode(action.Substring("wslmode ".Length).Trim());
                else if (action.StartsWith("closeinstance ", StringComparison.OrdinalIgnoreCase))
                    _service.CloseInstanceBackend(action.Substring("closeinstance ".Length).Trim());
            }
            catch (Exception ex)
            {
                FileLog.Error("ControlServer handle: " + ex.ToString());
            }
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}