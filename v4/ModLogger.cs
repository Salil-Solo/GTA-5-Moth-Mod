using System;
using System.IO;
using System.Text;

namespace QuantumRealityShiftV4
{
    /// <summary>
    /// Thread-safe file logger for Quantum Reality Shift V4.
    /// Writes timestamped mod actions and Moth Atlas API requests/responses
    /// (excluding API keys and presigned URL tokens) to GTA V\scripts\QuantumRealityV4.log.
    /// </summary>
    public static class ModLogger
    {
        private static readonly object _lock = new object();
        private static string _logPath = null;
        private const long MaxLogBytes = 5 * 1024 * 1024; // 5 MB rotation threshold

        public static string LogFilePath => _logPath;

        public static void Init(string scriptDirectory)
        {
            if (string.IsNullOrWhiteSpace(scriptDirectory))
                return;

            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(scriptDirectory);
                    _logPath = Path.Combine(scriptDirectory, "QuantumRealityV4.log");

                    if (File.Exists(_logPath))
                    {
                        var info = new FileInfo(_logPath);
                        if (info.Length > MaxLogBytes)
                        {
                            string backupPath = Path.Combine(scriptDirectory, "QuantumRealityV4.prev.log");
                            if (File.Exists(backupPath))
                                File.Delete(backupPath);
                            File.Move(_logPath, backupPath);
                        }
                    }

                    AppendLineUnsafe("INFO", "============================================================");
                    AppendLineUnsafe("INFO", "Quantum Reality Shift V4 Session Started");
                    AppendLineUnsafe("INFO", "============================================================");
                }
                catch
                {
                    _logPath = null;
                }
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Api(string message) => Write("ATLAS", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            if (string.IsNullOrEmpty(_logPath) || string.IsNullOrEmpty(message))
                return;

            lock (_lock)
            {
                AppendLineUnsafe(level, message);
            }
        }

        private static void AppendLineUnsafe(string level, string message)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string cleanMsg = message.Replace("\r", " ").Replace("\n", " ");
                File.AppendAllText(_logPath, $"[{stamp}] [{level}] {cleanMsg}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Never crash GTA V due to log I/O locks
            }
        }
    }
}
