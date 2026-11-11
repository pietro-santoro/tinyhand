using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace TinyHand
{
    /// <summary>
    /// Handles tinyhand:// links and mirrors the app state to %APPDATA%\TinyHand\state.json, so
    /// launchers and scripts can drive TinyHand and read what it is doing. Port of Automation.swift.
    /// </summary>
    internal sealed class Automation
    {
        public static string StatePath => Path.Combine(AppPaths.Roaming, "state.json");

        private readonly OverlayController overlay;

        public Automation(OverlayController overlay)
        {
            this.overlay = overlay;
            CustomHands.Changed += WriteState;
        }

        public void Handle(string link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("tinyhand", StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"ignored link: {link}");
                return;
            }
            string command = uri.Host.ToLowerInvariant();
            string argument = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (argument != null)
            {
                argument = Uri.UnescapeDataString(argument);
            }
            Log.Write($"url command: {command} {argument ?? ""}");

            switch (command)
            {
                case "on":
                    overlay.Activate();
                    break;
                case "off":
                    overlay.Deactivate();
                    break;
                case "toggle":
                    overlay.Toggle();
                    break;
                case "clear":
                    overlay.ClearInk();
                    break;
                case "style":
                    SelectStyle(argument);
                    break;
                case "sound":
                    SetSound(argument);
                    break;
                default:
                    Log.Write($"unknown url command: {command}");
                    break;
            }
        }

        private void SelectStyle(string id)
        {
            var style = id == null ? null : PointerStyle.FromId(id);
            if (style == null)
            {
                Log.Write($"unknown pointer style: {id ?? "none"}");
                return;
            }
            overlay.Pick(style);
            overlay.Activate();
        }

        private void SetSound(string value)
        {
            switch (value)
            {
                case "on":
                    overlay.SoundOn = true;
                    break;
                case "off":
                    overlay.SoundOn = false;
                    break;
                case "toggle":
                case null:
                    overlay.SoundOn = !overlay.SoundOn;
                    break;
                default:
                    Log.Write($"unknown sound value: {value}");
                    break;
            }
        }

        /// <summary>Same schema as the Mac: active, pid, soundOn, style, styles[{id,name,type}], keys sorted.</summary>
        public void WriteState()
        {
            try
            {
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    json.WriteStartObject();
                    json.WriteBoolean("active", overlay.IsActive);
                    json.WriteNumber("pid", Environment.ProcessId);
                    json.WriteBoolean("soundOn", overlay.SoundOn);
                    json.WriteString("style", overlay.Style.Id);
                    json.WriteStartArray("styles");
                    foreach (var style in PointerStyle.All)
                    {
                        string type = style.IsLaser ? "laser" : style.IsCustom ? "custom" : "hand";
                        json.WriteStartObject();
                        json.WriteString("id", style.Id);
                        json.WriteString("name", style.Title);
                        json.WriteString("type", type);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                Directory.CreateDirectory(AppPaths.Roaming);
                string tmp = StatePath + ".tmp";
                File.WriteAllBytes(tmp, buffer.ToArray());
                File.Move(tmp, StatePath, true);
            }
            catch (Exception e)
            {
                Log.Write($"state write failed: {e.Message}");
            }
        }

        public static void RemoveState()
        {
            try
            {
                File.Delete(StatePath);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        /// <summary>
        /// Registers tinyhand:// for the current user (HKCU\Software\Classes\tinyhand), and updates it
        /// if the exe moved. No admin rights needed.
        /// </summary>
        public static void RegisterUrlScheme()
        {
            try
            {
                string exe = AppPaths.ExePath;
                string command = $"\"{exe}\" \"%1\"";
                using var existing = Registry.CurrentUser.OpenSubKey(@"Software\Classes\tinyhand\shell\open\command");
                if (existing?.GetValue(null) as string == command)
                {
                    return;
                }
                using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\tinyhand");
                root.SetValue(null, "URL:TinyHand Protocol");
                root.SetValue("URL Protocol", "");
                using (var icon = root.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(null, $"\"{exe}\",0");
                }
                using (var cmd = root.CreateSubKey(@"shell\open\command"))
                {
                    cmd.SetValue(null, command);
                }
                Log.Write($"registered tinyhand:// for {exe}");
            }
            catch (Exception e)
            {
                Log.Write($"could not register tinyhand://: {e.Message}");
            }
        }
    }

    /// <summary>
    /// One TinyHand per user session: a named mutex says who runs, and a named pipe carries
    /// tinyhand:// links from a second launch to the running one (macOS does this through
    /// application(_:open:)).
    /// </summary>
    internal static class SingleInstance
    {
        private static Mutex mutex;

        private static string Suffix
        {
            get
            {
                string sid;
                try
                {
                    sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
                }
                catch (Exception)
                {
                    sid = Environment.UserName;
                }
                int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                return $"{sid}-{session}";
            }
        }

        private static string PipeName => "TinyHand-" + Suffix;

        /// <returns>true if this process is the first one.</returns>
        public static bool Claim()
        {
            mutex = new Mutex(true, @"Local\TinyHand-" + Suffix, out bool created);
            if (!created)
            {
                mutex.Dispose();
                mutex = null;
            }
            return created;
        }

        public static bool Forward(string[] links)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                    client.Connect(500);
                    var payload = Encoding.UTF8.GetBytes(string.Join("\n", links) + "\n");
                    client.Write(payload, 0, payload.Length);
                    client.Flush();
                    return true;
                }
                catch (Exception)
                {
                    // The first instance may still be starting its pipe server.
                    Thread.Sleep(200);
                }
            }
            return false;
        }

        public static void Listen(Action<string> onLink)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                        server.WaitForConnection();
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            line = line.Trim();
                            if (line.Length > 0 && line.Length < 2048)
                            {
                                onLink(line);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Write($"link pipe error: {e.Message}");
                        Thread.Sleep(500);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "TinyHand link pipe",
            };
            thread.Start();
        }
    }
}
