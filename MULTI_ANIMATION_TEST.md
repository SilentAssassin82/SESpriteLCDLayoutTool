# Multi-Animation System Test

## Test Scenario

This documents the expected behavior of the multi-animation system with unique variable naming per sprite.

### Scenario 1: Two Independent Rotation Animations

**Setup:**
1. Create Sprite A (SemiCircle) at source line 100 with rotation animation (0° → 360°)
2. Create Sprite B (Triangle) at source line 200 with rotation animation (0° → -360°)
3. Both sprites get their own keyframe animation, NOT in a group

**Expected Output (Snippet 1 for Sprite A):**
```csharp
// ─── Keyframe Animation: "SemiCircle" [PB] ───
// 5 keyframes over 150 ticks  |  Loop: Loop  |  Animation Index: 1

int _tick1 = 0;  // Unique variable name

int[] kfTick = { 0, 60, 90, 120, 150 };
int[] kfEase = { 0, 0, 0, 0, 0 };
float[] kfRot = { 0.0000f, 0.0000f, 0.0000f, 0.0000f, 6.3000f };

_tick1++;  // Increments Sprite A's tick
int t = _tick1 % 150;

float arot1 = kfRot[seg] + (kfRot[next] - kfRot[seg]) * ef;  // Unique variable arot1

frame.Add(new MySprite {
    ...
    RotationOrScale = arot1,  // Uses arot1 (NOT arot)
});
```

**Expected Output (Snippet 2 for Sprite B, merged into code):**
```csharp
// ─── Keyframe Animation: "SemiCircle" [PB] ───
// (Sprite A code as above...)
frame.Add(new MySprite {
    ...
    RotationOrScale = arot1,
});

// ─── Keyframe Animation: "Triangle" [PB] ───
// 5 keyframes over 150 ticks  |  Loop: Loop  |  Animation Index: 2

int _tick2 = 0;  // Different variable name for Sprite B

int[] kfTick = { 0, 60, 90, 120, 150 };
int[] kfEase = { 0, 0, 0, 0, 0 };
float[] kfRot = { 0.0000f, 0.0000f, 0.0000f, 0.0000f, -6.3000f };

_tick2++;  // Increments Sprite B's tick (independent of _tick1)
int t = _tick2 % 150;

float arot2 = kfRot[seg] + (kfRot[next] - kfRot[seg]) * ef;  // Unique variable arot2

frame.Add(new MySprite {
    ...
    RotationOrScale = arot2,  // Uses arot2 (NOT arot or arot1)
});
```

### Key Requirements Met

✅ **Sprite A gets _tick1, arot1**
✅ **Sprite B gets _tick2, arot2** (different from A)
✅ **No variable collision** (can't have both using _tick or arot)
✅ **Identified by source line** (line 100 → index 1, line 200 → index 2)
✅ **Unlimited animations** can be added (index 3, 4, 5, ...)
✅ **Each animation runs independently** (_tick1 increments separately from _tick2)
✅ **No cross-animation variable bleed** (Triangle doesn't inherit arot from SemiCircle)

## Implementation Architecture

### MultiAnimationRegistry
- Static dictionary mapping source line number → animation index
- `GetAnimationIndex(int sourceLineNumber)` → returns 1, 2, 3, ... deterministically
- Variable name helpers:
  - `GetTickVariableName(line)` → "_tick1", "_tick2", etc.
  - `GetRotationVariableName(line)` → "arot1", "arot2", etc.
  - `GetPositionXVariableName(line)` → "ax1", "ax2", etc.
  - Similar for Y, Width, Height, Colors, Scale

### GenerateKeyframed Integration
- Calls registry to get unique variable names based on `sprite.SourceLineNumber`
- Emits all variable declarations with the suffix (e.g., `int _tick1 = 0`)
- All interpolation logic uses the unique names (e.g., `float arot1 = ...`)
- Sprite.Add() call uses the unique names (e.g., `RotationOrScale = arot1`)

### MergeAnimationCodeIntoPanel
- Initializes registry when processing each sprite
- Tier-1 merge: Replaces old animation block with new one (uses SourceLine marker)
- Tier-2 merge: Updates keyframe arrays if no block found
- Tier-3 append: Adds complete new animation block if no target found

## Testing Checklist

- [ ] Add Sprite A with rotation animation → code shows _tick1, arot1
- [ ] Add Sprite B with different animation → code shows _tick2, arot2 (NOT arot)
- [ ] Verify Sprite A animation still works after adding Sprite B
- [ ] Verify Sprite B animation runs independently
- [ ] Both animations sync to each other when using animation groups
- [ ] Code compiles without errors
- [ ] Canvas preview shows both animations playing
