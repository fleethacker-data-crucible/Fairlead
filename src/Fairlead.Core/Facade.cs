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

        public static FairleadHost CreateHost(FairleadSettings settings, string webRoot, int preferredPort, Dictionary<string, object> launcherState)
        {
            return CreateHost(settings, webRoot, preferredPort, launcherState, null);
        }

        /// <summary>
        /// processPath is what this process runs, reported as the FleetPort id's path: Start-Fairlead.ps1 passes its own
        /// script ($PSCommandPath). Null or empty uses the process executable.
        /// </summary>
        public static FairleadHost CreateHost(FairleadSettings settings, string webRoot, int preferredPort, Dictionary<string, object> launcherState, string processPath)
        {
            return new FairleadHost(new HostOptions
            {
                Settings = settings,
                WebRoot = webRoot,
                PreferredPort = preferredPort,
                LauncherState = launcherState ?? new Dictionary<string, object>(),
                ProcessPath = processPath,
            });
        }

        /// <summary>One-line state for the tray tooltip (max 63 characters, the NotifyIcon limit): name and status.</summary>
        public static string TrayText(FairleadHost host)
        {
            var text = "Fairlead - running on 127.0.0.1:" + host.Port;
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        /// <summary>The disabled status line at the top of the tray menu.</summary>
        public static string TrayStatus(FairleadHost host)
        {
            var c = host.Health.Connectivity;
            var mode = c.Kedge ? "Kedge, local models only" : c.Internet == Resilience.InternetState.Down ? "offline" : "cloud + local";
            return "Running on 127.0.0.1:" + host.Port + " (" + mode + ")";
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
