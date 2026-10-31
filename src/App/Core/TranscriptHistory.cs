using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Murmur.App.Core
{
    public sealed class HistoryEntry
    {
        public DateTime TimeUtc;
        public string Text;
    }

    public sealed class TranscriptHistory
    {
        private readonly string filePath;
        private readonly object sync = new object();
        private List<HistoryEntry> entries = new List<HistoryEntry>();
        private int limit;

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Murmur.TranscriptHistory.v1");

        public TranscriptHistory(string path, int limit)
        {
            filePath = path;
            this.limit = ClampLimit(limit);
            lock (sync)
            {
                LoadLocked();
            }
        }

        public IList<HistoryEntry> Items
        {
            get
            {
                lock (sync)
                {
                    List<HistoryEntry> copy = new List<HistoryEntry>(entries.Count);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        HistoryEntry src = entries[i];
                        HistoryEntry dst = new HistoryEntry();
                        dst.TimeUtc = src.TimeUtc;
                        dst.Text = src.Text;
                        copy.Add(dst);
                    }
                    return new ReadOnlyCollection<HistoryEntry>(copy);
                }
            }
        }

        public int Limit
        {
            get
            {
                lock (sync)
                {
                    return limit;
                }
            }
            set
            {
                int clamped = ClampLimit(value);
                lock (sync)
                {
                    limit = clamped;
                    bool trimmed = false;
                    while (entries.Count > limit)
                    {
                        entries.RemoveAt(entries.Count - 1);
                        trimmed = true;
                    }
                    if (trimmed)
                    {
                        PersistLocked();
                    }
                }
            }
        }

        public void Add(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (text.Trim().Length == 0)
            {
                return;
            }
            lock (sync)
            {
                HistoryEntry entry = new HistoryEntry();
                entry.TimeUtc = DateTime.UtcNow;
                entry.Text = text;
                entries.Insert(0, entry);
                while (entries.Count > limit)
                {
                    entries.RemoveAt(entries.Count - 1);
                }
                PersistLocked();
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                entries.Clear();
                try
                {
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private void LoadLocked()
        {
            entries.Clear();
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    return;
                }
                if (!File.Exists(filePath))
                {
                    return;
                }
                byte[] encrypted = File.ReadAllBytes(filePath);
                if (encrypted == null || encrypted.Length == 0)
                {
                    entries.Clear();
                    return;
                }
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                if (plain == null || plain.Length < 8)
                {
                    entries.Clear();
                    return;
                }
                List<HistoryEntry> loaded = new List<HistoryEntry>();
                using (MemoryStream stream = new MemoryStream(plain, false))
                {
                    using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                    {
                        int version = reader.ReadInt32();
                        if (version != 1)
                        {
                            entries.Clear();
                            return;
                        }
                        int count = reader.ReadInt32();
                        if (count < 0 || count > 1000)
                        {
                            entries.Clear();
                            return;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            long ticks = reader.ReadInt64();
                            int byteCount = reader.ReadInt32();
                            if (byteCount < 0 || byteCount > 1048576)
                            {
                                entries.Clear();
                                return;
                            }
                            byte[] textBytes = reader.ReadBytes(byteCount);
                            if (textBytes.Length != byteCount)
                            {
                                entries.Clear();
                                return;
                            }
                            string text = Encoding.UTF8.GetString(textBytes);
                            DateTime time;
                            try
                            {
                                time = new DateTime(ticks, DateTimeKind.Utc);
                            }
                            catch
                            {
                                continue;
                            }
                            if (string.IsNullOrEmpty(text))
                            {
                                continue;
                            }
                            HistoryEntry entry = new HistoryEntry();
                            entry.TimeUtc = time;
                            entry.Text = text;
                            loaded.Add(entry);
                        }
                    }
                }
                entries = loaded;
                while (entries.Count > limit)
                {
                    entries.RemoveAt(entries.Count - 1);
                }
            }
            catch
            {
                entries.Clear();
            }
        }

        private void PersistLocked()
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    return;
                }
                byte[] plain;
                using (MemoryStream stream = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
                    {
                        writer.Write(1);
                        writer.Write(entries.Count);
                        for (int i = 0; i < entries.Count; i++)
                        {
                            HistoryEntry entry = entries[i];
                            writer.Write(entry.TimeUtc.Ticks);
                            byte[] textBytes = Encoding.UTF8.GetBytes(entry.Text != null ? entry.Text : "");
                            writer.Write(textBytes.Length);
                            if (textBytes.Length > 0)
                            {
                                writer.Write(textBytes);
                            }
                        }
                        writer.Flush();
                        plain = stream.ToArray();
                    }
                }
                byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string temp = filePath + ".tmp";
                File.WriteAllBytes(temp, encrypted);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Replace(temp, filePath, null);
                    }
                    else
                    {
                        File.Move(temp, filePath);
                    }
                }
                catch
                {
                    try
                    {
                        if (File.Exists(temp))
                        {
                            File.Delete(temp);
                        }
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

        private static int ClampLimit(int value)
        {
            if (value < 1)
            {
                return 1;
            }
            if (value > 200)
            {
                return 200;
            }
            return value;
        }
    }
}
