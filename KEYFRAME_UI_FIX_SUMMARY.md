# Keyframe UI Animation Update Fix

## Problem

The keyframe editor dialog was no longer updating the code panel with generated animation code. After clicking "Update Code" in the keyframe editor, nothing was appearing in the code panel.

## Root Cause

When implementing the multi-animation registry system with unique variable names (arot1, arot2, etc.), a critical bug was introduced:

**Sprites without source line numbers were all assigned animation index 1**, causing all new animations to use the same variable names, resulting in collisions and silent failures.

### Specific Issue

```csharp
// OLD CODE - BROKEN
public static int GetAnimationIndex(int sourceLineNumber)
{
    if (sourceLineNumber <= 0)
        return 1; // ← ALL untracked sprites get the SAME index!
    ...
}
```

When a user created animations on manually-added sprites (not parsed from code), all those sprites would have `SourceLineNumber = -1`. They would ALL be assigned index 1, resulting in:
- Sprite A animation: `_tick1`, `arot1`, `ax1`, etc.
- Sprite B animation: `_tick1`, `arot1`, `ax1`, etc. (COLLISION!)

This caused silent failures and the code generation to be ignored.

## Solution

Implemented a **fallback registry** for sprites without source lines:

```csharp
// NEW CODE - FIXED
private static readonly Dictionary<int, int> AnimationIndices = new Dictionary<int, int>();
private static readonly Dictionary<int, int> FallbackIndices = new Dictionary<int, int>();

public static int GetAnimationIndex(int sourceLineNumber, SpriteEntry sprite = null)
{
    // If sprite has a valid source line, use it
    if (sourceLineNumber > 0)
    {
        if (!AnimationIndices.ContainsKey(sourceLineNumber))
            AnimationIndices[sourceLineNumber] = AnimationIndices.Count + 1;
        return AnimationIndices[sourceLineNumber];
    }

    // For sprites without source lines, use sprite object identity as fallback
    if (sprite != null)
    {
        int spriteId = sprite.GetHashCode();
        if (!FallbackIndices.ContainsKey(spriteId))
        {
            // Assign next available index, accounting for both registries
            int nextIndex = Math.Max(
                AnimationIndices.Count > 0 ? AnimationIndices.Values.Max() : 0,
                FallbackIndices.Count > 0 ? FallbackIndices.Values.Max() : 0) + 1;
            FallbackIndices[spriteId] = nextIndex;
        }
        return FallbackIndices[spriteId];
    }

    return 1;
}
```

### Key Changes

1. **Dual-registry system**:
   - `AnimationIndices`: For sprites parsed from code (have `SourceLineNumber > 0`)
   - `FallbackIndices`: For manually-created sprites (using object identity via `GetHashCode()`)

2. **Updated all variable name generators** to accept optional `sprite` parameter:
   ```csharp
   public static string GetRotationVariableName(int sourceLineNumber, SpriteEntry sprite = null)
       => "arot" + GetAnimationIndex(sourceLineNumber, sprite);
   ```

3. **Updated call sites**:
   - `KeyframedCodeGenerator.GenerateKeyframed()` now passes sprite to registry
   - `KeyframedCodeGenerator.GenerateKeyframedGroup()` now passes sprite to registry
   - `MainForm.MergeAnimationCodeIntoPanel()` now passes sprite to registry

## Result

✅ **Multiple independent animations now work correctly**:
- Sprite A (manually created): Gets unique variables `_tick1`, `arot1`, `ax1`, etc.
- Sprite B (manually created): Gets different unique variables `_tick2`, `arot2`, `ax2`, etc.
- No variable name collisions
- Code generation succeeds
- Keyframe editor "Update Code" button now correctly updates the code panel

## Testing

After applying this fix:

1. Create a sprite (e.g., "SemiCircle")
2. Open keyframe editor → click "Update Code"
3. Code panel should update with generated animation code
4. Create another sprite (e.g., "Triangle")
5. Open keyframe editor → click "Update Code"
6. Code panel should now have TWO independent animations with unique variables
7. Both animations should play independently without interference

## Files Modified

- `SESpriteLCDLayoutTool/Services/MultiAnimationRegistry.cs` - Added fallback registry and sprite parameter
- `SESpriteLCDLayoutTool/Services/KeyframedCodeGenerator.cs` - Updated to pass sprite to registry
- `SESpriteLCDLayoutTool/MainForm.Animation.cs` - Updated to pass sprite to registry
