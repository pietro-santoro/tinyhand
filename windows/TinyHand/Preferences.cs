using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;

namespace TinyHand
{
    /// <summary>A global shortcut (RegisterHotKey). The Mac defaults ⌃⌥H / ⌃⌥X become Ctrl+Alt+H / Ctrl+Alt+X.</summary>
    internal sealed class Shortcut : IEquatable<Shortcut>
    {
        public uint KeyCode { get; }
        public uint Modifiers { get; }
        public string Display { get; }

        public Shortcut(uint keyCode, uint modifiers, string display)
        {
            KeyCode = keyCode;
            Modifiers = modifiers;
            Display = display;
        }

        // ⌃⌥H / ⌃⌥X on the Mac.
        public static readonly Shortcut ToggleDefault = new Shortcut(0x48, Native.MOD_CONTROL | Native.MOD_ALT, "Ctrl+Alt+H");
        public static readonly Shortcut ClearDefault = new Shortcut(0x58, Native.MOD_CONTROL | Native.MOD_ALT, "Ctrl+Alt+X");

        public bool Equals(Shortcut other) => other != null && other.KeyCode == KeyCode && other.Modifiers == Modifiers;
        public override bool Equals(object obj) => Equals(obj as Shortcut);
        public override int GetHashCode() => (int)(KeyCode * 31 + Modifiers);

        public static string ModifierText(uint mods)
        {
            var parts = new List<string>();
            if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
            return string.Join("+", parts);
        }
    }

    /// <summary>The key held to click for real. macOS Option → Alt, Command → Ctrl.</summary>
    internal enum InteractKey { Option, Command, Off }

    internal static class InteractKeyInfo
    {
        public static readonly InteractKey[] All = { InteractKey.Option, InteractKey.Command, InteractKey.Off };

        public static string Raw(this InteractKey k) => k switch
        {
            InteractKey.Option => "option",
            InteractKey.Command => "command",
            _ => "off",
        };

        public static InteractKey Parse(string raw) => raw switch
        {
            "option" => InteractKey.Option,
            "command" => InteractKey.Command,
            "control" => InteractKey.Command, // the Mac's ⌃ choice has no separate Windows key
            "off" => InteractKey.Off,
            _ => InteractKey.Option,
        };

        public static string Title(this InteractKey k) => k switch
        {
            InteractKey.Option => "Alt",
            InteractKey.Command => "Ctrl",
            _ => "Off",
        };

        public static string Symbol(this InteractKey k) => k switch
        {
            InteractKey.Option => "Alt",
            InteractKey.Command => "Ctrl",
            _ => "",
        };

        /// <summary>Virtual key to test with GetAsyncKeyState, 0 when off.</summary>
        public static int VirtualKey(this InteractKey k) => k switch
        {
            InteractKey.Option => Native.VK_MENU,
            InteractKey.Command => Native.VK_CONTROL,
            _ => 0,
        };
    }

    internal enum InkChoice { Match, Red, Orange, Yellow, Green, Blue, Purple, White, Black }

    internal static class InkChoiceInfo
    {
        public static readonly InkChoice[] All = (InkChoice[])Enum.GetValues(typeof(InkChoice));

        public static string Raw(this InkChoice c) => c.ToString().ToLowerInvariant();

        public static InkChoice Parse(string raw) =>
            All.FirstOrDefault(c => c.Raw() == raw);

        public static string Title(this InkChoice c) => c == InkChoice.Match ? "Match the pointer" : c.ToString();

        public static Color? Color(this InkChoice c) => c switch
        {
            InkChoice.Match => null,
            InkChoice.Red => Ink.Red,
            InkChoice.Orange => Ink.Hex(0xFF8A00),
            InkChoice.Yellow => Ink.Hex(0xFFCC00),
            InkChoice.Green => Ink.Hex(0x30C25A),
            InkChoice.Blue => Ink.Hex(0x0A84FF),
            InkChoice.Purple => Ink.Hex(0x8754F2),
            InkChoice.White => Ink.Hex(0xFFFFFF),
            _ => Ink.Hex(0x1D1D21),
        };

        public static Color Resolved(this InkChoice c, PointerStyle style) => c.Color() ?? style.InkColor;
    }

    internal enum TapSoundChoice { Thock, Click, Tock, Clack, Typewriter, Snap, Wood, Pop, Boop, Blip, Coin, Pew, Squeak, Quack, Chirp }

