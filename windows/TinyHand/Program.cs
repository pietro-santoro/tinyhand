using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace TinyHand
{
    public static class Program
    {
        private static readonly Queue<DateTime> RecentErrors = new Queue<DateTime>();

        [STAThread]
        public static int Main(string[] args)
        {
            // Watchdog that restores the cursor if the main process dies. Must not touch WPF.
            if (args.Length >= 2 && args[0] == "--cursor-guard")
            {
                return CursorHider.RunGuard(args[1]);
            }

            if (SelfTests.TryRun(args, out int testResult))
            {
                return testResult;
            }

            var links = args.Where(a => a.StartsWith("tinyhand:", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!SingleInstance.Claim())
            {
                // Already running: hand the links over (macOS: application(_:open:) on the running app).
                if (links.Length > 0)
                {
                    SingleInstance.Forward(links);
                }
                return 0;
            }

            CursorHider.RestoreIfLeftHidden();
            CursorHider.StartGuard();
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Write($"crash: {e.ExceptionObject}");
                CursorHider.EmergencyRestore();
                Automation.RemoveState();
            };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CursorHider.EmergencyRestore();

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var controller = new AppController(app.Dispatcher);
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write($"unhandled: {e.Exception}");
                e.Handled = true;
                var now = DateTime.Now;
                RecentErrors.Enqueue(now);
                while (RecentErrors.Count > 0 && (now - RecentErrors.Peek()).TotalSeconds > 10)
                {
                    RecentErrors.Dequeue();
                }
                if (RecentErrors.Count > 20)
                {
                    // Something keeps failing every frame: get out cleanly rather than spin.
                    Log.Write("too many errors; quitting");
                    controller.Quit();
                }
            };
            app.SessionEnding += (s, e) => controller.Shutdown();
            app.Exit += (s, e) => CursorHider.EmergencyRestore();
            app.Startup += (s, e) => controller.Start(links);
            SingleInstance.Listen(controller.Open);
            return app.Run();
        }
    }
}
