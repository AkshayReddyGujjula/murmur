using System;
using System.IO;
using System.Reflection;

namespace Murmur.App
{
    internal static class AppDataLocation
    {
        internal static string Resolve(string executableDirectory, string configuredPath)
        {
            if (String.IsNullOrWhiteSpace(configuredPath)) return Path.Combine(executableDirectory, "data");
            string path = configuredPath.Trim();
            if (path.IndexOf('\n') >= 0 || path.IndexOf('\r') >= 0 || !Path.IsPathRooted(path))
                throw new IOException("Murmur's data-location.txt must contain one absolute directory path.");
            return Path.GetFullPath(path);
        }

        internal static string Root
        {
            get
            {
                string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string location = Path.Combine(folder, "data-location.txt");
                return Resolve(folder, File.Exists(location) ? File.ReadAllText(location) : null);
            }
        }
    }
}
