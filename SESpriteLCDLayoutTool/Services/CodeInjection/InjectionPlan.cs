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
        MethodEnd
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

        /// <summary>Convenience: enumerate every op regardless of category.</summary>
        public IEnumerable<InjectionOp> AllOps()
        {
            foreach (var f in Fields)       yield return f;
            foreach (var m in Methods)      yield return m;
            foreach (var b in RenderBlocks) yield return b;
            foreach (var s in SpriteAdds)   yield return s;
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
