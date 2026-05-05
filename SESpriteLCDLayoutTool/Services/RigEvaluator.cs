using System.Collections.Generic;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Models.Rig;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Pure (no UI, no codegen) evaluator that walks a <see cref="Rig"/> hierarchy and produces
    /// world-space transforms for each bone, plus per-binding transformed sprite poses.
    ///
    /// Phase 2 deliberately keeps this read-only: it does NOT mutate <see cref="SpriteEntry"/>.
    /// Consumers (canvas, code emitter) can read the resulting <see cref="EvaluatedSprite"/>
    /// list and decide how to use it.
    /// </summary>
    public static class RigEvaluator
    {
        /// <summary>
        /// Result of evaluating a single sprite binding against a posed rig.
        /// Position is the world-space CENTER of the sprite (matches <see cref="SpriteEntry.X"/>/Y semantics).
        /// </summary>
        public struct EvaluatedSprite
        {
            public int SpriteIndex;
            public string BoneId;
            public float X;
            public float Y;
            public float Rotation;
            public float ScaleX;
            public float ScaleY;
        }

        /// <summary>
        /// Compute world transforms for every bone in the rig, keyed by bone id.
        /// Bones with a missing/unknown <see cref="Bone.ParentId"/> are treated as roots and
        /// composed against the rig origin. Cycles are broken silently (a bone whose parent
        /// chain loops back to itself is treated as a root).
        /// </summary>
        public static Dictionary<string, RigTransform> EvaluateBones(Rig rig)
        {
            return EvaluateBones(rig, null);
        }

        /// <summary>
        /// Compute world transforms for every bone, applying optional per-bone local-transform
        /// overrides (e.g. from an animation clip sample). When <paramref name="overrides"/> is null
        /// or a bone has no entry, the bone's rest local transform is used.
        /// </summary>
        public static Dictionary<string, RigTransform> EvaluateBones(Rig rig, Dictionary<string, RigKeyframe> overrides)
        {
            var result = new Dictionary<string, RigTransform>();
            if (rig == null || rig.Bones == null) return result;

            // Index for parent lookups.
            var byId = new Dictionary<string, Bone>(rig.Bones.Count);
            foreach (var b in rig.Bones)
            {
                if (b == null || string.IsNullOrEmpty(b.Id)) continue;
                byId[b.Id] = b;
            }

            var rigOrigin = new RigTransform(rig.OriginX, rig.OriginY, 0f, 1f, 1f);

            foreach (var bone in rig.Bones)
            {
                if (bone == null || string.IsNullOrEmpty(bone.Id)) continue;
                result[bone.Id] = ComputeBoneWorld(bone, byId, rigOrigin, result, overrides);
            }

            return result;
        }

        private static RigTransform ComputeBoneWorld(
            Bone bone,
            Dictionary<string, Bone> byId,
            RigTransform rigOrigin,
            Dictionary<string, RigTransform> cache,
            Dictionary<string, RigKeyframe> overrides)
        {
            // Walk up to the root, building a stack, with cycle detection.
            var chain = new List<Bone>();
            var seen = new HashSet<string>();
            var current = bone;
            while (current != null && seen.Add(current.Id))
            {
                chain.Add(current);
                if (string.IsNullOrEmpty(current.ParentId)) break;
                Bone parent;
                if (!byId.TryGetValue(current.ParentId, out parent)) break;
                current = parent;
            }

            // Compose top-down: rigOrigin -> root -> ... -> bone.
            var world = rigOrigin;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var b = chain[i];
                RigTransform cached;
                if (cache.TryGetValue(b.Id, out cached) && i != 0)
                {
                    // Cached parent world transform; jump straight to it and continue from below.
                    world = cached;
                    continue;
                }

                var local = GetLocalTransform(b, overrides);

                world = RigTransform.Compose(world, local);
            }

            return world;
        }

        private static RigTransform GetLocalTransform(Bone b, Dictionary<string, RigKeyframe> overrides)
        {
            RigKeyframe k;
            if (overrides != null && !string.IsNullOrEmpty(b.Id) && overrides.TryGetValue(b.Id, out k) && k != null)
            {
                return new RigTransform(k.LocalX, k.LocalY, k.LocalRotation, k.LocalScaleX, k.LocalScaleY);
            }
            return new RigTransform(b.LocalX, b.LocalY, b.LocalRotation, b.LocalScaleX, b.LocalScaleY);
        }

        /// <summary>
        /// Evaluate every binding in <paramref name="rig"/> against the given <paramref name="layout"/>,
        /// producing world-space sprite poses. Bindings whose <see cref="SpriteBinding.Muted"/> is true,
        /// whose <see cref="SpriteBinding.SpriteIndex"/> is out of range, or whose bone is missing are skipped.
        /// </summary>
        public static List<EvaluatedSprite> EvaluateBindings(Rig rig, LcdLayout layout)
        {
            return EvaluateBindings(rig, layout, null);
        }

        /// <summary>
        /// Same as <see cref="EvaluateBindings(Rig, LcdLayout)"/> but applies optional per-bone
        /// local-transform overrides (e.g. from a clip sample) before computing sprite poses.
        /// </summary>
        public static List<EvaluatedSprite> EvaluateBindings(Rig rig, LcdLayout layout, Dictionary<string, RigKeyframe> overrides)
        {
            var output = new List<EvaluatedSprite>();
            if (rig == null || layout == null || rig.Bindings == null) return output;
            if (!rig.Enabled) return output;

            var bones = EvaluateBones(rig, overrides);
            int spriteCount = layout.Sprites != null ? layout.Sprites.Count : 0;

            foreach (var bind in rig.Bindings)
            {
                if (bind == null || bind.Muted) continue;
                if (bind.SpriteIndex < 0 || bind.SpriteIndex >= spriteCount) continue;
                if (string.IsNullOrEmpty(bind.BoneId)) continue;

                RigTransform boneWorld;
                if (!bones.TryGetValue(bind.BoneId, out boneWorld)) continue;

                var bindLocal = new RigTransform(
                    bind.OffsetX,
                    bind.OffsetY,
                    bind.RotationOffset,
                    bind.ScaleX,
                    bind.ScaleY);

                var spriteWorld = RigTransform.Compose(boneWorld, bindLocal);

                output.Add(new EvaluatedSprite
                {
                    SpriteIndex = bind.SpriteIndex,
                    BoneId = bind.BoneId,
                    X = spriteWorld.X,
                    Y = spriteWorld.Y,
                    Rotation = spriteWorld.Rotation,
                    ScaleX = spriteWorld.ScaleX,
                    ScaleY = spriteWorld.ScaleY,
                });
            }

            return output;
        }
    }
}
