using System;
using System.IO;
using Microsoft.Win32;

namespace SESpriteLCDLayoutTool.Data
{
    /// <summary>
    /// Persists user settings (currently just the SE game path) to a plain text
    /// file next to the executable, and provides auto-detection helpers.
    /// </summary>
    internal static class AppSettings
    {
        private static readonly string SettingsPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");

        /// <summary>Saved SE Content directory path (null if not set).</summary>
        public static string GameContentPath { get; set; }

        /// <summary>Load settings from disk.</summary>
        public static void Load()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("GameContentPath=", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = trimmed.Substring("GameContentPath=".Length).Trim();
                        if (Directory.Exists(val))
                            GameContentPath = val;
                    }
                }
            }
            catch { /* ignore corrupt file */ }
        }

        /// <summary>Save current settings to disk.</summary>
        public static void Save()
        {
            try
            {
                File.WriteAllText(SettingsPath,
                    $"GameContentPath={GameContentPath ?? ""}\r\n");
            }
            catch { /* ignore write failures */ }
        }

        /// <summary>
        /// Attempts to auto-detect the SE Content directory from Steam registry
        /// entries and common installation locations.
        /// Returns null if not found.
        /// </summary>
        public static string AutoDetectContentPath()
        {
            // 1. Check Steam registry for SE (app ID 244850)
            string steamPath = TryGetSteamAppPath("244850");
            if (steamPath != null)
            {
                string content = Path.Combine(steamPath, "Content");
                if (Directory.Exists(content)) return content;
            }

            // 2. Check common Steam library locations
            string[] commonRoots =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers",
                @"C:\Program Files\Steam\steamapps\common\SpaceEngineers",
                @"D:\SteamLibrary\steamapps\common\SpaceEngineers",
                @"E:\SteamLibrary\steamapps\common\SpaceEngineers",
                @"F:\SteamLibrary\steamapps\common\SpaceEngineers",
                @"G:\SteamLibrary\steamapps\common\SpaceEngineers",
            };

            foreach (string root in commonRoots)
            {
                string content = Path.Combine(root, "Content");
                if (Directory.Exists(content)) return content;
            }

            return null;
        }

        private static string TryGetSteamAppPath(string appId)
        {
            try
            {
                // Uninstall registry (most reliable for install location)
                using (var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {appId}"))
                {
                    string loc = key?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                        return loc;
                }

                // Also try 32-bit view on 64-bit OS
                using (var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {appId}"))
                {
                    string loc = key?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                        return loc;
                }
            }
            catch { /* registry access may fail */ }

            return null;
        }
    }
}
