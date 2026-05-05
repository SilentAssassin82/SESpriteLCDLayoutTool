namespace SESpriteLCDLayoutTool.Services
{
    public static partial class CodeExecutor
    {
        // ── SE Type Stubs ─────────────────────────────────────────────────────
        // These mirror the minimal subset of the Space Engineers API used by
        // LCD rendering code.  They are compiled together with the user's code.
        // Each stub lives in its real SE namespace so that user code that has
        //   using VRageMath;
        //   using VRage.Game.GUI.TextPanel;
        //   using Sandbox.ModAPI.Ingame;
        // works without modification.
        internal const string StubsSource = @"
// ── Global sprite collector — captures sprites added via MySpriteDrawFrame ──
namespace SELcdExec
{
    using System.Collections.Generic;
    using VRage.Game.GUI.TextPanel;
    using Sandbox.ModAPI.Ingame;

    public static class SpriteCollector
    {
        [System.ThreadStatic] public static List<MySprite> Captured;
        [System.ThreadStatic] public static List<string> CapturedMethods;
        [System.ThreadStatic] public static List<int> CapturedMethodIndices;
        [System.ThreadStatic] public static List<int> CapturedLineNumbers;
        [System.ThreadStatic] public static List<int> CapturedInvocationIndices;
        [System.ThreadStatic] public static string CurrentMethod;
        [System.ThreadStatic] public static int CurrentInvocation;
        [System.ThreadStatic] private static Dictionary<string, int> _methodIndices;
        [System.ThreadStatic] private static Dictionary<string, int> _methodInvocations;
        [System.ThreadStatic] private static Dictionary<string, int> _methodNameIndices;
        [System.ThreadStatic] public static bool _skipNextRecord;
        [System.ThreadStatic] private static List<string> _preMethods;
        [System.ThreadStatic] private static List<int> _preIndices;
        [System.ThreadStatic] private static List<int> _preLines;
        [System.ThreadStatic] private static List<int> _preInvocations;
        [System.ThreadStatic] private static int _preConsumeIdx;

        public static void Reset()
        {
            if (Captured == null) Captured = new List<MySprite>();
            else Captured.Clear();
            if (CapturedMethods == null) CapturedMethods = new List<string>();
            else CapturedMethods.Clear();
            if (CapturedMethodIndices == null) CapturedMethodIndices = new List<int>();
            else CapturedMethodIndices.Clear();
            if (CapturedLineNumbers == null) CapturedLineNumbers = new List<int>();
            else CapturedLineNumbers.Clear();
            if (CapturedInvocationIndices == null) CapturedInvocationIndices = new List<int>();
            else CapturedInvocationIndices.Clear();
            CurrentMethod = null;
            CurrentInvocation = -1;
            if (_methodIndices == null) _methodIndices = new Dictionary<string, int>();
            else _methodIndices.Clear();
            if (_methodInvocations == null) _methodInvocations = new Dictionary<string, int>();
            else _methodInvocations.Clear();
            if (_methodNameIndices == null) _methodNameIndices = new Dictionary<string, int>();
            else _methodNameIndices.Clear();
            _skipNextRecord = false;
            if (_preMethods == null) { _preMethods = new List<string>(); _preIndices = new List<int>(); _preLines = new List<int>(); _preInvocations = new List<int>(); }
            else { _preMethods.Clear(); _preIndices.Clear(); _preLines.Clear(); _preInvocations.Clear(); }
            _preConsumeIdx = 0;
        }

        public static void SetCurrentMethod(string methodName)
        {
            CurrentMethod = methodName;
            if (_methodIndices == null) _methodIndices = new Dictionary<string, int>();
            if (!string.IsNullOrEmpty(methodName) && !_methodIndices.ContainsKey(methodName))
                _methodIndices[methodName] = 0;
            // Track how many times this method has been entered (invocation count)
            if (_methodInvocations == null) _methodInvocations = new Dictionary<string, int>();
            if (!string.IsNullOrEmpty(methodName))
            {
                if (!_methodInvocations.ContainsKey(methodName))
                    _methodInvocations[methodName] = 0;
                CurrentInvocation = _methodInvocations[methodName]++;
            }
            else
            {
                CurrentInvocation = -1;
            }
        }

        /// <summary>
        /// Pre-records method attribution at List&lt;MySprite&gt;.Add() time.
        /// When sprites are batch-collected into a local list then flushed to
        /// MySpriteDrawFrame.Add() in a separate loop, the per-case method name
        /// would be lost.  PreRecord queues the attribution so that the later
        /// RecordSpriteMethod(-1) call from MySpriteDrawFrame.Add() can consume it.
        /// </summary>
        public static void PreRecord(int line)
        {
            if (_preMethods == null) { _preMethods = new List<string>(); _preIndices = new List<int>(); _preLines = new List<int>(); _preInvocations = new List<int>(); }
            // Store -1 as placeholder; real per-name index computed at consume time
            // when the sprite is in Captured and we know its Data/name.
            _preMethods.Add(CurrentMethod);
            _preIndices.Add(-1);
            _preLines.Add(line);
            _preInvocations.Add(CurrentInvocation);
        }

        public static void RecordSpriteMethod(int line = -1)
        {
            // Guard against double-recording: MySpriteDrawFrame.Add() already calls
            // RecordSpriteMethod(-1) internally; if InstrumentAddCalls also injected a
            // call with the real line number, merge it into the existing record instead
            // of double-recording. This is critical: without merging, SourceLineNumber
            // would ALWAYS be -1 for frame.Add() sprites (PBs, Mods, Pulsar, Torch).
            if (_skipNextRecord) { _skipNextRecord = false; if (line > 0) { if (CapturedLineNumbers != null && CapturedLineNumbers.Count > 0) CapturedLineNumbers[CapturedLineNumbers.Count - 1] = line; return; } }
            if (CapturedMethods == null) CapturedMethods = new List<string>();
            if (CapturedMethodIndices == null) CapturedMethodIndices = new List<int>();
            if (CapturedLineNumbers == null) CapturedLineNumbers = new List<int>();
            if (CapturedInvocationIndices == null) CapturedInvocationIndices = new List<int>();
            // Consume pre-recorded entry if available (batch-flush pattern:
            // sprites collected via List.Add then flushed through frame.Add).
            // Only consume when line==-1 (from MySpriteDrawFrame.Add), not for
            // directly instrumented List.Add calls that pass a real line number.
            if (line == -1 && _preMethods != null && _preConsumeIdx < _preMethods.Count)
            {
                int pi = _preConsumeIdx++;
                string preMeth = _preMethods[pi] ?? """";
                string preSpName = (Captured != null && Captured.Count > 0) ? (Captured[Captured.Count - 1].Data ?? """") : """";
                string preKey = preMeth + ""|"" + preSpName;
                if (_methodNameIndices == null) _methodNameIndices = new Dictionary<string, int>();
                if (!_methodNameIndices.ContainsKey(preKey)) _methodNameIndices[preKey] = 0;
                int preIdx = _methodNameIndices[preKey]++;
                CapturedMethods.Add(_preMethods[pi]);
                CapturedMethodIndices.Add(preIdx);
                CapturedLineNumbers.Add(_preLines[pi]);
                CapturedInvocationIndices.Add(_preInvocations[pi]);
                return;
            }
            CapturedMethods.Add(CurrentMethod);
            int idx = -1;
            if (line == -1 && Captured != null && Captured.Count > 0)
            {
                string meth = CurrentMethod ?? """";
                string spName = Captured[Captured.Count - 1].Data ?? """";
                string key = meth + ""|"" + spName;
                if (_methodNameIndices == null) _methodNameIndices = new Dictionary<string, int>();
                if (!_methodNameIndices.ContainsKey(key)) _methodNameIndices[key] = 0;
                idx = _methodNameIndices[key]++;
            }
            else if (!string.IsNullOrEmpty(CurrentMethod))
            {
                if (_methodIndices == null) _methodIndices = new Dictionary<string, int>();
                if (!_methodIndices.ContainsKey(CurrentMethod)) _methodIndices[CurrentMethod] = 0;
                idx = _methodIndices[CurrentMethod]++;
            }
            CapturedMethodIndices.Add(idx);
            CapturedLineNumbers.Add(line);
            CapturedInvocationIndices.Add(CurrentInvocation);
        }
    }

    /// <summary>
    /// A List&lt;MySprite&gt; wrapper used by the animation/execution harness.
    /// Method attribution is handled by InstrumentAddCalls which injects
    /// SpriteCollector.RecordSpriteMethod() at each .Add() call site.
    /// </summary>
    public class TrackedSpriteList : List<MySprite>
    {
        private int _trackStart = 0;

        /// <summary>Call before invoking a render method to mark the starting point.</summary>
        public void BeginTrack()
        {
            _trackStart = this.Count;
        }

        /// <summary>Call after invoking a render method — recording is handled by instrumentation.</summary>
        public void EndTrack()
        {
            // Recording is now done via InstrumentAddCalls
            _trackStart = this.Count;
        }
    }
}

namespace VRage.Game.GUI.TextPanel
{
    public enum SpriteType { TEXTURE = 0, TEXT = 1, CLIP_RECT = 2 }
    public enum ContentType { NONE = 0, TEXT_AND_IMAGE = 1, SCRIPT = 2 }
    public enum TextAlignment { LEFT = 0, CENTER = 1, RIGHT = 2 }

    public struct MySprite
    {
        public SpriteType    Type;
        public string        Data;
        public VRageMath.Vector2?      Position;
        public VRageMath.Vector2?      Size;
        public VRageMath.Color?        Color;
        public string        FontId;
        public float         RotationOrScale;
        public TextAlignment Alignment;

        public MySprite(SpriteType type, string data,
                       VRageMath.Vector2? position = null, VRageMath.Vector2? size = null, VRageMath.Color? color = null,
                       string fontId = null, TextAlignment alignment = TextAlignment.LEFT,
                       float rotation = 0f)
        {
            Type=type; Data=data; Position=position; Size=size; Color=color;
            FontId=fontId; RotationOrScale=rotation; Alignment=alignment;
        }

        public static MySprite CreateText(string text, string font, VRageMath.Color color,
                                          float scale, TextAlignment alignment)
        {
            MySprite sp = new MySprite();
            sp.Type=SpriteType.TEXT; sp.Data=text; sp.FontId=font;
            sp.Color=color; sp.RotationOrScale=scale; sp.Alignment=alignment;
            return sp;
        }

        public static MySprite CreateSprite(string sprite, VRageMath.Vector2 position, VRageMath.Vector2 size)
        {
            MySprite sp = new MySprite();
            sp.Type=SpriteType.TEXTURE; sp.Data=sprite; sp.Position=position; sp.Size=size;
            sp.Color=VRageMath.Color.White;
            return sp;
        }
    }

    public struct MySpriteDrawFrame : System.IDisposable
    {
        public void Add(MySprite sprite)
        {
            SELcdExec.SpriteCollector.Captured.Add(sprite);
            SELcdExec.SpriteCollector.RecordSpriteMethod(-1);
            SELcdExec.SpriteCollector._skipNextRecord = true;
        }
        public void AddRange(System.Collections.Generic.IEnumerable<MySprite> sprites)
        {
            foreach (var s in sprites)
            {
                SELcdExec.SpriteCollector.Captured.Add(s);
                SELcdExec.SpriteCollector.RecordSpriteMethod(-1);
            }
        }
        public void Dispose() { }
    }
}

namespace VRageMath
{
    using System;

    public struct Vector2
    {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public Vector2(float v) { X = v; Y = v; }
        public static Vector2 operator+(Vector2 a, Vector2 b) { return new Vector2(a.X+b.X, a.Y+b.Y); }
        public static Vector2 operator-(Vector2 a, Vector2 b) { return new Vector2(a.X-b.X, a.Y-b.Y); }
        public static Vector2 operator+(Vector2 a, float f)   { return new Vector2(a.X+f,   a.Y+f);   }
        public static Vector2 operator-(Vector2 a, float f)   { return new Vector2(a.X-f,   a.Y-f);   }
        public static Vector2 operator+(float f,   Vector2 a) { return new Vector2(a.X+f,   a.Y+f);   }
        public static Vector2 operator-(float f,   Vector2 a) { return new Vector2(f-a.X,   f-a.Y);   }
        public static Vector2 operator*(Vector2 a, float f)   { return new Vector2(a.X*f,   a.Y*f);   }
        public static Vector2 operator*(float f,   Vector2 a) { return new Vector2(a.X*f,   a.Y*f);   }
        public static Vector2 operator*(Vector2 a, Vector2 b) { return new Vector2(a.X*b.X, a.Y*b.Y); }
        public static Vector2 operator/(Vector2 a, float f)   { return new Vector2(a.X/f,   a.Y/f);   }
        public static Vector2 operator/(Vector2 a, Vector2 b) { return new Vector2(a.X/b.X, a.Y/b.Y); }
        public static Vector2 operator-(Vector2 a)            { return new Vector2(-a.X,    -a.Y);     }
        public static bool operator==(Vector2 a, Vector2 b)   { return a.X==b.X && a.Y==b.Y; }
        public static bool operator!=(Vector2 a, Vector2 b)   { return !(a==b); }
        public override bool Equals(object obj) { return obj is Vector2 && this==(Vector2)obj; }
        public override int GetHashCode() { return X.GetHashCode() ^ Y.GetHashCode(); }
        public float Length() { return (float)Math.Sqrt(X*X+Y*Y); }
        public float LengthSquared() { return X*X+Y*Y; }
        public static Vector2 Zero { get { return new Vector2(0,0); } }
        public static Vector2 One  { get { return new Vector2(1,1); } }
        public override string ToString() { return X+"",""+Y; }
    }

    public struct Color
    {
        public byte R, G, B, A;
        public Color(int r, int g, int b)        { R=(byte)r; G=(byte)g; B=(byte)b; A=255;     }
        public Color(int r, int g, int b, int a) { R=(byte)r; G=(byte)g; B=(byte)b; A=(byte)a; }
        public Color(float r, float g, float b)  { R=(byte)(r*255); G=(byte)(g*255); B=(byte)(b*255); A=255; }
        public Color(float r, float g, float b, float a) { R=(byte)(r*255); G=(byte)(g*255); B=(byte)(b*255); A=(byte)(a*255); }
        public Color(Color c, float a) { R=c.R; G=c.G; B=c.B; A=(byte)(a*255); }
        public static Color White       { get { return new Color(255,255,255); } }
        public static Color Black       { get { return new Color(0,0,0);       } }
        public static Color Red         { get { return new Color(255,0,0);     } }
        public static Color Green       { get { return new Color(0,255,0);     } }
        public static Color Blue        { get { return new Color(0,0,255);     } }
        public static Color Yellow      { get { return new Color(255,255,0);   } }
        public static Color Cyan        { get { return new Color(0,255,255);   } }
        public static Color Magenta     { get { return new Color(255,0,255);   } }
        public static Color Gray        { get { return new Color(128,128,128); } }
        public static Color Orange      { get { return new Color(255,165,0);   } }
        public static Color Lime        { get { return new Color(0,255,0);     } }
        public static Color DarkGray    { get { return new Color(64,64,64);    } }
        public static Color LightGray   { get { return new Color(192,192,192); } }
        public static Color Transparent { get { return new Color(0,0,0,0);    } }
        public static Color operator*(Color c, float f) { return new Color((int)(c.R*f),(int)(c.G*f),(int)(c.B*f),(int)c.A); }
    }

    public struct Vector3D
    {
        public double X, Y, Z;
        public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vector3D operator+(Vector3D a, Vector3D b) { return new Vector3D(a.X+b.X, a.Y+b.Y, a.Z+b.Z); }
        public static Vector3D operator-(Vector3D a, Vector3D b) { return new Vector3D(a.X-b.X, a.Y-b.Y, a.Z-b.Z); }
        public static Vector3D operator*(Vector3D a, double f)   { return new Vector3D(a.X*f,   a.Y*f,   a.Z*f);   }
        public static Vector3D operator*(double f, Vector3D a)   { return new Vector3D(a.X*f,   a.Y*f,   a.Z*f);   }
        public static Vector3D operator/(Vector3D a, double f)   { return new Vector3D(a.X/f,   a.Y/f,   a.Z/f);   }
        public static Vector3D operator-(Vector3D a)             { return new Vector3D(-a.X,    -a.Y,    -a.Z);     }
        public static bool operator==(Vector3D a, Vector3D b)    { return a.X==b.X && a.Y==b.Y && a.Z==b.Z; }
        public static bool operator!=(Vector3D a, Vector3D b)    { return !(a==b); }
        public override bool Equals(object obj) { return obj is Vector3D && this==(Vector3D)obj; }
        public override int GetHashCode() { return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode(); }
        public double Length() { return System.Math.Sqrt(X*X+Y*Y+Z*Z); }
        public double LengthSquared() { return X*X+Y*Y+Z*Z; }
        public static Vector3D Zero { get { return new Vector3D(0,0,0); } }
        public static Vector3D One  { get { return new Vector3D(1,1,1); } }
        public static Vector3D Normalize(Vector3D v) { double l = v.Length(); return l > 0 ? v / l : Zero; }
        public override string ToString() { return X+"",""+Y+"",""+Z; }
    }

    // Minimal MathHelper stub used by some PB scripts
    public static class MathHelper
    {
        public const float Pi = 3.14159265f;
        public const float TwoPi = 6.28318530f;
        public const float PiOver2 = 1.57079632f;
        public const float PiOver4 = 0.78539816f;
        public static float Clamp(float v, float min, float max) { return v < min ? min : v > max ? max : v; }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        public static float ToRadians(float degrees) { return degrees * Pi / 180f; }
        public static float ToDegrees(float radians) { return radians * 180f / Pi; }
    }

    /// <summary>Minimal Vector3 stub — float counterpart to Vector3D, used by radar/3D scripts.</summary>
    public struct Vector3
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public Vector3(float v) { X = v; Y = v; Z = v; }
        public static Vector3 operator+(Vector3 a, Vector3 b) { return new Vector3(a.X+b.X, a.Y+b.Y, a.Z+b.Z); }
        public static Vector3 operator-(Vector3 a, Vector3 b) { return new Vector3(a.X-b.X, a.Y-b.Y, a.Z-b.Z); }
        public static Vector3 operator*(Vector3 a, float f)   { return new Vector3(a.X*f,   a.Y*f,   a.Z*f);   }
        public static Vector3 operator*(float f, Vector3 a)   { return new Vector3(a.X*f,   a.Y*f,   a.Z*f);   }
        public static Vector3 operator/(Vector3 a, float f)   { return new Vector3(a.X/f,   a.Y/f,   a.Z/f);   }
        public static Vector3 operator-(Vector3 a)            { return new Vector3(-a.X,    -a.Y,    -a.Z);     }
        public static bool operator==(Vector3 a, Vector3 b)   { return a.X==b.X && a.Y==b.Y && a.Z==b.Z; }
        public static bool operator!=(Vector3 a, Vector3 b)   { return !(a==b); }
        public override bool Equals(object obj) { return obj is Vector3 && this==(Vector3)obj; }
        public override int GetHashCode() { return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode(); }
        public float Length() { return (float)System.Math.Sqrt(X*X+Y*Y+Z*Z); }
        public float LengthSquared() { return X*X+Y*Y+Z*Z; }
        public static Vector3 Zero { get { return new Vector3(0,0,0); } }
        public static Vector3 One  { get { return new Vector3(1,1,1); } }
        public static Vector3 Up      { get { return new Vector3(0,1,0); } }
        public static Vector3 Down    { get { return new Vector3(0,-1,0); } }
        public static Vector3 Forward { get { return new Vector3(0,0,-1); } }
        public static Vector3 Backward{ get { return new Vector3(0,0,1); } }
        public static Vector3 Left    { get { return new Vector3(-1,0,0); } }
        public static Vector3 Right   { get { return new Vector3(1,0,0); } }
        public static Vector3 Normalize(Vector3 v) { float l = v.Length(); return l > 0 ? v / l : Zero; }
        public static float Dot(Vector3 a, Vector3 b) { return a.X*b.X + a.Y*b.Y + a.Z*b.Z; }
        public static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(a.Y*b.Z - a.Z*b.Y, a.Z*b.X - a.X*b.Z, a.X*b.Y - a.Y*b.X);
        }
        public static Vector3 TransformNormal(Vector3 v, Matrix m)
        {
            return new Vector3(
                v.X*m.M11 + v.Y*m.M21 + v.Z*m.M31,
                v.X*m.M12 + v.Y*m.M22 + v.Z*m.M32,
                v.X*m.M13 + v.Y*m.M23 + v.Z*m.M33);
        }
        public static implicit operator Vector3(Vector3D v) { return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
        public static implicit operator Vector3D(Vector3 v) { return new Vector3D(v.X, v.Y, v.Z); }
        public override string ToString() { return X+"",""+Y+"",""+Z; }
    }

    /// <summary>Minimal 4x4 float matrix stub — used by scripts for orientation transforms.</summary>
    public struct Matrix
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;
        public static Matrix Identity
        {
            get
            {
                Matrix m = new Matrix();
                m.M11 = 1; m.M22 = 1; m.M33 = 1; m.M44 = 1;
                return m;
            }
        }
        public static Matrix Transpose(Matrix m)
        {
            Matrix r = new Matrix();
            r.M11 = m.M11; r.M12 = m.M21; r.M13 = m.M31; r.M14 = m.M41;
            r.M21 = m.M12; r.M22 = m.M22; r.M23 = m.M32; r.M24 = m.M42;
            r.M31 = m.M13; r.M32 = m.M23; r.M33 = m.M33; r.M34 = m.M43;
            r.M41 = m.M14; r.M42 = m.M24; r.M43 = m.M34; r.M44 = m.M44;
            return r;
        }
        public Vector3 Forward { get { return new Vector3(-M31, -M32, -M33); } }
        public Vector3 Up      { get { return new Vector3(M21, M22, M23); } }
        public Vector3 Right   { get { return new Vector3(M11, M12, M13); } }
        public Vector3 Translation { get { return new Vector3(M41, M42, M43); } set { M41 = value.X; M42 = value.Y; M43 = value.Z; } }
        public static implicit operator Matrix(MatrixD m)
        {
            Matrix r = new Matrix();
            r.M11=(float)m.M11; r.M12=(float)m.M12; r.M13=(float)m.M13; r.M14=(float)m.M14;
            r.M21=(float)m.M21; r.M22=(float)m.M22; r.M23=(float)m.M23; r.M24=(float)m.M24;
            r.M31=(float)m.M31; r.M32=(float)m.M32; r.M33=(float)m.M33; r.M34=(float)m.M34;
            r.M41=(float)m.M41; r.M42=(float)m.M42; r.M43=(float)m.M43; r.M44=(float)m.M44;
            return r;
        }
    }

    /// <summary>Minimal 4x4 double matrix stub — SE WorldMatrix is a MatrixD.</summary>
    public struct MatrixD
    {
        public double M11, M12, M13, M14;
        public double M21, M22, M23, M24;
        public double M31, M32, M33, M34;
        public double M41, M42, M43, M44;
        public static MatrixD Identity
        {
            get
            {
                MatrixD m = new MatrixD();
                m.M11 = 1; m.M22 = 1; m.M33 = 1; m.M44 = 1;
                return m;
            }
        }
        public static MatrixD Transpose(MatrixD m)
        {
            MatrixD r = new MatrixD();
            r.M11 = m.M11; r.M12 = m.M21; r.M13 = m.M31; r.M14 = m.M41;
            r.M21 = m.M12; r.M22 = m.M22; r.M23 = m.M32; r.M24 = m.M42;
            r.M31 = m.M13; r.M32 = m.M23; r.M33 = m.M33; r.M34 = m.M43;
            r.M41 = m.M14; r.M42 = m.M24; r.M43 = m.M34; r.M44 = m.M44;
            return r;
        }
        public Vector3D Forward { get { return new Vector3D(-M31, -M32, -M33); } }
        public Vector3D Up      { get { return new Vector3D(M21, M22, M23); } }
        public Vector3D Right   { get { return new Vector3D(M11, M12, M13); } }
        public Vector3D Translation { get { return new Vector3D(M41, M42, M43); } set { M41 = value.X; M42 = value.Y; M43 = value.Z; } }
    }

    /// <summary>Minimal axis-aligned bounding box stub.</summary>
    public struct BoundingBoxD
    {
        public Vector3D Min;
        public Vector3D Max;
        public BoundingBoxD(Vector3D min, Vector3D max) { Min = min; Max = max; }
        public Vector3D Center { get { return (Min + Max) * 0.5; } }
        public Vector3D Size   { get { return Max - Min; } }
    }

    /// <summary>VRageMath RectangleF stub — used by SE scripts for viewport calculations.</summary>
    public struct RectangleF
    {
        public Vector2 Position;
        public Vector2 Size;
        public float X { get { return Position.X; } set { Position.X = value; } }
        public float Y { get { return Position.Y; } set { Position.Y = value; } }
        public float Width { get { return Size.X; } set { Size.X = value; } }
        public float Height { get { return Size.Y; } set { Size.Y = value; } }
        public Vector2 Center { get { return Position + Size / 2f; } }
        public RectangleF(Vector2 position, Vector2 size) { Position = position; Size = size; }
        public RectangleF(float x, float y, float w, float h) { Position = new Vector2(x, y); Size = new Vector2(w, h); }
        public bool Contains(float x, float y) { return x >= Position.X && x <= Position.X + Size.X && y >= Position.Y && y <= Position.Y + Size.Y; }
        public bool Contains(Vector2 point) { return Contains(point.X, point.Y); }
    }
}

namespace VRage
{
    public struct MyFixedPoint
    {
        private long _raw;

        public int RawValue { get { return (int)_raw; } }

        public static implicit operator MyFixedPoint(int v)    { var fp = new MyFixedPoint(); fp._raw = (long)v * 1000000; return fp; }
        public static implicit operator MyFixedPoint(float v)  { var fp = new MyFixedPoint(); fp._raw = (long)(v * 1000000); return fp; }
        public static implicit operator MyFixedPoint(double v) { var fp = new MyFixedPoint(); fp._raw = (long)(v * 1000000); return fp; }
        public static implicit operator float(MyFixedPoint fp)  { return fp._raw / 1000000f; }
        public static implicit operator double(MyFixedPoint fp) { return fp._raw / 1000000.0; }

        public static MyFixedPoint operator +(MyFixedPoint a, MyFixedPoint b) { var fp = new MyFixedPoint(); fp._raw = a._raw + b._raw; return fp; }
        public static MyFixedPoint operator -(MyFixedPoint a, MyFixedPoint b) { var fp = new MyFixedPoint(); fp._raw = a._raw - b._raw; return fp; }
        public static MyFixedPoint operator *(MyFixedPoint a, MyFixedPoint b) { var fp = new MyFixedPoint(); fp._raw = a._raw * b._raw / 1000000; return fp; }
        public static bool operator >(MyFixedPoint a, MyFixedPoint b)  { return a._raw > b._raw; }
        public static bool operator <(MyFixedPoint a, MyFixedPoint b)  { return a._raw < b._raw; }
        public static bool operator >=(MyFixedPoint a, MyFixedPoint b) { return a._raw >= b._raw; }
        public static bool operator <=(MyFixedPoint a, MyFixedPoint b) { return a._raw <= b._raw; }
        public static bool operator ==(MyFixedPoint a, MyFixedPoint b) { return a._raw == b._raw; }
        public static bool operator !=(MyFixedPoint a, MyFixedPoint b) { return a._raw != b._raw; }

        public override bool Equals(object obj) { return obj is MyFixedPoint && this == (MyFixedPoint)obj; }
        public override int GetHashCode() { return _raw.GetHashCode(); }
        public override string ToString() { return ((float)this).ToString(); }

        public int ToIntSafe() { return (int)(_raw / 1000000); }
        public static MyFixedPoint MaxValue { get { MyFixedPoint fp = new MyFixedPoint(); fp._raw = long.MaxValue; return fp; } }
        public static MyFixedPoint MinValue { get { MyFixedPoint fp = new MyFixedPoint(); fp._raw = long.MinValue; return fp; } }
    }

    // ── MyTuple — value-type tuples used heavily by IGC payloads ──────────
    public struct MyTuple<T1, T2>
    {
        public T1 Item1; public T2 Item2;
        public MyTuple(T1 item1, T2 item2) { Item1 = item1; Item2 = item2; }
    }
    public struct MyTuple<T1, T2, T3>
    {
        public T1 Item1; public T2 Item2; public T3 Item3;
        public MyTuple(T1 item1, T2 item2, T3 item3) { Item1 = item1; Item2 = item2; Item3 = item3; }
    }
    public struct MyTuple<T1, T2, T3, T4>
    {
        public T1 Item1; public T2 Item2; public T3 Item3; public T4 Item4;
        public MyTuple(T1 item1, T2 item2, T3 item3, T4 item4) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; }
    }
    public struct MyTuple<T1, T2, T3, T4, T5>
    {
        public T1 Item1; public T2 Item2; public T3 Item3; public T4 Item4; public T5 Item5;
        public MyTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; }
    }
    public struct MyTuple<T1, T2, T3, T4, T5, T6>
    {
        public T1 Item1; public T2 Item2; public T3 Item3; public T4 Item4; public T5 Item5; public T6 Item6;
        public MyTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; Item5 = item5; Item6 = item6; }
    }
}

namespace VRage.Game.ModAPI.Ingame
{
    using VRage;

    public struct MyItemType
    {
        public string TypeId { get; private set; }
        public string SubtypeId { get; private set; }
        public MyItemType(string typeId, string subtypeId) { TypeId = typeId; SubtypeId = subtypeId; }
        public static MyItemType Parse(string str)
        {
            var parts = str.Split('/');
            return parts.Length == 2 ? new MyItemType(parts[0], parts[1]) : new MyItemType(str, """");
        }
        public static MyItemType MakeOre(string subtype)      { return new MyItemType(""MyObjectBuilder_Ore"", subtype); }
        public static MyItemType MakeIngot(string subtype)     { return new MyItemType(""MyObjectBuilder_Ingot"", subtype); }
        public static MyItemType MakeComponent(string subtype) { return new MyItemType(""MyObjectBuilder_Component"", subtype); }
        public static MyItemType MakeAmmo(string subtype)      { return new MyItemType(""MyObjectBuilder_AmmoMagazine"", subtype); }
        public static MyItemType MakeTool(string subtype)      { return new MyItemType(""MyObjectBuilder_PhysicalGunObject"", subtype); }
        public override string ToString() { return TypeId + ""/"" + SubtypeId; }
    }

    public struct MyInventoryItem
    {
        public MyItemType Type { get; set; }
        public MyFixedPoint Amount { get; set; }
    }

    public interface IMyInventory
    {
        MyFixedPoint CurrentVolume { get; }
        MyFixedPoint MaxVolume { get; }
        MyFixedPoint CurrentMass { get; }
        int ItemCount { get; }
        void GetItems(System.Collections.Generic.List<MyInventoryItem> items, System.Func<MyInventoryItem, bool> filter = null);
        MyInventoryItem? GetItemAt(int index);
        bool CanItemsBeAdded(MyFixedPoint amount, MyItemType type);
        bool ContainItems(MyFixedPoint amount, MyItemType type);
        MyFixedPoint GetItemAmount(MyItemType type);
    }

    public class StubInventory : IMyInventory
    {
        public MyFixedPoint CurrentVolume { get { return 0; } }
        public MyFixedPoint MaxVolume { get { return 1000; } }
        public MyFixedPoint CurrentMass { get { return 0; } }
        public int ItemCount { get { return 0; } }
        public void GetItems(System.Collections.Generic.List<MyInventoryItem> items, System.Func<MyInventoryItem, bool> filter = null) { items.Clear(); }
        public MyInventoryItem? GetItemAt(int index) { return null; }
        public bool CanItemsBeAdded(MyFixedPoint amount, MyItemType type) { return true; }
        public bool ContainItems(MyFixedPoint amount, MyItemType type) { return false; }
        public MyFixedPoint GetItemAmount(MyItemType type) { return 0; }
    }

    // ── Detection / relationship enums used by sensors, turret controllers, IGC payloads ──
    public enum MyDetectedEntityType
    {
        None = 0,
        Unknown = 1,
        SmallGrid = 2,
        LargeGrid = 3,
        CharacterHuman = 4,
        CharacterOther = 5,
        FloatingObject = 6,
        Asteroid = 7,
        Planet = 8,
        Meteor = 9,
        Missile = 10,
    }

    public enum MyRelationsBetweenPlayerAndBlock
    {
        NoOwnership = 0,
        Owner = 1,
        FactionShare = 2,
        Neutral = 3,
        Enemies = 4,
        Friends = 5,
    }
}

namespace Sandbox.ModAPI.Ingame
{
    using System;
    using System.Collections.Generic;
    using VRage;
    using VRage.Game.GUI.TextPanel;
    using VRage.Game.ModAPI.Ingame;
    using VRageMath;

    [Flags] public enum UpdateType { None = 0, Once = 128, Update1 = 16, Update10 = 32, Update100 = 64, Terminal = 256, Trigger = 512 }
    [Flags] public enum UpdateFrequency { None = 0, Update1 = 1, Update10 = 2, Update100 = 4, Once = 8 }

    // ── Functional stubs ──────────────────────────────────────────────────

    public class StubRuntime : IMyRuntime
    {
        public UpdateFrequency UpdateFrequency { get; set; }
        private double _tsLastRun = 0.016;
        public double TimeSinceLastRun { get { return _tsLastRun; } set { _tsLastRun = value; } }
        public double LastRunTimeMs { get { return 0.1; } }
        public int MaxInstructionCount { get { return 50000; } }
    }

    public class StubTextSurface : IMyTextSurface, IMyTextPanel, VRage.ModAPI.IMyEntity
    {
        private string _text = """";
        private readonly StubInventory _inv = new StubInventory();
        public ContentType ContentType { get; set; }
        public Color FontColor { get; set; }
        public Color BackgroundColor { get; set; }
        public Color ScriptBackgroundColor { get; set; }
        public Color ScriptForegroundColor { get; set; }
        public float FontSize { get; set; }
        public string Font { get; set; }
        public float TextPadding { get; set; }
        public string Script { get; set; }
        public Vector2 SurfaceSize { get; set; }
        public Vector2 TextureSize { get; set; }
        public void WriteText(string text, bool append = false) { _text = append ? _text + text : text; }
        public string ReadText() { return _text; }
        public MySpriteDrawFrame DrawFrame() { return new MySpriteDrawFrame(); }
        public void GetSprites(List<string> sprites) { sprites.Clear(); }

        // IMyFunctionalBlock / IMyTerminalBlock members
        public bool Enabled { get; set; }
        public string CustomName { get; set; }
        public string CustomData { get; set; }
        public string DetailedInfo { get; set; }
        public string DisplayName { get { return CustomName ?? ""LCD Panel""; } }
        public bool IsWorking { get { return true; } }
        public bool IsFunctional { get { return true; } }
        public long EntityId { get; set; }
        public VRage.Game.ModAPI.IMyCubeGrid CubeGrid { get; set; }
        public ITerminalProperty GetProperty(string name) { return new StubTerminalProperty(name); }
        public ITerminalAction GetAction(string name) { return new StubTerminalAction(name); }
        public bool HasInventory { get { return false; } }
        public int InventoryCount { get { return 0; } }
        public IMyInventory GetInventory() { return _inv; }
        public IMyInventory GetInventory(int index) { return _inv; }
        public Vector3D GetPosition() { return Vector3D.Zero; }
        public MatrixD WorldMatrix { get { return MatrixD.Identity; } }

        public StubTextSurface() : this(512f, 512f) { }
        public StubTextSurface(float w, float h)
        {
            SurfaceSize = new Vector2(w, h);
            TextureSize = new Vector2(w, h);
            FontSize = 1f;
            Font = ""White"";
            FontColor = Color.White;
            BackgroundColor = Color.Black;
            ScriptBackgroundColor = new Color(0, 88, 151);
            ScriptForegroundColor = Color.White;
            Enabled = true;
            CustomName = ""LCD Panel"";
            CustomData = """";
            DetailedInfo = """";
            EntityId = 2;
            CubeGrid = new VRage.Game.ModAPI.StubModCubeGrid();
        }
    }

    public interface IMyTextSurfaceProvider
    {
        IMyTextSurface GetSurface(int index);
        int SurfaceCount { get; }
    }

    public class StubTerminalBlock : IMyTerminalBlock, IMyTextSurfaceProvider, IMyFunctionalBlock
    {
        private readonly StubTextSurface[] _surfaces;
        private readonly StubInventory _inv = new StubInventory();
        public string CustomName { get; set; }
        public string CustomData { get; set; }
        public string DetailedInfo { get; set; }
        public bool IsWorking { get { return true; } }
        public bool IsFunctional { get { return true; } }
        public bool Enabled { get; set; }
        public long EntityId { get; set; }
        public VRage.Game.ModAPI.IMyCubeGrid CubeGrid { get; set; }
        public VRageMath.Vector3D GetPosition() { return VRageMath.Vector3D.Zero; }
        public VRageMath.MatrixD WorldMatrix { get { return VRageMath.MatrixD.Identity; } }
        public string DisplayName { get { return CustomName; } }
        public int SurfaceCount { get { return _surfaces.Length; } }
        public IMyTextSurface GetSurface(int index) { return _surfaces[index]; }
        public ITerminalProperty GetProperty(string name) { return new StubTerminalProperty(name); }
        public ITerminalAction GetAction(string name) { return new StubTerminalAction(name); }
        public bool HasInventory { get { return true; } }
        public int InventoryCount { get { return 1; } }
        public IMyInventory GetInventory() { return _inv; }
        public IMyInventory GetInventory(int index) { return _inv; }

        public StubTerminalBlock(int surfaceCount)
        {
            _surfaces = new StubTextSurface[surfaceCount];
            for (int i = 0; i < surfaceCount; i++)
                _surfaces[i] = new StubTextSurface();
            CustomName = ""LCD Panel"";
            CustomData = """";
            DetailedInfo = """";
            Enabled = true;
            EntityId = 1;
            CubeGrid = new VRage.Game.ModAPI.StubModCubeGrid();
        }
    }

    public class StubProgrammableBlock : StubTerminalBlock, IMyProgrammableBlock
    {
        public StubProgrammableBlock() : base(2) { CustomName = ""Programmable Block""; }
    }

    public class StubCockpit : StubTerminalBlock, IMyCockpit
    {
        public bool IsMainCockpit { get; set; }
        public bool CanControlShip { get; set; }
        public bool ShowHorizonIndicator { get; set; }
        public bool HandBrake { get; set; }
        public bool DampenersOverride { get; set; }
        public bool IsUnderControl { get { return false; } }
        public Vector3D MoveIndicator { get { return Vector3D.Zero; } }
        public Vector2 RotationIndicator { get { return Vector2.Zero; } }
        public float RollIndicator { get { return 0f; } }
        public Vector3D GetNaturalGravity() { return Vector3D.Zero; }
        public StubCockpit() : base(4) { CustomName = ""Cockpit""; IsMainCockpit = true; }
    }

    public class StubGridTerminalSystem : IMyGridTerminalSystem
    {
        private readonly List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();

        public void RegisterBlock(IMyTerminalBlock block) { _blocks.Add(block); }

        public void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class
        {
            blocks.Clear();
            foreach (var b in _blocks)
            {
                T typed = b as T;
                if (typed != null && (collect == null || collect(typed)))
                    blocks.Add(typed);
            }
        }

        public IMyTerminalBlock GetBlockWithId(long id)
        {
            foreach (var b in _blocks) if (b.EntityId == id) return b;
            return null;
        }

        public IMyTerminalBlock GetBlockWithName(string name)
        {
            foreach (var b in _blocks)
                if (b.CustomName == name) return b;
            // Fallback: return first non-PB block so preview always works
            foreach (var b in _blocks)
                if (!(b is IMyProgrammableBlock)) return b;
            return _blocks.Count > 0 ? _blocks[0] : null;
        }

        public void SearchBlocksOfName(string name, List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null)
        {
            blocks.Clear();
            foreach (var b in _blocks)
                if (b.CustomName.Contains(name) && (collect == null || collect(b)))
                    blocks.Add(b);
        }

        private readonly Dictionary<string, StubBlockGroup> _groups = new Dictionary<string, StubBlockGroup>();

        public IMyBlockGroup GetBlockGroupWithName(string name)
        {
            StubBlockGroup g;
            if (!_groups.TryGetValue(name, out g))
            {
                g = new StubBlockGroup(name);
                _groups[name] = g;
            }
            return g;
        }

        public void GetBlockGroups(List<IMyBlockGroup> groups, Func<IMyBlockGroup, bool> collect = null)
        {
            groups.Clear();
            foreach (var g in _groups.Values)
                if (collect == null || collect(g))
                    groups.Add(g);
        }
    }

    public class StubBlockGroup : IMyBlockGroup
    {
        public string Name { get; private set; }
        private readonly List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();

        public StubBlockGroup(string name) { Name = name; }

        public void GetBlocks(List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null)
        {
            blocks.Clear();
            foreach (var b in _blocks)
                if (collect == null || collect(b))
                    blocks.Add(b);
        }

        public void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class
        {
            blocks.Clear();
            foreach (var b in _blocks)
            {
                T typed = b as T;
                if (typed != null && (collect == null || collect(typed)))
                    blocks.Add(typed);
            }
        }
    }

    // ── Interfaces ────────────────────────────────────────────────────────

    public interface IMyRuntime
    {
        UpdateFrequency UpdateFrequency { get; set; }
        double TimeSinceLastRun { get; }
        double LastRunTimeMs { get; }
        int MaxInstructionCount { get; }
    }

    public interface ITerminalProperty
    {
        string Id { get; }
    }

    public class StubTerminalProperty : ITerminalProperty
    {
        public string Id { get; private set; }
        public StubTerminalProperty(string id) { Id = id; }
    }

    public interface ITerminalAction
    {
        string Id { get; }
        void Apply(IMyTerminalBlock block);
    }

    public class StubTerminalAction : ITerminalAction
    {
        public string Id { get; private set; }
        public StubTerminalAction(string id) { Id = id; }
        public void Apply(IMyTerminalBlock block) { }
    }

    public interface IMyTerminalBlock : VRage.ModAPI.IMyEntity
    {
        string CustomName { get; set; }
        string CustomData { get; set; }
        string DetailedInfo { get; }
        bool IsWorking { get; }
        bool IsFunctional { get; }
        new long EntityId { get; }
        VRage.Game.ModAPI.IMyCubeGrid CubeGrid { get; }
        ITerminalProperty GetProperty(string name);
        ITerminalAction GetAction(string name);
        bool HasInventory { get; }
        int InventoryCount { get; }
        IMyInventory GetInventory();
        IMyInventory GetInventory(int index);
        new VRageMath.Vector3D GetPosition();
    }

    public interface IMyFunctionalBlock : IMyTerminalBlock
    {
        bool Enabled { get; set; }
    }

    public interface IMyBlockGroup
    {
        string Name { get; }
        void GetBlocks(List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null);
        void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class;
    }

    public interface IMyTextSurface
    {
        ContentType ContentType { get; set; }
        Color FontColor { get; set; }
        Color BackgroundColor { get; set; }
        Color ScriptBackgroundColor { get; set; }
        Color ScriptForegroundColor { get; set; }
        float FontSize { get; set; }
        string Font { get; set; }
        float TextPadding { get; set; }
        string Script { get; set; }
        Vector2 SurfaceSize { get; }
        Vector2 TextureSize { get; }
        void WriteText(string text, bool append = false);
        string ReadText();
        MySpriteDrawFrame DrawFrame();
        void GetSprites(List<string> sprites);
    }

    public interface IMyProgrammableBlock
    {
        IMyTextSurface GetSurface(int index);
        int SurfaceCount { get; }
    }

    public interface IMyGridTerminalSystem
    {
        void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class;
        IMyTerminalBlock GetBlockWithId(long id);
        IMyTerminalBlock GetBlockWithName(string name);
        void SearchBlocksOfName(string name, List<IMyTerminalBlock> blocks, Func<IMyTerminalBlock, bool> collect = null);
        IMyBlockGroup GetBlockGroupWithName(string name);
        void GetBlockGroups(List<IMyBlockGroup> groups, Func<IMyBlockGroup, bool> collect = null);
    }

    public class MyGridProgram
    {
        public IMyRuntime Runtime { get; set; }
        public IMyProgrammableBlock Me { get; set; }
        public IMyGridTerminalSystem GridTerminalSystem { get; set; }
        public IMyIntergridCommunicationSystem IGC { get; set; }
        public string Storage { get; set; }
        public void Echo(string text) { }  // Base class stub — overridden in LcdRunner
        protected virtual void Save() { }
    }

    // ── Block interfaces

    public enum ChargeMode { Auto = 0, Recharge = 1, Discharge = 2 }
    public enum MyShipConnectorStatus { Unconnected = 0, Connectable = 1, Connected = 2 }
    public enum DoorStatus { Open = 0, Closed = 1, Opening = 2, Closing = 3 }
    public enum PistonStatus { Extended = 0, Retracted = 1, Extending = 2, Retracting = 3, Stopped = 4 }

    public interface IMyBatteryBlock : IMyFunctionalBlock
    {
        float CurrentStoredPower { get; }
        float MaxStoredPower { get; }
        float CurrentInput { get; }
        float CurrentOutput { get; }
        ChargeMode ChargeMode { get; set; }
        bool IsCharging { get; }
        bool HasCapacityRemaining { get; }
    }

    public interface IMyGasTank : IMyFunctionalBlock
    {
        double FilledRatio { get; }
        float Capacity { get; }
        bool AutoRefillBottles { get; set; }
        bool Stockpile { get; set; }
    }

    public interface IMyShipConnector : IMyFunctionalBlock
    {
        MyShipConnectorStatus Status { get; }
        bool IsConnected { get; }
        IMyShipConnector OtherConnector { get; }
        void Connect();
        void Disconnect();
        void ToggleConnect();
    }

    public interface IMyThrust : IMyFunctionalBlock
    {
        float ThrustOverride { get; set; }
        float ThrustOverridePercentage { get; set; }
        float MaxThrust { get; }
        float MaxEffectiveThrust { get; }
        float CurrentThrust { get; }
    }

    public interface IMyGyro : IMyFunctionalBlock
    {
        bool GyroOverride { get; set; }
        float Yaw { get; set; }
        float Pitch { get; set; }
        float Roll { get; set; }
        float GyroPower { get; set; }
    }

    public interface IMySensorBlock : IMyFunctionalBlock
    {
        bool IsActive { get; }
        bool DetectPlayers { get; set; }
        bool DetectSmallShips { get; set; }
        bool DetectLargeShips { get; set; }
        bool DetectStations { get; set; }
        bool DetectSubgrids { get; set; }
        bool DetectAsteroids { get; set; }
        float LeftExtend { get; set; }
        float RightExtend { get; set; }
        float TopExtend { get; set; }
        float BottomExtend { get; set; }
        float FrontExtend { get; set; }
        float BackExtend { get; set; }
    }

    public interface IMyDoor : IMyFunctionalBlock
    {
        DoorStatus Status { get; }
        float OpenRatio { get; }
        void OpenDoor();
        void CloseDoor();
        void ToggleDoor();
    }

    public interface IMyLightingBlock : IMyFunctionalBlock
    {
        Color Color { get; set; }
        float Radius { get; set; }
        float Intensity { get; set; }
        float BlinkIntervalSeconds { get; set; }
        float BlinkLength { get; set; }
        float BlinkOffset { get; set; }
    }

    public interface IMyMotorStator : IMyFunctionalBlock
    {
        float Angle { get; }
        float UpperLimitDeg { get; set; }
        float LowerLimitDeg { get; set; }
        float TargetVelocityRPM { get; set; }
        float Torque { get; set; }
        bool IsAttached { get; }
        VRage.Game.ModAPI.IMyCubeGrid TopGrid { get; }
    }

    public interface IMyPistonBase : IMyFunctionalBlock
    {
        float CurrentPosition { get; }
        float MinLimit { get; set; }
        float MaxLimit { get; set; }
        float Velocity { get; set; }
        PistonStatus Status { get; }
        void Extend();
        void Retract();
    }

    public interface IMyShipController : IMyTerminalBlock
    {
        Vector3D GetNaturalGravity();
        Vector3D MoveIndicator { get; }
        Vector2 RotationIndicator { get; }
        float RollIndicator { get; }
        bool IsUnderControl { get; }
        bool CanControlShip { get; set; }
        bool ShowHorizonIndicator { get; set; }
        bool HandBrake { get; set; }
        bool DampenersOverride { get; set; }
    }

    public interface IMyTextPanel : IMyTextSurface, IMyFunctionalBlock { }

    public interface IMyPowerProducer : IMyFunctionalBlock
    {
        float CurrentOutput { get; }
        float MaxOutput { get; }
    }

    public interface IMyWindTurbine : IMyPowerProducer { }

    public interface IMySolarPanel : IMyPowerProducer { }

    // ── Cockpit / ship-controller surfaces ────────────────────────────────
    public interface IMyCockpit : IMyShipController, IMyTextSurfaceProvider, IMyFunctionalBlock
    {
        bool IsMainCockpit { get; set; }
    }

    // ── IGC (intergrid communication) — receive-side only stubs ───────────
    public class MyIGCMessage
    {
        public string Tag;
        public long Source;
        public object Data;
        /// <summary>
        /// Real SE returns (T)Data. With no live traffic, Data is null — for
        /// ImmutableList&lt;U&gt; we fall back to ImmutableList&lt;U&gt;.Empty so
        /// chained .ForEach calls in user code don't NRE during preview.
        /// </summary>
        public T As<T>()
        {
            if (Data is T) return (T)Data;
            var t = typeof(T);
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableList<>))
            {
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                // ImmutableList<T>.Empty is a static field (not a property) on .NET Framework.
                var emptyField = t.GetField(""Empty"", flags);
                if (emptyField != null) return (T)emptyField.GetValue(null);
                var emptyProp = t.GetProperty(""Empty"", flags);
                if (emptyProp != null) return (T)emptyProp.GetValue(null);
            }
            return default(T);
        }
    }

    public interface IMyBroadcastListener
    {
        bool HasPendingMessage { get; }
        string Tag { get; }
        MyIGCMessage AcceptMessage();
        void DisableMessageCallback();
        void SetMessageCallback(string argument);
    }

    /// <summary>
    /// Stub IGC broadcast listener. AcceptMessage() returns a message whose
    /// Data is an empty ImmutableList of the type requested via .As&lt;T&gt;()
    /// in user code, so .ForEach / iteration over it is a no-op (no live
    /// game data exists in the editor).
    /// </summary>
    public class StubBroadcastListener : IMyBroadcastListener
    {
        public bool HasPendingMessage { get { return false; } }
        public string Tag { get; private set; }
        public StubBroadcastListener(string tag) { Tag = tag; }
        public MyIGCMessage AcceptMessage()
        {
            return new MyIGCMessage { Tag = Tag, Source = 0, Data = null };
        }
        public void DisableMessageCallback() { }
        public void SetMessageCallback(string argument) { }
    }

    public interface IMyIntergridCommunicationSystem
    {
        IMyBroadcastListener RegisterBroadcastListener(string tag);
        void DisableBroadcastListener(IMyBroadcastListener listener);
        long Me { get; }
    }

    public class StubIGC : IMyIntergridCommunicationSystem
    {
        public long Me { get { return 0; } }
        public IMyBroadcastListener RegisterBroadcastListener(string tag) { return new StubBroadcastListener(tag); }
        public void DisableBroadcastListener(IMyBroadcastListener listener) { }
    }
}

// ── Sandbox.ModAPI — mod/plugin-side block interfaces ─────────────────
namespace Sandbox.ModAPI
{
    using VRageMath;

    public interface IMyTerminalBlock : Sandbox.ModAPI.Ingame.IMyTerminalBlock
    {
    }

    public interface IMyFunctionalBlock : IMyTerminalBlock, Sandbox.ModAPI.Ingame.IMyFunctionalBlock
    {
    }

    // NOTE: Sandbox.ModAPI.IMyTextPanel intentionally omitted to avoid name collision
    // with Sandbox.ModAPI.Ingame.IMyTextPanel when both usings are in scope.

    // ── MyAPIGateway — core mod API entry point ───────────────────────────

    public static class MyAPIGateway
    {
        public static IMySession Session = new StubSession();
        public static IMyMultiplayer Multiplayer = new StubMultiplayer();
        public static IMyEntities Entities = new StubEntities();
        public static IMyTerminalActionsHelper TerminalActionsHelper = new StubTerminalActionsHelper();
        public static IMyUtilities Utilities = new StubUtilities();
    }

    public interface IMySession
    {
        IMyWeatherEffects WeatherEffects { get; }
        System.TimeSpan ElapsedPlayTime { get; }
    }

    public interface IMyWeatherEffects
    {
        string GetWeather(Vector3D position);
    }

    public interface IMyMultiplayer
    {
        bool IsServer { get; }
    }

    public interface IMyEntities
    {
        void GetEntities(System.Collections.Generic.HashSet<VRage.ModAPI.IMyEntity> entities);
        VRage.ModAPI.IMyEntity GetEntityById(long entityId);
    }

    public interface IMyTerminalActionsHelper
    {
        Sandbox.ModAPI.Ingame.IMyGridTerminalSystem GetTerminalSystemForGrid(VRage.Game.ModAPI.IMyCubeGrid grid);
    }

    public interface IMyUtilities
    {
        void ShowMissionScreen(string screenTitle, string currentObjectivePrefix, string currentObjective, string description);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────

    public class StubSession : IMySession
    {
        private readonly StubWeatherEffects _weather = new StubWeatherEffects();
        public IMyWeatherEffects WeatherEffects { get { return _weather; } }
        public static double _elapsedTotalSeconds = 120.0;
        public System.TimeSpan ElapsedPlayTime { get { return System.TimeSpan.FromSeconds(_elapsedTotalSeconds); } }
    }

    public class StubWeatherEffects : IMyWeatherEffects
    {
        public string GetWeather(Vector3D position) { return ""Clear""; }
    }

    public class StubMultiplayer : IMyMultiplayer
    {
        public bool IsServer { get { return true; } }
    }

    public class StubEntities : IMyEntities
    {
        private readonly Sandbox.ModAPI.Ingame.StubTextSurface _defaultSurface = new Sandbox.ModAPI.Ingame.StubTextSurface();
        public void GetEntities(System.Collections.Generic.HashSet<VRage.ModAPI.IMyEntity> entities) { entities.Clear(); }
        public VRage.ModAPI.IMyEntity GetEntityById(long entityId) { return _defaultSurface; }
    }

    public class StubTerminalActionsHelper : IMyTerminalActionsHelper
    {
        public Sandbox.ModAPI.Ingame.IMyGridTerminalSystem GetTerminalSystemForGrid(VRage.Game.ModAPI.IMyCubeGrid grid)
        {
            return new Sandbox.ModAPI.Ingame.StubGridTerminalSystem();
        }
    }

    public class StubUtilities : IMyUtilities
    {
        public void ShowMissionScreen(string screenTitle, string currentObjectivePrefix, string currentObjective, string description) { }
    }
}

// ── VRage.ModAPI — entity interfaces ──────────────────────────────────
namespace VRage.ModAPI
{
    public interface IMyEntity
    {
        long EntityId { get; }
        string DisplayName { get; }
        VRageMath.Vector3D GetPosition();
        VRageMath.MatrixD WorldMatrix { get; }
    }
}

// ── VRage.Game.ModAPI — grid & slim block interfaces ──────────────────
namespace VRage.Game.ModAPI
{
    using System.Collections.Generic;

    public interface IMyCubeGrid : VRage.ModAPI.IMyEntity
    {
        string CustomName { get; set; }
        void GetBlocks(List<IMySlimBlock> blocks);
    }

    public interface IMySlimBlock
    {
        object FatBlock { get; }
    }

    public class StubSlimBlock : IMySlimBlock
    {
        public object FatBlock { get; set; }
    }

    public class StubModCubeGrid : IMyCubeGrid
    {
        public long EntityId { get; set; }
        public string DisplayName { get { return CustomName; } }
        public string CustomName { get; set; }
        public VRageMath.Vector3D GetPosition() { return VRageMath.Vector3D.Zero; }
        public VRageMath.MatrixD WorldMatrix { get { return VRageMath.MatrixD.Identity; } }
        public void GetBlocks(List<IMySlimBlock> blocks) { blocks.Clear(); }
        public StubModCubeGrid() { CustomName = ""Grid""; }
    }
}

// ── VRage.Game.Components — session component base ────────────────────
namespace VRage.Game.Components
{
    [System.Flags]
    public enum MyUpdateOrder { NoUpdate = 0, BeforeSimulation = 1, Simulation = 2, AfterSimulation = 4 }

    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class MySessionComponentDescriptor : System.Attribute
    {
        public MyUpdateOrder UpdateOrder;
        public MySessionComponentDescriptor(MyUpdateOrder updateOrder) { UpdateOrder = updateOrder; }
    }

    public abstract class MySessionComponentBase
    {
        public virtual void LoadData() { }
        public virtual void UnloadData() { }
        public virtual void UpdateBeforeSimulation() { }
        public virtual void UpdateAfterSimulation() { }
        public virtual void BeforeStart() { }
        public virtual void Init(object sessionComponent) { }
    }
}

// ── VRage.Utils ───────────────────────────────────────────────────────
namespace VRage.Utils
{
    public class MyLog
    {
        public static MyLog Default = new MyLog();
        public void WriteLineAndConsole(string msg) { }
        public void WriteLine(string msg) { }
    }
}

// ── Sandbox.Game.GameSystems (stub namespace) ─────────────────────────
namespace Sandbox.Game.GameSystems { }

// ── Empty namespace stubs for community SE scripts ────────────────────
// These exist solely so that 'using' directives in user code resolve.
namespace Sandbox.ModAPI.Interfaces { }
namespace Sandbox.Game.EntityComponents { }
namespace VRage.Collections { }
namespace VRage.Game.ObjectBuilders.Definitions { }
namespace SpaceEngineers.Game.ModAPI.Ingame { }

// ── InventoryManagerLight — Torch plugin types used by synced LCD code ─
namespace InventoryManagerLight
{
    public class RuntimeConfig
    {
        public enum LogLevel { Error = 0, Warn = 1, Info = 2, Debug = 3 }
        public LogLevel LoggingLevel { get; set; }
    }

    public static class Log
    {
        public static RuntimeConfig.LogLevel CurrentLevel { get; set; }
    }

    public interface ILogger
    {
        void Info(string msg);
        void Debug(string msg);
        void Warn(string msg);
        void Error(string msg);
        bool IsEnabled(RuntimeConfig.LogLevel level);
    }

    public class DefaultLogger : ILogger
    {
        public DefaultLogger(RuntimeConfig.LogLevel minLevel = RuntimeConfig.LogLevel.Info) { }
        public bool IsEnabled(RuntimeConfig.LogLevel level) { return false; }
        public void Info(string msg) { }
        public void Debug(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
    }

    public struct LcdSpriteRow
    {
        public enum Kind { Header, Separator, Item, Bar, Stat, Footer, ItemBar }
        public Kind   RowKind;
        public string Text;
        public string StatText;
        public string IconSprite;
        public VRageMath.Color  TextColor;
        public bool   ShowAlert;
        public float  BarFill;
        public VRageMath.Color  BarFillColor;
    }

    public class LcdManager
    {
        private static readonly System.Lazy<LcdManager> _lazy = new System.Lazy<LcdManager>(() => new LcdManager());
        public static LcdManager Instance { get { return _lazy.Value; } }
        private ILogger _logger;
        public LcdManager() { _logger = new DefaultLogger(); }
        public void SetLogger(ILogger logger) { _logger = logger ?? new DefaultLogger(); }
        public static void Initialize(ILogger logger) { if (_lazy.IsValueCreated) _lazy.Value.SetLogger(logger); }
        public void EnqueueUpdate(long lcdEntityId, LcdSpriteRow[] rows, bool isAlert = false) { }
        public void ApplyPendingUpdates() { }
        public void SetPluginDir(string dir) { }
        public bool HasPendingSnapshot(long entityId) { return false; }
        public void RequestSnapshot(long entityId, string name) { }
        public string LastSnapshotPath { get { return null; } }
        public string StartLiveFeed(long entityId, string name, int seconds) { return null; }
        public void StopLiveFeed(long entityId) { }
    }
}
";
    }
}
