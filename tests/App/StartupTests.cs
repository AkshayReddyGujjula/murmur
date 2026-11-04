using System;
using System.IO;
using System.Text;
using System.Threading;
using Murmur.App;

namespace Murmur.Tests.App
{
    internal static class StartupTests
    {
        private static int checks;

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }

        [STAThread]
        public static int Main()
        {
            try
            {
                CheckArguments();
                CheckDataLocation();
                CheckInstanceSignal();
                CheckFailureFormatting();
                CheckReportWriting();
                CheckSelfInstall();
                Console.WriteLine("PASS: Startup argument boundaries, duplicate signaling and error reporting (" + checks + " checks).");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error.Message);
                return 1;
            }
        }

        private static void CheckSelfInstall()
        {
            string root = Path.Combine(Path.GetTempPath(), "murmur-selfinstall-" + Guid.NewGuid().ToString("N"));
            try
            {
                string install = Path.Combine(root, "Local", "Murmur");
                string downloads = Path.Combine(root, "Downloads");
                string portable = Path.Combine(root, "Portable");
                string linked = Path.Combine(root, "Linked");
                Directory.CreateDirectory(install); Directory.CreateDirectory(downloads);
                Directory.CreateDirectory(Path.Combine(portable, "data")); Directory.CreateDirectory(linked);
                File.WriteAllText(Path.Combine(linked, "data-location.txt"), @"C:\data");
                Check(SelfInstall.ShouldInstall(Path.Combine(downloads, "Murmur-Setup.exe"), install), "download folder copy installs itself");
                Check(!SelfInstall.ShouldInstall(Path.Combine(install, "Murmur.exe"), install), "installed copy runs in place");
                Check(!SelfInstall.ShouldInstall(Path.Combine(install.ToUpperInvariant(), "Murmur.exe"), install + "\\"), "install folder match ignores case and trailing slash");
                Check(!SelfInstall.ShouldInstall(Path.Combine(portable, "Murmur.exe"), install), "portable copy beside data runs in place");
                Check(!SelfInstall.ShouldInstall(Path.Combine(linked, "Murmur.exe"), install), "copy with data-location.txt runs in place");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void CheckDataLocation()
        {
            string folder = Path.Combine(Path.GetTempPath(), "murmur-path-test");
            Check(AppDataLocation.Resolve(folder, null) == Path.Combine(folder, "data"), "portable data stays beside executable");
            string original = Path.Combine(folder, "original-data");
            Check(AppDataLocation.Resolve(folder, original + "\r\n") == original, "installed binary preserves original owned data path");
            bool rejected = false;
            try { AppDataLocation.Resolve(folder, "relative-data"); } catch (IOException) { rejected = true; }
            Check(rejected, "relative data location rejected");
            rejected = false;
            try { AppDataLocation.Resolve(folder, original + "\nother"); } catch (IOException) { rejected = true; }
            Check(rejected, "multiple data location lines rejected");
            Check(DesktopAccess.IntegrityLevel() > 0, "process integrity can be read without shell registration");
        }

        private static void CheckArguments()
        {
            Check(!StartupArguments.IsPromptExport(null), "prompt export rejects null args");
            Check(!StartupArguments.IsPromptExport(new string[0]), "prompt export rejects no args");
            Check(!StartupArguments.IsPromptExport(new string[] { "--export-prompt" }), "prompt export needs a path");
            Check(StartupArguments.IsPromptExport(new string[] { "--export-prompt", "C:\\session\\prompt.txt" }), "prompt export accepts switch and path");
            Check(StartupArguments.IsPromptExport(new string[] { "--export-prompt", "--background" }), "prompt export accepts a flag-looking path");
            Check(!StartupArguments.IsPromptExport(new string[] { "--export-prompt", "a", "b" }), "prompt export rejects extra args");
            Check(!StartupArguments.IsPromptExport(new string[] { "x", "--export-prompt" }), "prompt export switch must be first");

            Check(!StartupArguments.IsBackground(null), "background rejects null args");
            Check(!StartupArguments.IsBackground(new string[0]), "background is off with no args");
            Check(StartupArguments.IsBackground(new string[] { "--background" }), "background switch is recognized");
            Check(!StartupArguments.IsBackground(new string[] { "--other", "--background", "x" }), "background rejects unrelated arguments");
            Check(!StartupArguments.IsBackground(new string[] { "--Background" }), "background is case-sensitive");
            Check(!StartupArguments.IsPromptExport(new string[] { "--background", "--export-prompt", "x" }), "prompt export stays inert when not first");
            Check(StartupArguments.IsValidLaunch(new string[0]), "explicit launch accepted");
            Check(StartupArguments.IsValidLaunch(new string[] { "--background" }), "quiet launch accepted");
            Check(!StartupArguments.IsValidLaunch(new string[] { "--other" }), "unknown switches rejected before startup");
            Check(!StartupArguments.IsValidLaunch(new string[] { "--export-prompt" }), "malformed export cannot launch app");
        }

        private static void CheckInstanceSignal()
        {
            string key = "test-" + Guid.NewGuid().ToString("N");
            Check(!InstanceSignal.TryReveal(key), "reveal without an owner is a no-op");

            InstanceSignal owner = InstanceSignal.CreateOwner(key);
            Check(owner != null, "signal owner is created");
            try
            {
                Check(!owner.Consume(), "signal starts clear");
                Check(InstanceSignal.TryReveal(key), "duplicate launch reaches the owner");
                Check(owner.Consume(), "owner observes the duplicate launch");
                Check(!owner.Consume(), "signal auto-resets after handling");
                Check(InstanceSignal.TryReveal(key), "a second duplicate launch is still delivered");
                Check(owner.Consume(), "owner observes the second launch");
                using (ManualResetEvent callback = new ManualResetEvent(false))
                {
                    Check(InstanceSignal.TryReveal(key), "early duplicate queues before UI registration");
                    RegisteredWaitHandle registration = owner.Register(delegate { callback.Set(); });
                    try
                    {
                        Check(callback.WaitOne(2000), "event-driven reveal callback runs without polling");
                    }
                    finally
                    {
                        using (ManualResetEvent complete = new ManualResetEvent(false))
                        {
                            registration.Unregister(complete);
                            Check(complete.WaitOne(2000), "reveal registration tears down cleanly");
                        }
                    }
                }
            }
            finally
            {
                owner.Dispose();
            }
            Check(!InstanceSignal.TryReveal(key), "reveal after the owner closes is a no-op");
            Check(!owner.Consume(), "disposed signal stays empty");
        }

        private static void CheckFailureFormatting()
        {
            Check(StartupFailure.FormatSummary(null) == "Unknown error.", "null error has a safe summary");

            Exception messy = new InvalidOperationException("line one\r\nline two\nline three");
            string summary = StartupFailure.FormatSummary(messy);
            Check(summary.Contains("System.InvalidOperationException"), "summary includes the exception type");
            Check(summary.Contains("line one line two line three"), "summary flattens line breaks");
            Check(summary.IndexOf('\r') < 0 && summary.IndexOf('\n') < 0, "summary is a single line");

            StringBuilder longMessage = new StringBuilder();
            for (int index = 0; index < 800; index++) longMessage.Append('x');
            string shortened = StartupFailure.FormatSummary(new Exception(longMessage.ToString()));
            Check(shortened.Length <= StartupFailure.SummaryLimit + 64, "summary is bounded");
            Check(shortened.EndsWith("..."), "summary marks truncation");

            string withReport = StartupFailure.FormatMessage(messy, "C:\\logs\\startup-error.txt");
            Check(withReport.Contains("Murmur could not start"), "message explains the failure");
            Check(withReport.Contains("start Murmur again"), "message says what to do next");
            Check(withReport.Contains("C:\\logs\\startup-error.txt"), "message points at the report");

            string withoutReport = StartupFailure.FormatMessage(messy, null);
            Check(withoutReport.Contains("could not write a details file"), "message handles an unwritable report path");
            string stopped = StartupFailure.FormatMessage(messy, "C:\\logs\\startup-error.txt", "Murmur stopped unexpectedly.");
            Check(stopped.Contains("Murmur stopped unexpectedly"), "message supports a UI-crash headline");
        }

        private static void CheckReportWriting()
        {
            string root = Path.Combine(Path.GetTempPath(), "murmur-startup-" + Guid.NewGuid().ToString("N"));
            string preferred = Path.Combine(root, "preferred");
            string fallback = Path.Combine(root, "fallback");
            DateTime when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            try
            {
                Directory.CreateDirectory(root);
                Exception error = new InvalidOperationException("startup failed\r\nwhile loading");
                string path = StartupFailure.WriteReport(preferred, fallback, error, "test stage", when);
                string expected = Path.Combine(preferred, StartupFailure.ReportFileName);
                Check(path == expected, "report uses the writable preferred folder");
                Check(File.Exists(path), "report file is created");
                string content = File.ReadAllText(path);
                Check(content.Contains("Stage: test stage"), "report records the stage");
                Check(content.Contains("Type: System.InvalidOperationException"), "report records the exception type");
                Check(content.Contains("Message: startup failed while loading"), "report flattens the message");
                Check(!content.Contains("\r\nwhile"), "report does not keep raw message line breaks");

                Exception thrown = null;
                try { throw new InvalidOperationException("thrown"); }
                catch (Exception caught) { thrown = caught; }
                string stackPath = StartupFailure.WriteReport(preferred, fallback, thrown, "thrown stage", when);
                Check(File.ReadAllText(stackPath).Contains("Stack:"), "report keeps a bounded stack trace");

                Exception privateData = new InvalidOperationException("safe message");
                privateData.Data["token"] = "SECRET-TRANSCRIPT-CONTENT";
                string dataPath = StartupFailure.WriteReport(preferred, fallback, privateData, "data stage", when);
                string dataContent = File.ReadAllText(dataPath);
                Check(dataContent.Contains("safe message"), "report keeps the safe message");
                Check(!dataContent.Contains("SECRET-TRANSCRIPT-CONTENT"), "report never dumps exception data or transcript content");

                string blocker = Path.Combine(root, "blocked");
                File.WriteAllText(blocker, "not a directory");
                string blockedPreferred = Path.Combine(blocker, "nested");
                string fallbackPath = StartupFailure.WriteReport(blockedPreferred, fallback, error, "fallback stage", when);
                Check(fallbackPath == Path.Combine(fallback, StartupFailure.ReportFileName), "report falls back when the exe folder is denied");
                Check(File.Exists(fallbackPath), "fallback report is created");
                Check(File.ReadAllText(fallbackPath).Contains("Stage: fallback stage"), "fallback report keeps the stage");

                string secondBlocker = Path.Combine(root, "blocked-two");
                File.WriteAllText(secondBlocker, "not a directory");
                string unwritable = StartupFailure.WriteReport(blockedPreferred, Path.Combine(secondBlocker, "nested"), error, "none", when);
                Check(unwritable == null, "report returns null when both folders are unavailable");
                Check(StartupFailure.WriteReport(null, null, error, "none", when) == null, "report tolerates missing folders");
                Check(!String.IsNullOrEmpty(StartupFailure.FallbackDirectory()), "fallback directory is available for diagnostics");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
