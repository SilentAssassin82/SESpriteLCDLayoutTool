using System;
using System.Collections.Generic;

namespace SESpriteLCDLayoutTool.Services.CodeInjection
{
    /// <summary>
    /// Action verb applied to a single op inside an <see cref="InjectionPlan"/>.
    ///
    /// Semantics:
    ///   Add      — op does not yet exist in source; create it.
    ///   Update   — op exists; rewrite its body / arguments in place, keeping identity + position.
    ///   Remove   — op exists; delete it from source.
    ///   Preserve — op exists; leave it untouched. Required because callers build
    ///              <see cref="InjectionPlan"/>s incrementally — an absent op MUST NOT
    ///              be interpreted as "delete". Only explicit Remove deletes.
    /// </summary>
    public enum InjectionAction
    {
        Add,
        Update,
        Remove,
        Preserve
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Op base + four concrete op types
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Base class for all injection ops. Each op has a stable identity (used by the
    /// injector to locate the op in the existing syntax tree) and a verb.
    /// </summary>
    public abstract class InjectionOp
    {
        public InjectionAction Action { get; set; } = InjectionAction.Preserve;

        /// <summary>
        /// Human-readable identity string for diff output and warnings.
        /// Concrete subclasses compose this from their identity fields.
        /// </summary>
        public abstract string Identity { get; }
    }

    /// <summary>
    /// Class-level field declaration (e.g. <c>private float thermX;</c> or
    /// <c>private readonly List&lt;MySprite&gt; _frames = new List&lt;MySprite&gt;();</c>).
    /// Identity = field name (case-sensitive, must be unique within target class).
    /// </summary>
    public sealed class ClassFieldOp : InjectionOp
    {
        /// <summary>Field name. Identity. Required.</summary>
        public string Name { get; set; }

        /// <summary>Full declaration text WITHOUT trailing semicolon, e.g. <c>private float thermX = 0.5f</c>.</summary>
        public string Declaration { get; set; }

        /// <summary>Optional XML/// or // banner above the field. Null = no banner.</summary>
        public string LeadingComment { get; set; }

        public override string Identity => "field:" + (Name ?? "(unnamed)");
    }

    /// <summary>
    /// Class-level helper method (e.g. <c>void DrawBar(List&lt;MySprite&gt; sprites, ...)</c>).
    /// Identity = method signature (name + parameter types) so overloads coexist.
    /// </summary>
    public sealed class HelperMethodOp : InjectionOp
    {
        /// <summary>Method name. Required.</summary>
        public string Name { get; set; }

        /// <summary>
        /// Canonical parameter-type signature, e.g. <c>"List&lt;MySprite&gt;,float"</c>.
        /// Combined with <see cref="Name"/> forms the full identity.
        /// May be empty for parameterless methods.
        /// </summary>
        public string ParameterSignature { get; set; }

        /// <summary>
        /// Full method declaration including modifiers, return type, signature,
        /// body braces, and body. Inserted verbatim by the injector.
        /// </summary>
        public string FullDeclaration { get; set; }

        public override string Identity => "method:" + (Name ?? "(unnamed)") + "(" + (ParameterSignature ?? string.Empty) + ")";
    }

    /// <summary>
    /// A contiguous block of statements injected at a specific anchor inside a
    /// render method's body. Used for keyframe groups, animation prologues,
    /// switch-case bodies, and anything else that lives inside a method but
    /// outside the per-sprite Add list.
    ///
    /// Identity = (AnchorMethod, AnchorKey). AnchorKey is the caller's stable
    /// label for the block (e.g. "keyframe-group:semicircle", "prologue:thermometer").
    /// </summary>
    public sealed class RenderBlockOp : InjectionOp
    {
        /// <summary>Method name to inject into (must exist on <see cref="InjectionAction.Add"/>).</summary>
        public string AnchorMethod { get; set; }

        /// <summary>
        /// Stable key identifying this block within the anchor method.
        /// Encoded as a banner comment so the injector can re-locate the block on Update/Remove.
        /// </summary>
        public string AnchorKey { get; set; }

        /// <summary>
        /// Where inside the anchor method to place the block on initial Add.
        /// Ignored on Update/Remove (existing position is preserved).
        /// </summary>
        public RenderBlockPlacement Placement { get; set; } = RenderBlockPlacement.BeforeFirstAdd;

        /// <summary>
        /// Literal token to anchor against when <see cref="Placement"/> is
        /// <see cref="RenderBlockPlacement.BeforeToken"/>. The injector locates
        /// the first occurrence of this exact substring inside the anchor
        /// method's body and inserts the block at the start of that line.
        /// Ignored for other placements.
        /// </summary>
        public string AnchorToken { get; set; }

        /// <summary>The raw statement text (without enclosing braces). Newlines preserved.</summary>
        public string BodyText { get; set; }

        public override string Identity =>
            "block:" + (AnchorMethod ?? "(no-method)") + "/" + (AnchorKey ?? "(no-key)");
    }

    /// <summary>Placement hint for a newly-Added <see cref="RenderBlockOp"/>.</summary>
    public enum RenderBlockPlacement
    {
        /// <summary>At the top of the method body, after any var/local declarations.</summary>
        MethodTop,

        /// <summary>Immediately before the first sprite Add call. Default.</summary>
        BeforeFirstAdd,

        /// <summary>Immediately after the last sprite Add call.</summary>
        AfterLastAdd,

        /// <summary>At the very end of the method body, before the closing brace.</summary>
        MethodEnd,

        /// <summary>
        /// Immediately before the line containing
        /// <see cref="RenderBlockOp.AnchorToken"/>. Used by snippet-merge to land
        /// the render section right before <c>frame.Dispose()</c> the way the
        /// legacy regex pipeline did.
        /// </summary>
        BeforeToken
    }

    /// <summary>
    /// A single sprite Add inside a render method. Identity = (GroupKey, Index).
    ///
    /// GroupKey lets multiple SpriteAdds share a logical owner (e.g. all sprites
    /// belonging to one keyframe group, or one template instance) so they can
    /// be Updated/Removed atomically. Index gives stable order within the group.
    ///
    /// IMPORTANT: <see cref="CreationStatement"/> and <see cref="AddStatement"/>
    /// are kept SEPARATE so the injector can preserve the line-jump contract
    /// (cursor lands on the creation line, not the Add line). See
    /// <c>LineJumpGoldenTests</c> for the pinned semantics.
    /// </summary>
    public sealed class SpriteAddOp : InjectionOp
    {
        /// <summary>Method to inject into. Required for Add.</summary>
        public string AnchorMethod { get; set; }

        /// <summary>
        /// Stable group label. All SpriteAdds with the same GroupKey are treated
        /// as one logical unit. Examples: "keyframe:semicircle", "template:thermometer",
        /// "switch-case:Header". Required.
        /// </summary>
        public string GroupKey { get; set; }

        /// <summary>0-based index within the group. Defines source order. Required.</summary>
        public int Index { get; set; }

        /// <summary>
        /// The sprite-creation statement, e.g.
        /// <c>var t = MySprite.CreateText("hello", "Debug", Color.White, 1f);</c>.
        /// Emitted on its OWN line so SourceLineNumber can target it directly.
        /// May be null for direct-inline creations (then <see cref="AddStatement"/>
        /// contains the full <c>frame.Add(new MySprite(...))</c> in one line).
        /// </summary>
        public string CreationStatement { get; set; }

        /// <summary>
        /// The Add statement, e.g. <c>frame.Add(t);</c> or, for direct inline,
        /// <c>frame.Add(new MySprite(...));</c>. Required.
        /// </summary>
        public string AddStatement { get; set; }

        /// <summary>
        /// Local-variable name introduced by <see cref="CreationStatement"/>, e.g. "t".
        /// Null when the Add is a direct-inline creation. Used by the injector to
        /// validate that <see cref="AddStatement"/> references it.
        /// </summary>
        public string LocalName { get; set; }

        public override string Identity =>
            "sprite:" + (AnchorMethod ?? "(no-method)") + "/" + (GroupKey ?? "(no-group)") + "#" + Index;
    }

    /// <summary>
    /// Replaces the values inside an existing array-field initializer
    /// (e.g. <c>int[] kfTick = { 0, 30, 60 };</c>) without touching the type,
    /// name, indentation, or trailing trivia/comments of the declaration.
    /// Identity = field name. The op is intended for keyframe array value
    /// swaps where the legacy regex pipeline only edits the contents inside
    /// the braces. Action is normally <see cref="InjectionAction.Update"/>;
    /// <see cref="InjectionAction.Add"/> is rejected with a warning because
    /// adding a fresh array belongs in a <see cref="ClassFieldOp"/>.
    /// </summary>
    public sealed class ArrayValueSwapOp : InjectionOp
    {
        /// <summary>Array variable name (identity). Required.</summary>
        public string Name { get; set; }

        /// <summary>
        /// Replacement values to place between the braces, e.g. <c>"0, 45, 90"</c>.
        /// Must be a comma-separated list of valid C# expressions; the injector
        /// parses them via Roslyn rather than splicing text. Required.
        /// </summary>
        public string NewValuesText { get; set; }

        public override string Identity => "array-values:" + (Name ?? "(unnamed)");
    }

    /// <summary>
    /// Surgical literal replacement at a known absolute span in the source buffer.
    /// Used by the round-trip property patcher (sprite color/text/font/position
    /// edits coming from the canvas) to replace a single literal in place WITHOUT
    /// reformatting or re-emitting surrounding syntax. This is the op type that
    /// preserves the line-jump contract: callers feed in the exact span they
    /// already know about (typically a sprite's literal extracted via
    /// <c>SpriteNavigationIndex</c>) and the injector reports back the resulting
    /// length delta in <see cref="InjectionResult.Edits"/> so callers can shift
    /// any external offsets (sprite SourceStart/SourceEnd) accordingly.
    ///
    /// Identity = (Anchor, Span) — Anchor is a free-form caller label
    /// ("sprite#3:Text", "sprite#3:Color"), Span is the [Start,End) range. Only
    /// <see cref="InjectionAction.Update"/> is meaningful; Add/Remove are warned
    /// (use <see cref="ClassFieldOp"/> / structural ops to add or remove syntax).
    /// </summary>
    public sealed class PropertyPatchOp : InjectionOp
    {
        /// <summary>Caller-supplied label for diff/log output. Required.</summary>
        public string Anchor { get; set; }

        /// <summary>Absolute character offset of the first character to replace. Required.</summary>
        public int Start { get; set; }

        /// <summary>Absolute character offset just past the last character to replace. Required.</summary>
        public int End { get; set; }

        /// <summary>
        /// Expected current text inside [<see cref="Start"/>, <see cref="End"/>).
        /// If non-null, the injector verifies the buffer matches before patching
        /// and warns if it doesn't (stale offsets / external edit). Optional.
        /// </summary>
        public string ExpectedOldText { get; set; }

        /// <summary>Replacement text. Required (may be empty to delete the span).</summary>
        public string NewText { get; set; }

        public override string Identity =>
            "property:" + (Anchor ?? "(unnamed)") + "@" + Start + ".." + End;
    }

    /// <summary>
    /// One concrete edit applied by the injector, in absolute pre-edit
    /// coordinates of the source buffer. Reported in <see cref="InjectionResult.Edits"/>
    /// so callers can shift external offsets (e.g. sprite SourceStart/SourceEnd)
    /// without re-scanning the file.
    ///
    /// Edits are reported in the same order they were applied. The injector
    /// applies edits in descending Start order, so each edit's coordinates are
    /// valid against the pre-edit buffer; callers may apply them as-is to any
    /// external offset map.
    /// </summary>
    public sealed class SourceEdit
    {
        /// <summary>Pre-edit start offset of the replaced span.</summary>
        public int Start { get; set; }

        /// <summary>Pre-edit end offset (exclusive) of the replaced span.</summary>
        public int End { get; set; }

        /// <summary>Length delta produced by the edit (newLen - oldLen).</summary>
        public int Delta { get; set; }

        /// <summary>Op identity that produced this edit, for cross-reference with <see cref="InjectionResult.Diff"/>.</summary>
        public string OpIdentity { get; set; }
    }

    /// <summary>
    /// Removes a static (non-animated) <c>frame.Add(new MySprite { … Data = "name" … });</c>
    /// block from the anchor method body. Used by snippet-merge so an animated
    /// version of a previously-static sprite cleanly supersedes the static one.
    ///
    /// The injector matches the first <c>frame.Add(new MySprite { … });</c>
    /// statement whose body contains a <c>Data = "&lt;SpriteName&gt;"</c> assignment
    /// AND does NOT carry any of the animation marker tokens (e.g. lambdas,
    /// keyframe array references) so genuinely-animated blocks are skipped.
    /// On match, the entire statement plus any leading blank line are removed.
    ///
    /// Identity = "static-sprite-remove:&lt;SpriteName&gt;" inside &lt;AnchorMethod&gt;.
    /// Only <see cref="InjectionAction.Remove"/> is meaningful.
    /// </summary>
    public sealed class StaticSpriteRemoveOp : InjectionOp
    {
        /// <summary>Method to scan (e.g. "DrawFrame"). Required.</summary>
        public string AnchorMethod { get; set; }

        /// <summary>Sprite name to match in the <c>Data = "…"</c> assignment. Required.</summary>
        public string SpriteName { get; set; }

        public override string Identity =>
            "static-sprite-remove:" + (SpriteName ?? "(unnamed)") + "@" + (AnchorMethod ?? "(unnamed)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Plan + Result
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structured description of all changes a caller wants to apply to the
    /// editor source in a single transaction. Built by callers (keyframe
    /// generator, template gallery, snippet generator, ...) and handed to
    /// <see cref="ICodeInjector.Apply"/>.
    ///
    /// A plan is declarative and does not contain raw text patches — the
    /// injector decides where and how to write each op into the syntax tree.
    /// </summary>
    public sealed class InjectionPlan
    {
        /// <summary>Optional human-readable label for diff/log output (e.g. "Apply keyframe group: semicircle").</summary>
        public string Description { get; set; }

        /// <summary>Target class name. Optional — null = first/only top-level class.</summary>
        public string TargetClass { get; set; }

        public List<ClassFieldOp> Fields { get; } = new List<ClassFieldOp>();
        public List<HelperMethodOp> Methods { get; } = new List<HelperMethodOp>();
        public List<RenderBlockOp> RenderBlocks { get; } = new List<RenderBlockOp>();
        public List<SpriteAddOp> SpriteAdds { get; } = new List<SpriteAddOp>();
        public List<ArrayValueSwapOp> ArrayValueSwaps { get; } = new List<ArrayValueSwapOp>();
        public List<PropertyPatchOp> PropertyPatches { get; } = new List<PropertyPatchOp>();
        public List<StaticSpriteRemoveOp> StaticSpriteRemoves { get; } = new List<StaticSpriteRemoveOp>();

        /// <summary>Convenience: enumerate every op regardless of category.</summary>
        public IEnumerable<InjectionOp> AllOps()
        {
            foreach (var f in Fields)            yield return f;
            foreach (var m in Methods)           yield return m;
            foreach (var b in RenderBlocks)      yield return b;
            foreach (var s in SpriteAdds)        yield return s;
            foreach (var a in ArrayValueSwaps)   yield return a;
            foreach (var p in PropertyPatches)   yield return p;
            foreach (var r in StaticSpriteRemoves) yield return r;
        }
    }

    /// <summary>
    /// A non-fatal issue raised by the injector while applying a plan.
    /// Anchor missing, identity collision, source unparseable, etc.
    /// Callers inspect these to decide whether to surface UI feedback.
    /// </summary>
    public sealed class InjectionWarning
    {
        public string OpIdentity { get; set; }
        public InjectionAction AttemptedAction { get; set; }
        public string Message { get; set; }

        public override string ToString() =>
            $"[{AttemptedAction}] {OpIdentity}: {Message}";
    }

    /// <summary>
    /// Per-op record of what the injector actually did. Used for diff logging
    /// (Phase 2 side-by-side comparison vs the legacy engines) and for unit tests.
    /// </summary>
    public sealed class InjectionDiffEntry
    {
        public string OpIdentity { get; set; }
        public InjectionAction Action { get; set; }
        public bool Applied { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>
    /// Result of <see cref="ICodeInjector.Apply"/>. Always non-null; check
    /// <see cref="Success"/> and <see cref="Warnings"/> before using
    /// <see cref="RewrittenSource"/>.
    /// </summary>
    public sealed class InjectionResult
    {
        public bool Success { get; set; }

        /// <summary>The rewritten source code. Equals the original on hard failure.</summary>
        public string RewrittenSource { get; set; }

        public List<InjectionWarning> Warnings { get; } = new List<InjectionWarning>();
        public List<InjectionDiffEntry> Diff { get; } = new List<InjectionDiffEntry>();

        /// <summary>
        /// Concrete text edits applied by the injector, reported in pre-edit
        /// coordinates. Currently populated only for <see cref="PropertyPatchOp"/>
        /// (the round-trip patcher's bread-and-butter); structural ops do not
        /// emit individual edits because they don't carry stable absolute spans.
        /// Callers use this list to shift external offset maps (e.g. sprite
        /// SourceStart/SourceEnd) without re-scanning the rewritten source.
        /// </summary>
        public List<SourceEdit> Edits { get; } = new List<SourceEdit>();

        /// <summary>Hard-failure error message (parse failure, internal bug). Null on success.</summary>
        public string Error { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Injector contract
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The single chokepoint for mutating editor source code.
    ///
    /// Implementations MUST:
    ///   • Round-trip the source through Roslyn (no regex hacks).
    ///   • Preserve all text the plan does not explicitly target (incl. user comments,
    ///     whitespace, expressions in templates — see copilot-instructions.md).
    ///   • Maintain the line-jump contract pinned by <c>LineJumpGoldenTests</c>:
    ///     <see cref="SpriteAddOp.CreationStatement"/> stays on its own physical
    ///     line, distinct from <see cref="SpriteAddOp.AddStatement"/>.
    ///   • Be idempotent: applying the same plan twice produces the same source.
    ///   • Never throw for "anchor missing" or "identity collision" — record an
    ///     <see cref="InjectionWarning"/> and continue with remaining ops.
    /// </summary>
    public interface ICodeInjector
    {
        /// <summary>
        /// Apply <paramref name="plan"/> to <paramref name="source"/> and return
        /// the rewritten source plus a structured diff.
        /// </summary>
        InjectionResult Apply(string source, InjectionPlan plan);
    }
}
