using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace TinyHand
{
    /// <summary>Wires everything together. Port of AppDelegate.swift.</summary>
    internal sealed class AppController
    {
        private readonly Dispatcher dispatcher;
        private OverlayController overlay;
        private SettingsWindow settings;
        private WelcomeWindow welcome;
        private TrayIcon tray;
        private Automation automation;
        private int? toggleHotKey;
        private int? clearHotKey;
        private bool launched;
        private readonly List<string> pendingLinks = new List<string>();

        public AppController(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
        }

        /// <summary>A link from the command line or from a second launch (any thread).</summary>
        public void Open(string link)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!launched)
                {
                    pendingLinks.Add(link);
                    return;
                }
                automation.Handle(link);
            }));
        }

        public void Start(IEnumerable<string> links)
        {
            pendingLinks.AddRange(links);
            Log.Write($"launch {AppPaths.ExePath}");
            HandAsset.PreloadThumbnails(PointerStyle.All.Where(s => s.AssetName != null).Select(s => s.AssetName));

            overlay = new OverlayController(dispatcher);
            settings = new SettingsWindow();
            welcome = new WelcomeWindow();
            automation = new Automation(overlay);
            tray = new TrayIcon();

            overlay.OnActiveChanged = () =>
            {
                tray.UpdateIcon();
                automation.WriteState();
            };
            overlay.OnSoundChanged = () => automation.WriteState();

            Automation.RegisterUrlScheme();
            EnableLaunchAtLoginOnFirstRun();
            Preferences.RefreshLaunchAtLoginPath();
            SetupTray();
            SetupSettings();
            RegisterHotKeys();

            overlay.OnNeedsPermission = () => welcome.Show();
            welcome.OnTryIt = () => overlay.Activate();
            if (!Preferences.GetFlag(WelcomeWindow.ShownKey))
            {
                welcome.Show();
            }

            automation.WriteState();
            launched = true;
            foreach (var link in pendingLinks)
            {
                automation.Handle(link);
            }
            pendingLinks.Clear();
        }

        private void SetupTray()
        {
            tray.IsActive = () => overlay.IsActive;
            tray.IsClickThrough = () => overlay.IsClickThrough;
            tray.SoundOn = () => overlay.SoundOn;
            tray.OnToggle = () => overlay.Toggle();
            tray.OnClear = () => overlay.ClearInk();
            tray.OnToggleSound = () => overlay.SoundOn = !overlay.SoundOn;
            tray.OnSettings = () =>
            {
                overlay.Exit();
                settings.Show();
            };
            tray.OnCheckForUpdates = () =>
            {
                overlay.Exit();
                try
                {
                    Process.Start(new ProcessStartInfo(SettingsWindow.ReleasesUrl) { UseShellExecute = true });
                }
                catch (Exception e)
                {
                    Log.Write($"open releases failed: {e.Message}");
                }
            };
            tray.OnQuit = Quit;
            tray.OnMenuOpen = open => overlay.SuspendInput(open);
            tray.Show();
        }

        private void SetupSettings()
        {
            settings.ApplyToggleShortcut = s => Apply(s, true);
            settings.ApplyClearShortcut = s => Apply(s, false);
            settings.OnRecording = recording =>
            {
                if (recording)
                {
                    UnregisterHotKeys();
                }
                else
                {
                    RegisterHotKeys();
                }
            };
        }

        private bool Apply(Shortcut s, bool isToggle)
        {
            UnregisterHotKeys();
            var old = isToggle ? Preferences.ToggleShortcut : Preferences.ClearShortcut;
            if (isToggle) Preferences.ToggleShortcut = s; else Preferences.ClearShortcut = s;
            if (RegisterHotKeys())
            {
                return true;
            }
            UnregisterHotKeys();
            if (isToggle) Preferences.ToggleShortcut = old; else Preferences.ClearShortcut = old;
            RegisterHotKeys();
            return false;
        }

        private bool RegisterHotKeys()
        {
            if (toggleHotKey != null || clearHotKey != null)
            {
                return true;
            }
            var t = Preferences.ToggleShortcut;
            var c = Preferences.ClearShortcut;
            toggleHotKey = HotKeys.Shared.Register(t.KeyCode, t.Modifiers, () => overlay.Toggle());
            clearHotKey = HotKeys.Shared.Register(c.KeyCode, c.Modifiers, () => overlay.ClearInk());
            if (toggleHotKey == null) Log.Write($"hot key {t.Display} is taken");
            if (clearHotKey == null) Log.Write($"hot key {c.Display} is taken");
            return toggleHotKey != null && clearHotKey != null;
        }

        private void UnregisterHotKeys()
        {
            if (toggleHotKey.HasValue) HotKeys.Shared.Unregister(toggleHotKey.Value);
            if (clearHotKey.HasValue) HotKeys.Shared.Unregister(clearHotKey.Value);
            toggleHotKey = null;
            clearHotKey = null;
        }

        /// <summary>
        /// The Mac turns on open-at-login the first time it runs from /Applications. The Windows
        /// equivalent is running from an installed location (Program Files or %LOCALAPPDATA%\Programs);
        /// a zip unpacked anywhere else leaves it off until the user turns it on in Settings.
        /// </summary>
        private static void EnableLaunchAtLoginOnFirstRun()
        {
            const string key = "loginItemSetUp";
            string path = AppPaths.ExePath;
            string programs = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            bool installed = path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(programs, StringComparison.OrdinalIgnoreCase);
            if (!installed || Preferences.GetFlag(key))
            {
                return;
            }
            Preferences.SetFlag(key, true);
            Preferences.LaunchAtLogin = true;
            Log.Write($"first run: open at login = {Preferences.LaunchAtLogin}");
        }

        public void Quit()
        {
            Shutdown();
            Application.Current.Shutdown();
        }

        /// <summary>Also called when Windows ends the session.</summary>
        public void Shutdown()
        {
            try
            {
                overlay?.Deactivate();
            }
            catch (Exception e)
            {
                Log.Write($"deactivate on quit failed: {e.Message}");
            }
            CursorHider.Show();
            Automation.RemoveState();
            tray?.Dispose();
            tray = null;
        }
    }
}
