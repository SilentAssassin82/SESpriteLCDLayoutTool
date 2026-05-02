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
                result.Error = "No class declaration found in source.";
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

        private static SyntaxTriviaList PrependBanner(SyntaxTriviaList existing, string identity)
        {
            var banner = SyntaxFactory.Comment(MakeBanner(identity));
            var eol = SyntaxFactory.EndOfLine(Environment.NewLine);
            // Preserve any existing leading whitespace as the indent for the banner.
            return SyntaxFactory.TriviaList(banner, eol).AddRange(existing);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Field ops
        // ─────────────────────────────────────────────────────────────────────

        private static CompilationUnitSyntax ApplyFieldOps(
            CompilationUnitSyntax root, ClassDeclarationSyntax targetClass,
            InjectionPlan plan, InjectionResult result)
        {
            if (plan.Fields.Count == 0 || targetClass == null) return root;

            var newMembers = targetClass.Members.ToList();

            foreach (var op in plan.Fields)
            {
                if (op.Action == InjectionAction.Preserve) continue;

                int existingIdx = FindMemberIndex(newMembers, m => HasBanner(m, op.Identity));

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

        private static FieldDeclarationSyntax BuildFieldSyntax(ClassFieldOp op, InjectionResult result)
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

        // ─────────────────────────────────────────────────────────────────────
        // Method ops
        // ─────────────────────────────────────────────────────────────────────

        private static CompilationUnitSyntax ApplyMethodOps(
            CompilationUnitSyntax root, ClassDeclarationSyntax targetClass,
            InjectionPlan plan, InjectionResult result)
        {
            if (plan.Methods.Count == 0 || targetClass == null) return root;

            var newMembers = targetClass.Members.ToList();

            foreach (var op in plan.Methods)
            {
                if (op.Action == InjectionAction.Preserve) continue;

                int existingIdx = FindMemberIndex(newMembers, m => HasBanner(m, op.Identity));

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

        private static MethodDeclarationSyntax BuildMethodSyntax(HelperMethodOp op, InjectionResult result)
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

        // ─────────────────────────────────────────────────────────────────────
        // RenderBlock ops
        // ─────────────────────────────────────────────────────────────────────

        private static CompilationUnitSyntax ApplyRenderBlockOps(
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
                            int insertIdx = ChooseInsertIndex(newStatements, op.Placement);
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

        private static BlockSyntax BuildRenderBlock(RenderBlockOp op, InjectionResult result)
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

        private static int ChooseInsertIndex(List<StatementSyntax> stmts, RenderBlockPlacement placement)
        {
            switch (placement)
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

        private static CompilationUnitSyntax ApplySpriteAddOps(
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

        private static BlockSyntax BuildSpriteGroupBlock(string groupIdentity, List<SpriteAddOp> ordered, InjectionResult result)
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
