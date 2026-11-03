using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Murmur.App.Core
{
    public enum HotkeyChoice
    {
        CtrlSpace,
        CtrlShiftSpace,
        CtrlAltSpace,
        AltShiftSpace
    }

    public enum EngineMode
    {
        AlwaysReady,
        UnloadWhenIdle,
        OnDemand
    }

    public enum OverlayPlacement
    {
        BottomCenter,
        TopCenter
    }

    public enum PasteDestination
    {
        CurrentWindow,
        OriginalWindow
    }

    public sealed class AppSettings
    {
        public HotkeyChoice Hotkey = HotkeyChoice.CtrlSpace;
        public EngineMode Engine = EngineMode.AlwaysReady;
        public int IdleUnloadMinutes = 15;
        public OverlayPlacement Placement = OverlayPlacement.BottomCenter;
        public bool KeepHistory = true;
        public int HistoryLimit = 25;
        public bool AutoPaste = false;
        public PasteDestination PasteInto = PasteDestination.CurrentWindow;
        public bool EscapeCancels = true;

        public static AppSettings Load(string path)
        {
            AppSettings result = new AppSettings();
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return result;
                }
                if (!File.Exists(path))
                {
                    return result;
                }
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null)
                    {
                        continue;
                    }
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }
                    if (trimmed[0] == '#' || trimmed[0] == ';')
                    {
                        continue;
                    }
                    int equals = trimmed.IndexOf('=');
                    if (equals < 0)
                    {
                        continue;
                    }
                    string key = trimmed.Substring(0, equals).Trim();
                    string value = trimmed.Substring(equals + 1).Trim();
                    try
                    {
                        if (string.Equals(key, "Hotkey", StringComparison.OrdinalIgnoreCase))
                        {
                            HotkeyChoice parsed = (HotkeyChoice)Enum.Parse(typeof(HotkeyChoice), value, true);
                            if (Enum.IsDefined(typeof(HotkeyChoice), parsed))
                            {
                                result.Hotkey = parsed;
                            }
                        }
                        else if (string.Equals(key, "Engine", StringComparison.OrdinalIgnoreCase))
                        {
                            EngineMode parsedEngine = (EngineMode)Enum.Parse(typeof(EngineMode), value, true);
                            if (Enum.IsDefined(typeof(EngineMode), parsedEngine))
                            {
                                result.Engine = parsedEngine;
                            }
                        }
                        else if (string.Equals(key, "IdleUnloadMinutes", StringComparison.OrdinalIgnoreCase))
                        {
                            int parsedInt;
                            if (int.TryParse(value, out parsedInt))
                            {
                                result.IdleUnloadMinutes = parsedInt;
                            }
                        }
                        else if (string.Equals(key, "Placement", StringComparison.OrdinalIgnoreCase))
                        {
                            OverlayPlacement parsedPlacement = (OverlayPlacement)Enum.Parse(typeof(OverlayPlacement), value, true);
                            if (Enum.IsDefined(typeof(OverlayPlacement), parsedPlacement))
                            {
                                result.Placement = parsedPlacement;
                            }
                        }
                        else if (string.Equals(key, "KeepHistory", StringComparison.OrdinalIgnoreCase))
                        {
                            bool parsedBool;
                            if (TryParseBool(value, out parsedBool))
                            {
                                result.KeepHistory = parsedBool;
                            }
                        }
                        else if (string.Equals(key, "HistoryLimit", StringComparison.OrdinalIgnoreCase))
                        {
                            int parsedLimit;
                            if (int.TryParse(value, out parsedLimit))
                            {
                                result.HistoryLimit = parsedLimit;
                            }
                        }
                        else if (string.Equals(key, "AutoPaste", StringComparison.OrdinalIgnoreCase))
                        {
                            bool parsedAuto;
                            if (TryParseBool(value, out parsedAuto))
                            {
                                result.AutoPaste = parsedAuto;
                            }
                        }
                        else if (string.Equals(key, "PasteInto", StringComparison.OrdinalIgnoreCase))
                        {
                            PasteDestination parsed = (PasteDestination)Enum.Parse(typeof(PasteDestination), value, true);
                            if (Enum.IsDefined(typeof(PasteDestination), parsed)) result.PasteInto = parsed;
                        }
                        else if (string.Equals(key, "EscapeCancels", StringComparison.OrdinalIgnoreCase))
                        {
                            bool parsedEscape;
                            if (TryParseBool(value, out parsedEscape))
                            {
                                result.EscapeCancels = parsedEscape;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                result.IdleUnloadMinutes = Clamp(result.IdleUnloadMinutes, 1, 240);
                result.HistoryLimit = Clamp(result.HistoryLimit, 1, 200);
                return result;
            }
            catch
            {
                AppSettings defaults = new AppSettings();
                return defaults;
            }
        }

        public void Save(string path)
        {
            IdleUnloadMinutes = Clamp(IdleUnloadMinutes, 1, 240);
            HistoryLimit = Clamp(HistoryLimit, 1, 200);
            List<string> lines = new List<string>();
            lines.Add("Hotkey=" + Hotkey.ToString());
            lines.Add("Engine=" + Engine.ToString());
            lines.Add("IdleUnloadMinutes=" + IdleUnloadMinutes.ToString());
            lines.Add("Placement=" + Placement.ToString());
            lines.Add("KeepHistory=" + KeepHistory.ToString());
            lines.Add("HistoryLimit=" + HistoryLimit.ToString());
            lines.Add("AutoPaste=" + AutoPaste.ToString());
            lines.Add("PasteInto=" + PasteInto.ToString());
            lines.Add("EscapeCancels=" + EscapeCancels.ToString());
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string temp = path + ".tmp";
            File.WriteAllLines(temp, lines.ToArray(), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
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
                throw;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }

        private static bool TryParseBool(string value, out bool result)
        {
            result = false;
            if (value == null)
            {
                return false;
            }
            string v = value.Trim().ToUpperInvariant();
            if (v == "TRUE" || v == "1" || v == "YES" || v == "ON")
            {
                result = true;
                return true;
            }
            if (v == "FALSE" || v == "0" || v == "NO" || v == "OFF")
            {
                result = false;
                return true;
            }
            return false;
        }
    }
}
