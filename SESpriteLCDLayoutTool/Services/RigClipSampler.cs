using System.Collections.Generic;
using SESpriteLCDLayoutTool.Models.Rig;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Samples a <see cref="RigClip"/> at a given time and produces per-bone local-transform
    /// overrides (one <see cref="RigKeyframe"/> per affected bone). The sampler does not mutate
    /// the rig; consumers fold the overrides into <see cref="RigEvaluator"/> at render time.
    /// </summary>
    public static class RigClipSampler
    {
        /// <summary>
        /// Samples <paramref name="clip"/> at <paramref name="time"/> seconds. Returns a
        /// dictionary keyed by bone id with the interpolated local pose for each track.
        /// Tracks without keyframes are omitted; bones not in the dictionary keep their rest pose.
        /// </summary>
        public static Dictionary<string, RigKeyframe> Sample(RigClip clip, float time)
        {
            var result = new Dictionary<string, RigKeyframe>();
            if (clip == null || clip.Tracks == null) return result;

            float t = NormalizeTime(time, clip.Duration, clip.Loop);

            foreach (var track in clip.Tracks)
            {
                if (track == null || track.Keys == null || track.Keys.Count == 0) continue;
                if (string.IsNullOrEmpty(track.BoneId)) continue;

                var sample = SampleTrack(track, t);
                if (sample != null) result[track.BoneId] = sample;
            }

            return result;
        }

        private static float NormalizeTime(float time, float duration, bool loop)
        {
            if (duration <= 0f) return 0f;
            if (!loop)
            {
                if (time < 0f) return 0f;
                if (time > duration) return duration;
                return time;
            }
            // Loop: wrap into [0, duration).
            float t = time % duration;
            if (t < 0f) t += duration;
            return t;
        }

        private static RigKeyframe SampleTrack(RigBoneTrack track, float t)
        {
            var keys = track.Keys;
            int n = keys.Count;
            if (n == 1) return keys[0].Clone();

            // Keys are expected to be sorted by time; tolerate unsorted by linear scan.
            // Find the segment [a, b] such that a.Time <= t <= b.Time.
            RigKeyframe a = null, b = null;
            for (int i = 0; i < n; i++)
            {
                var k = keys[i];
                if (k == null) continue;
                if (k.Time <= t && (a == null || k.Time >= a.Time)) a = k;
                if (k.Time >= t && (b == null || k.Time <= b.Time)) b = k;
            }

            if (a == null) return b?.Clone();
            if (b == null) return a.Clone();
            if (a == b || b.Time <= a.Time) return a.Clone();

            float u = (t - a.Time) / (b.Time - a.Time);
            u = ApplyEasing(u, a.Easing);

            return new RigKeyframe
            {
                Time = t,
                LocalX = Lerp(a.LocalX, b.LocalX, u),
                LocalY = Lerp(a.LocalY, b.LocalY, u),
                LocalRotation = LerpAngle(a.LocalRotation, b.LocalRotation, u),
                LocalScaleX = Lerp(a.LocalScaleX, b.LocalScaleX, u),
                LocalScaleY = Lerp(a.LocalScaleY, b.LocalScaleY, u),
                Length = Lerp(a.Length, b.Length, u),
                Easing = a.Easing,
            };
        }

        private static float ApplyEasing(float u, RigEasing easing)
        {
            if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
            switch (easing)
            {
                case RigEasing.Step: return 0f;
                case RigEasing.EaseInOut: return u * u * (3f - 2f * u);
                case RigEasing.Linear:
                default: return u;
            }
        }

        private static float Lerp(float a, float b, float u) => a + (b - a) * u;

        private static float LerpAngle(float a, float b, float u)
        {
            // Wrap into shortest path so ±π crossings interpolate smoothly.
            const float TwoPi = 6.2831853071795864769f;
            float diff = (b - a) % TwoPi;
            if (diff > TwoPi * 0.5f) diff -= TwoPi;
            else if (diff < -TwoPi * 0.5f) diff += TwoPi;
            return a + diff * u;
        }
    }
}
