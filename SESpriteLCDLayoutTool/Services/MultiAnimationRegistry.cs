using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Tracks multiple independent keyframe animations by sprite source line number.
    /// Index 1 uses unsuffixed names (kfTick, kfEase, arot, _tick, etc.) for backward
    /// compatibility with hand-written code. Index 2+ gets numeric suffixes (kfTick2, arot2, etc.).
    /// </summary>
    public static class MultiAnimationRegistry
    {
        private static readonly Dictionary<int, int> AnimationIndices = new Dictionary<int, int>();
        private static int _manualSpriteCounter = 0;

        public static void Reset()
        {
            AnimationIndices.Clear();
            _manualSpriteCounter = 0;
        }

        /// <summary>
        /// Scan existing code for kfTick/kfTick2/kfTick3... declarations and reserve
        /// those index slots so the next new animation gets a non-colliding suffix.
        /// Call after Reset() and before generating any snippets.
        /// </summary>
        public static void ReserveExistingIndices(string existingCode)
        {
            if (string.IsNullOrEmpty(existingCode)) return;

            // Match declarations like: int[] kfTick = { ... }  or  int[] kfTick2 = { ... }
            var rx = new Regex(@"int\s*\[\s*\]\s+kfTick(\d*)\s*=");
            int maxIndex = 0;
            foreach (Match m in rx.Matches(existingCode))
            {
                string suffix = m.Groups[1].Value;
                int idx = string.IsNullOrEmpty(suffix) ? 1 : int.Parse(suffix);
                if (idx > maxIndex) maxIndex = idx;
            }
            // Set counter so next GetAnimationIndex call returns maxIndex + 1
            _manualSpriteCounter = maxIndex;
        }

        /// <summary>
        /// Get or assign a unique animation index for a sprite by source line.
        /// Returns 1, 2, 3, ... Index 1 = first animation (unsuffixed names).
        /// </summary>
        public static int GetAnimationIndex(int sourceLineNumber)
        {
            if (sourceLineNumber > 0)
            {
                if (!AnimationIndices.ContainsKey(sourceLineNumber))
                {
                    int maxExisting = AnimationIndices.Count > 0 ? AnimationIndices.Values.Max() : 0;
                    int nextIndex = Math.Max(maxExisting, _manualSpriteCounter) + 1;
                    AnimationIndices[sourceLineNumber] = nextIndex;
                }
                return AnimationIndices[sourceLineNumber];
            }

            _manualSpriteCounter++;
            return _manualSpriteCounter;
        }

        /// <summary>
        /// Force-register a specific source line with a known animation index.
        /// Used when a sprite already has a stored AnimationIndex from a prior edit.
        /// </summary>
        public static void RegisterAnimationIndex(int sourceLineNumber, int animIndex)
        {
            if (sourceLineNumber > 0 && animIndex > 0)
            {
                AnimationIndices[sourceLineNumber] = animIndex;
                if (animIndex > _manualSpriteCounter)
                    _manualSpriteCounter = animIndex;
            }
        }

        /// <summary>Returns "" for index 1, "2" for index 2, "3" for index 3, etc.</summary>
        internal static string Suffix(int index) => index <= 1 ? "" : index.ToString();

        // ── Index-based overloads (use these when you already have animIndex) ──
        public static string GetTickVariableName(int index, bool _) => "_tick" + Suffix(index);
        public static string GetTickArrayName(int index, bool _) => "kfTick" + Suffix(index);
        public static string GetEasingArrayName(int index, bool _) => "kfEase" + Suffix(index);
        public static string GetRotationVariableName(int index, bool _) => "arot" + Suffix(index);
        public static string GetPositionXVariableName(int index, bool _) => "ax" + Suffix(index);
        public static string GetPositionYVariableName(int index, bool _) => "ay" + Suffix(index);
        public static string GetSizeWidthVariableName(int index, bool _) => "aw" + Suffix(index);
        public static string GetSizeHeightVariableName(int index, bool _) => "ah" + Suffix(index);
        public static string GetScaleVariableName(int index, bool _) => "ascl" + Suffix(index);
        public static string GetColorRVariableName(int index, bool _) => "ar" + Suffix(index);
        public static string GetColorGVariableName(int index, bool _) => "ag" + Suffix(index);
        public static string GetColorBVariableName(int index, bool _) => "ab" + Suffix(index);
        public static string GetColorAVariableName(int index, bool _) => "aa" + Suffix(index);
        public static string GetRotArrayName(int index, bool _) => "kfRot" + Suffix(index);
        public static string GetPosXArrayName(int index, bool _) => "kfX" + Suffix(index);
        public static string GetPosYArrayName(int index, bool _) => "kfY" + Suffix(index);
        public static string GetWidthArrayName(int index, bool _) => "kfW" + Suffix(index);
        public static string GetHeightArrayName(int index, bool _) => "kfH" + Suffix(index);
        public static string GetScaleArrayName(int index, bool _) => "kfScl" + Suffix(index);
        public static string GetColorRArrayName(int index, bool _) => "kfR" + Suffix(index);
        public static string GetColorGArrayName(int index, bool _) => "kfG" + Suffix(index);
        public static string GetColorBArrayName(int index, bool _) => "kfB" + Suffix(index);
        public static string GetColorAArrayName(int index, bool _) => "kfA" + Suffix(index);
        public static string GetEasingVariableName(int index, bool _) => "kfEase" + Suffix(index);

        // ── Source-line-based overloads (each call to GetAnimationIndex may increment counter) ──
        public static string GetTickVariableName(int sourceLineNumber) => GetTickVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetTickArrayName(int sourceLineNumber) => GetTickArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetEasingArrayName(int sourceLineNumber) => GetEasingArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetRotationVariableName(int sourceLineNumber) => GetRotationVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetPositionXVariableName(int sourceLineNumber) => GetPositionXVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetPositionYVariableName(int sourceLineNumber) => GetPositionYVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetSizeWidthVariableName(int sourceLineNumber) => GetSizeWidthVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetSizeHeightVariableName(int sourceLineNumber) => GetSizeHeightVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetScaleVariableName(int sourceLineNumber) => GetScaleVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorRVariableName(int sourceLineNumber) => GetColorRVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorGVariableName(int sourceLineNumber) => GetColorGVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorBVariableName(int sourceLineNumber) => GetColorBVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorAVariableName(int sourceLineNumber) => GetColorAVariableName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetRotArrayName(int sourceLineNumber) => GetRotArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetPosXArrayName(int sourceLineNumber) => GetPosXArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetPosYArrayName(int sourceLineNumber) => GetPosYArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetWidthArrayName(int sourceLineNumber) => GetWidthArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetHeightArrayName(int sourceLineNumber) => GetHeightArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetScaleArrayName(int sourceLineNumber) => GetScaleArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorRArrayName(int sourceLineNumber) => GetColorRArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorGArrayName(int sourceLineNumber) => GetColorGArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorBArrayName(int sourceLineNumber) => GetColorBArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetColorAArrayName(int sourceLineNumber) => GetColorAArrayName(GetAnimationIndex(sourceLineNumber), true);
        public static string GetEasingVariableName(int sourceLineNumber) => GetEasingVariableName(GetAnimationIndex(sourceLineNumber), true);
    }
}