    internal static class TapSoundInfo
    {
        public static readonly TapSoundChoice[] All = (TapSoundChoice[])Enum.GetValues(typeof(TapSoundChoice));

        public static string Raw(this TapSoundChoice c) => c.ToString().ToLowerInvariant();

        public static TapSoundChoice? Parse(string raw)
        {
            foreach (var c in All)
            {
                if (c.Raw() == raw) return c;
            }
            return null;
        }

        public static string Title(this TapSoundChoice c) => c == TapSoundChoice.Wood ? "Wood block" : c.ToString();
    }

    /// <summary>
    /// Settings, stored as JSON in %APPDATA%\TinyHand\settings.json with the same keys the
    /// Mac app keeps in UserDefaults. Every setter saves and raises <see cref="Changed"/>.
    /// </summary>
    internal static class Preferences
    {
        public static event Action Changed;

        private static readonly object Gate = new object();
        private static JsonObject store;
        private static string FilePath => Path.Combine(AppPaths.Roaming, "settings.json");

        private static JsonObject Store
        {
            get
            {
                lock (Gate)
                {
                    if (store != null)
                    {
                        return store;
                    }
                    try
                    {
                        if (File.Exists(FilePath))
                        {
                            store = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Write($"settings unreadable, starting fresh: {e.Message}");
                    }
                    store ??= new JsonObject();
                    return store;
                }
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Roaming);
                string tmp = FilePath + ".tmp";
                string text;
                lock (Gate)
                {
                    text = Store.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                }
                File.WriteAllText(tmp, text);
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e)
            {
                Log.Write($"settings save failed: {e.Message}");
            }
        }

        public static void NotifyChanged() => Changed?.Invoke();

        private static void Set(string key, JsonNode value, bool notify = true)
        {
            lock (Gate)
            {
                Store[key] = value;
            }
            Save();
            if (notify)
            {
                Changed?.Invoke();
            }
        }

        private static void Remove(string key)
        {
            lock (Gate)
            {
                Store.Remove(key);
            }
        }

        private static JsonNode Get(string key)
        {
            lock (Gate)
            {
                return Store.TryGetPropertyValue(key, out var node) ? node : null;
            }
        }

        private static bool GetBool(string key, bool fallback)
        {
            try { return Get(key)?.GetValue<bool>() ?? fallback; } catch (Exception) { return fallback; }
        }

        private static double GetDouble(string key, double fallback)
        {
            try { return Get(key)?.GetValue<double>() ?? fallback; } catch (Exception) { return fallback; }
        }

        private static string GetString(string key)
        {
            try { return Get(key)?.GetValue<string>(); } catch (Exception) { return null; }
        }

        public static bool GetFlag(string key) => GetBool(key, false);
        public static void SetFlag(string key, bool value) => Set(key, JsonValue.Create(value), notify: false);

        // ---- Shortcuts ----
        public static Shortcut ToggleShortcut
        {
            get => LoadShortcut("shortcut.toggle") ?? Shortcut.ToggleDefault;
            set => SaveShortcut(value, "shortcut.toggle");
        }

        public static Shortcut ClearShortcut
        {
            get => LoadShortcut("shortcut.clear") ?? Shortcut.ClearDefault;
            set => SaveShortcut(value, "shortcut.clear");
        }

        private static Shortcut LoadShortcut(string key)
        {
            try
            {
                if (Get(key) is JsonObject o)
                {
                    uint code = o["keyCode"]!.GetValue<uint>();
                    uint mods = o["modifiers"]!.GetValue<uint>();
                    string label = o["display"]!.GetValue<string>();
                    return new Shortcut(code, mods, label);
                }
            }
            catch (Exception)
            {
                // fall back to the default
            }
            return null;
        }

        private static void SaveShortcut(Shortcut s, string key)
        {
            Set(key, new JsonObject { ["keyCode"] = s.KeyCode, ["modifiers"] = s.Modifiers, ["display"] = s.Display });
        }

        // ---- Pointer ----
        public const double PointerSizeMin = 0.08;
        public const double PointerSizeMax = 0.20;

        public static double PointerSize
        {
            get => Math.Clamp(GetDouble("pointerSize", 0.13), PointerSizeMin, PointerSizeMax);
            set => Set("pointerSize", JsonValue.Create(value));
        }

