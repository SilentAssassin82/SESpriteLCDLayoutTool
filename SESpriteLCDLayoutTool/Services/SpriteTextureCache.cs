using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Xml.Linq;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Discovers SE sprite textures from the game's Content directory and installed
    /// mods by parsing SBC definition files, then loads and caches textures as Bitmaps.
    /// All textures are disposed when <see cref="Unload"/> is called.
    /// </summary>
    public sealed class SpriteTextureCache : IDisposable
    {
        private readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _spriteToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _loadErrors = new List<string>();
        private string _contentPath;
        private SeFontAtlas _fontAtlas;

        /// <summary>Number of successfully loaded sprite textures.</summary>
        public int LoadedCount => _cache.Count;

        /// <summary>Number of sprite→texture mappings discovered in SBC files.</summary>
        public int MappingCount => _spriteToPath.Count;

        /// <summary>The loaded SE font atlas (for glyph rendering). May be null.</summary>
        public SeFontAtlas FontAtlas => _fontAtlas;

        /// <summary>
        /// Detailed per-texture error messages for textures that failed to load.
        /// Useful for debugging missing/corrupt/unsupported textures in mods.
        /// </summary>
        public IReadOnlyList<string> LoadErrors => _loadErrors;

        /// <summary>
        /// Returns the cached Bitmap for a given SE sprite name, or null if not loaded.
        /// </summary>
        public Bitmap GetTexture(string spriteName)
        {
            if (spriteName == null) return null;
            if (_cache.TryGetValue(spriteName, out Bitmap bmp))
                return bmp;

            // Fallback: if sprite name looks like a texture path (e.g. faction logos
            // use "Textures\FactionLogo\...\icon.dds" as the sprite name), try
            // loading directly from Content directory.
            if (_contentPath != null && IsTexturePath(spriteName))
            {
                string absPath = Path.Combine(_contentPath, spriteName.Replace('/', '\\'));
                bmp = TryLoadAndCache(spriteName, absPath);
                if (bmp != null) return bmp;
            }

            return null;
        }

        /// <summary>
        /// Scans SBC files in the SE Content directory and installed mods,
        /// resolves sprite→texture mappings, and loads every texture it can find.
        /// </summary>
        /// <param name="gameContentPath">
        /// Full path to SE's Content directory (e.g. …\SpaceEngineers\Content).
        /// </param>
        /// <returns>Summary string describing what was loaded.</returns>
        public string LoadFromContent(string gameContentPath)
        {
            Unload();

            if (string.IsNullOrWhiteSpace(gameContentPath) || !Directory.Exists(gameContentPath))
                return "Content directory not found.";

            _contentPath = gameContentPath;

            // Phase 1a — parse vanilla SBC files
            string dataPath = Path.Combine(gameContentPath, "Data");
            if (Directory.Exists(dataPath))
            {
                foreach (string sbcFile in Directory.EnumerateFiles(dataPath, "*.sbc", SearchOption.AllDirectories))
                {
                    try { ParseSbcFile(sbcFile, gameContentPath); }
                    catch { /* skip malformed files */ }
                }
            }

            // Phase 1b — parse local mods (%APPDATA%\SpaceEngineers\Mods\*\)
            string localModsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpaceEngineers", "Mods");
            if (Directory.Exists(localModsPath))
            {
                foreach (string modDir in Directory.GetDirectories(localModsPath))
                    ScanModDirectory(modDir);
            }

            // Phase 1c — parse Workshop mods (steamapps\workshop\content\244850\*\)
            string seRoot = Path.GetDirectoryName(gameContentPath); // …\SpaceEngineers
            string steamApps = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(seRoot))); // …\steamapps
            if (steamApps != null)
            {
                string workshopPath = Path.Combine(steamApps, "workshop", "content", "244850");
                if (Directory.Exists(workshopPath))
                {
                    foreach (string modDir in Directory.GetDirectories(workshopPath))
                        ScanModDirectory(modDir);
                }
            }

            // Phase 2 — load textures (paths are already absolute)
            int loaded = 0;
            int failed = 0;
            int notFound = 0;
            _loadErrors.Clear();
            foreach (var kv in _spriteToPath)
            {
                string spriteName = kv.Key;
                string texPath = kv.Value;

                if (!File.Exists(texPath))
                {
                    // Some SBC entries use paths without extension
                    if (!texPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    {
                        string withDds = texPath + ".dds";
                        if (File.Exists(withDds)) texPath = withDds;
                    }
                    if (!File.Exists(texPath))
                    {
                        notFound++;
                        _loadErrors.Add($"[MISSING] {spriteName}  →  {texPath}");
                        continue;
                    }
                }

                Bitmap bmp = null;
                string decodeError = null;
                if (texPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    bmp = DdsLoader.Load(texPath, out decodeError);
                }
                else
                {
                    // PNG, JPG, BMP — load via GDI+
                    try { bmp = new Bitmap(texPath); }
                    catch (Exception ex) { decodeError = ex.Message; }
                }

                if (bmp != null)
                {
                    Debug.WriteLine($"[TextureCache] Loaded: {spriteName} -> {texPath}");
                    // Resize large textures to save memory (preview only — 256px max)
                    if (bmp.Width > 256 || bmp.Height > 256)
                    {
                        float scale = Math.Min(256f / bmp.Width, 256f / bmp.Height);
                        int tw = Math.Max(1, (int)(bmp.Width * scale));
                        int th = Math.Max(1, (int)(bmp.Height * scale));
                        var thumb = new Bitmap(tw, th);
                        using (var g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(bmp, 0, 0, tw, th);
                        }
                        bmp.Dispose();
                        bmp = thumb;
                    }

                    _cache[spriteName] = bmp;
                    loaded++;
                }
                else
                {
                    Debug.WriteLine($"[TextureCache] DECODE FAILED: {spriteName} -> {texPath}");
                    _loadErrors.Add($"[DECODE FAILED] {spriteName}  →  {texPath}  —  {decodeError ?? "unknown"}");
                    failed++;
                }
            }

            string msg = $"Loaded {loaded} sprite textures ({_spriteToPath.Count} mappings found";
            if (failed > 0) msg += $", {failed} decode errors";
            if (notFound > 0) msg += $", {notFound} files missing";
            msg += ").";

            // Phase 3 — load SE font glyph atlases
            _fontAtlas = new SeFontAtlas();
            string fontMsg = _fontAtlas.LoadFromContent(gameContentPath);
            msg += "  " + fontMsg;

            return msg;
        }

        /// <summary>Disposes all cached bitmaps and clears mappings.</summary>
        public void Unload()
        {
            foreach (var bmp in _cache.Values)
                bmp.Dispose();
            _cache.Clear();
            _spriteToPath.Clear();
            _loadErrors.Clear();
            _contentPath = null;
            _fontAtlas?.Dispose();
            _fontAtlas = null;
        }

        public void Dispose() => Unload();

        // ── SBC parsing ───────────────────────────────────────────────────────────

        private void ScanModDirectory(string modDir)
        {
            string modDataPath = Path.Combine(modDir, "Data");
            if (!Directory.Exists(modDataPath)) return;
            foreach (string sbcFile in Directory.EnumerateFiles(modDataPath, "*.sbc", SearchOption.AllDirectories))
            {
                try { ParseSbcFile(sbcFile, modDir); }
                catch { /* skip malformed files */ }
            }
        }

        private void ParseSbcFile(string path, string textureRoot)
        {
            XDocument doc;
            using (var stream = File.OpenRead(path))
                doc = XDocument.Load(stream);

            var root = doc.Root;
            if (root == null) return;

            // Walk all elements looking for sprite/texture definitions.
            // SE uses several definition types, but they all follow the pattern:
            //   <Id><SubtypeId>SpriteName</SubtypeId></Id>
            //   + a texture path in SpritePath, TexturePath, Texture, or Icon.
            foreach (var def in root.Descendants())
            {
                string localName = def.Name.LocalName;

                // LCDTextureDefinition entries
                if (localName == "LCDTextureDefinition")
                {
                    TryExtractMapping(def, textureRoot, "SpritePath", "TexturePath");
                    continue;
                }

                // TransparentMaterial entries (many SE sprites are these)
                if (localName == "TransparentMaterial")
                {
                    TryExtractMapping(def, textureRoot, "Texture");
                    continue;
                }

                // Generic definitions with xsi:type containing "Sprite" or "LCD"
                var xsiType = def.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"));
                if (xsiType != null)
                {
                    string typeVal = xsiType.Value;
                    if (typeVal.IndexOf("LCDTexture", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeVal.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TryExtractMapping(def, textureRoot, "SpritePath", "TexturePath", "Texture", "Icon");
                        continue;
                    }
                }

                // Definitions with an Icon element (blocks, items, components — MyObjectBuilder_ sprites)
                TryExtractObjectBuilderMapping(def, textureRoot);
            }
        }

        private void TryExtractMapping(XElement def, string textureRoot, params string[] texElementNames)
        {
            string name = GetSubtypeId(def);
            if (string.IsNullOrWhiteSpace(name)) return;

            foreach (string elName in texElementNames)
            {
                string texRelPath = GetDescendantValue(def, elName);
                if (!string.IsNullOrWhiteSpace(texRelPath))
                {
                    _spriteToPath[name] = Path.Combine(textureRoot, texRelPath.Replace('/', '\\'));
                    return;
                }
            }
        }

        /// <summary>
        /// Parses definitions that have an Icon element and builds
        /// "MyObjectBuilder_{TypeId}/{SubtypeId}" → icon path mappings.
        /// This covers block, item, component, ammo, and tool sprites.
        /// </summary>
        private void TryExtractObjectBuilderMapping(XElement def, string textureRoot)
        {
            string typeId = null;
            string subtypeId = null;
            string iconPath = null;

            foreach (var child in def.Elements())
            {
                string ln = child.Name.LocalName;
                if (ln == "Id")
                {
                    foreach (var idChild in child.Elements())
                    {
                        if (idChild.Name.LocalName == "TypeId")
                            typeId = idChild.Value?.Trim();
                        else if (idChild.Name.LocalName == "SubtypeId")
                            subtypeId = idChild.Value?.Trim();
                    }
                    // Attribute form: <Id Type="..." Subtype="..." />
                    if (typeId == null)
                        typeId = (child.Attribute("Type") ?? child.Attribute("TypeId"))?.Value?.Trim();
                    if (subtypeId == null)
                        subtypeId = (child.Attribute("Subtype") ?? child.Attribute("SubtypeId"))?.Value?.Trim();
                }
                else if (ln == "Icon" && iconPath == null)
                {
                    iconPath = child.Value?.Trim();
                }
            }

            if (string.IsNullOrEmpty(typeId) || string.IsNullOrEmpty(subtypeId) || string.IsNullOrEmpty(iconPath))
                return;

            // SE exposes these as "MyObjectBuilder_TypeId/SubtypeId"
            string spriteName = $"MyObjectBuilder_{typeId}/{subtypeId}";
            if (!_spriteToPath.ContainsKey(spriteName))
                _spriteToPath[spriteName] = Path.Combine(textureRoot, iconPath.Replace('/', '\\'));
        }

        private static bool IsTexturePath(string name)
        {
            return name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0;
        }

        private Bitmap TryLoadAndCache(string spriteName, string absPath)
        {
            if (!File.Exists(absPath))
            {
                // Try appending .dds if no extension
                if (!Path.HasExtension(absPath))
                {
                    string withDds = absPath + ".dds";
                    if (File.Exists(withDds)) absPath = withDds;
                }
                if (!File.Exists(absPath)) return null;
            }

            Bitmap bmp = null;
            if (absPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                bmp = DdsLoader.Load(absPath);
            }
            else
            {
                try { bmp = new Bitmap(absPath); }
                catch { /* unsupported or corrupt image */ }
            }

            if (bmp == null) return null;

            // Resize large textures (preview only — 256px max)
            if (bmp.Width > 256 || bmp.Height > 256)
            {
                float scale = Math.Min(256f / bmp.Width, 256f / bmp.Height);
                int tw = Math.Max(1, (int)(bmp.Width * scale));
                int th = Math.Max(1, (int)(bmp.Height * scale));
                var thumb = new Bitmap(tw, th);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, tw, th);
                }
                bmp.Dispose();
                bmp = thumb;
            }

            _cache[spriteName] = bmp;
            return bmp;
        }

        private static string GetSubtypeId(XElement def)
        {
            // Try <Id><SubtypeId>...</SubtypeId></Id>
            foreach (var id in def.Descendants())
            {
                if (id.Name.LocalName == "Id")
                {
                    foreach (var child in id.Elements())
                    {
                        if (child.Name.LocalName == "SubtypeId")
                            return child.Value?.Trim();
                    }
                    // Some defs use <Id Subtype="...">
                    var subtypeAttr = id.Attribute("Subtype") ?? id.Attribute("SubtypeId");
                    if (subtypeAttr != null)
                        return subtypeAttr.Value?.Trim();
                }
            }

            // Direct SubtypeId element (without Id wrapper)
            foreach (var child in def.Elements())
            {
                if (child.Name.LocalName == "SubtypeId")
                    return child.Value?.Trim();
            }

            return null;
        }

        private static string GetDescendantValue(XElement parent, string localName)
        {
            foreach (var el in parent.Descendants())
            {
                if (el.Name.LocalName == localName)
                    return el.Value?.Trim();
            }
            return null;
        }
    }
}
