using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Models.Rig;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Tests
{
    [TestClass]
    public class RigCodeGeneratorTests
    {
        private static LcdLayout MakeLayoutWithRig()
        {
            var layout = new LcdLayout();
            layout.Sprites.Add(new SpriteEntry { SpriteName = "SquareSimple", X = 100, Y = 50, Width = 32, Height = 32 });
            layout.Sprites.Add(new SpriteEntry { SpriteName = "Circle", X = 10, Y = 10, Width = 16, Height = 16 });

            var rig = new Rig { Name = "Test", OriginX = 256, OriginY = 256, Enabled = true };
            var root = new Bone { Id = "root", Name = "Root", LocalRotation = 0.5f };
            var child = new Bone { Id = "child", ParentId = "root", LocalX = 32, Name = "Child" };
            rig.Bones.Add(root);
            rig.Bones.Add(child);
            rig.Bindings.Add(new SpriteBinding { BoneId = "child", SpriteIndex = 0, OffsetX = 4, ScaleX = 1, ScaleY = 1 });

            var clip = new RigClip { Id = "c1", Name = "Idle", Duration = 1f, Loop = true };
            clip.GetOrCreateTrack("child").Keys.Add(new RigKeyframe { Time = 0f, LocalX = 32, LocalRotation = 0f });
            clip.GetOrCreateTrack("child").Keys.Add(new RigKeyframe { Time = 0.5f, LocalX = 32, LocalRotation = 1f });
            rig.Clips.Add(clip);
            rig.ActiveClipId = "c1";

            layout.Rigs.Add(rig);
            return layout;
        }

        [TestMethod]
        public void Generate_Empty_Layout_Returns_Stub()
        {
            var layout = new LcdLayout();
            var code = RigCodeGenerator.Generate(layout);
            Assert.IsTrue(code.Contains("No rig present"));
            Assert.IsTrue(code.Contains("public void DrawRig"));
        }

        [TestMethod]
        public void Generate_With_Rig_Emits_Required_Sections()
        {
            var layout = MakeLayoutWithRig();
            var code = RigCodeGenerator.Generate(layout);
            Assert.IsTrue(code.Contains("RigBoneData"));
            Assert.IsTrue(code.Contains("RigBindingData"));
            Assert.IsTrue(code.Contains("RigClipTracks"));
            Assert.IsTrue(code.Contains("RigEvaluate"));
            Assert.IsTrue(code.Contains("public void DrawRig"));
            Assert.IsTrue(code.Contains("frame.Add(new MySprite"));
        }

        [TestMethod]
        public void Generate_Embeds_Bone_Parent_Indices()
        {
            var layout = MakeLayoutWithRig();
            var code = RigCodeGenerator.Generate(layout);
            // Root bone is index 0 with ParentIndex = -1; child bone references parent 0.
            Assert.IsTrue(code.Contains("ParentIndex = -1"));
            Assert.IsTrue(code.Contains("ParentIndex = 0"));
        }

        [TestMethod]
        public void Generate_Bound_Sprite_Uses_Bones_Array()
        {
            var layout = MakeLayoutWithRig();
            var code = RigCodeGenerator.Generate(layout);
            // First sprite is bound to bone 1 (child).
            Assert.IsTrue(code.Contains("var b = bones[1]"));
            // Second sprite is unbound and should appear as a literal Vector2.
            Assert.IsTrue(code.Contains("new Vector2(10"));
        }

        [TestMethod]
        public void Generate_Honors_Clip_Duration_And_Loop()
        {
            var layout = MakeLayoutWithRig();
            var code = RigCodeGenerator.Generate(layout);
            Assert.IsTrue(code.Contains("RigClipDuration"));
            Assert.IsTrue(code.Contains("RigClipLoop = true"));
        }
    }
}
