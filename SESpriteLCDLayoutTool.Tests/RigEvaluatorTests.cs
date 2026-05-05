using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Models.Rig;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Tests
{
    [TestClass]
    public class RigEvaluatorTests
    {
        private const float Eps = 1e-3f;

        private static void AssertNear(float expected, float actual, string msg = null)
        {
            Assert.IsTrue(Math.Abs(expected - actual) < Eps,
                (msg ?? "value") + $": expected {expected}, got {actual}");
        }

        [TestMethod]
        public void Identity_Composes_To_Identity()
        {
            var c = RigTransform.Compose(RigTransform.Identity, RigTransform.Identity);
            AssertNear(0f, c.X, "x");
            AssertNear(0f, c.Y, "y");
            AssertNear(0f, c.Rotation, "rot");
            AssertNear(1f, c.ScaleX, "sx");
            AssertNear(1f, c.ScaleY, "sy");
        }

        [TestMethod]
        public void Compose_Applies_Parent_Rotation_To_Child_Translation()
        {
            // Parent rotated 90deg at origin, child offset (10,0) locally -> world (0,10).
            var parent = new RigTransform(0f, 0f, (float)(Math.PI / 2.0), 1f, 1f);
            var child = new RigTransform(10f, 0f, 0f, 1f, 1f);
            var c = RigTransform.Compose(parent, child);
            AssertNear(0f, c.X, "x");
            AssertNear(10f, c.Y, "y");
        }

        [TestMethod]
        public void Compose_Multiplies_Scales_And_Adds_Rotations()
        {
            var parent = new RigTransform(5f, 5f, 1f, 2f, 3f);
            var child = new RigTransform(0f, 0f, 0.5f, 4f, 5f);
            var c = RigTransform.Compose(parent, child);
            AssertNear(1.5f, c.Rotation, "rot");
            AssertNear(8f, c.ScaleX, "sx");
            AssertNear(15f, c.ScaleY, "sy");
        }

        [TestMethod]
        public void EvaluateBones_Single_Root_Uses_Rig_Origin()
        {
            var rig = new Rig
            {
                OriginX = 100f,
                OriginY = 200f,
                Bones = { new Bone { Id = "root", LocalX = 10f, LocalY = 0f } }
            };
            var bones = RigEvaluator.EvaluateBones(rig);
            Assert.IsTrue(bones.ContainsKey("root"));
            AssertNear(110f, bones["root"].X, "x");
            AssertNear(200f, bones["root"].Y, "y");
        }

        [TestMethod]
        public void EvaluateBones_Child_Inherits_Parent_Rotation()
        {
            var rig = new Rig
            {
                OriginX = 0f,
                OriginY = 0f,
                Bones =
                {
                    new Bone { Id = "root", LocalRotation = (float)(Math.PI / 2.0) },
                    new Bone { Id = "child", ParentId = "root", LocalX = 10f }
                }
            };
            var bones = RigEvaluator.EvaluateBones(rig);
            AssertNear(0f, bones["child"].X, "child x");
            AssertNear(10f, bones["child"].Y, "child y");
        }

        [TestMethod]
        public void EvaluateBones_Cycle_Is_Broken_Silently()
        {
            var rig = new Rig
            {
                Bones =
                {
                    new Bone { Id = "a", ParentId = "b", LocalX = 1f },
                    new Bone { Id = "b", ParentId = "a", LocalX = 1f }
                }
            };
            // Should not stack-overflow / throw.
            var bones = RigEvaluator.EvaluateBones(rig);
            Assert.AreEqual(2, bones.Count);
        }

        [TestMethod]
        public void EvaluateBindings_Skips_When_Rig_Disabled()
        {
            var layout = new LcdLayout();
            layout.Sprites.Add(new SpriteEntry());
            var rig = new Rig
            {
                Enabled = false,
                Bones = { new Bone { Id = "b" } },
                Bindings = { new SpriteBinding { BoneId = "b", SpriteIndex = 0 } }
            };
            var result = RigEvaluator.EvaluateBindings(rig, layout);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void EvaluateBindings_Skips_Out_Of_Range_And_Muted()
        {
            var layout = new LcdLayout();
            layout.Sprites.Add(new SpriteEntry());
            var rig = new Rig
            {
                Bones = { new Bone { Id = "b" } },
                Bindings =
                {
                    new SpriteBinding { BoneId = "b", SpriteIndex = 5 },          // out of range
                    new SpriteBinding { BoneId = "b", SpriteIndex = 0, Muted = true },
                    new SpriteBinding { BoneId = "missing", SpriteIndex = 0 },    // unknown bone
                    new SpriteBinding { BoneId = "b", SpriteIndex = 0 }           // valid
                }
            };
            var result = RigEvaluator.EvaluateBindings(rig, layout);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0, result[0].SpriteIndex);
            Assert.AreEqual("b", result[0].BoneId);
        }

        [TestMethod]
        public void EvaluateBindings_Composes_Bone_And_Binding_Offsets()
        {
            var layout = new LcdLayout { SurfaceWidth = 512, SurfaceHeight = 512 };
            layout.Sprites.Add(new SpriteEntry());
            var rig = new Rig
            {
                OriginX = 100f,
                OriginY = 100f,
                Bones = { new Bone { Id = "b", LocalX = 50f, LocalY = 0f, LocalRotation = (float)(Math.PI / 2.0) } },
                Bindings = { new SpriteBinding { BoneId = "b", SpriteIndex = 0, OffsetX = 10f, OffsetY = 0f } }
            };
            var result = RigEvaluator.EvaluateBindings(rig, layout);
            Assert.AreEqual(1, result.Count);
            // Bone world = origin(100,100) + rot0 * (50,0) = (150,100), rot=90deg.
            // Binding world = boneWorld + rot90 * (10,0) = (150, 110).
            AssertNear(150f, result[0].X, "x");
            AssertNear(110f, result[0].Y, "y");
        }
    }
}
