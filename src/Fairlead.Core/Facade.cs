using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Fairlead.Core.Config;
using Fairlead.Core.Host;

namespace Fairlead.Core
{
    /// <summary>The entry points PowerShell calls. Everything else in the module is plumbing around these.</summary>
    public static class Facade
    {
        public static int LoadEnvFile(string path)
        {
            return FairleadSettings.LoadEnvFile(path);
        }

        public static FairleadSettings SettingsFromEnvironment(string rootDirectory)
        {
            return FairleadSettings.FromEnvironment(rootDirectory);
        }

        /// <summary>Validates an AI gateway model file against the current environment without starting anything (Test-FairleadModelConfig).</summary>
        public static GatewayModelFile TestGatewayModelFile(string path)
        {
            return GatewayModelFile.Load(path, null);
        }

        public static string GatewayModelPath(string rootDirectory)
        {
            return GatewayModelFile.ResolvePath(rootDirectory, null);
        }

        public static FairleadHost CreateHost(FairleadSettings settings, string webRoot, int preferredPort, Dictionary<string, object> launcherState, bool autoStop)
        {
            return new FairleadHost(new HostOptions
            {
                Settings = settings,
                WebRoot = webRoot,
                PreferredPort = preferredPort,
                LauncherState = launcherState ?? new Dictionary<string, object>(),
                AutoStop = autoStop,
            });
        }

        /// <summary>One-line state for the tray tooltip (max 63 characters, the NotifyIcon limit).</summary>
        public static string TrayText(FairleadHost host)
        {
            var c = host.Health.Connectivity;
            var mode = c.Kedge ? "Kedge (local only)" : c.Internet == Resilience.InternetState.Down ? "offline" : "cloud + local";
            var text = "Fairlead :" + host.Port + " - " + mode;
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        public static void SetKedge(FairleadHost host, string mode)
        {
            var m = mode.ToLowerInvariant() == "on" ? KedgeMode.On : mode.ToLowerInvariant() == "off" ? KedgeMode.Off : KedgeMode.Auto;
            host.Config.Update(c => c.Kedge = m);
        }

        public static bool SetConsoleVisible(bool visible)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            try
            {
                var handle = NativeMethods.GetConsoleWindow();
                if (handle == IntPtr.Zero) return false;
                return NativeMethods.ShowWindow(handle, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static class NativeMethods
        {
            public const int SW_HIDE = 0;
            public const int SW_SHOW = 5;

            [DllImport("kernel32.dll")]
            public static extern IntPtr GetConsoleWindow();

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        }
    }
}
