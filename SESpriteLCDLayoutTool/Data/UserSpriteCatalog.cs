using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SESpriteLCDLayoutTool.Data
{
    /// <summary>
    /// Manages user-imported sprite names (from SE's GetSprites() output).
    /// Persists to a plain-text file next to the executable so the list
    /// survives across sessions.
    /// </summary>
    public static class UserSpriteCatalog
    {
        private static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "imported_sprites.txt");

        private static readonly HashSet<string> _sprites = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>All imported sprite names, sorted alphabetically.</summary>
        public static IReadOnlyList<string> Sprites
        {
            get
            {
                var list = _sprites.ToList();
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            }
        }

        public static int Count => _sprites.Count;

        /// <summary>
        /// Loads the persisted sprite list from disk (call once at startup).
        /// </summary>
        public static void Load()
        {
            _sprites.Clear();
            if (!File.Exists(FilePath)) return;

            foreach (var line in File.ReadAllLines(FilePath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("#"))
                    _sprites.Add(trimmed);
            }
        }

        /// <summary>
        /// Parses raw text (one sprite name per line, or comma/semicolon separated)
        /// and merges into the existing set. Returns the number of NEW sprites added.
        /// </summary>
        public static int Import(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return 0;

            int added = 0;
            // Split on newlines, commas, semicolons
            var names = rawText.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in names)
            {
                var name = raw.Trim().Trim('"');
                if (name.Length == 0) continue;
                if (_sprites.Add(name))
                    added++;
            }

            if (added > 0)
                Save();

            return added;
        }

        /// <summary>Removes all imported sprites and deletes the file.</summary>
        public static void Clear()
        {
            _sprites.Clear();
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { /* best effort */ }
        }

        private static void Save()
        {
            var lines = new List<string>
            {
                "# SE Sprite LCD Layout Tool — Imported sprite names",
                "# Paste from: surface.GetSprites(list); → string.Join(\"\\n\", list)",
                $"# Last updated: {DateTime.Now:yyyy-MM-dd HH:mm}",
                $"# Count: {_sprites.Count}",
                ""
            };
            lines.AddRange(Sprites); // sorted
            File.WriteAllLines(FilePath, lines);
        }

        /// <summary>
        /// Groups imported sprites into rough categories based on name prefixes.
        /// Returns (categoryName, spriteNames[]) pairs.
        /// </summary>
        public static List<SpriteCategory> GetCategorised()
        {
            var result = new List<SpriteCategory>();
            if (_sprites.Count == 0) return result;

            // Known SE built-in names that are already in SpriteCatalog.Categories
            var builtIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in SpriteCatalog.Categories)
                foreach (var s in cat.Sprites)
                    builtIn.Add(s);

            var remaining = Sprites.Where(s => !builtIn.Contains(s)).ToList();
            if (remaining.Count == 0) return result;

            // Group by prefix
            var objectBuilder = new List<string>();
            var icons = new List<string>();
            var hud = new List<string>();
            var other = new List<string>();

            foreach (var name in remaining)
            {
                if (name.StartsWith("MyObjectBuilder_", StringComparison.Ordinal))
                    objectBuilder.Add(name);
                else if (name.StartsWith("Icon", StringComparison.OrdinalIgnoreCase))
                    icons.Add(name);
                else if (name.StartsWith("AH_", StringComparison.Ordinal)
                      || name.StartsWith("Hud", StringComparison.OrdinalIgnoreCase))
                    hud.Add(name);
                else
                    other.Add(name);
            }

            if (icons.Count > 0)
                result.Add(new SpriteCategory { Name = "Imported — Icons", Sprites = icons });
            if (hud.Count > 0)
                result.Add(new SpriteCategory { Name = "Imported — HUD", Sprites = hud });
            if (objectBuilder.Count > 0)
                result.Add(new SpriteCategory { Name = "Imported — Block/Item Icons", Sprites = objectBuilder });
            if (other.Count > 0)
                result.Add(new SpriteCategory { Name = "Imported — Other", Sprites = other });

            return result;
        }
    }
}
