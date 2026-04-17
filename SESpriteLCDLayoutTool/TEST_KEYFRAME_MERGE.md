# Keyframe Merge Test Workflow

## Test Scenario: Update Animation with Multiple Keyframes Already Present

**Setup:**
1. Create or load a sprite with keyframe animation
2. Open keyframe editor dialog  
3. Modify keyframes (e.g., change rotation value)
4. Click "Update Code" button

**Expected Result:**
- Code panel updates with new animation arrays (kfRot, kfTick, etc.)
- Multiple keyframes are preserved
- No "Could not safely merge" message
- Animation still compiles and runs

## Root Cause Fixed
The issue was in `MergeAnimationCodeIntoPanel()` at line 1136:
- **Before**: When existing keyframe arrays were detected, the merge was rejected entirely
- **After**: Uses `AnimationSnippetGenerator.MergeKeyframedIntoCode()` to intelligently merge array values even when existing keyframes are present

## Code Changes
**File**: `SESpriteLCDLayoutTool/MainForm.Animation.cs` (lines 1130-1160)

Changed from:
```csharp
if (hasAnyNamedBlock || hasAnyKeyframeArrays)
{
    // FAIL: Leave code unchanged
    newCode = existing;
    SetStatus("Could not safely merge this animation into existing keyframe blocks; original code preserved.");
}
```

Changed to:
```csharp
if (hasAnyNamedBlock || hasAnyKeyframeArrays)
{
    // TRY: Use smart array-level merge
    string merged = AnimationSnippetGenerator.MergeKeyframedIntoCode(existing, snippetCode);
    if (merged != null)
    {
        newCode = merged;
        SetStatus("✅ Animation arrays merged into existing code");
    }
    else
    {
        // FALLBACK: Preserve code if merge fails
        newCode = existing;
        SetStatus("Could not safely merge...");
    }
}
```

## Test Instructions
1. Paste your test PBDebug.cs script (with 5 keyframes) into code panel
2. Select a sprite with keyframe animation
3. Edit keyframe properties (e.g., change rotation value from 6.3 to 3.15)
4. Click "Update Code" button
5. **Verify**: Code panel shows updated kfRot array with your new value
6. No error messages should appear
