using System;

namespace DtHelper
{
    /// <summary>
    /// Builds a "pre-DT" audio file that sounds like the original once osu! plays it with Double Time.
    ///
    /// Slowing a song down with any stretcher and then letting osu!'s DT speed it back up stacks two
    /// sets of artifacts. Instead we exploit that DT is deterministic: bass_fx's SoundTouch stretcher
    /// works in frames, each reading W samples, emitting W-O of them (O = crossfade overlap) and
    /// advancing its input by P = 1.5*(W-O). If every frame finds an unbroken run of the original
    /// exactly where it reads, the crossfades join identical material and DT reproduces the original.
    ///
    /// Where a frame reads is decided by a correlation search we cannot fully predict (this bass_fx
    /// does not match any public SoundTouch release - e.g. some frames only search a tiny window), so
    /// the layout is refined in a closed loop with the real DLL: build, run DT, measure where every
    /// frame actually read, move the run there, repeat.
    /// </summary>
    internal class GridBuilder
    {
        private const double Tempo = 1.5;
        private const int XFade = 32;     // smooths the splices between runs; DT never reads them when aligned
        private const int ProbeLen = 256; // samples compared when locating a frame's read position
        private const int ProbeSkip = 64; // start the probe this far into the frame's pure-copy region

        private readonly int sr, ch;
        private readonly int W, O, S, L;
        private readonly double P;

        private int Mid => (S + 1) / 2 / 16 * 16;

        private const int NarrowOff = 9;  // narrow frames only land on odd offsets in ~[0,16)

        public int Iterations { get; private set; }
        public int Frames { get; private set; }
        public int Splices { get; private set; }

        public GridBuilder(int sampleRate, int channels)
        {
            sr = sampleRate;
            ch = channels;
            // SoundTouch's own parameter derivation for osu!'s settings (30 ms / 4 ms / auto seek)
            O = Math.Max(sr * 4 / 1000, 16);
            O -= O % 8;
            W = Math.Max(sr * 30 / 1000, 2 * O);
            double seekMs = 20.0 + (15.0 - 20.0) / 1.5 * (Tempo - 0.5);
            S = sr * (int)(seekMs + 0.5) / 1000;
            L = W - O;
            P = Tempo * L;
        }

        public float[] Build(float[] original, int maxIterations, Action<string> log)
        {
            int nIn = original.Length / ch;
            int nOut = (int)(nIn * Tempo);
            long[] pos = Grid(nOut);
            int nfr = pos.Length;

            // Initial guess: the middle of the search window (which the search favours), snapped to the
            // 16-sample step its coarse pass scans so the exact match is hit directly. On frames where
            // this bass_fx only searches the very start of the window, aim there instead.
            bool[] narrow = FindNarrowFrames(nOut, pos);
            var off = new long[nfr];
            int nNarrow = 0;
            for (int k = 1; k < nfr; k++)
            {
                off[k] = narrow[k] ? NarrowOff : Mid;
                if (narrow[k]) nNarrow++;
            }
            log?.Invoke($"{nfr} frames, {nNarrow} with a narrow search window");

            float[] best = null;
            int bestSplices = int.MaxValue, sinceImproved = 0;
            for (int it = 0; it < maxIterations && sinceImproved < 2; it++)
            {
                float[] y = Layout(original, nIn, nOut, pos, off);
                float[] z = Bass.DoubleTime(y, sr, ch);
                long[] actual = FindReads(y, z, pos, off, -24, S + 40, out int framesSeen);

                // A frame that reads somewhere other than planned is harmless as long as it is off by
                // the same amount as the frame before it: DT then just follows the original a few samples
                // late and every crossfade still joins identical audio. Only a change in that drift (or a
                // frame that read garbage) is an audible splice.
                int splices = 0, drifted = 0;
                for (int k = 1; k < framesSeen; k++)
                {
                    if (actual[k] != off[k]) drifted++;
                    if (actual[k] == long.MinValue || actual[k - 1] == long.MinValue ||
                        actual[k] - off[k] != actual[k - 1] - off[k - 1])
                        splices++;
                }
                log?.Invoke($"iteration {it}: {splices} splices, {drifted} frames drifted");

                if (splices < bestSplices)
                {
                    bestSplices = splices;
                    best = y;
                    Iterations = it + 1;
                    sinceImproved = 0;
                }
                else sinceImproved++;
                Frames = framesSeen;
                Splices = bestSplices;
                if (splices == 0)
                    break;

                // Adopt where DT really read. Its search found the best match there, and once the right
                // material is placed at that spot it becomes an exact match, so it tends to stay.
                for (int k = 1; k < framesSeen; k++)
                    if (actual[k] != long.MinValue)
                        off[k] = actual[k];
            }
            return best;
        }

        /// <summary>
        /// Frames on which this bass_fx's quick seek only looks at roughly the first 16 offsets. It is
        /// positional, not content dependent (identical for any input of the same length and format),
        /// so find it by running DT over two unrelated noise signals and keeping frames where both
        /// read from the very start of the window.
        /// </summary>
        private bool[] FindNarrowFrames(int nOut, long[] pos)
        {
            var narrow = new bool[pos.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                var rng = new Random(1234 + pass);
                var noise = new float[(long)nOut * ch];
                for (long i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() - 0.5) * 0.5f;
                float[] z = Bass.DoubleTime(noise, sr, ch);
                long[] reads = FindReads(noise, z, pos, null, -8, 32, out int seen);
                for (int k = 1; k < pos.Length; k++)
                {
                    bool small = k < seen && reads[k] != long.MinValue;
                    narrow[k] = pass == 0 ? small : narrow[k] && small;
                }
            }
            return narrow;
        }

