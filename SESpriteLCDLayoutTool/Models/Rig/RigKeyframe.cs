using System;

namespace SESpriteLCDLayoutTool.Models.Rig
{
    /// <summary>
    /// A single keyframe on a bone track. Stores the full local transform that the
    /// bone should hold at <see cref="Time"/> seconds. Tracks are evaluated by linearly
    /// interpolating between adjacent keyframes; if a clip has only one keyframe the
    /// bone is simply pinned to that pose.
    /// </summary>
    [Serializable]
    public class RigKeyframe
    {
        /// <summary>Time in seconds from the start of the clip.</summary>
        public float Time { get; set; }

        public float LocalX { get; set; }
        public float LocalY { get; set; }
        /// <summary>Local rotation in radians.</summary>
        public float LocalRotation { get; set; }
        public float LocalScaleX { get; set; } = 1f;
        public float LocalScaleY { get; set; } = 1f;
        public float Length { get; set; } = 32f;

        /// <summary>
        /// Easing curve applied between this keyframe and the next.
        /// Linear is the default; smoother curves keep the runtime evaluator cheap.
        /// </summary>
        public RigEasing Easing { get; set; } = RigEasing.Linear;

        public RigKeyframe Clone() => new RigKeyframe
        {
            Time = Time,
            LocalX = LocalX,
            LocalY = LocalY,
            LocalRotation = LocalRotation,
            LocalScaleX = LocalScaleX,
            LocalScaleY = LocalScaleY,
            Length = Length,
            Easing = Easing,
        };
    }

    public enum RigEasing
    {
        Linear = 0,
        EaseInOut = 1,
        Step = 2,
    }
}
