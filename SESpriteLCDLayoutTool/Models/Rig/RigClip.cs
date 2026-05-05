using System;
using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Models.Rig
{
    /// <summary>
    /// A named animation clip on a rig. Each clip owns one track per bone (keyed by
    /// <see cref="Bone.Id"/>); at evaluation time the clip samples each track at the
    /// current playhead and produces per-bone local-transform overrides which the
    /// canvas folds into the rig pose before rendering.
    ///
    /// Tracks not present in the clip leave the bone at its rest pose, so a clip can
    /// drive only the bones it cares about.
    /// </summary>
    [Serializable]
    public class RigClip
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Clip";

        /// <summary>Total clip duration in seconds. Clips loop when <see cref="Loop"/> is true.</summary>
        public float Duration { get; set; } = 1.0f;

        public bool Loop { get; set; } = true;

        /// <summary>Per-bone tracks keyed by <see cref="Bone.Id"/>.</summary>
        public List<RigBoneTrack> Tracks { get; set; } = new List<RigBoneTrack>();

        public RigBoneTrack GetOrCreateTrack(string boneId)
        {
            var t = Tracks.Find(x => x.BoneId == boneId);
            if (t == null)
            {
                t = new RigBoneTrack { BoneId = boneId };
                Tracks.Add(t);
            }
            return t;
        }
    }

    /// <summary>One bone's keyframe sequence within a <see cref="RigClip"/>.</summary>
    [Serializable]
    public class RigBoneTrack
    {
        public string BoneId { get; set; }
        public List<RigKeyframe> Keys { get; set; } = new List<RigKeyframe>();
    }
}
