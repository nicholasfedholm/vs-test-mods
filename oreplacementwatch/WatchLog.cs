using System;
using System.IO;
using System.Threading;
using Vintagestory.API.Config;

namespace OrePlacementWatch
{
    /// <summary>
    /// Writes one line per event to Logs/oreplacementwatch-&lt;timestamp&gt;.log and keeps the counters
    /// reported by /orewatch status. Thread-safe; worldgen runs off the main thread.
    /// </summary>
    internal static class WatchLog
    {
        // Stop writing per-event lines past this so a bad world can't fill the disk; counters keep going
        private const int MaxEventLines = 20000;

        private static readonly object sync = new object();
        private static StreamWriter writer;
        private static int eventLines;

        public static string FilePath { get; private set; }

        private static long skippedIndex, skippedOutsideChunks, skippedInvalidInput, exceptionsSuppressed;

        public static long SkippedIndex => Interlocked.Read(ref skippedIndex);
        public static long SkippedOutsideChunks => Interlocked.Read(ref skippedOutsideChunks);
        public static long SkippedInvalidInput => Interlocked.Read(ref skippedInvalidInput);
        public static long Skipped => SkippedIndex + SkippedOutsideChunks + SkippedInvalidInput;
        public static long ExceptionsSuppressed => Interlocked.Read(ref exceptionsSuppressed);

        public static void Open()
        {
            lock (sync)
            {
                if (writer != null) return;
                try
                {
                    string dir = GamePaths.Logs;
                    Directory.CreateDirectory(dir);
                    FilePath = Path.Combine(dir, "oreplacementwatch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                    writer = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }
                catch (Exception)
                {
                    writer = null;
                    FilePath = null;
                }
            }
        }

        public static void Close()
        {
            lock (sync)
            {
                writer?.Dispose();
                writer = null;
            }
        }

        public static void CountSkip(SkipKind kind)
        {
            switch (kind)
            {
                case SkipKind.IndexOutOfBounds: Interlocked.Increment(ref skippedIndex); break;
                case SkipKind.OutsideGeneratingChunks: Interlocked.Increment(ref skippedOutsideChunks); break;
                default: Interlocked.Increment(ref skippedInvalidInput); break;
            }
        }

        public static void CountException() => Interlocked.Increment(ref exceptionsSuppressed);

        /// <summary>Informational line, not subject to the event cap.</summary>
        public static void Info(string message) => Write(message, false);

        /// <summary>SKIPPED / EXCEPTION_SUPPRESSED line, subject to the event cap.</summary>
        public static void Event(string message) => Write(message, true);

        private static void Write(string message, bool isEvent)
        {
            lock (sync)
            {
                if (writer == null) return;
                if (isEvent)
                {
                    if (eventLines == MaxEventLines)
                    {
                        message = "Event line cap (" + MaxEventLines + ") reached, further events are only counted (see /orewatch status)";
                    }
                    else if (eventLines > MaxEventLines)
                    {
                        return;
                    }
                    eventLines++;
                }
                try
                {
                    writer.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] [OREWATCH] " + message);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }

    internal enum SkipKind
    {
        IndexOutOfBounds,
        OutsideGeneratingChunks,
        InvalidInput
    }
}
