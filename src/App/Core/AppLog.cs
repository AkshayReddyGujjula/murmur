using System;
using System.IO;
using System.Text;

namespace Murmur.App.Core
{
    public sealed class AppLog
    {
        private readonly string filePath;
        private readonly object sync = new object();
        private const long MaxBytes = 524288;

        public AppLog(string path)
        {
            filePath = path;
        }

        public void Info(string message)
        {
            Write("INFO", message, null);
        }

        public void Error(string message, Exception error)
        {
            Write("ERROR", message, error);
        }

        private void Write(string level, string message, Exception error)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    return;
                }
                string clean = Flatten(message);
                if (error != null)
                {
                    string detail;
                    try
                    {
                        detail = Flatten(error.GetType().FullName + ": " + error.Message);
                    }
                    catch
                    {
                        detail = "error";
                    }
                    if (clean.Length == 0)
                    {
                        clean = detail;
                    }
                    else if (detail.Length > 0)
                    {
                        clean = clean + " | " + detail;
                    }
                }
                string timestamp;
                try
                {
                    timestamp = DateTime.UtcNow.ToString("o");
                }
                catch
                {
                    timestamp = "unknown-time";
                }
                string line = timestamp + " " + level + " " + clean;
                lock (sync)
                {
                    try
                    {
                        string directory = Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                    }
                    catch
                    {
                    }
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            long length = 0;
                            try
                            {
                                FileInfo info = new FileInfo(filePath);
                                length = info.Length;
                            }
                            catch
                            {
                            }
                            if (length > MaxBytes)
                            {
                                string oldPath = OldPathFor(filePath);
                                try
                                {
                                    if (File.Exists(oldPath))
                                    {
                                        File.Delete(oldPath);
                                    }
                                }
                                catch
                                {
                                }
                                try
                                {
                                    File.Move(filePath, oldPath);
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                    try
                    {
                        File.AppendAllText(filePath, line + "\r\n", new UTF8Encoding(false));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static string Flatten(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            string v = value.Replace("\r", " ").Replace("\n", " ");
            return v.Trim();
        }

        private static string OldPathFor(string path)
        {
            if (path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(0, path.Length - 4) + ".old.log";
            }
            return path + ".old.log";
        }
    }
}
