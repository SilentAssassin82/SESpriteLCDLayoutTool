using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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
        private readonly Dictionary<string, Size> _originalSizes = new Dictionary<string, Size>(StringComparer.OrdinalIgnoreCase);
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

            // Phase 2 — load textures in parallel (DDS decompression is CPU-bound)
            int loaded = 0;
            int failed = 0;
            int notFound = 0;
            _loadErrors.Clear();
            var lockObj = new object();

            Parallel.ForEach(_spriteToPath,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                kv =>
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
                        lock (lockObj)
                        {
                            notFound++;
                            _loadErrors.Add($"[MISSING] {spriteName}  →  {texPath}");
                        }
                        return;
                    }
                }

                Bitmap bmp = null;
                string decodeError = null;
                Size originalSize = Size.Empty;

                if (texPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    // Use mip-level selection: skip to ~256px mip to avoid
                    // decompressing full 2048×2048 textures for preview.
                    bmp = DdsLoader.Load(texPath, 256, out decodeError,
                        out int origW, out int origH);
                    if (origW > 0 && origH > 0)
                        originalSize = new Size(origW, origH);
                }
                else
                {
                    // PNG, JPG, BMP — load via GDI+
                    try { bmp = new Bitmap(texPath); }
                    catch (Exception ex) { decodeError = ex.Message; }
                }

                if (bmp != null)
                {
                    // Record original file dimensions for VRAM budget analysis
                    if (originalSize.IsEmpty)
                        originalSize = new Size(bmp.Width, bmp.Height);

                    // Resize if still larger than 256px (mip selection gets close,
                    // but the mip may be e.g. 512px if no smaller mip existed)
                    if (bmp.Width > 256 || bmp.Height > 256)
                    {
                        float scale = Math.Min(256f / bmp.Width, 256f / bmp.Height);
                        int tw = Math.Max(1, (int)(bmp.Width * scale));
                        int th = Math.Max(1, (int)(bmp.Height * scale));
                        var thumb = new Bitmap(tw, th);
                        using (var g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = InterpolationMode.Bilinear;
                            g.DrawImage(bmp, 0, 0, tw, th);
                        }
                        bmp.Dispose();
                        bmp = thumb;
                    }

                    lock (lockObj)
                    {
                        _originalSizes[spriteName] = originalSize;
                        _cache[spriteName] = bmp;
                        loaded++;
                    }
                }
                else
                {
                    lock (lockObj)
                    {
                        _loadErrors.Add($"[DECODE FAILED] {spriteName}  →  {texPath}  —  {decodeError ?? "unknown"}");
                        failed++;
                    }
                }
            });

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

        /// <summary>
        /// Returns the original (pre-downscale) dimensions of a loaded texture,
        /// or null if the texture was never loaded.
        /// </summary>
        public Size? GetOriginalSize(string spriteName)
        {
            if (spriteName != null && _originalSizes.TryGetValue(spriteName, out Size size))
                return size;
            return null;
        }

        /// <summary>Disposes all cached bitmaps and clears mappings.</summary>
        public void Unload()
        {
            foreach (var bmp in _cache.Values)
                bmp.Dispose();
            _cache.Clear();
            _spriteToPath.Clear();
            _originalSizes.Clear();
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
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            };

            using (var stream = File.OpenRead(path))
            using (var reader = XmlReader.Create(stream, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                        continue;

                    string localName = reader.LocalName;

                    if (localName == "LCDTextureDefinition")
                    {
                        // Load the full element so we can prefer SpritePath over TexturePath.
                        // ReadSubtypeAndTexture uses a HashSet and takes whichever element
                        // appears first in the XML stream — but SE SBCs always put TexturePath
                        // (3D model albedo) before SpritePath (LCD sprite), so we must
                        // explicitly prefer SpritePath.
                        var el = XElement.Load(reader.ReadSubtree());
                        string name = GetSubtypeId(el);
                        string texPath = GetDescendantValue(el, "SpritePath")
                                      ?? GetDescendantValue(el, "TexturePath");
                        AddMappingIfValid(name, texPath, textureRoot);
                        continue;
                    }

                    if (localName == "TransparentMaterial")
                    {
                        using (var sub = reader.ReadSubtree())
                        {
                            string name, texPath;
                            ReadSubtypeAndTexture(sub, out name, out texPath, "Texture");
                            AddMappingIfValid(name, texPath, textureRoot);
                        }
                        continue;
                    }

                    if (localName != "Definition")
                        continue;

                    string xsiType = reader.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
                    bool hasSpriteLikeType = !string.IsNullOrEmpty(xsiType)
                        && (xsiType.IndexOf("LCDTexture", StringComparison.OrdinalIgnoreCase) >= 0
                            || xsiType.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0);

                    using (var sub = reader.ReadSubtree())
                    {
                        if (hasSpriteLikeType)
                        {
                            string name, texPath;
                            ReadSubtypeAndTexture(sub, out name, out texPath,
                                "SpritePath", "TexturePath", "Texture", "Icon");
                            AddMappingIfValid(name, texPath, textureRoot);
                        }
                        else
                        {
                            string typeId, subtypeId, iconPath;
                            ReadObjectBuilderInfo(sub, out typeId, out subtypeId, out iconPath);
                            if (!string.IsNullOrEmpty(typeId) && !string.IsNullOrEmpty(subtypeId) && !string.IsNullOrEmpty(iconPath))
                            {
                                string spriteName = "MyObjectBuilder_" + typeId + "/" + subtypeId;
                                if (!_spriteToPath.ContainsKey(spriteName))
                                    _spriteToPath[spriteName] = Path.Combine(textureRoot, iconPath.Replace('/', '\\'));
                            }
                        }
                    }
                }
            }
        }

        private void AddMappingIfValid(string name, string texRelPath, string textureRoot)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(texRelPath))
                return;

            _spriteToPath[name] = Path.Combine(textureRoot, texRelPath.Replace('/', '\\'));
        }

        private static void ReadSubtypeAndTexture(XmlReader reader, out string subtypeId, out string texturePath, params string[] texElementNames)
        {
            subtypeId = null;
            texturePath = null;
            var texNames = new HashSet<string>(texElementNames, StringComparer.Ordinal);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                string ln = reader.LocalName;

                if (ln == "Id")
                {
                    if (string.IsNullOrWhiteSpace(subtypeId))
                    {
                        var attr = reader.GetAttribute("Subtype") ?? reader.GetAttribute("SubtypeId");
                        if (!string.IsNullOrWhiteSpace(attr))
                            subtypeId = attr.Trim();
                    }
                    continue;
                }

                if (ln == "SubtypeId" && string.IsNullOrWhiteSpace(subtypeId))
                {
                    string value = ReadElementValue(reader);
                    if (!string.IsNullOrWhiteSpace(value))
                        subtypeId = value.Trim();
                    continue;
                }

                if (string.IsNullOrWhiteSpace(texturePath) && texNames.Contains(ln))
                {
                    string value = ReadElementValue(reader);
                    if (!string.IsNullOrWhiteSpace(value))
                        texturePath = value.Trim();
                }
            }
        }

        private static void ReadObjectBuilderInfo(XmlReader reader, out string typeId, out string subtypeId, out string iconPath)
        {
            typeId = null;
            subtypeId = null;
            iconPath = null;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                string ln = reader.LocalName;

                if (ln == "Id")
                {
                    if (string.IsNullOrWhiteSpace(typeId))
                    {
                        var t = reader.GetAttribute("Type") ?? reader.GetAttribute("TypeId");
                        if (!string.IsNullOrWhiteSpace(t))
                            typeId = t.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(subtypeId))
                    {
                        var s = reader.GetAttribute("Subtype") ?? reader.GetAttribute("SubtypeId");
                        if (!string.IsNullOrWhiteSpace(s))
                            subtypeId = s.Trim();
                    }
                    continue;
                }

                if (ln == "TypeId" && string.IsNullOrWhiteSpace(typeId))
                {
                    string value = ReadElementValue(reader);
                    if (!string.IsNullOrWhiteSpace(value))
                        typeId = value.Trim();
                    continue;
                }

                if (ln == "SubtypeId" && string.IsNullOrWhiteSpace(subtypeId))
                {
                    string value = ReadElementValue(reader);
                    if (!string.IsNullOrWhiteSpace(value))
                        subtypeId = value.Trim();
                    continue;
                }

                if (ln == "Icon" && string.IsNullOrWhiteSpace(iconPath))
                {
                    string value = ReadElementValue(reader);
                    if (!string.IsNullOrWhiteSpace(value))
                        iconPath = value.Trim();
                }
            }
        }

        private static string ReadElementValue(XmlReader reader)
        {
            if (reader == null || reader.NodeType != XmlNodeType.Element)
                return null;

            if (reader.IsEmptyElement)
                return string.Empty;

            try { return reader.ReadElementContentAsString(); }
            catch { return null; }
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
            Size originalSize = Size.Empty;
            if (absPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                bmp = DdsLoader.Load(absPath, 256, out _,
                    out int origW, out int origH);
                if (origW > 0 && origH > 0)
                    originalSize = new Size(origW, origH);
            }
            else
            {
                try { bmp = new Bitmap(absPath); }
                catch { /* unsupported or corrupt image */ }
            }

            if (bmp == null) return null;

            // Record original file dimensions for VRAM budget analysis
            if (originalSize.IsEmpty)
                originalSize = new Size(bmp.Width, bmp.Height);
            _originalSizes[spriteName] = originalSize;

            // Resize large textures (preview only — 256px max)
            if (bmp.Width > 256 || bmp.Height > 256)
            {
                float scale = Math.Min(256f / bmp.Width, 256f / bmp.Height);
                int tw = Math.Max(1, (int)(bmp.Width * scale));
                int th = Math.Max(1, (int)(bmp.Height * scale));
                var thumb = new Bitmap(tw, th);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
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
            // Fast path: direct <Id> child
            foreach (var child in def.Elements())
            {
                if (child.Name.LocalName != "Id")
                    continue;

                foreach (var idChild in child.Elements())
                {
                    if (idChild.Name.LocalName == "SubtypeId")
                        return idChild.Value?.Trim();
                }

                var subtypeAttr = child.Attribute("Subtype") ?? child.Attribute("SubtypeId");
                if (subtypeAttr != null)
                    return subtypeAttr.Value?.Trim();
            }

            // Fast path: direct <SubtypeId> child
            foreach (var child in def.Elements())
            {
                if (child.Name.LocalName == "SubtypeId")
                    return child.Value?.Trim();
            }

            // Fallback: search descendants for uncommon layouts
            foreach (var id in def.Descendants())
            {
                if (id.Name.LocalName != "Id")
                    continue;

                foreach (var child in id.Elements())
                {
                    if (child.Name.LocalName == "SubtypeId")
                        return child.Value?.Trim();
                }

                var subtypeAttr = id.Attribute("Subtype") ?? id.Attribute("SubtypeId");
                if (subtypeAttr != null)
                    return subtypeAttr.Value?.Trim();
            }

            return null;
        }

        private static string GetDescendantValue(XElement parent, string localName)
        {
            // Fast path: direct child lookup
            foreach (var el in parent.Elements())
            {
                if (el.Name.LocalName == localName)
                    return el.Value?.Trim();
            }

            // Fallback: deep lookup
            foreach (var el in parent.Descendants())
            {
                if (el.Name.LocalName == localName)
                    return el.Value?.Trim();
            }
            return null;
        }
    }
}