        /// <summary>Input position at which DT starts frame k (before its seek offset).</summary>
        private long[] Grid(int nOut)
        {
            int nfr = (int)(nOut / P) + 2;
            var pos = new long[nfr];
            long p = 0;
            // the first frame skips the crossfade, which SoundTouch compensates in the input skip
            double frac = -(int)(Tempo * O + 0.5);
            for (int k = 0; k < nfr; k++)
            {
                pos[k] = p;
                frac += P;
                long a = (long)frac;
                frac -= a;
                p += a;
            }
            return pos;
        }

        /// <summary>Output sample at which frame k starts: frame 0 emits W-2O, later ones W-O.</summary>
        private long FrameOutStart(int k) => k == 0 ? 0 : (W - 2 * O) + (long)(k - 1) * L;

        private float[] Layout(float[] x, int nIn, int nOut, long[] pos, long[] off)
        {
            int nfr = pos.Length;
            var y = new float[(long)nOut * ch];
            var r = new long[nfr];
            for (int k = 0; k < nfr; k++) r[k] = pos[k] + off[k];

            for (int k = 0; k < nfr; k++)
            {
                // frame k owns the slow-file span between the midpoints of the unread gaps around it
                long a = k == 0 ? 0 : (r[k - 1] + W + r[k]) / 2;
                long b = k == nfr - 1 ? nOut : (r[k] + W + r[k + 1]) / 2;
                a = Math.Max(a, 0);
                b = Math.Min(b, nOut);
                long shift = FrameOutStart(k) - r[k]; // slow-file index -> original index
                long prevShift = k > 0 ? FrameOutStart(k - 1) - r[k - 1] : 0;

                for (long t = a; t < b; t++)
                {
                    long i = t + shift;
                    double w = 1.0;
                    long iPrev = 0;
                    bool fade = k > 0 && t - a < XFade && b - a > XFade;
                    if (fade)
                    {
                        w = (t - a) / (double)(XFade - 1);
                        iPrev = t + prevShift;
                    }
                    for (int c = 0; c < ch; c++)
                    {
                        double v = Sample(x, nIn, i, c);
                        if (fade)
                            v = Sample(x, nIn, iPrev, c) * (1 - w) + v * w;
                        y[t * ch + c] = (float)v;
                    }
                }
            }
            return y;
        }

        private float Sample(float[] x, int nIn, long i, int c) =>
            i < 0 || i >= nIn ? 0f : x[i * ch + c];

        /// <summary>
        /// Where each frame of the DT output actually read from the slow file, as an offset from
        /// <see cref="Grid"/>'s position, searched within [lo, hi]. long.MinValue = no exact match.
        /// </summary>
        private long[] FindReads(float[] y, float[] z, long[] pos, long[] planned, int lo, int hi, out int framesSeen)
        {
            int nfr = pos.Length;
            float[] ym = ToMono(y), zm = ToMono(z);
            long ny = ym.Length, nz = zm.Length;
            var actual = new long[nfr];
            for (int k = 0; k < nfr; k++) actual[k] = long.MinValue;
            actual[0] = 0;

            int seen = 1;
            while (seen < nfr && FrameOutStart(seen) + O + ProbeSkip + ProbeLen <= nz) seen++;
            framesSeen = seen;

            // frames are independent here, so search them in parallel
            System.Threading.Tasks.Parallel.For(1, seen, k =>
            {
                long o = FrameOutStart(k) + O + ProbeSkip;
                double energy = 0;
                for (int j = 0; j < ProbeLen; j++) energy += (double)zm[o + j] * zm[o + j];
                double tol = 1e-3 * energy + 1e-6;

                // most frames read where we planned - check that first
                if (planned != null)
                {
                    long at = pos[k] + planned[k] + O + ProbeSkip;
                    if (at >= 0 && at + ProbeLen <= ny && Ssd(ym, at, zm, o, tol) < tol)
                    {
                        actual[k] = planned[k];
                        return;
                    }
                }

                long first = Math.Max(pos[k] + lo, 0), last = Math.Min(pos[k] + hi, ny - ProbeLen - O - ProbeSkip);
                double bestD = tol; // only exact matches are of interest
                long bestR = long.MinValue;
                for (long rr = first; rr <= last; rr++)
                {
                    double d = Ssd(ym, rr + O + ProbeSkip, zm, o, bestD);
                    if (d < bestD) { bestD = d; bestR = rr; }
                }
                if (bestR != long.MinValue)
                    actual[k] = bestR - pos[k];
            });
            return actual;
        }

        private float[] ToMono(float[] a)
        {
            var m = new float[a.Length / ch];
            for (long i = 0; i < m.Length; i++)
            {
                float v = 0;
                for (int c = 0; c < ch; c++) v += a[i * ch + c];
                m[i] = v;
            }
            return m;
        }

        // sum of squared differences of y[ys..] and z[zs..] over the probe, bailing out above limit
        private static double Ssd(float[] y, long ys, float[] z, long zs, double limit)
        {
            double d = 0;
            for (int j = 0; j < ProbeLen; j++)
            {
                double e = y[ys + j] - z[zs + j];
                d += e * e;
                if ((j & 15) == 15 && d > limit) return d;
            }
            return d;
        }
    }
}
