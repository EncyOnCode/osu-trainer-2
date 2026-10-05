using System;
using System.Globalization;
using System.IO;

namespace DtHelper
{
    /// <summary>
    /// x86 companion of osu-trainer that drives osu!'s own (32-bit) bass / bass_fx.
    ///
    ///   decode &lt;osuDir&gt; &lt;in&gt; &lt;out.wav&gt;   decode exactly as osu! does (BASS trims mp3 encoder delay)
    ///   build &lt;osuDir&gt; &lt;in&gt; &lt;out.wav&gt;    lay &lt;in&gt; out on the DT grid (see GridBuilder)
    ///   delay &lt;osuDir&gt; &lt;encoded&gt; &lt;ref&gt;  print by how many samples BASS's decode of &lt;encoded&gt; lags &lt;ref&gt;
    ///   align &lt;osuDir&gt; &lt;orig&gt; &lt;stretched&gt; &lt;rate&gt; [pitch]  print how many samples &lt;stretched&gt; must be
    ///                                     delayed to line up with &lt;orig&gt; played at &lt;rate&gt; (stretcher latency);
    ///                                     "pitch" = &lt;stretched&gt; was resampled rather than time-stretched
    ///
    /// Exit code 0 on success; errors go to stderr.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length < 4)
                    throw new ArgumentException("usage: decode|build <osuDir> <in> <out.wav> | delay <osuDir> <encoded> <ref>");

