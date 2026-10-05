using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DtHelper
{
    /// <summary>
    /// Minimal BASS / BASS_FX bindings. The DLLs are loaded from osu!'s own install folder so the
    /// time-stretcher we simulate is byte-for-byte the one osu! uses for Double Time. Those DLLs are
    /// 32-bit, which is the only reason this helper exists as a separate x86 process.
    /// </summary>
    internal static class Bass
    {
        public const int DECODE = 0x200000, FLOAT = 0x100, PRESCAN = 0x20000, FX_FREESOURCE = 0x10000;
        public const int UNICODE = unchecked((int)0x80000000);
        public const int STREAMPROC_END = unchecked((int)0x80000000);
        public const int DATA_FLOAT = 0x40000000;

        public const int ATTRIB_TEMPO = 0x10000;
        public const int ATTRIB_TEMPO_QUICKALGO = 0x10012;
        public const int ATTRIB_TEMPO_SEQUENCE_MS = 0x10013;
        public const int ATTRIB_TEMPO_OVERLAP_MS = 0x10015;

        [StructLayout(LayoutKind.Sequential)]
        public struct ChannelInfo { public int freq, chans, flags, ctype, origres, plugin, sample; public IntPtr filename; }

        public delegate int StreamProc(int handle, IntPtr buffer, int length, IntPtr user);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("bass.dll")] public static extern bool BASS_Init(int device, int freq, int flags, IntPtr win, IntPtr clsid);
        [DllImport("bass.dll")] public static extern int BASS_ErrorGetCode();
        [DllImport("bass.dll", CharSet = CharSet.Unicode)] public static extern int BASS_StreamCreateFile(bool mem, string file, long offset, long length, int flags);
        [DllImport("bass.dll")] public static extern int BASS_StreamCreate(int freq, int chans, int flags, StreamProc proc, IntPtr user);
        [DllImport("bass.dll")] public static extern int BASS_ChannelGetData(int handle, float[] buffer, int length);
        [DllImport("bass.dll")] public static extern bool BASS_ChannelGetInfo(int handle, out ChannelInfo info);
        [DllImport("bass.dll")] public static extern bool BASS_ChannelSetAttribute(int handle, int attrib, float value);
        [DllImport("bass.dll")] public static extern bool BASS_StreamFree(int handle);
        [DllImport("bass_fx.dll")] public static extern int BASS_FX_TempoCreate(int chan, int flags);

        public static void Load(string dir)
        {
            foreach (string dll in new[] { "bass.dll", "bass_fx.dll" })
            {
                string path = Path.Combine(dir, dll);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"{dll} not found in osu! folder", path);
                if (LoadLibrary(path) == IntPtr.Zero)
                    throw new Exception($"could not load {path} (error {Marshal.GetLastWin32Error()})");
            }
            // device 0 = no sound output, we only decode
            if (!BASS_Init(0, 44100, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Exception("BASS_Init failed: " + BASS_ErrorGetCode());
        }

        /// <summary>Decodes any file BASS can read (exactly like osu! does) to interleaved float.</summary>
        public static float[] Decode(string file, out int sampleRate, out int channels)
        {
            int h = BASS_StreamCreateFile(false, file, 0, 0, DECODE | FLOAT | PRESCAN | UNICODE);
            if (h == 0)
                throw new Exception($"BASS could not open {file}: error {BASS_ErrorGetCode()}");
            BASS_ChannelGetInfo(h, out ChannelInfo info);
            sampleRate = info.freq;
            channels = info.chans;
            float[] result = ReadAll(h, 1 << 20);
            BASS_StreamFree(h);
            return result;
        }

        /// <summary>
        /// Runs interleaved float audio through osu!'s Double Time: bass_fx tempo +50% with the
        /// settings osu! stable applies (read out of osu!.exe: quick seek on, 30 ms sequence,
        /// 4 ms overlap, automatic seek window).
        /// </summary>
        public static float[] DoubleTime(float[] audio, int sampleRate, int channels)
        {
            int readPos = 0;
            StreamProc proc = (handle, buffer, length, user) =>
            {
                int want = length / 4;
                int n = Math.Min(want, audio.Length - readPos);
                if (n > 0)
                    Marshal.Copy(audio, readPos, buffer, n);
                readPos += n;
                return n * 4 | (readPos >= audio.Length ? STREAMPROC_END : 0);
            };

            int src = BASS_StreamCreate(sampleRate, channels, DECODE | FLOAT, proc, IntPtr.Zero);
            if (src == 0)
                throw new Exception("BASS_StreamCreate failed: " + BASS_ErrorGetCode());
            int fx = BASS_FX_TempoCreate(src, DECODE | FX_FREESOURCE);
            if (fx == 0)
                throw new Exception("BASS_FX_TempoCreate failed: " + BASS_ErrorGetCode());

            BASS_ChannelSetAttribute(fx, ATTRIB_TEMPO, 50f);
            BASS_ChannelSetAttribute(fx, ATTRIB_TEMPO_QUICKALGO, 1f);
            BASS_ChannelSetAttribute(fx, ATTRIB_TEMPO_SEQUENCE_MS, 30f);
            BASS_ChannelSetAttribute(fx, ATTRIB_TEMPO_OVERLAP_MS, 4f);

            float[] result = ReadAll(fx, (int)(audio.Length / 1.5) + (1 << 16));
            BASS_StreamFree(fx);
            GC.KeepAlive(proc);
            return result;
        }

        private static float[] ReadAll(int handle, int initialCapacity)
        {
            var result = new float[Math.Max(initialCapacity, 1 << 16)];
            var chunk = new float[1 << 16];
            int total = 0, got;
            while ((got = BASS_ChannelGetData(handle, chunk, chunk.Length * 4 | DATA_FLOAT)) > 0)
            {
                int n = got / 4;
                if (total + n > result.Length)
                    Array.Resize(ref result, Math.Max(result.Length * 2, total + n));
                Array.Copy(chunk, 0, result, total, n);
                total += n;
            }
            Array.Resize(ref result, total);
            return result;
        }
    }
}
