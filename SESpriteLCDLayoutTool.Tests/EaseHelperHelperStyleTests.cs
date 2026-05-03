using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Tests
{
    [TestClass]
    public class EaseHelperHelperStyleTests
    {
        // Mirrors CodeGenerator.Generate() output: bare DrawLayout with DrawFrame, NO class wrapper.
        private const string HelperStyleCode =
@"private void DrawLayout(IMyTextSurface surface)
{
    surface.ContentType = ContentType.SCRIPT;
    surface.Script = """";

    using (var frame = surface.DrawFrame())
    {
        // [1] Square
        frame.Add(new MySprite
        {
            Type           = SpriteType.TEXTURE,
            Data           = ""SquareSimple"",
            Position       = new Vector2(256.0000f, 256.0000f),
            Size           = new Vector2(100.0000f, 100.0000f),
            Color          = new Color(255, 255, 255, 255),
            Alignment      = TextAlignment.CENTER,
            RotationOrScale = 0.0000f,
        });
    }
}
";

        [TestMethod]
        public void Keyframed_HelperStyle_IncludesEaseHelper()
        {
            var sp = new SpriteEntry
            {
                Type = SpriteEntryType.Texture,
                SpriteName = "SquareSimple",
                X = 256, Y = 256, Width = 100, Height = 100,
                ColorR = 255, ColorG = 255, ColorB = 255, ColorA = 255,
                AnimationEffects = new List<IAnimationEffect>
                {
                    new KeyframeEffect
                    {
                        Loop = LoopMode.Loop,
                        Keyframes = new List<Keyframe>
                        {
                            new Keyframe { Tick = 0,   X = 100, Y = 100 },
                            new Keyframe { Tick = 60,  X = 400, Y = 100 },
                            new Keyframe { Tick = 120, X = 100, Y = 100 },
                        },
                    },
                },
            };

            var result = RoslynAnimationInjector.InjectAnimations(HelperStyleCode, new[] { sp });

            Assert.IsTrue(result.Success, "Injection should succeed: " + result.Error);
            StringAssert.Contains(result.Code, "// ──▶ ANIM-EASE ◀──");
            StringAssert.Contains(result.Code, "public float Ease(float t, int easeType)");
        }
    }
}