                Bass.Load(args[1]);
                switch (args[0])
                {
                    case "decode": return DecodeCommand(args[2], args[3]);
                    case "build": return BuildCommand(args[2], args[3]);
                    case "delay": return DelayCommand(args[2], args[3]);
                    case "align": return AlignCommand(args[2], args[3], double.Parse(args[4], CultureInfo.InvariantCulture),
                                                      args.Length > 5 && args[5] == "pitch");
                    default: throw new ArgumentException("unknown command " + args[0]);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.ToString());
                return 1;
            }
        }

        private static int DecodeCommand(string inFile, string outFile)
        {
            float[] x = Bass.Decode(inFile, out int sr, out int ch);
            WriteWav16(outFile, x, sr, ch);
            return 0;
        }

        private static int BuildCommand(string inFile, string outFile)
        {
            float[] x = Bass.Decode(inFile, out int sr, out int ch);
            var builder = new GridBuilder(sr, ch);
            float[] y = builder.Build(x, 12, Console.WriteLine);
            WriteWav16(outFile, y, sr, ch);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "done: {0} frames, {1} splices, best after {2} iterations",
                builder.Frames, builder.Splices, builder.Iterations));
            return 0;
        }

        private static int DelayCommand(string encodedFile, string refFile)
        {
            float[] enc = Bass.Decode(encodedFile, out int sr, out int ch);
            float[] reference = Bass.Decode(refFile, out int sr2, out int ch2);
            if (sr != sr2 || ch != ch2)
                throw new Exception($"format mismatch: {sr}/{ch} vs {sr2}/{ch2}");
            Console.WriteLine(FindDelay(enc, reference, ch).ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int AlignCommand(string origFile, string stretchedFile, double rate, bool resampled)
        {
            float[] orig = Bass.Decode(origFile, out int sr, out int ch);
            float[] st = Bass.Decode(stretchedFile, out int sr2, out int ch2);
            if (sr != sr2 || ch != ch2)
                throw new Exception($"format mismatch: {sr}/{ch} vs {sr2}/{ch2}");
            Console.WriteLine(StretchLatency(orig, st, ch, rate, resampled).ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        /// <summary>
        /// A time-stretcher's output leads or lags the ideal timeline (stretched[t] = orig[t * rate]).
        /// WSOLA output is made of verbatim copies of the input, so blocks of it can be located in the
        /// original directly (for resampled audio, in the original read at <paramref name="rate"/>x);
        /// the median over many loud blocks gives the latency. Returns how many samples the stretched
        /// audio must be delayed by (negative = trimmed).
        /// </summary>
        private static int StretchLatency(float[] orig, float[] st, int ch, double rate, bool resampled)
        {
            const int Block = 1024, Range = 3000, Blocks = 24;
            long nO = orig.Length / ch, nS = st.Length / ch;
            Func<float[], long, double> mono = (a, i) =>
            {
                double v = 0;
                for (int c = 0; c < ch; c++) v += a[i * ch + c];
                return v;
            };
            var found = new System.Collections.Generic.List<double>();
            for (int b = 1; b <= Blocks; b++)
            {
                long s0 = nS * b / (Blocks + 1);
                long expect = (long)(s0 * rate);
                if (s0 + Block >= nS || expect - Range < 0 || expect + Range + (long)(Block * rate) + 2 >= nO) continue;

                double qq = 0;
                var q = new double[Block];
                for (int j = 0; j < Block; j++) { q[j] = mono(st, s0 + j); qq += q[j] * q[j]; }
                if (qq < 1e-3 * Block) continue; // too quiet to match reliably

                double bestC = -1; long bestE = 0;
                for (long e = -Range; e <= Range; e++)
                {
                    double xy = 0, xx = 0;
                    for (int j = 0; j < Block; j++)
                    {
                        double v;
                        if (resampled)
                        {
                            double at = expect + e + j * rate;
                            long i0 = (long)at;
                            double f = at - i0;
                            v = mono(orig, i0) * (1 - f) + mono(orig, i0 + 1) * f;
                        }
                        else
                            v = mono(orig, expect + e + j);
                        xy += v * q[j]; xx += v * v;
                    }
                    double c = xx > 0 ? xy / Math.Sqrt(xx * qq) : 0;
                    if (c > bestC) { bestC = c; bestE = e; }
                }
                // only trust near-verbatim matches
                if (bestC > 0.9)
                    found.Add(bestE / rate);
            }
            if (found.Count == 0)
                return 0;
            found.Sort();
            // stretched shows orig from e samples later than ideal -> it runs early -> delay it
            return (int)Math.Round(found[found.Count / 2]);
        }

        /// <summary>d such that enc[t + d] ~= ref[t]. Searches +-4000 samples around the loudest block.</summary>
        private static int FindDelay(float[] enc, float[] reference, int ch)
        {
            const int Block = 4096, Range = 4000;
            long nRef = reference.Length / ch, nEnc = enc.Length / ch;
            Func<float[], long, double> mono = (a, i) =>
            {
                double s = 0;
                for (int c = 0; c < ch; c++) s += a[i * ch + c];
                return s;
            };

            // probe with the loudest block of the first ~60 s so silence can't fool us
            long start = Range, limit = Math.Min(nRef, Math.Min(nEnc, 60L * 48000)) - Block - Range;
            if (limit <= start)
                return 0;
            double bestE = -1;
            for (long s0 = Range; s0 < limit; s0 += Block)
            {
                double e = 0;
                for (int j = 0; j < Block; j += 4) { double v = mono(reference, s0 + j); e += v * v; }
                if (e > bestE) { bestE = e; start = s0; }
            }

            var q = new double[Block];
            for (int j = 0; j < Block; j++) q[j] = mono(reference, start + j);

            int bestD = 0;
            double bestErr = double.MaxValue;
            for (int d = -Range; d <= Range; d++)
            {
                double err = 0;
                for (int j = 0; j < Block && err < bestErr; j++)
                {
                    double e = mono(enc, start + d + j) - q[j];
                    err += e * e;
                }
                if (err < bestErr) { bestErr = err; bestD = d; }
            }
            return bestD;
        }

        private static void WriteWav16(string path, float[] data, int sr, int ch)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int bytes = data.Length * 2;
                w.Write("RIFF".ToCharArray()); w.Write(36 + bytes);
                w.Write("WAVEfmt ".ToCharArray()); w.Write(16);
                w.Write((short)1); w.Write((short)ch); w.Write(sr);
                w.Write(sr * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
                w.Write("data".ToCharArray()); w.Write(bytes);
                foreach (float f in data)
                {
                    double v = Math.Round(f * 32767.0);
                    w.Write((short)Math.Max(-32768, Math.Min(32767, v)));
                }
            }
        }
    }
}
