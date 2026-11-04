using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Murmur.App
{
    // One-click install: a downloaded Murmur-Setup.exe copies itself to
    // %LOCALAPPDATA%\Murmur\Murmur.exe, adds a Start menu shortcut and starts that copy.
    // Copies that already run from the install folder, or beside portable data, run in place.
    internal static class SelfInstall
    {
        internal static string InstallDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Murmur"); }
        }

        internal static bool ShouldInstall(string executable, string installDirectory)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(executable));
            if (String.Equals(folder.TrimEnd('\\'), Path.GetFullPath(installDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return false;
            // Portable or development copies keep using the data stored beside them.
            return !File.Exists(Path.Combine(folder, "data-location.txt")) && !Directory.Exists(Path.Combine(folder, "data"));
        }

        // Returns true when this process has handed over to the installed copy and should exit.
        internal static bool Run(string[] args, string userKey)
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            string directory = InstallDirectory;
            if (args.Length != 0 || !ShouldInstall(executable, directory)) return false;
            string installed = Path.Combine(directory, "Murmur.exe");
            byte[] bytes = File.ReadAllBytes(executable);

            Mutex running;
            if (Mutex.TryOpenExisting("Global\\Murmur.App." + userKey, out running))
            {
                running.Dispose();
                if (!SameBytes(installed, bytes))
                {
                    StartupFailure.ShowMessage("Murmur is already running, so it can't be updated right now.\r\n\r\n" +
                        "Right-click the Murmur icon near the clock (it may be hidden under the ^ arrow), choose Quit Murmur, then open this file again.");
                    return true;
                }
                Launch(installed, directory); // the running copy shows where it is
                return true;
            }

            Directory.CreateDirectory(directory);
            // Fresh bytes (not a file copy) so the installed app carries no download
            // zone mark and inherits normal permissions. Existing data is untouched.
            string staging = Path.Combine(directory, "Murmur.next.exe");
            File.WriteAllBytes(staging, bytes);
            if (File.Exists(installed)) File.Replace(staging, installed, null);
            else File.Move(staging, installed);
            CreateShortcut(installed, directory);
            Launch(installed, directory);
            return true;
        }

        private static bool SameBytes(string path, byte[] bytes)
        {
            try
            {
                if (!File.Exists(path)) return false;
                byte[] existing = File.ReadAllBytes(path);
                if (existing.Length != bytes.Length) return false;
                for (int i = 0; i < bytes.Length; i++) if (existing[i] != bytes[i]) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void CreateShortcut(string target, string directory)
        {
            try
            {
                string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Murmur.lnk");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type linkType = shortcut.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { directory });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch (Exception) { } // the app still works without its Start menu entry
        }

        private static void Launch(string installed, string directory)
        {
            ProcessStartInfo info = new ProcessStartInfo(installed);
            info.UseShellExecute = false;
            info.WorkingDirectory = directory;
            Process.Start(info);
        }
    }
}
