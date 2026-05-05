using System;

namespace SESpriteLCDLayoutTool.Models.Rig
{
    /// <summary>
    /// A 2D affine transform expressed as translation + rotation (radians) + non-uniform scale.
    /// Composition is applied in TRS order: child = parent * local, where applying to a point
    /// scales first, then rotates, then translates.
    /// </summary>
    public struct RigTransform
    {
        public float X;
        public float Y;
        public float Rotation;
        public float ScaleX;
        public float ScaleY;

        public static RigTransform Identity => new RigTransform { ScaleX = 1f, ScaleY = 1f };

        public RigTransform(float x, float y, float rotation, float scaleX, float scaleY)
        {
            X = x;
            Y = y;
            Rotation = rotation;
            ScaleX = scaleX;
            ScaleY = scaleY;
        }

        /// <summary>
        /// Compose <paramref name="parent"/> with a child <paramref name="local"/> transform.
        /// The result is: take a point in the child's local space, apply <paramref name="local"/>,
        /// then apply <paramref name="parent"/>.
        /// </summary>
        public static RigTransform Compose(RigTransform parent, RigTransform local)
        {
            // Rotate the local translation by the parent rotation, scale by parent scale.
            float cos = (float)Math.Cos(parent.Rotation);
            float sin = (float)Math.Sin(parent.Rotation);

            float lx = local.X * parent.ScaleX;
            float ly = local.Y * parent.ScaleY;

            float rx = lx * cos - ly * sin;
            float ry = lx * sin + ly * cos;

            return new RigTransform
            {
                X = parent.X + rx,
                Y = parent.Y + ry,
                Rotation = parent.Rotation + local.Rotation,
                ScaleX = parent.ScaleX * local.ScaleX,
                ScaleY = parent.ScaleY * local.ScaleY,
            };
        }

        /// <summary>
        /// Apply this transform to a local-space point, returning the world-space point.
        /// </summary>
        public void TransformPoint(float localX, float localY, out float worldX, out float worldY)
        {
            float cos = (float)Math.Cos(Rotation);
            float sin = (float)Math.Sin(Rotation);

            float sx = localX * ScaleX;
            float sy = localY * ScaleY;

            worldX = X + (sx * cos - sy * sin);
            worldY = Y + (sx * sin + sy * cos);
        }
    }
}
