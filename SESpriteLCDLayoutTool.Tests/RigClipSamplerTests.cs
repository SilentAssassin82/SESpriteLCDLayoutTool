using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SESpriteLCDLayoutTool.Models.Rig;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool.Tests
{
    [TestClass]
    public class RigClipSamplerTests
    {
        private const float Eps = 1e-3f;
        private static void AssertNear(float expected, float actual, string msg = null)
        {
            Assert.IsTrue(Math.Abs(expected - actual) < Eps,
                (msg ?? "value") + $": expected {expected}, got {actual}");
        }

        private static RigClip MakeClip(string boneId, params (float t, float x)[] keys)
        {
            var clip = new RigClip { Duration = 2f, Loop = false };
            var track = clip.GetOrCreateTrack(boneId);
            foreach (var k in keys)
                track.Keys.Add(new RigKeyframe { Time = k.t, LocalX = k.x });
            return clip;
        }

        [TestMethod]
        public void Empty_Clip_Returns_Empty_Sample()
        {
            var clip = new RigClip();
            var s = RigClipSampler.Sample(clip, 0.5f);
            Assert.AreEqual(0, s.Count);
        }

        [TestMethod]
        public void Single_Key_Pins_Bone_To_That_Pose()
        {
            var clip = MakeClip("b1", (0.5f, 42f));
            var s = RigClipSampler.Sample(clip, 0f);
            Assert.IsTrue(s.ContainsKey("b1"));
            AssertNear(42f, s["b1"].LocalX);
        }

        [TestMethod]
        public void Linear_Interpolation_Between_Two_Keys()
        {
            var clip = MakeClip("b1", (0f, 0f), (1f, 10f));
            var s = RigClipSampler.Sample(clip, 0.25f);
            AssertNear(2.5f, s["b1"].LocalX);
        }

        [TestMethod]
        public void Time_Before_First_Key_Returns_First_Key_Value()
        {
            var clip = MakeClip("b1", (0.5f, 5f), (1.5f, 10f));
            var s = RigClipSampler.Sample(clip, 0f);
            AssertNear(5f, s["b1"].LocalX);
        }

        [TestMethod]
        public void Time_After_Last_Key_Returns_Last_Key_Value()
        {
            var clip = MakeClip("b1", (0f, 5f), (1f, 10f));
            clip.Loop = false;
            clip.Duration = 1f;
            var s = RigClipSampler.Sample(clip, 5f);
            AssertNear(10f, s["b1"].LocalX);
        }

        [TestMethod]
        public void Looping_Clip_Wraps_Time()
        {
            var clip = MakeClip("b1", (0f, 0f), (1f, 10f));
            clip.Duration = 1f;
            clip.Loop = true;
            // 1.25 wraps to 0.25, expecting value 2.5
            var s = RigClipSampler.Sample(clip, 1.25f);
            AssertNear(2.5f, s["b1"].LocalX);
        }

        [TestMethod]
        public void Step_Easing_Holds_First_Key_Until_Next()
        {
            var clip = new RigClip { Duration = 1f, Loop = false };
            var t = clip.GetOrCreateTrack("b1");
            t.Keys.Add(new RigKeyframe { Time = 0f, LocalX = 0f, Easing = RigEasing.Step });
            t.Keys.Add(new RigKeyframe { Time = 1f, LocalX = 10f });
            var s = RigClipSampler.Sample(clip, 0.99f);
            AssertNear(0f, s["b1"].LocalX);
        }

        [TestMethod]
        public void EaseInOut_Midpoint_Is_Half()
        {
            var clip = new RigClip { Duration = 1f, Loop = false };
            var t = clip.GetOrCreateTrack("b1");
            t.Keys.Add(new RigKeyframe { Time = 0f, LocalX = 0f, Easing = RigEasing.EaseInOut });
            t.Keys.Add(new RigKeyframe { Time = 1f, LocalX = 10f });
            var s = RigClipSampler.Sample(clip, 0.5f);
            // smoothstep(0.5) = 0.5
            AssertNear(5f, s["b1"].LocalX);
        }

        [TestMethod]
        public void Evaluator_Applies_Override_To_Bone_Local_Transform()
        {
            var rig = new Rig();
            var bone = new Bone { Name = "b1", LocalX = 0f, LocalY = 0f, Length = 32f };
            rig.Bones.Add(bone);

            // Override moves the bone to (50, 0).
            var overrides = new Dictionary<string, RigKeyframe>
            {
                [bone.Id] = new RigKeyframe { LocalX = 50f, LocalScaleX = 1f, LocalScaleY = 1f }
            };

            var worlds = RigEvaluator.EvaluateBones(rig, overrides);
            AssertNear(50f, worlds[bone.Id].X);
            AssertNear(0f, worlds[bone.Id].Y);
        }
    }
}
