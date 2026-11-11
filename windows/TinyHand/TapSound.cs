using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TinyHand
{
    /// <summary>
    /// Plays the tap sound. Port of TapSound.swift: the WAVs are decoded once, four voices let quick
    /// taps overlap, every audio call runs on a worker thread (never the UI thread), and the device is
    /// let go after 30 s without taps. Uses winmm waveOut, which starts in a few milliseconds and
    /// mixes with other apps in shared mode.
    /// </summary>
    internal sealed class TapSound : IDisposable
    {
        private const int VoiceCount = 4;
        private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(30);

        private static Dictionary<TapSoundChoice, byte[]> pcmCache;
        private static Native.WAVEFORMATEX format;
        private static readonly object CacheGate = new object();

        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread worker;
        private readonly Voice[] voices = new Voice[VoiceCount];
        private readonly Timer idleTimer;
        private TapSoundChoice choice; // worker thread only
        private int next;

        /// <summary>Read and written on the UI thread.</summary>
        public bool Enabled = true;

        private sealed class Voice
        {
            public IntPtr Handle;
            public IntPtr Header;
            public IntPtr Data;
            public int Capacity;
            public bool Prepared;
        }

        public TapSound(TapSoundChoice? initial = null)
        {
            choice = initial ?? Preferences.TapSound;
            LoadAll();
            worker = new Thread(Run) { IsBackground = true, Name = "TinyHand tap sound", Priority = ThreadPriority.AboveNormal };
            worker.Start();
            idleTimer = new Timer(_ => Enqueue(CloseAll), null, Timeout.Infinite, Timeout.Infinite);
        }

        private static void LoadAll()
        {
            lock (CacheGate)
            {
                if (pcmCache != null)
                {
                    return;
                }
                pcmCache = new Dictionary<TapSoundChoice, byte[]>();
                foreach (var c in TapSoundInfo.All)
                {
                    var bytes = Resources.Bytes($"sounds/tap_{c.Raw()}.wav");
                    if (bytes != null && TryParseWav(bytes, out var fmt, out var pcm))
                    {
                        format = fmt;
                        pcmCache[c] = pcm;
                    }
                }
            }
        }

        /// <summary>Minimal RIFF reader: PCM "fmt " and "data" chunks.</summary>
        public static bool TryParseWav(byte[] b, out Native.WAVEFORMATEX fmt, out byte[] pcm)
        {
            fmt = default;
            pcm = null;
            if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            {
                return false;
            }
            int pos = 12;
            bool haveFmt = false;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > b.Length)
                {
                    size = b.Length - body;
                }
                if (id == "fmt " && size >= 16)
                {
                    fmt = new Native.WAVEFORMATEX
                    {
                        wFormatTag = BitConverter.ToUInt16(b, body),
                        nChannels = BitConverter.ToUInt16(b, body + 2),
                        nSamplesPerSec = BitConverter.ToUInt32(b, body + 4),
                        nAvgBytesPerSec = BitConverter.ToUInt32(b, body + 8),
                        nBlockAlign = BitConverter.ToUInt16(b, body + 12),
                        wBitsPerSample = BitConverter.ToUInt16(b, body + 14),
                        cbSize = 0,
                    };
                    haveFmt = fmt.wFormatTag == 1; // PCM
                }
                else if (id == "data")
                {
                    pcm = new byte[size];
                    Buffer.BlockCopy(b, body, pcm, 0, size);
                }
                pos = body + size + (size & 1);
            }
            return haveFmt && pcm != null;
        }

        public void Load(TapSoundChoice c) => Enqueue(() => choice = c);

        /// <summary>Opens the device ahead of the first tap.</summary>
        public void Warm() => Enqueue(() => OpenAll());

        public void Play()
        {
            if (!Enabled)
            {
                return;
            }
            Enqueue(PlayNow);
        }

        private void Enqueue(Action a)
        {
            if (!queue.IsAddingCompleted)
            {
                try
                {
                    queue.Add(a);
                }
                catch (InvalidOperationException)
                {
                    // disposed
                }
            }
        }

        private void Run()
        {
            foreach (var action in queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.Write($"tap sound error: {e.Message}");
                    CloseAll();
                }
            }
            CloseAll();
        }

        private bool OpenAll()
        {
            idleTimer.Change(IdleStop, Timeout.InfiniteTimeSpan);
            if (voices[0] != null)
            {
                return true;
            }
            if (format.nSamplesPerSec == 0)
            {
                return false;
            }
            var fmt = format;
            int largest = 0;
            foreach (var pcm in pcmCache.Values) largest = Math.Max(largest, pcm.Length);
            for (int i = 0; i < VoiceCount; i++)
            {
                uint result = Native.waveOutOpen(out IntPtr handle, Native.WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                if (result != 0)
                {
                    Log.Write($"waveOutOpen failed: {result}");
                    CloseAll();
                    return false;
                }
                voices[i] = new Voice
                {
                    Handle = handle,
                    Header = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WAVEHDR>()),
                    Data = Marshal.AllocHGlobal(largest),
                    Capacity = largest,
                };
            }
            return true;
        }

        private void PlayNow()
        {
            if (!pcmCache.TryGetValue(choice, out var pcm) || !OpenAll())
            {
                return;
            }
            var v = voices[next++ % VoiceCount];
            uint headerSize = (uint)Marshal.SizeOf<Native.WAVEHDR>();
            if (v.Prepared)
            {
                Native.waveOutReset(v.Handle);
                Native.waveOutUnprepareHeader(v.Handle, v.Header, headerSize);
                v.Prepared = false;
            }
            int length = Math.Min(pcm.Length, v.Capacity);
            Marshal.Copy(pcm, 0, v.Data, length);
            var header = new Native.WAVEHDR { lpData = v.Data, dwBufferLength = (uint)length };
            Marshal.StructureToPtr(header, v.Header, false);
            uint r = Native.waveOutPrepareHeader(v.Handle, v.Header, headerSize);
            if (r != 0)
            {
                Log.Write($"waveOutPrepareHeader failed: {r}; reopening the device");
                CloseAll();
                return;
            }
            v.Prepared = true;
            r = Native.waveOutWrite(v.Handle, v.Header, headerSize);
            if (r != 0)
            {
                Log.Write($"waveOutWrite failed: {r}; reopening the device");
                CloseAll();
            }
        }

        private void CloseAll()
        {
            uint headerSize = (uint)Marshal.SizeOf<Native.WAVEHDR>();
            for (int i = 0; i < VoiceCount; i++)
            {
                var v = voices[i];
                if (v == null)
                {
                    continue;
                }
                voices[i] = null;
                Native.waveOutReset(v.Handle);
                if (v.Prepared)
                {
                    Native.waveOutUnprepareHeader(v.Handle, v.Header, headerSize);
                }
                Native.waveOutClose(v.Handle);
                Marshal.FreeHGlobal(v.Header);
                Marshal.FreeHGlobal(v.Data);
            }
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            idleTimer.Dispose();
        }
    }
}
