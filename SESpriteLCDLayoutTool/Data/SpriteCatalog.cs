using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Data
{
    public class SpriteCategory
    {
        public string Name { get; set; }
        public List<string> Sprites { get; set; } = new List<string>();
    }

    public static class SpriteCatalog
    {
        // All sprite names confirmed from SE ingame scripting and modding references.
        // SE also exposes block/item icons via "MyObjectBuilder_X/Y" paths — add those as custom sprites.
        public static readonly List<SpriteCategory> Categories = new List<SpriteCategory>
        {
            new SpriteCategory
            {
                Name = "Shapes",
                Sprites = new List<string>
                {
                    "Circle",
                    "SemiCircle",
                    "Triangle",
                    "RightTriangle",
                    "SquareSimple",
                    "Dot",
                }
            },
            new SpriteCategory
            {
                Name = "Icons — Status",
                Sprites = new List<string>
                {
                    "IconEnergy",
                    "IconShield",
                    "IconHydrogen",
                    "IconOxygen",
                    "IconSpeed",
                    "IconMass",
                    "IconVolume",
                    "IconTime",
                }
            },
            new SpriteCategory
            {
                Name = "Icons — Warning",
                Sprites = new List<string>
                {
                    "Danger",
                    "Cross",
                    "Arrow",
                }
            },
            new SpriteCategory
            {
                Name = "HUD / Targeting",
                Sprites = new List<string>
                {
                    "AH_BoreSight",
                    "AH_PipMain",
                    "AH_PipBore",
                }
            },
            new SpriteCategory
            {
                Name = "Backgrounds",
                Sprites = new List<string>
                {
                    "Grid",
                    "ScreenOverlay",
                }
            },
        };

        // Fonts available in SE (White is the most commonly used)
        public static readonly string[] Fonts =
        {
            "White", "Red", "Green", "Blue", "DarkBlue", "UrlFont", "Monospace"
        };

        // LCD surface size presets matching common SE block types.
        // SurfaceSize (drawable area) equals TextureSize for most blocks at default padding.
        public static readonly string[] SurfacePresetNames =
        {
            "512 × 512   (Standard 1×1 LCD)",
            "512 × 256   (Wide LCD / Text Panel 1×2)",
            "256 × 512   (Tall)",
            "1024 × 512  (Corner LCD)",
        };

        public static readonly int[] SurfacePresetWidths  = { 512,  512, 256, 1024 };
        public static readonly int[] SurfacePresetHeights = { 512,  256, 512,  512 };
    }
}
