using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SESpriteLCDLayoutTool.Models
{
    /// <summary>
    /// Identifies a sprite by its key characteristics for element matching.
    /// Includes approximate position to distinguish sprites with the same texture in different locations.
    /// </summary>
    [Serializable]
    public class SpriteSignature
    {
        /// <summary>Sprite name (Data field) - texture name or text content pattern.</summary>
        public string Name { get; set; }

        /// <summary>Type of sprite (Texture or Text).</summary>
        public SpriteEntryType Type { get; set; }

        /// <summary>
        /// Approximate X position (rounded to nearest 10 pixels) to help distinguish
        /// sprites with the same texture in different locations.
        /// </summary>
        public int ApproxX { get; set; }

        /// <summary>
        /// Approximate Y position (rounded to nearest 10 pixels) to help distinguish
        /// sprites with the same texture in different locations.
        /// </summary>
        public int ApproxY { get; set; }

        /// <summary>
        /// For text sprites, a hash of the format string to distinguish
        /// text sprites with different content patterns.
        /// </summary>
        public int ContentHash { get; set; }

        public SpriteSignature() { }

        public SpriteSignature(SpriteEntry sprite)
        {
            Type = sprite.Type;
            // Round position to nearest 10 pixels for fuzzy matching
            // (allows for small animation movements while still distinguishing different sprites)
            ApproxX = (int)(sprite.X / 10) * 10;
            ApproxY = (int)(sprite.Y / 10) * 10;

            // Use SpriteName for textures, Text for text sprites
            if (Type == SpriteEntryType.Texture)
            {
                Name = sprite.SpriteName ?? "";
            }
            else
            {
                Name = sprite.Text ?? "";
                ContentHash = Name.GetHashCode();
            }
        }

        public override bool Equals(object obj)
        {
            if (obj is SpriteSignature other)
            {
                return Name == other.Name 
                    && Type == other.Type
                    && ApproxX == other.ApproxX
                    && ApproxY == other.ApproxY;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (Name?.GetHashCode() ?? 0) 
                ^ Type.GetHashCode() 
                ^ ApproxX.GetHashCode() 
                ^ ApproxY.GetHashCode();
        }

        public override string ToString() => $"{Type}:{Name}@({ApproxX},{ApproxY})";
    }

    /// <summary>
    /// Maps element/method names to the sprites they produce across all animation frames.
    /// This captures the complete picture of what sprites each element uses.
    /// </summary>
    [Serializable]
    public class ElementSpriteMapping
    {
        /// <summary>
        /// Maps method/element name to list of sprite signatures it produces.
        /// Built by running N frames of animation and collecting all unique sprites.
        /// </summary>
        public Dictionary<string, HashSet<SpriteSignature>> MethodToSprites { get; set; }
            = new Dictionary<string, HashSet<SpriteSignature>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reverse lookup: sprite signature to list of methods that produce it.
        /// Built from MethodToSprites for fast isolation queries.
        /// </summary>
        [NonSerialized]
        private Dictionary<SpriteSignature, List<string>> _spriteToMethods;

        /// <summary>Number of frames used to build this mapping.</summary>
        public int FramesCaptured { get; set; }

        /// <summary>When this mapping was last built.</summary>
        public DateTime LastBuilt { get; set; }

        /// <summary>
        /// Maps method name to its Y offset in the full orchestrator scene.
        /// Used to position isolated animations correctly.
        /// </summary>
        public Dictionary<string, float> MethodYOffsets { get; set; }
            = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Adds a sprite to a method's sprite set.
        /// </summary>
        public void AddSprite(string methodName, SpriteEntry sprite)
        {
            if (string.IsNullOrEmpty(methodName)) return;

            if (!MethodToSprites.TryGetValue(methodName, out var sprites))
            {
                sprites = new HashSet<SpriteSignature>();
                MethodToSprites[methodName] = sprites;
            }

            sprites.Add(new SpriteSignature(sprite));
            _spriteToMethods = null; // Invalidate reverse lookup
        }

        /// <summary>
        /// Gets all sprite signatures for a given method.
        /// </summary>
        public IEnumerable<SpriteSignature> GetSpritesForMethod(string methodName)
        {
            if (MethodToSprites.TryGetValue(methodName, out var sprites))
            {
                return sprites;
            }
            return Enumerable.Empty<SpriteSignature>();
        }

        /// <summary>
        /// Checks if a sprite belongs to a specific method.
        /// Uses position-based matching (ApproxX, ApproxY).
        /// </summary>
        public bool SpritesBelongsToMethod(SpriteEntry sprite, string methodName)
        {
            if (!MethodToSprites.TryGetValue(methodName, out var sprites))
            {
                return false;
            }

            var sig = new SpriteSignature(sprite);
            return sprites.Contains(sig);
        }

        /// <summary>
        /// Checks if a sprite belongs to a specific method using ONLY type and name matching.
        /// Ignores position - useful for animation playback where sprites move.
        /// </summary>
        public bool SpritesBelongsToMethodByName(SpriteEntry sprite, string methodName)
        {
            if (!MethodToSprites.TryGetValue(methodName, out var sprites))
            {
                return false;
            }

            // Match by type and name only (no position)
            var spriteType = sprite.Type;
            var spriteName = spriteType == SpriteEntryType.Texture ? sprite.SpriteName : sprite.Text;

            foreach (var sig in sprites)
            {
                if (sig.Type == spriteType && sig.Name == spriteName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets all methods that produce a given sprite.
        /// </summary>
        public IEnumerable<string> GetMethodsForSprite(SpriteEntry sprite)
        {
            BuildReverseLookup();
            var sig = new SpriteSignature(sprite);
            if (_spriteToMethods.TryGetValue(sig, out var methods))
            {
                return methods;
            }
            return Enumerable.Empty<string>();
        }

        private void BuildReverseLookup()
        {
            if (_spriteToMethods != null) return;

            _spriteToMethods = new Dictionary<SpriteSignature, List<string>>();
            foreach (var kvp in MethodToSprites)
            {
                foreach (var sig in kvp.Value)
                {
                    if (!_spriteToMethods.TryGetValue(sig, out var methods))
                    {
                        methods = new List<string>();
                        _spriteToMethods[sig] = methods;
                    }
                    methods.Add(kvp.Key);
                }
            }
        }

        /// <summary>
        /// Saves the mapping to a file.
        /// Format: Simple text with methodName:sprite1|sprite2|sprite3 per line
        /// v3 format includes Y offsets: YOffset:123.45
        /// </summary>
        public void SaveToFile(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# ElementSpriteMapping v3");
            sb.AppendLine($"# FramesCaptured: {FramesCaptured}");
            sb.AppendLine($"# LastBuilt: {LastBuilt:O}");
            sb.AppendLine();

            foreach (var kvp in MethodToSprites.OrderBy(x => x.Key))
            {
                // v3 format: Type:Name@X,Y to include position, YOffset:value for positioning
                var sprites = string.Join("|", kvp.Value.Select(s => $"{(int)s.Type}:{Escape(s.Name)}@{s.ApproxX},{s.ApproxY}"));

                // Add Y offset if available
                string yOffsetPart = "";
                if (MethodYOffsets.TryGetValue(kvp.Key, out float yOffset))
                {
                    yOffsetPart = $"|YOffset:{yOffset:F1}";
                }

                sb.AppendLine($"{Escape(kvp.Key)}={sprites}{yOffsetPart}");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Loads a mapping from a file.
        /// Supports v1 (Type:Name), v2 (Type:Name@X,Y), and v3 (+ YOffset) formats.
        /// </summary>
        public static ElementSpriteMapping LoadFromFile(string path)
        {
            if (!File.Exists(path)) return null;

            var mapping = new ElementSpriteMapping();
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            bool isV2OrHigher = false;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Check format version
                if (line.StartsWith("# ElementSpriteMapping v2") || line.StartsWith("# ElementSpriteMapping v3"))
                {
                    isV2OrHigher = true;
                    continue;
                }
                if (line.StartsWith("#")) continue;

                // Parse header comments
                if (line.StartsWith("# FramesCaptured:"))
                {
                    if (int.TryParse(line.Substring(17).Trim(), out int frames))
                        mapping.FramesCaptured = frames;
                    continue;
                }
                if (line.StartsWith("# LastBuilt:"))
                {
                    if (DateTime.TryParse(line.Substring(12).Trim(), out DateTime dt))
                        mapping.LastBuilt = dt;
                    continue;
                }

                var eqIdx = line.IndexOf('=');
                if (eqIdx < 0) continue;

                var methodName = Unescape(line.Substring(0, eqIdx));
                var spritePart = line.Substring(eqIdx + 1);

                var sprites = new HashSet<SpriteSignature>();
                float yOffset = 0f;

                if (!string.IsNullOrEmpty(spritePart))
                {
                    foreach (var sp in spritePart.Split('|'))
                    {
                        // Check for YOffset marker (v3 format)
                        if (sp.StartsWith("YOffset:"))
                        {
                            if (float.TryParse(sp.Substring(8), out float offset))
                            {
                                yOffset = offset;
                            }
                            continue;
                        }

                        var colonIdx = sp.IndexOf(':');
                        if (colonIdx < 0) continue;

                        var typeStr = sp.Substring(0, colonIdx);
                        var rest = sp.Substring(colonIdx + 1);

                        // v2+ format: Type:Name@X,Y
                        string name;
                        int approxX = 0, approxY = 0;
                        if (isV2OrHigher)
                        {
                            var atIdx = rest.IndexOf('@');
                            if (atIdx >= 0)
                            {
                                name = Unescape(rest.Substring(0, atIdx));
                                var coords = rest.Substring(atIdx + 1).Split(',');
                                if (coords.Length == 2)
                                {
                                    int.TryParse(coords[0], out approxX);
                                    int.TryParse(coords[1], out approxY);
                                }
                            }
                            else
                            {
                                name = Unescape(rest);
                            }
                        }
                        else
                        {
                            // v1 format: Type:Name (no position)
                            name = Unescape(rest);
                        }

                        if (int.TryParse(typeStr, out int typeInt))
                        {
                            sprites.Add(new SpriteSignature
                            {
                                Type = (SpriteEntryType)typeInt,
                                Name = name,
                                ApproxX = approxX,
                                ApproxY = approxY
                            });
                        }
                    }
                }

                mapping.MethodToSprites[methodName] = sprites;
                if (yOffset != 0f)
                {
                    mapping.MethodYOffsets[methodName] = yOffset;
                }
            }

            return mapping;
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("|", "\\|").Replace("=", "\\=").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string Unescape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    var next = s[i + 1];
                    if (next == '\\') { sb.Append('\\'); i++; }
                    else if (next == '|') { sb.Append('|'); i++; }
                    else if (next == '=') { sb.Append('='); i++; }
                    else if (next == 'n') { sb.Append('\n'); i++; }
                    else if (next == 'r') { sb.Append('\r'); i++; }
                    else sb.Append(s[i]);
                }
                else
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Clears all mapping data.
        /// </summary>
        public void Clear()
        {
            MethodToSprites.Clear();
            _spriteToMethods = null;
            FramesCaptured = 0;
        }

        /// <summary>
        /// Returns true if the mapping has any data.
        /// </summary>
        public bool HasData => MethodToSprites.Count > 0;
    }
}
