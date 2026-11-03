using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Murmur.App
{
    // A startup failure must never vanish because the app is a winexe. Reports contain only the
    // exception type, message, stage and stack: never transcript, prompt, auth or settings content.
    internal static class StartupFailure
    {
        internal const string ReportFileName = "startup-error.txt";
        internal const int SummaryLimit = 500;
        private const int StageLimit = 120;
        private const int StackLimit = 4000;

        public static string FormatSummary(Exception error)
        {
            if (error == null) return "Unknown error.";
            string type;
            try
            {
                type = error.GetType().FullName;
            }
            catch (Exception)
            {
                type = "Exception";
            }
            string message = Flatten(error.Message);
            if (message.Length > SummaryLimit)
                message = message.Substring(0, SummaryLimit).TrimEnd() + "...";
            return message.Length == 0 ? type : type + ": " + message;
        }

        public static string FormatMessage(Exception error, string reportPath)
        {
            return FormatMessage(error, reportPath, "Murmur could not start.");
        }

        public static string FormatMessage(Exception error, string reportPath, string headline)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(String.IsNullOrEmpty(headline) ? "Murmur stopped unexpectedly." : headline);
            text.AppendLine();
            text.AppendLine(FormatSummary(error));
            text.AppendLine();
            text.AppendLine("Close any other Murmur windows, then start Murmur again.");
            text.AppendLine();
            if (String.IsNullOrEmpty(reportPath))
            {
                text.AppendLine("Murmur could not write a details file. Move Murmur to a folder you can write to, then try again.");
            }
            else
            {
                text.AppendLine("Details were saved to:");
                text.AppendLine(reportPath);
            }
            return text.ToString();
        }

        public static string WriteReport(string preferredDirectory, string fallbackDirectory, Exception error, string stage, DateTime utcNow)
        {
            string content = BuildReport(error, stage, utcNow);
            string path = TryWrite(preferredDirectory, content);
            if (path != null) return path;
            return TryWrite(fallbackDirectory, content);
        }

        public static string FallbackDirectory()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!String.IsNullOrEmpty(local)) return Path.Combine(local, "Murmur");
            }
            catch (Exception)
            {
            }
            try
            {
                return Path.Combine(Path.GetTempPath(), "Murmur");
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void ShowMessage(string text)
        {
            try
            {
                MessageBox.Show(text, "Murmur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
            }
        }

        private static string BuildReport(Exception error, string stage, DateTime utcNow)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Murmur startup error");
            text.AppendLine("Time (UTC): " + utcNow.ToString("o"));
            text.AppendLine("Stage: " + Truncate(Flatten(stage), StageLimit));
            if (error == null)
            {
                text.AppendLine("Type: Unknown");
                text.AppendLine("Message: Unknown error.");
                return text.ToString();
            }
            try
            {
                text.AppendLine("Type: " + error.GetType().FullName);
            }
            catch (Exception)
            {
                text.AppendLine("Type: Exception");
            }
            text.AppendLine("Message: " + Truncate(Flatten(error.Message), SummaryLimit));
            try
            {
                string stack = error.StackTrace;
                if (!String.IsNullOrEmpty(stack))
                {
                    text.AppendLine("Stack:");
                    text.AppendLine(Truncate(stack, StackLimit));
                }
            }
            catch (Exception)
            {
            }
            return text.ToString();
        }

        private static string TryWrite(string directory, string content)
        {
            if (String.IsNullOrEmpty(directory)) return null;
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, ReportFileName);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Flatten(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            string flattened = value.Replace("\r", " ").Replace("\n", " ").Trim();
            while (flattened.IndexOf("  ", StringComparison.Ordinal) >= 0)
                flattened = flattened.Replace("  ", " ");
            return flattened;
        }

        private static string Truncate(string value, int limit)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= limit) return value ?? String.Empty;
            return value.Substring(0, limit).TrimEnd() + "...";
        }
    }
}
