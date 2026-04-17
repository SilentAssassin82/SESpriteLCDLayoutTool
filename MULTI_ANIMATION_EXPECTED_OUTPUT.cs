// ─── Multi-Animation Test Program ───
// This demonstrates the expected behavior after implementing MultiAnimationRegistry
// Run this in a PB to see two independent animations working simultaneously

int _tick1 = 0;  // Sprite A's tick counter
int _tick2 = 0;  // Sprite B's tick counter (independent)
IMyTextSurface _surface;

// ── Keyframe data for Sprite A (SemiCircle Rotation) ──
int[] kfTick1 = { 0, 60, 90, 120, 150 };
int[] kfEase1 = { 0, 0, 0, 0, 0 };
float[] kfRot1 = { 0.0000f, 0.0000f, 0.0000f, 0.0000f, 6.3000f };

// ── Keyframe data for Sprite B (Triangle Y-Move) ──
int[] kfTick2 = { 0, 50, 100, 150 };
int[] kfEase2 = { 0, 0, 0, 0 };
float[] kfY2 = { 256.0f, 200.0f, 312.0f, 256.0f };

// ── Easing helper ──
public float Ease(float t, int easeType)
{
    switch (easeType)
    {
        case 0: return t;
        case 1: return (float)(0.5 - 0.5 * Math.Cos(t * Math.PI));
        default: return t;
    }
}

public Program()
{
    _surface = Me.GetSurface(0);
    _surface.ContentType = ContentType.SCRIPT;
    _surface.Script = "";
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
}

public void Main(string argument, UpdateType updateSource)
{
    var frame = _surface.DrawFrame();

    // ── Animation for Sprite A (SemiCircle) ──
    _tick1++;
    int t1 = _tick1 % 150;

    int seg1 = 0;
    for (int i = 1; i < 5; i++)
        if (t1 >= kfTick1[i]) seg1 = i;
    int next1 = (seg1 + 1 < 5) ? seg1 + 1 : seg1;
    float span1 = kfTick1[next1] - kfTick1[seg1];
    float frac1 = span1 > 0 ? (t1 - kfTick1[seg1]) / span1 : 0f;
    float ef1 = Ease(frac1, kfEase1[seg1]);

    float arot1 = kfRot1[seg1] + (kfRot1[next1] - kfRot1[seg1]) * ef1;

    frame.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "SemiCircle",
        Position = new Vector2(256.0f, 256.0f),
        Size = new Vector2(100.0f, 100.0f),
        Color = new Color(255, 255, 255, 255),
        Alignment = TextAlignment.CENTER,
        RotationOrScale = arot1,  // Uses arot1 (Sprite A's rotation)
    });

    // ── Animation for Sprite B (Triangle) ──
    _tick2++;
    int t2 = _tick2 % 150;

    int seg2 = 0;
    for (int i = 1; i < 4; i++)
        if (t2 >= kfTick2[i]) seg2 = i;
    int next2 = (seg2 + 1 < 4) ? seg2 + 1 : seg2;
    float span2 = kfTick2[next2] - kfTick2[seg2];
    float frac2 = span2 > 0 ? (t2 - kfTick2[seg2]) / span2 : 0f;
    float ef2 = Ease(frac2, kfEase2[seg2]);

    float ay2 = kfY2[seg2] + (kfY2[next2] - kfY2[seg2]) * ef2;

    frame.Add(new MySprite
    {
        Type = SpriteType.TEXTURE,
        Data = "Triangle",
        Position = new Vector2(128.0f, ay2),  // Uses ay2 (Sprite B's Y position)
        Size = new Vector2(80.0f, 80.0f),
        Color = new Color(255, 0, 0, 255),
        Alignment = TextAlignment.CENTER,
        RotationOrScale = 0.0f,
    });

    frame.Dispose();
}

// ── Key observations ──
// ✅ _tick1 and _tick2 are independent counters
// ✅ arot1 is used for Sprite A's rotation (NOT arot)
// ✅ ay2 is used for Sprite B's Y position (NOT ay)
// ✅ Both animations run simultaneously without interference
// ✅ Triangle doesn't incorrectly inherit arot from SemiCircle
// ✅ Variable names are predictable and unique per animation
