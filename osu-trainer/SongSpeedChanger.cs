using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NAudio.Wave;
using NAudio.Lame;
using NAudio.Vorbis;

namespace osu_trainer
{
    internal class SongSpeedChanger
    {
        private static readonly string DtHelperPath = Path.Combine("binaries", "osu-trainer-dthelper.exe");

        /// <summary>osu! install folder, set by MainForm once the osu! process has been seen.</summary>
        public static string OsuDirectory { get; set; }

        /// <summary>
        /// Folder holding osu!'s bass.dll / bass_fx.dll, or null. Falls back to the parent of the
        /// Songs folder, which is where it lives in a default install.
        /// </summary>
        private static string BassDirectory
        {
            get
            {
                string songs = Properties.Settings.Default.SongsFolder;
                foreach (string dir in new[] { OsuDirectory, string.IsNullOrEmpty(songs) ? null : Path.GetDirectoryName(songs.TrimEnd('\\')) })
                    if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "bass_fx.dll")) && File.Exists(Path.Combine(dir, "bass.dll")))
                        return dir;
                return null;
            }
        }

        /// <summary>
        /// True if maps meant to be played with DT can get DT-grid-aligned audio (see
        /// <see cref="GenerateForDT"/>). Requires the helper and osu!'s own bass_fx.dll.
        /// </summary>
        public static bool CanAlignToDT => File.Exists(DtHelperPath) && BassDirectory != null;

        public static void GenerateAudioFile(string inFile, string outFile, decimal effectiveMultiplier, bool changePitch = false, bool preDT = false, bool highQuality = false)
        {
            if (preDT && CanAlignToDT)
            {
                GenerateForDT(inFile, outFile, effectiveMultiplier, changePitch, highQuality);
                return;
            }

            decimal DTCompensatedMultiplier = effectiveMultiplier / 1.5M;

            string temp1 = Guid.NewGuid().ToString() + ".wav"; // decoded wav
            string temp2 = Guid.NewGuid().ToString() + ".wav"; // stretched file

            DecodeToWav(inFile, temp1);

            // stretch (or speed up) wav
            string quick = highQuality ? "" : "-quick";
            string naa = highQuality ? "" : "-naa";

            decimal multiplier = preDT ? DTCompensatedMultiplier : effectiveMultiplier;
            string tempo = $"-tempo={(multiplier - 1) * 100}";

            decimal cents = (decimal)(1200.0 * Math.Log((double)effectiveMultiplier) / Math.Log(2));
            decimal semitones = cents / 100.0M;
            string pitch = changePitch ? $"-pitch={semitones}" : "";

            RunSoundStretch($"\"{temp1}\" \"{temp2}\" {quick} {naa} {tempo} {pitch}");

            EncodeMp3(temp2, outFile, highQuality);

            DeleteQuietly(temp1, temp2);
        }

        /// <summary>
        /// Audio for a map that will be played with Double Time.
        ///
        /// Slowing the song down here and letting osu!'s DT speed it back up means two time-stretches,
        /// and their artifacts stack - that is why these maps used to sound so bad. Instead the file is
        /// laid out on the frame grid of osu!'s DT stretcher (bass_fx), so that DT reads unbroken runs of
        /// the target audio and reproduces it nearly losslessly. The helper does that against osu!'s own
        /// bass_fx.dll, so what we simulate is exactly what the game will play.
        ///
        /// The target audio is the song at the final rate: untouched at 1x, stretched once (pitch kept)
        /// or resampled (pitch follows speed) otherwise - one processing step instead of two.
        ///
        /// Caveat: the alignment holds when osu! plays the file from the start. Seeking (e.g. the Skip
        /// button) shifts DT's grid and quality drops to about the old method's level from there on.
        /// </summary>
        private static void GenerateForDT(string inFile, string outFile, decimal effectiveMultiplier, bool changePitch, bool highQuality)
        {
            string decoded = Guid.NewGuid().ToString() + ".wav";
            string target = Guid.NewGuid().ToString() + ".wav";
            string aligned = Guid.NewGuid().ToString() + ".wav";
            try
            {
                // decode with osu!'s own BASS: unlike NAudio it trims the mp3 encoder delay, so sample 0
                // here is sample 0 in game (NAudio's decode starts ~25 ms late)
                RunDtHelper("decode", inFile, decoded);

                if (Math.Abs(effectiveMultiplier - 1M) > 0.001M)
                {
                    // the one and only stretch, at full quality
                    string percent = ((effectiveMultiplier - 1M) * 100M).ToString("0.####", CultureInfo.InvariantCulture);
                    RunSoundStretch($"\"{decoded}\" \"{target}\" {(changePitch ? "-rate" : "-tempo")}={percent}");

                    // soundstretch starts its output early or late by a rate-dependent amount (the
                    // resampler too, less so); measure and undo it so the map stays on time
                    string rate = effectiveMultiplier.ToString("0.######", CultureInfo.InvariantCulture);
                    string latency = RunDtHelper("align", decoded, target, rate + (changePitch ? " pitch" : ""));
                    int shift = int.Parse(latency.Trim(), CultureInfo.InvariantCulture);
                    if (shift != 0)
                        ShiftWav(target, shift);
                }
                else
                {
                    File.Copy(decoded, target);
                }

                RunDtHelper("build", target, aligned);
                EncodeMp3(aligned, outFile, highQuality);

                // The grid only lines up if osu! decodes the mp3 starting exactly at our sample 0.
                // Measure the encoder/decoder delay with osu!'s own decoder and compensate it.
                int delay = int.Parse(RunDtHelper("delay", outFile, aligned).Trim(), CultureInfo.InvariantCulture);
                if (delay != 0)
                {
                    ShiftWav(aligned, -delay);
                    EncodeMp3(aligned, outFile, highQuality);
                }
            }
            finally
            {
                DeleteQuietly(decoded, target, aligned);
            }
        }

        private static void DecodeToWav(string inFile, string wavFile)
        {
            string ext = Path.GetExtension(inFile).ToLower();
            string copy = Guid.NewGuid().ToString() + ext;
            File.Copy(inFile, copy);
            try
            {
                if (ext == ".mp3")
                {
                    using (Mp3FileReader mp3 = new Mp3FileReader(copy))
                    using (WaveStream wav = WaveFormatConversionStream.CreatePcmStream(mp3))
                        WaveFileWriter.CreateWaveFile(wavFile, wav);
                }
                else if (ext == ".ogg")
                {
                    using (VorbisWaveReader vorbis = new VorbisWaveReader(copy))
                        WaveFileWriter.CreateWaveFile(wavFile, vorbis.ToWaveProvider16());
                }
                else
                {
                    throw new Exception($"audio file not supported: {ext}");
                }
            }
            finally
            {
                DeleteQuietly(copy);
            }
        }

        private static void EncodeMp3(string wavFile, string mp3File, bool highQuality)
        {
            if (File.Exists(mp3File))
                File.Delete(mp3File);
            using (var wav = new WaveFileReader(wavFile))
            using (var mp3 = new LameMP3FileWriter(mp3File, wav.WaveFormat, highQuality ? LAMEPreset.STANDARD : LAMEPreset.MEDIUM))
                wav.CopyTo(mp3);
        }

        /// <summary>Moves the audio in a 16-bit wav later (positive) or earlier (negative) by whole sample frames.</summary>
        private static void ShiftWav(string wavFile, int frames)
        {
            string shifted = Guid.NewGuid().ToString() + ".wav";
            using (var reader = new WaveFileReader(wavFile))
            using (var writer = new WaveFileWriter(shifted, reader.WaveFormat))
            {
                int blockAlign = reader.WaveFormat.BlockAlign;
                if (frames > 0)
                    writer.Write(new byte[frames * blockAlign], 0, frames * blockAlign);
                else
                    reader.Position = Math.Min(reader.Length, (long)-frames * blockAlign);
                reader.CopyTo(writer);
            }
            File.Delete(wavFile);
            File.Move(shifted, wavFile);
        }

        private static void RunSoundStretch(string arguments)
        {
            Process soundstretch = new Process();
            soundstretch.StartInfo.FileName = Path.Combine("binaries", "soundstretch.exe");
            soundstretch.StartInfo.Arguments = arguments;
            Console.WriteLine(soundstretch.StartInfo.Arguments);
            soundstretch.StartInfo.UseShellExecute = false;
            soundstretch.StartInfo.CreateNoWindow = true;
            soundstretch.Start();
            soundstretch.WaitForExit();
        }

        /// <summary>Runs the x86 helper against osu!'s bass dlls and returns its stdout.</summary>
        private static string RunDtHelper(string command, string arg1, string arg2, string arg3 = null)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var helper = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = DtHelperPath,
                    Arguments = $"{command} \"{BassDirectory}\" \"{arg1}\" \"{arg2}\"" + (arg3 == null ? "" : $" {arg3}"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            helper.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            helper.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            helper.Start();
            helper.BeginOutputReadLine();
            helper.BeginErrorReadLine();
            helper.WaitForExit();

            Console.Write(stdout);
            if (helper.ExitCode != 0)
                throw new Exception($"DT audio helper failed ({command}):{Environment.NewLine}{stderr}");
            // only the last line carries the result for "delay"; "build" just logs
            string[] lines = stdout.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[lines.Length - 1] : "";
        }

        private static void DeleteQuietly(params string[] files)
        {
            foreach (string f in files)
            {
                try { if (File.Exists(f)) File.Delete(f); }
                catch { } // don't shit the bed if we can't delete temp files
            }
        }
    }
}
