using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SESpriteLCDLayoutTool.Services.CodeInjection
{
    /// <summary>
    /// Roslyn-based implementation of <see cref="ICodeInjector"/>.
    ///
    /// Strategy:
    ///   1. Parse source into a CompilationUnitSyntax.
    ///   2. Locate the target ClassDeclarationSyntax (named or first).
    ///   3. Apply each op category in a fixed order:
    ///         Fields → Methods → RenderBlocks → SpriteAdds
    ///      Each pass uses a SyntaxRewriter that performs Add/Update/Remove
    ///      idempotently using the op's Identity.
    ///   4. Emit ToFullString().
    ///
    /// Identity encoding inside source:
    ///   - RenderBlock: leading-trivia banner   // [INJ:block:Method/Key]
    ///                  on the wrapped { ... } statement.
    ///   - SpriteAdd group: leading-trivia banner   // [INJ:sprite-group:Method/Group]
    ///                  on a wrapped { creation; add; ...; } statement.
    ///   - ClassField:  leading-trivia banner   // [INJ:field:Name]   above the field.
    ///   - HelperMethod: leading-trivia banner  // [INJ:method:Name(sig)]   above the method.
    ///
    /// The banners are how we re-locate ops on Update/Remove. They are deliberately
    /// boring single-line // comments so they round-trip through the editor cleanly.
    /// </summary>
    public sealed class RoslynCodeInjector : ICodeInjector
    {
        // Marker constants. Keep public so tests can assert on them.
        public const string BannerPrefix = "// [INJ:";
        public const string BannerSuffix = "]";

        /// <summary>
        /// When true (default), each Add/Update emits a `// [INJ:...]` banner
        /// in leading trivia so subsequent Update/Remove can re-locate the op
        /// by identity. When false (option A), banners are suppressed and the
        /// injector falls back to STRUCTURAL identity:
        ///   • ClassFieldOp     → matched by field name (variable declarator)
        ///   • HelperMethodOp   → matched by method name + parameter type signature
        ///   • RenderBlockOp    → no structural identity exists; Update/Remove of
        ///                        an existing block in this mode is unsupported and
        ///                        produces a warning. Add still works (banner-free).
        ///   • SpriteAddOp groups → no structural identity exists; Update/Remove
        ///                        produces a warning. Add still works.
        ///
        /// Suppressed mode is the parity mode used by ShadowCompareRunner when
        /// comparing against legacy regex pipelines (e.g. MergeKeyframedIntoCode)
        /// whose output never contains banners.
        /// </summary>
        private readonly bool _emitBanners;

        public RoslynCodeInjector() : this(emitBanners: true) { }

        public RoslynCodeInjector(bool emitBanners)
        {
            _emitBanners = emitBanners;
        }

        public InjectionResult Apply(string source, InjectionPlan plan)
        {
            var result = new InjectionResult { RewrittenSource = source ?? string.Empty };

            if (plan == null)
            {
                result.Success = true;
                return result;
            }

            if (string.IsNullOrEmpty(source))
            {
                result.Error = "Source is empty.";
                return result;
            }

            // ── Pre-parse text pass: StaticSpriteRemoveOps ───────────────────
            // Snippet-merge supersedes a previously-static sprite with an
            // animated version. Removal runs FIRST so any property patches in
            // the same plan can be authored against the post-removal buffer
            // (no current caller mixes the two, but ordering here is the safe
            // contract). Round-trip patcher plans don't carry static removes.
            source = ApplyStaticSpriteRemoveOps(source, plan, result);

            // ── Pre-parse text pass: PropertyPatchOps ────────────────────────
            // Property patches carry absolute pre-edit spans (typically derived
            // from SpriteNavigationIndex against the ORIGINAL source). Applying
            // them as a pure text rewrite means subsequent structural ops
            // parse the already-patched text and never see stale offsets, while
            // callers receive a SourceEdit list expressed in original-buffer
            // coordinates so they can shift external offset maps in lockstep.
            source = ApplyPropertyPatchOps(source, plan, result);
            result.RewrittenSource = source;

            SyntaxTree tree;
            CompilationUnitSyntax root;
            try
            {
                tree = CSharpSyntaxTree.ParseText(source);
                root = tree.GetCompilationUnitRoot();
            }
            catch (Exception ex)
            {
                result.Error = "Parse failed: " + ex.Message;
                return result;
            }

            // Find target class
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            if (classes.Count == 0)
            {
                // PropertyPatchOps already ran above and don't need a class.
                // If the plan ONLY contains property patches, that is a valid
                // success; otherwise the structural ops cannot land anywhere.
                bool hasStructural = plan.Fields.Count > 0 || plan.Methods.Count > 0 ||
                                     plan.RenderBlocks.Count > 0 || plan.SpriteAdds.Count > 0 ||
                                     plan.ArrayValueSwaps.Count > 0;
                if (hasStructural)
                {
                    result.Error = "No class declaration found in source.";
                    return result;
                }
                result.Success = true;
                return result;
            }
            var targetClass = string.IsNullOrEmpty(plan.TargetClass)
                ? classes[0]
                : classes.FirstOrDefault(c => c.Identifier.Text == plan.TargetClass) ?? classes[0];

            // Apply each category. We rebuild root after each pass to keep node identities valid.
            root = ApplyFieldOps(root, targetClass, plan, result);
            targetClass = ResolveTargetClass(root, plan);

            root = ApplyMethodOps(root, targetClass, plan, result);
            targetClass = ResolveTargetClass(root, plan);

            root = ApplyRenderBlockOps(root, targetClass, plan, result);
            targetClass = ResolveTargetClass(root, plan);

            root = ApplySpriteAddOps(root, targetClass, plan, result);
            targetClass = ResolveTargetClass(root, plan);

            root = ApplyArrayValueSwapOps(root, targetClass, plan, result);


            result.RewrittenSource = root.ToFullString();
            result.Success = true;
            return result;
        }

        private static ClassDeclarationSyntax ResolveTargetClass(CompilationUnitSyntax root, InjectionPlan plan)
        {
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            if (classes.Count == 0) return null;
            if (string.IsNullOrEmpty(plan.TargetClass)) return classes[0];
            return classes.FirstOrDefault(c => c.Identifier.Text == plan.TargetClass) ?? classes[0];
        }

        // ─────────────────────────────────────────────────────────────────────
        // Banner helpers
        // ─────────────────────────────────────────────────────────────────────

        private static string MakeBanner(string identity) => BannerPrefix + identity + BannerSuffix;

        /// <summary>
        /// Returns true if <paramref name="node"/>'s leading trivia contains a banner
        /// matching <paramref name="identity"/>.
        /// </summary>
        private static bool HasBanner(SyntaxNode node, string identity)
        {
            if (node == null) return false;
            string banner = MakeBanner(identity);
            foreach (var t in node.GetLeadingTrivia())
            {
                if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().Trim() == banner)
                    return true;
            }
            return false;
        }

        private SyntaxTriviaList PrependBanner(SyntaxTriviaList existing, string identity)
        {
            // Suppressed mode (option A): no banner is emitted. Identity is
            // recovered structurally on subsequent passes (see FindFieldByName /
            // FindMethodBySignature). For ops that have no structural identity
            // (RenderBlock, SpriteAdd group) the caller must already have warned.
            if (!_emitBanners) return existing;

            var banner = SyntaxFactory.Comment(MakeBanner(identity));
            var eol = SyntaxFactory.EndOfLine(Environment.NewLine);
            // Preserve any existing leading whitespace as the indent for the banner.
            return SyntaxFactory.TriviaList(banner, eol).AddRange(existing);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Field ops
        // ─────────────────────────────────────────────────────────────────────

        private CompilationUnitSyntax ApplyFieldOps(
            CompilationUnitSyntax root, ClassDeclarationSyntax targetClass,
            InjectionPlan plan, InjectionResult result)
        {
            if (plan.Fields.Count == 0 || targetClass == null) return root;

            var newMembers = targetClass.Members.ToList();

            foreach (var op in plan.Fields)
            {
                if (op.Action == InjectionAction.Preserve) continue;

                int existingIdx = FindMemberIndex(newMembers, m => HasBanner(m, op.Identity));
                // Structural fallback: a field declared by name still counts as
                // "existing" even when no banner is present. This keeps suppressed
                // mode (option A) idempotent and lets us update/remove fields the
                // legacy regex pipeline emitted without banners.
                if (existingIdx < 0 && !string.IsNullOrEmpty(op.Name))
                    existingIdx = FindFieldByName(newMembers, op.Name);

                if (op.Action == InjectionAction.Remove)
                {
                    if (existingIdx >= 0)
                    {
                        newMembers.RemoveAt(existingIdx);
                        Diff(result, op, true, "removed");
                    }
                    else Warn(result, op, "field not found for Remove");
                    continue;
                }

                FieldDeclarationSyntax fieldSyntax = BuildFieldSyntax(op, result);
                if (fieldSyntax == null) continue;

                if (op.Action == InjectionAction.Add)
                {
                    if (existingIdx >= 0)
                    {
                        Warn(result, op, "field already exists; treating as Update");
                        newMembers[existingIdx] = fieldSyntax;
                        Diff(result, op, true, "updated (was Add)");
                    }
                    else
                    {
                        // Insert at top of class so fields cluster together.
                        int insertIdx = FirstNonFieldIndex(newMembers);
                        newMembers.Insert(insertIdx, fieldSyntax);
                        Diff(result, op, true, "added");
                    }
                }
                else if (op.Action == InjectionAction.Update)
                {
                    if (existingIdx >= 0)
                    {
                        newMembers[existingIdx] = fieldSyntax;
                        Diff(result, op, true, "updated");
                    }
                    else Warn(result, op, "field not found for Update");
                }
            }

            var newClass = targetClass.WithMembers(SyntaxFactory.List(newMembers));
            return root.ReplaceNode(targetClass, newClass);
        }

        private FieldDeclarationSyntax BuildFieldSyntax(ClassFieldOp op, InjectionResult result)
        {
            string text = (op.Declaration ?? string.Empty).TrimEnd().TrimEnd(';') + ";";
            try
            {
                var member = SyntaxFactory.ParseMemberDeclaration(text);
                if (!(member is FieldDeclarationSyntax field))
                {
                    Warn(result, op, "Declaration is not a valid field");
                    return null;
                }
                field = field.WithLeadingTrivia(PrependBanner(field.GetLeadingTrivia(), op.Identity));
                if (!field.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
                    field = field.WithTrailingTrivia(field.GetTrailingTrivia().Add(SyntaxFactory.EndOfLine(Environment.NewLine)));
                return field;
            }
            catch (Exception ex)
            {
                Warn(result, op, "field parse failed: " + ex.Message);
                return null;
            }
        }

        private static int FirstNonFieldIndex(List<MemberDeclarationSyntax> members)
        {
            for (int i = 0; i < members.Count; i++)
                if (!(members[i] is FieldDeclarationSyntax)) return i;
            return members.Count;
        }

        /// <summary>
        /// Structural fallback used by suppressed-banner mode: locate a field by
        /// the variable-declarator name. Returns the first match or -1.
        /// </summary>
        private static int FindFieldByName(List<MemberDeclarationSyntax> members, string name)
        {
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] is FieldDeclarationSyntax fd &&
                    fd.Declaration.Variables.Any(v => v.Identifier.Text == name))
                    return i;
            }
            return -1;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Method ops
        // ─────────────────────────────────────────────────────────────────────

        private CompilationUnitSyntax ApplyMethodOps(
            CompilationUnitSyntax root, ClassDeclarationSyntax targetClass,
            InjectionPlan plan, InjectionResult result)
        {
            if (plan.Methods.Count == 0 || targetClass == null) return root;

            var newMembers = targetClass.Members.ToList();

            foreach (var op in plan.Methods)
            {
                if (op.Action == InjectionAction.Preserve) continue;

                int existingIdx = FindMemberIndex(newMembers, m => HasBanner(m, op.Identity));
                // Structural fallback: match by method name + parameter type sig.
                if (existingIdx < 0 && !string.IsNullOrEmpty(op.Name))
                    existingIdx = FindMethodBySignature(newMembers, op.Name, op.ParameterSignature);

                if (op.Action == InjectionAction.Remove)
                {
                    if (existingIdx >= 0)
                    {
                        newMembers.RemoveAt(existingIdx);
                        Diff(result, op, true, "removed");
                    }
                    else Warn(result, op, "method not found for Remove");
                    continue;
                }

                MethodDeclarationSyntax methodSyntax = BuildMethodSyntax(op, result);
                if (methodSyntax == null) continue;

                if (op.Action == InjectionAction.Add)
                {
                    if (existingIdx >= 0)
                    {
                        newMembers[existingIdx] = methodSyntax;
                        Diff(result, op, true, "updated (was Add)");
                    }
                    else
                    {
                        newMembers.Add(methodSyntax);
                        Diff(result, op, true, "added");
                    }
                }
                else if (op.Action == InjectionAction.Update)
                {
                    if (existingIdx >= 0)
                    {
                        newMembers[existingIdx] = methodSyntax;
                        Diff(result, op, true, "updated");
                    }
                    else Warn(result, op, "method not found for Update");
                }
            }

            var newClass = targetClass.WithMembers(SyntaxFactory.List(newMembers));
            return root.ReplaceNode(targetClass, newClass);
        }

        private MethodDeclarationSyntax BuildMethodSyntax(HelperMethodOp op, InjectionResult result)
        {
            try
            {
                var member = SyntaxFactory.ParseMemberDeclaration(op.FullDeclaration ?? string.Empty);
                if (!(member is MethodDeclarationSyntax method))
                {
                    Warn(result, op, "FullDeclaration is not a valid method");
                    return null;
                }
                method = method.WithLeadingTrivia(PrependBanner(method.GetLeadingTrivia(), op.Identity));
                if (!method.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
                    method = method.WithTrailingTrivia(method.GetTrailingTrivia().Add(SyntaxFactory.EndOfLine(Environment.NewLine)));
                return method;
            }
            catch (Exception ex)
            {
                Warn(result, op, "method parse failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Structural fallback used by suppressed-banner mode: locate a method by
        /// name + canonical parameter-type signature. Comma-separated, whitespace
        /// normalised; matches the encoding used in <see cref="HelperMethodOp.ParameterSignature"/>.
        /// </summary>
        private static int FindMethodBySignature(List<MemberDeclarationSyntax> members, string name, string parameterSignature)
        {
            string targetSig = NormalizeSignature(parameterSignature ?? string.Empty);
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] is MethodDeclarationSyntax md && md.Identifier.Text == name)
                {
                    string sig = string.Join(",",
                        md.ParameterList.Parameters.Select(p =>
                            NormalizeSignature(p.Type?.ToString() ?? "")));
                    if (string.Equals(sig, targetSig, StringComparison.Ordinal))
                        return i;
                }
            }
            return -1;
        }

        private static string NormalizeSignature(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        // RenderBlock ops
        // ─────────────────────────────────────────────────────────────────────

        private CompilationUnitSyntax ApplyRenderBlockOps(
            CompilationUnitSyntax root, ClassDeclarationSyntax targetClass,
            InjectionPlan plan, InjectionResult result)
        {
            if (plan.RenderBlocks.Count == 0 || targetClass == null) return root;

            // Group ops by anchor method so we mutate each method's body once.
            foreach (var byMethod in plan.RenderBlocks.GroupBy(o => o.AnchorMethod ?? ""))
            {
                var method = FindMethod(root, byMethod.Key);
                if (method == null || method.Body == null)
                {
                    foreach (var op in byMethod) Warn(result, op, "anchor method not found or has no body");
                    continue;
                }

                var newStatements = method.Body.Statements.ToList();

                foreach (var op in byMethod)
                {
                    if (op.Action == InjectionAction.Preserve) continue;

                    int existingIdx = FindStatementIndex(newStatements, s => HasBanner(s, op.Identity));

                    // RenderBlock has no structural identity once banners are
                    // suppressed. Update/Remove against banner-free source is
                    // therefore not supported in option A and warns rather than
                    // silently rewriting the wrong block.
                    if (!_emitBanners && existingIdx < 0 &&
                        (op.Action == InjectionAction.Update || op.Action == InjectionAction.Remove))
                    {
                        Warn(result, op, "banner-suppressed mode cannot locate render block by structural identity; "
                                       + "Update/Remove not supported here");
                        continue;
                    }

                    if (op.Action == InjectionAction.Remove)
                    {
                        if (existingIdx >= 0)
                        {
                            newStatements.RemoveAt(existingIdx);
                            Diff(result, op, true, "removed");
                        }
                        else Warn(result, op, "block not found for Remove");
                        continue;
                    }

                    BlockSyntax block = BuildRenderBlock(op, result);
                    if (block == null) continue;

                    if (op.Action == InjectionAction.Add)
                    {
                        if (existingIdx >= 0)
                        {
                            newStatements[existingIdx] = block;
                            Diff(result, op, true, "updated (was Add)");
                        }
                        else
                        {
                            int insertIdx = ChooseInsertIndex(newStatements, op);
                            newStatements.Insert(insertIdx, block);
                            Diff(result, op, true, "added @ " + op.Placement);
                        }
                    }
                    else if (op.Action == InjectionAction.Update)
                    {
                        if (existingIdx >= 0)
                        {
                            newStatements[existingIdx] = block;
                            Diff(result, op, true, "updated");
                        }
                        else Warn(result, op, "block not found for Update");
                    }
                }

                var newBody = method.Body.WithStatements(SyntaxFactory.List(newStatements));
                root = root.ReplaceNode(method.Body, newBody);
            }

            return root;
        }

        private BlockSyntax BuildRenderBlock(RenderBlockOp op, InjectionResult result)
        {
            try
            {
                string body = op.BodyText ?? string.Empty;
                // Wrap as a real { ... } statement so the whole thing is one node.
                var parsed = SyntaxFactory.ParseStatement("{\n" + body + "\n}") as BlockSyntax;
                if (parsed == null)
                {
                    Warn(result, op, "BodyText could not be parsed as a statement block");
                    return null;
                }
                parsed = parsed.WithLeadingTrivia(PrependBanner(parsed.GetLeadingTrivia(), op.Identity));
                if (!parsed.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
                    parsed = parsed.WithTrailingTrivia(parsed.GetTrailingTrivia().Add(SyntaxFactory.EndOfLine(Environment.NewLine)));
                return parsed;
            }
            catch (Exception ex)
            {
                Warn(result, op, "block parse failed: " + ex.Message);
                return null;
            }
        }

        private static int ChooseInsertIndex(List<StatementSyntax> stmts, RenderBlockOp op)
        {
            switch (op.Placement)
            {
                case RenderBlockPlacement.MethodTop:
                    // After leading var/local declarations.
                    int i = 0;
                    while (i < stmts.Count && stmts[i] is LocalDeclarationStatementSyntax) i++;
                    return i;
                case RenderBlockPlacement.AfterLastAdd:
                    int last = -1;
                    for (int j = 0; j < stmts.Count; j++)
                        if (StatementContainsAddCall(stmts[j])) last = j;
                    return last < 0 ? stmts.Count : last + 1;
                case RenderBlockPlacement.MethodEnd:
                    return stmts.Count;
                case RenderBlockPlacement.BeforeToken:
                    {
                        string token = op.AnchorToken;
                        if (string.IsNullOrEmpty(token)) return stmts.Count;
                        for (int t = 0; t < stmts.Count; t++)
                            if (stmts[t].ToString().IndexOf(token, StringComparison.Ordinal) >= 0)
                                return t;
                        return stmts.Count;
                    }
                case RenderBlockPlacement.BeforeFirstAdd:
                default:
                    for (int k = 0; k < stmts.Count; k++)
                        if (StatementContainsAddCall(stmts[k])) return k;
                    return stmts.Count;
            }
        }

        private static bool StatementContainsAddCall(StatementSyntax s)
        {
            return s.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(inv =>
                inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Add");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SpriteAdd ops (grouped)
        // ─────────────────────────────────────────────────────────────────────

        private CompilationUnitSyntax ApplySpriteAddOps(
            CompilationUnitSyntax root, ClassDeclarationSyntax targetClass,
            InjectionPlan plan, InjectionResult result)
        {
            if (plan.SpriteAdds.Count == 0 || targetClass == null) return root;

            // Group by (AnchorMethod, GroupKey). One block per group, ops ordered by Index.
            var groups = plan.SpriteAdds
                .GroupBy(o => Tuple.Create(o.AnchorMethod ?? "", o.GroupKey ?? ""))
                .ToList();

            foreach (var byMethod in groups.GroupBy(g => g.Key.Item1))
            {
                var method = FindMethod(root, byMethod.Key);
                if (method == null || method.Body == null)
                {
                    foreach (var g in byMethod)
                        foreach (var op in g) Warn(result, op, "anchor method not found or has no body");
                    continue;
                }

                var newStatements = method.Body.Statements.ToList();

                foreach (var group in byMethod)
                {
                    string groupKey = group.Key.Item2;
                    string groupIdentity = "sprite-group:" + byMethod.Key + "/" + groupKey;

                    var ordered = group.OrderBy(o => o.Index).ToList();

                    // Decide group action: if ANY op is Remove and ALL are Remove → remove the group.
                    // Mixed semantics inside a single group are rejected with a warning.
                    bool allRemove = ordered.All(o => o.Action == InjectionAction.Remove);
                    bool anyRemove = ordered.Any(o => o.Action == InjectionAction.Remove);
                    bool allPreserve = ordered.All(o => o.Action == InjectionAction.Preserve);
                    if (allPreserve) continue;

                    int existingIdx = FindStatementIndex(newStatements, s => HasBanner(s, groupIdentity));

                    // Sprite groups have no structural identity once banners are
                    // suppressed. Update/Remove against banner-free source is
                    // therefore not supported in option A.
                    if (!_emitBanners && existingIdx < 0 &&
                        (allRemove || ordered.Any(o => o.Action == InjectionAction.Update)))
                    {
                        foreach (var op in ordered.Where(o => o.Action != InjectionAction.Preserve))
                            Warn(result, op, "banner-suppressed mode cannot locate sprite group by structural identity; "
                                           + "Update/Remove not supported here");
                        continue;
                    }

                    if (allRemove)
                    {
                        if (existingIdx >= 0)
                        {
                            newStatements.RemoveAt(existingIdx);
                            foreach (var op in ordered) Diff(result, op, true, "removed (group)");
                        }
                        else
                        {
                            foreach (var op in ordered) Warn(result, op, "sprite group not found for Remove");
                        }
                        continue;
                    }
                    if (anyRemove)
                    {
                        foreach (var op in ordered.Where(o => o.Action == InjectionAction.Remove))
                            Warn(result, op, "mixed Remove with non-Remove in same sprite group is not supported; treat the whole group atomically");
                    }

                    BlockSyntax block = BuildSpriteGroupBlock(groupIdentity, ordered, result);
                    if (block == null) continue;

                    bool isUpdate = ordered.Any(o => o.Action == InjectionAction.Update) ||
                                    ordered.All(o => o.Action == InjectionAction.Preserve || o.Action == InjectionAction.Update);

                    if (existingIdx >= 0)
                    {
                        newStatements[existingIdx] = block;
                        foreach (var op in ordered.Where(o => o.Action != InjectionAction.Preserve))
                            Diff(result, op, true, "updated");
                    }
                    else
                    {
                        // Place new sprite groups at end of body — they ARE the Add list.
                        newStatements.Add(block);
                        foreach (var op in ordered.Where(o => o.Action != InjectionAction.Preserve))
                            Diff(result, op, true, "added");
                    }
                }

                var newBody = method.Body.WithStatements(SyntaxFactory.List(newStatements));
                root = root.ReplaceNode(method.Body, newBody);
            }

            return root;
        }

        private BlockSyntax BuildSpriteGroupBlock(string groupIdentity, List<SpriteAddOp> ordered, InjectionResult result)
        {
            // Each op contributes (CreationStatement?\n)?AddStatement\n
            // Creation statement is emitted on its OWN line so SpriteAddMapper
            // resolves LineNumber to the creation line, not the Add line.
            // (See LineJumpGoldenTests for the pinned contract.)
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            foreach (var op in ordered)
            {
                if (!string.IsNullOrWhiteSpace(op.CreationStatement))
                {
                    sb.Append(op.CreationStatement.TrimEnd());
                    if (!op.CreationStatement.TrimEnd().EndsWith(";")) sb.Append(';');
                    sb.Append('\n');
                }
                if (string.IsNullOrWhiteSpace(op.AddStatement))
                {
                    Warn(result, op, "AddStatement is empty");
                    continue;
                }
                sb.Append(op.AddStatement.TrimEnd());
                if (!op.AddStatement.TrimEnd().EndsWith(";")) sb.Append(';');
                sb.Append('\n');
            }
            sb.Append("}\n");

            try
            {
                var parsed = SyntaxFactory.ParseStatement(sb.ToString()) as BlockSyntax;
                if (parsed == null)
                {
                    foreach (var op in ordered) Warn(result, op, "sprite group block parse returned non-block");
                    return null;
                }
                parsed = parsed.WithLeadingTrivia(PrependBanner(parsed.GetLeadingTrivia(), groupIdentity));
                if (!parsed.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
                    parsed = parsed.WithTrailingTrivia(parsed.GetTrailingTrivia().Add(SyntaxFactory.EndOfLine(Environment.NewLine)));
                return parsed;
            }
            catch (Exception ex)
            {
                foreach (var op in ordered) Warn(result, op, "sprite group parse failed: " + ex.Message);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // ArrayValueSwap ops
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces the values inside an existing array-field initializer in
        /// place (e.g. <c>int[] kfTick = { 0, 30, 60 };</c> → <c>{ 0, 45, 90 }</c>),
        /// preserving the field's type, name, modifiers, indentation, and any
        /// trailing trivia/comments. Add is rejected — a brand-new array belongs
        /// in a <see cref="ClassFieldOp"/>. Identity is the field name; banners
        /// are not used because callers (legacy regex pipelines, structured plan
        /// builders) operate on banner-free source.
        /// </summary>
        private CompilationUnitSyntax ApplyArrayValueSwapOps(
            CompilationUnitSyntax root, ClassDeclarationSyntax targetClass,
            InjectionPlan plan, InjectionResult result)
        {
            if (plan.ArrayValueSwaps.Count == 0 || targetClass == null) return root;

            var newMembers = targetClass.Members.ToList();

            foreach (var op in plan.ArrayValueSwaps)
            {
                if (op.Action == InjectionAction.Preserve) continue;

                if (op.Action == InjectionAction.Add)
                {
                    Warn(result, op, "ArrayValueSwapOp does not support Add — use ClassFieldOp to declare a new array");
                    continue;
                }

                if (string.IsNullOrEmpty(op.Name))
                {
                    Warn(result, op, "ArrayValueSwapOp requires Name");
                    continue;
                }

                int existingIdx = -1;
                VariableDeclaratorSyntax declarator = null;
                FieldDeclarationSyntax field = null;
                for (int i = 0; i < newMembers.Count; i++)
                {
                    if (!(newMembers[i] is FieldDeclarationSyntax fd)) continue;
                    var v = fd.Declaration.Variables.FirstOrDefault(x => x.Identifier.Text == op.Name);
                    if (v == null) continue;
                    existingIdx = i;
                    field = fd;
                    declarator = v;
                    break;
                }

                if (op.Action == InjectionAction.Remove)
                {
                    if (existingIdx >= 0)
                    {
                        newMembers.RemoveAt(existingIdx);
                        Diff(result, op, true, "removed");
                    }
                    else Warn(result, op, "array not found for Remove");
                    continue;
                }

                // Update path (default for value swaps)
                if (existingIdx < 0 || declarator == null)
                {
                    Warn(result, op, "array not found for Update");
                    continue;
                }

                if (!(declarator.Initializer?.Value is InitializerExpressionSyntax oldInit) ||
                    !oldInit.IsKind(SyntaxKind.ArrayInitializerExpression))
                {
                    Warn(result, op, "field is not an array initializer; cannot value-swap");
                    continue;
                }

                InitializerExpressionSyntax newInit;
                try
                {
                    var parsedExpr = SyntaxFactory.ParseExpression("new[] { " + (op.NewValuesText ?? string.Empty) + " }");
                    if (!(parsedExpr is ImplicitArrayCreationExpressionSyntax implArr) ||
                        implArr.Initializer == null)
                    {
                        Warn(result, op, "NewValuesText could not be parsed as an array initializer body");
                        continue;
                    }
                    // Re-create the initializer expression as ArrayInitializerExpression so it
                    // matches the original kind, preserving the surrounding declarator shape.
                    newInit = SyntaxFactory.InitializerExpression(
                        SyntaxKind.ArrayInitializerExpression,
                        implArr.Initializer.Expressions);
                    // Carry over the original braces' trivia so spacing around `{ ... }` is stable.
                    newInit = newInit
                        .WithOpenBraceToken(oldInit.OpenBraceToken)
                        .WithCloseBraceToken(oldInit.CloseBraceToken);
                }
                catch (Exception ex)
                {
                    Warn(result, op, "array values parse failed: " + ex.Message);
                    continue;
                }

                var newDeclarator = declarator.WithInitializer(declarator.Initializer.WithValue(newInit));
                var newDecl = field.Declaration.ReplaceNode(declarator, newDeclarator);
                var newField = field.WithDeclaration(newDecl);
                newMembers[existingIdx] = newField;
                Diff(result, op, true, "values updated");
            }

            var newClass = targetClass.WithMembers(SyntaxFactory.List(newMembers));
            return root.ReplaceNode(targetClass, newClass);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Static sprite remove ops (snippet-merge superseding a static block)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes the first matching static <c>frame.Add(new MySprite { … });</c>
        /// statement from the source whose body contains a
        /// <c>Data = "&lt;SpriteName&gt;"</c> assignment. Animated blocks (those
        /// carrying the legacy "← animated" marker) are intentionally skipped
        /// so a freshly-merged keyframe block is not removed alongside its
        /// static predecessor. Runs as a pure text pass before structural
        /// rewrites so subsequent passes parse the post-removal buffer.
        /// </summary>
        private static string ApplyStaticSpriteRemoveOps(string source, InjectionPlan plan, InjectionResult result)
        {
            if (plan.StaticSpriteRemoves == null || plan.StaticSpriteRemoves.Count == 0) return source;

            foreach (var op in plan.StaticSpriteRemoves)
            {
                if (op == null) continue;
                if (op.Action == InjectionAction.Preserve) continue;
                if (op.Action != InjectionAction.Remove)
                {
                    Warn(result, op, "StaticSpriteRemoveOp only supports Remove");
                    continue;
                }
                if (string.IsNullOrEmpty(op.SpriteName))
                {
                    Warn(result, op, "StaticSpriteRemoveOp requires SpriteName");
                    continue;
                }

                int before = source.Length;
                source = RemoveStaticSpriteBlockText(source, op.SpriteName);
                Diff(result, op, source.Length != before,
                     source.Length != before ? "removed" : "no static block matched");
            }

            return source;
        }

        /// <summary>
        /// Text-level static-sprite removal mirroring the legacy
        /// <c>KeyframedCodeGenerator.RemoveStaticSpriteBlock</c> behavior so
        /// snippet-merge parity is preserved during the structured-plan flip.
        /// Walks back from <c>Data = "name"</c> to the enclosing
        /// <c>frame.Add(new MySprite</c> opener, forward to the closing
        /// <c>});</c>, skips animated blocks, and consumes a single preceding
        /// blank line on success.
        /// </summary>
        private static string RemoveStaticSpriteBlockText(string code, string spriteName)
        {
            string dataPattern = "\"" + spriteName + "\"";
            int searchFrom = 0;
            while (searchFrom < code.Length)
            {
                int dataIdx = code.IndexOf(dataPattern, searchFrom, StringComparison.Ordinal);
                if (dataIdx < 0) break;

                int lineStart = code.LastIndexOf('\n', dataIdx) + 1;
                string lineText = code.Substring(lineStart, dataIdx - lineStart).TrimStart();
                if (!lineText.StartsWith("Data", StringComparison.Ordinal))
                {
                    searchFrom = dataIdx + dataPattern.Length;
                    continue;
                }

                int blockStart = -1;
                int scan = lineStart - 1;
                while (scan >= 0)
                {
                    int ls = code.LastIndexOf('\n', scan) + 1;
                    string trimLine = code.Substring(ls, scan + 1 - ls).TrimStart();
                    if (trimLine.Contains("frame.Add(") || trimLine.Contains(".Add(new MySprite"))
                    {
                        blockStart = ls;
                        break;
                    }
                    if (trimLine.Contains("new MySprite"))
                    {
                        blockStart = ls;
                        break;
                    }
                    if (trimLine.Length > 0 && !trimLine.StartsWith("{") && !trimLine.StartsWith("//")
                        && !trimLine.StartsWith("Type") && !trimLine.StartsWith("Data")
                        && !trimLine.StartsWith("Position") && !trimLine.StartsWith("Size")
                        && !trimLine.StartsWith("Color") && !trimLine.StartsWith("Alignment")
                        && !trimLine.StartsWith("RotationOrScale") && !trimLine.StartsWith("FontId"))
                    {
                        break;
                    }
                    scan = ls - 2;
                    if (scan < 0) break;
                }

                if (blockStart < 0)
                {
                    searchFrom = dataIdx + dataPattern.Length;
                    continue;
                }

                int blockEnd = code.IndexOf("});", dataIdx, StringComparison.Ordinal);
                if (blockEnd < 0)
                {
                    searchFrom = dataIdx + dataPattern.Length;
                    continue;
                }
                blockEnd += 3;
                if (blockEnd < code.Length && code[blockEnd] == '\r') blockEnd++;
                if (blockEnd < code.Length && code[blockEnd] == '\n') blockEnd++;

                string block = code.Substring(blockStart, blockEnd - blockStart);
                if (block.Contains("\u2190 animated"))
                {
                    searchFrom = blockEnd;
                    continue;
                }

                int removeStart = blockStart;
                if (removeStart > 0 && code[removeStart - 1] == '\n')
                {
                    removeStart--;
                    if (removeStart > 0 && code[removeStart - 1] == '\r') removeStart--;
                }

                code = code.Remove(removeStart, blockEnd - removeStart);
                break;
            }

            return code;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Property patch ops (line-jump-preserving literal replacement)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies <see cref="PropertyPatchOp"/>s as a pure text rewrite over
        /// <paramref name="source"/>. Patches are applied in descending Start
        /// order so each op's absolute span remains valid against the working
        /// buffer; the resulting <see cref="SourceEdit"/> entries are reported
        /// in original (pre-edit) coordinates so callers can shift external
        /// offset maps without re-scanning. Overlapping spans are rejected
        /// with a warning to keep the line-jump contract intact.
        /// </summary>
        private static string ApplyPropertyPatchOps(string source, InjectionPlan plan, InjectionResult result)
        {
            if (plan.PropertyPatches == null || plan.PropertyPatches.Count == 0) return source;

            // Validate + collect actionable ops. Add/Remove are not meaningful
            // for span-replacement; warn and skip so callers can mix op kinds
            // freely without surprise mutations.
            var ops = new List<PropertyPatchOp>();
            foreach (var op in plan.PropertyPatches)
            {
                if (op == null) continue;
                if (op.Action == InjectionAction.Preserve) continue;
                if (op.Action == InjectionAction.Add || op.Action == InjectionAction.Remove)
                {
                    Warn(result, op, "PropertyPatchOp only supports Update; use ClassFieldOp/HelperMethodOp for Add/Remove");
                    continue;
                }
                if (op.NewText == null)
                {
                    Warn(result, op, "PropertyPatchOp requires NewText");
                    continue;
                }
                if (op.Start < 0 || op.End < op.Start || op.End > source.Length)
                {
                    Warn(result, op, "PropertyPatchOp span out of range");
                    continue;
                }
                ops.Add(op);
            }
            if (ops.Count == 0) return source;

            // Sort descending by Start so applying edits doesn't invalidate
            // earlier ops' offsets. Detect overlap pairwise on the sorted list.
            ops.Sort((a, b) => b.Start.CompareTo(a.Start));
            for (int i = 0; i < ops.Count - 1; i++)
            {
                // ops[i].Start >= ops[i+1].Start; overlap iff ops[i].Start < ops[i+1].End
                if (ops[i].Start < ops[i + 1].End)
                {
                    Warn(result, ops[i], "PropertyPatchOp overlaps another patch; skipping");
                    ops.RemoveAt(i);
                    i--;
                }
            }

            var buf = new System.Text.StringBuilder(source);
            // Walk descending so each op's pre-edit Start/End remain valid
            // against buf. Edits are recorded in pre-edit coordinates.
            foreach (var op in ops)
            {
                int len = op.End - op.Start;
                string current = buf.ToString(op.Start, len);
                if (op.ExpectedOldText != null && !string.Equals(current, op.ExpectedOldText, StringComparison.Ordinal))
                {
                    Warn(result, op, "PropertyPatchOp ExpectedOldText mismatch; skipping (stale span?)");
                    continue;
                }

                if (string.Equals(current, op.NewText, StringComparison.Ordinal))
                {
                    // No-op: idempotent re-apply against an already-patched buffer.
                    Diff(result, op, true, "no change");
                    continue;
                }

                buf.Remove(op.Start, len);
                buf.Insert(op.Start, op.NewText);

                int delta = op.NewText.Length - len;
                result.Edits.Add(new SourceEdit
                {
                    Start = op.Start,
                    End = op.End,
                    Delta = delta,
                    OpIdentity = op.Identity
                });
                Diff(result, op, true, "patched (delta " + delta + ")");
            }

            return buf.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Misc helpers
        // ─────────────────────────────────────────────────────────────────────

        private static MethodDeclarationSyntax FindMethod(CompilationUnitSyntax root, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == name);
        }

        private static int FindMemberIndex(List<MemberDeclarationSyntax> members, Func<MemberDeclarationSyntax, bool> pred)
        {
            for (int i = 0; i < members.Count; i++) if (pred(members[i])) return i;
            return -1;
        }

        private static int FindStatementIndex(List<StatementSyntax> stmts, Func<StatementSyntax, bool> pred)
        {
            for (int i = 0; i < stmts.Count; i++) if (pred(stmts[i])) return i;
            return -1;
        }

        private static void Warn(InjectionResult result, InjectionOp op, string message)
        {
            result.Warnings.Add(new InjectionWarning
            {
                OpIdentity = op?.Identity,
                AttemptedAction = op?.Action ?? InjectionAction.Preserve,
                Message = message
            });
        }

        private static void Diff(InjectionResult result, InjectionOp op, bool applied, string detail)
        {
            result.Diff.Add(new InjectionDiffEntry
            {
                OpIdentity = op?.Identity,
                Action = op?.Action ?? InjectionAction.Preserve,
                Applied = applied,
                Detail = detail
            });
        }
    }
}
