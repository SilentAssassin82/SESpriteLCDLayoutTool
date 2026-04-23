using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace SESpriteLCDLayoutTool.Data
{
    /// <summary>
    /// Persists user settings and workspace layout state to a plain text key=value
    /// file next to the executable, and provides auto-detection helpers.
    /// </summary>
    internal static class AppSettings
    {
        private static readonly string SettingsPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");

        private const int MaxRecentFiles = 10;

        // ── SE content path ───────────────────────────────────────────────────────
        /// <summary>Saved SE Content directory path (null if not set).</summary>
        public static string GameContentPath { get; set; }

        // ── Workspace / window state ──────────────────────────────────────────────
        /// <summary>Saved window left position (-1 = not set).</summary>
        public static int WindowX { get; set; } = -1;
        /// <summary>Saved window top position (-1 = not set).</summary>
        public static int WindowY { get; set; } = -1;
        /// <summary>Saved window width (0 = not set).</summary>
        public static int WindowWidth { get; set; }
        /// <summary>Saved window height (0 = not set).</summary>
        public static int WindowHeight { get; set; }
        /// <summary>Whether the window was maximised.</summary>
        public static bool WindowMaximised { get; set; }

        /// <summary>Saved distance for the main (left panel | work area) splitter.</summary>
        public static int MainSplitterDistance { get; set; }
        /// <summary>Saved distance for the work (canvas | code) splitter.</summary>
        public static int WorkSplitterDistance { get; set; }
        /// <summary>Saved distance for the top (canvas | properties) splitter.</summary>
        public static int TopSplitterDistance { get; set; }

        // ── Recent files ──────────────────────────────────────────────────────────
        /// <summary>Most-recently-used .seld file paths, newest first.</summary>
        public static List<string> RecentFiles { get; } = new List<string>();

        /// <summary>
        /// Pushes <paramref name="path"/> to the top of the recent-files list,
        /// removing any duplicate entry and capping the list at <see cref="MaxRecentFiles"/>.
        /// </summary>
        public static void PushRecentFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, path);
            while (RecentFiles.Count > MaxRecentFiles)
                RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }

        // ── Persistence ───────────────────────────────────────────────────────────

        /// <summary>Load settings from disk.</summary>
        public static void Load()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                RecentFiles.Clear();
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("GameContentPath=", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = trimmed.Substring("GameContentPath=".Length).Trim();
                        if (Directory.Exists(val))
                            GameContentPath = val;
                    }
                    else if (trimmed.StartsWith("WindowX=", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(trimmed.Substring("WindowX=".Length), out int wx)) WindowX = wx; }
                    else if (trimmed.StartsWith("WindowY=", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(trimmed.Substring("WindowY=".Length), out int wy)) WindowY = wy; }
                    else if (trimmed.StartsWith("WindowWidth=", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(trimmed.Substring("WindowWidth=".Length), out int ww)) WindowWidth = ww; }
                    else if (trimmed.StartsWith("WindowHeight=", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(trimmed.Substring("WindowHeight=".Length), out int wh)) WindowHeight = wh; }
                    else if (trimmed.StartsWith("WindowMaximised=", StringComparison.OrdinalIgnoreCase))
                    { if (bool.TryParse(trimmed.Substring("WindowMaximised=".Length), out bool wm)) WindowMaximised = wm; }
                    else if (trimmed.StartsWith("MainSplitterDistance=", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(trimmed.Substring("MainSplitterDistance=".Length), out int ms)) MainSplitterDistance = ms; }
                    else if (trimmed.StartsWith("WorkSplitterDistance=", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(trimmed.Substring("WorkSplitterDistance=".Length), out int ws)) WorkSplitterDistance = ws; }
                    else if (trimmed.StartsWith("TopSplitterDistance=", StringComparison.OrdinalIgnoreCase))
                    { if (int.TryParse(trimmed.Substring("TopSplitterDistance=".Length), out int ts)) TopSplitterDistance = ts; }
                    else if (trimmed.StartsWith("RecentFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        string rf = trimmed.Substring("RecentFile=".Length).Trim();
                        if (!string.IsNullOrEmpty(rf) && File.Exists(rf) && RecentFiles.Count < MaxRecentFiles)
                            RecentFiles.Add(rf);
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
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"GameContentPath={GameContentPath ?? ""}");
                sb.AppendLine($"WindowX={WindowX}");
                sb.AppendLine($"WindowY={WindowY}");
                sb.AppendLine($"WindowWidth={WindowWidth}");
                sb.AppendLine($"WindowHeight={WindowHeight}");
                sb.AppendLine($"WindowMaximised={WindowMaximised}");
                sb.AppendLine($"MainSplitterDistance={MainSplitterDistance}");
                sb.AppendLine($"WorkSplitterDistance={WorkSplitterDistance}");
                sb.AppendLine($"TopSplitterDistance={TopSplitterDistance}");
                foreach (string rf in RecentFiles)
                    sb.AppendLine($"RecentFile={rf}");
                File.WriteAllText(SettingsPath, sb.ToString());
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