        public static bool ShowShadow
        {
            get => GetBool("showShadow", true);
            set => Set("showShadow", JsonValue.Create(value));
        }

        public static double FadeSeconds
        {
            get => GetDouble("fadeSeconds", 3);
            set => Set("fadeSeconds", JsonValue.Create(value));
        }

        public static bool HandSound
        {
            get => GetBool("sound.hands", true);
            set => Set("sound.hands", JsonValue.Create(value));
        }

        public static TapSoundChoice TapSound
        {
            get => TapSoundInfo.Parse(GetString("tapSound") ?? "") ?? TapSoundChoice.Thock;
            set => Set("tapSound", JsonValue.Create(value.Raw()));
        }

        public static bool LaserSound
        {
            get => GetBool("sound.laser", false);
            set => Set("sound.laser", JsonValue.Create(value));
        }

        public static bool InteractKeepsHand
        {
            get => GetBool("interactKeepsHand", false);
            set => Set("interactKeepsHand", JsonValue.Create(value));
        }

        public static bool DoubleShiftClickThrough
        {
            get => GetBool("doubleShiftClickThrough", true);
            set => Set("doubleShiftClickThrough", JsonValue.Create(value));
        }

        public static InteractKey InteractKey
        {
            get => InteractKeyInfo.Parse(GetString("interactKey") ?? "option");
            set => Set("interactKey", JsonValue.Create(value.Raw()));
        }

        public static InkChoice InkChoice
        {
            get => InkChoiceInfo.Parse(GetString("inkChoice") ?? "match");
            set => Set("inkChoice", JsonValue.Create(value.Raw()));
        }

        public static bool ShowToolbar
        {
            get => GetBool("showToolbar", true);
            set => Set("showToolbar", JsonValue.Create(value));
        }

        public static bool ShowInkButton
        {
            get => GetBool("showInkButton", true);
            set => Set("showInkButton", JsonValue.Create(value));
        }

        public const int MaxPinned = 8;

        public static List<PointerStyle> PinnedStyles
        {
            get
            {
                if (!(Get("pinnedStyles") is JsonArray raw))
                {
                    return PointerStyle.DefaultPinned.ToList();
                }
                var seen = new HashSet<PointerStyle>();
                var pinned = new List<PointerStyle>();
                foreach (var node in raw)
                {
                    string id = null;
                    try { id = node?.GetValue<string>(); } catch (Exception) { }
                    var style = id == null ? null : PointerStyle.FromId(id);
                    if (style != null && seen.Add(style))
                    {
                        pinned.Add(style);
                    }
                }
                return pinned.Count == 0 ? PointerStyle.DefaultPinned.ToList() : pinned;
            }
            set => Set("pinnedStyles", new JsonArray(value.Select(s => (JsonNode)JsonValue.Create(s.Id)).ToArray()));
        }

        public static string SavedStyleId
        {
            get => GetString("pointerStyle");
            set => Set("pointerStyle", JsonValue.Create(value), notify: false);
        }

        // ---- Startup (macOS: SMAppService; Windows: HKCU Run key) ----
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool LaunchAtLogin
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue("TinyHand") is string;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                    if (value)
                    {
                        key.SetValue("TinyHand", $"\"{AppPaths.ExePath}\"");
                    }
                    else
                    {
                        key.DeleteValue("TinyHand", false);
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"launch at login change failed: {e.Message}");
                }
                Changed?.Invoke();
            }
        }

        /// <summary>Keeps the Run entry pointing at this exe if the user moved it.</summary>
        public static void RefreshLaunchAtLoginPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key?.GetValue("TinyHand") is string current && current != $"\"{AppPaths.ExePath}\"")
                {
                    key.SetValue("TinyHand", $"\"{AppPaths.ExePath}\"");
                }
            }
            catch (Exception)
            {
                // not important
            }
        }

        public static void ResetAll()
        {
            foreach (var key in new[] { "shortcut.toggle", "shortcut.clear", "pointerSize", "showShadow", "fadeSeconds",
                         "sound.hands", "sound.laser", "interactKey", "interactKeepsHand", "doubleShiftClickThrough", "pinnedStyles", "pointerStyle",
                         "inkChoice", "showInkButton", "showToolbar", "tapSound" })
            {
                Remove(key);
            }
            Save();
            Changed?.Invoke();
        }
    }
}
