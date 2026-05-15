using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Injects a <see cref="RigCodeGenerator"/> snippet into the user's source code
    /// idempotently. Surrounds the generated block with marker comments so re-running
    /// replaces the prior region instead of stacking duplicates.
    ///
    /// The injector targets complete-program shapes (PB <c>class Program</c> or any
    /// helper class with a <c>.DrawFrame()</c> method) and inserts the rig block as
    /// private members at class scope. The user is expected to call <c>DrawRig(surface, time)</c>
    /// themselves from their entry point — it opens its own <c>DrawFrame</c>, so it
    /// can't be nested inside an existing one.
    /// </summary>
    public static class RigCodeInjector
    {
        // Marker comments for idempotent re-injection.
        public const string RigStart = "// ──▶ RIG ◀──";
        public const string RigEnd   = "// ──▶ END RIG ◀──";
        public const string WireStart = "// ──▶ RIG WIRE ◀──";
        public const string WireEnd   = "// ──▶ END RIG WIRE ◀──";
        public const string MuteTag   = "// ──▶ RIG-MUTED ◀──";

        public class InjectionResult
        {
            public bool Success { get; set; }
            public string Code { get; set; }
            public string Error { get; set; }
            /// <summary>True when an existing rig region was replaced (vs first-time insert).</summary>
            public bool Replaced { get; set; }
        }

        /// <summary>
        /// Injects a rig snippet generated from <paramref name="layout"/> into
        /// <paramref name="sourceCode"/>. Returns the original code when there is no
        /// rig to emit, or an error result when the source has no obvious anchor.
        /// </summary>
        public static InjectionResult Inject(string sourceCode, LcdLayout layout,
            string methodName = "DrawRig", string surfaceParam = "surface")
        {
            var result = new InjectionResult();
            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                result.Error = "Source code is empty.";
                return result;
            }
            if (layout == null)
            {
                result.Error = "Layout is null.";
                return result;
            }

            try
            {
                bool hadPrior, applied;
                string code = ApplyRig(sourceCode, layout, methodName, surfaceParam,
                    out hadPrior, out applied, out string err);
                if (err != null)
                {
                    result.Error = err;
                    return result;
                }

                result.Code = code;
                result.Success = true;
                result.Replaced = hadPrior;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Injection failed: " + ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Single rig-application pipeline shared by <see cref="Inject"/> and the unified
        /// <c>RoslynAnimationInjector.InjectAnimations</c> path. Strips prior rig regions,
        /// mutes/unmutes bound sprite Adds, wires <c>AddRigSprites</c> into an existing
        /// <c>DrawFrame</c> when present, and inserts the rig runtime region.
        /// <para>Returns the original code unchanged (with any prior rig region stripped)
        /// when the layout has no rig content to emit. <paramref name="applied"/> is true
        /// only when a fresh rig region was inserted.</para>
        /// </summary>
        public static string ApplyRig(string sourceCode, LcdLayout layout,
            string methodName, string surfaceParam,
            out bool hadPrior, out bool applied, out string error)
        {
            hadPrior = false;
            applied = false;
            error = null;

            if (string.IsNullOrEmpty(sourceCode)) return sourceCode ?? string.Empty;
            if (layout == null) return sourceCode;

            // Always strip prior rig artefacts first so re-injection is idempotent and
            // un-rigging (no bones) cleanly removes the previous block.
            string code = StripRigRegion(sourceCode, out hadPrior);
            code = StripWireBlock(code);
            code = UnmuteBoundSpriteAdds(code);

            // Generate fresh snippet; if there's no rig, we're done after the cleanup.
            string snippet;
            try
            {
                snippet = RigCodeGenerator.Generate(layout, methodName, surfaceParam);
            }
            catch (Exception ex)
            {
                error = "Generator failed: " + ex.Message;
                return code;
            }
            if (string.IsNullOrWhiteSpace(snippet)) return code;

            // Mute static frame.Add lines for sprites the rig now owns. Done BEFORE
            // inserting the rig region so the regex only sees the user's own static Add
            // blocks (the rig snippet's emitted Adds carry no ⟦id:GUID⟧ tag).
            var boundIds = RigCodeGenerator.GetBoundSpriteIds(layout);
            if (boundIds != null && boundIds.Count > 0)
                code = MuteBoundSpriteAdds(code, boundIds);

            // Wire AddRigSprites into the user's existing DrawFrame using-block. Run
            // BEFORE inserting the rig region so we don't accidentally target DrawRig's
            // own DrawFrame instead.
            bool wired;
            code = InsertWireBlock(code, out wired);

            // Always strip the standalone DrawRig method from the snippet. The
            // editor's executor matches any `void Foo(IMyTextSurface ...)` signature
            // via _rxSurfaceMethod and invokes it as a top-level render entry point,
            // so leaving DrawRig in place would render the bound sprites a SECOND
            // time per frame — the user's own draw method emits them via the wire
            // block (animated, following bones) and DrawRig emits another copy
            // (sampled at its own time, near rest pose) producing visible ghost
            // duplicates. The wire block is the canonical path; if no DrawFrame
            // could be wired, the user calls AddRigSprites themselves from inside
            // their own using-block, so DrawRig is never the right answer.
            //
            // Reminder: re-inject (▶ Generate Code / animation merge) after toggling
            // this — stripping is a no-op on already-emitted source.
            snippet = StripStandaloneDrawRig(snippet, methodName);

            int insertPos;
            string indent;
            if (!FindInsertionPoint(code, out insertPos, out indent))
            {
                error = "Could not find a class or method to host the rig block.";
                return code;
            }

            var sb = new StringBuilder();
            sb.AppendLine(indent + RigStart);
            foreach (var line in snippet.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) sb.AppendLine();
                else sb.AppendLine(indent + line);
            }
            sb.AppendLine(indent + RigEnd);
            sb.AppendLine();

            code = code.Insert(insertPos, sb.ToString());
            applied = true;
            return code;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Removes any existing rig region delimited by the marker pair.</summary>
        private static string StripRigRegion(string code, out bool found)
        {
            found = false;
            if (string.IsNullOrEmpty(code)) return code ?? string.Empty;

            // Match the start marker through to the end marker, including any leading
            // whitespace on the start line and the trailing newline after the end marker.
            // RegexOptions.Singleline so '.' spans newlines.
            var rx = new Regex(
                @"[ \t]*" + Regex.Escape(RigStart) + @".*?" + Regex.Escape(RigEnd) + @"[ \t]*\r?\n?",
                RegexOptions.Singleline);

            bool didMatch = false;
            string result = rx.Replace(code, m => { didMatch = true; return string.Empty; });
            found = didMatch;
            // Collapse the blank line we typically appended after the region.
            if (found)
            {
                result = Regex.Replace(result, "(\r?\n){3,}", "\n\n");
            }
            return result;
        }

        /// <summary>
        /// Removes a previously-inserted wire block (AddRigSprites + tick) so we can
        /// re-emit it cleanly. Idempotent: a no-op if no wire block is present.
        /// </summary>
        private static string StripWireBlock(string code)
        {
            if (string.IsNullOrEmpty(code)) return code ?? string.Empty;
            var rx = new Regex(
                @"[ \t]*" + Regex.Escape(WireStart) + @".*?" + Regex.Escape(WireEnd) + @"[ \t]*\r?\n?",
                RegexOptions.Singleline);
            return rx.Replace(code, string.Empty);
        }

        /// <summary>
        /// Inserts an "AddRigSprites(frame, _rigTime); _rigTime += RigTimeStep;" block at
        /// the top of the first <c>using (var frame = surface.DrawFrame())</c> body in the
        /// code, wrapped in wire markers for idempotent re-runs. If no such block exists
        /// the code is returned unchanged — DrawRig stays callable manually.
        /// </summary>
        private static string InsertWireBlock(string code, out bool wired)
        {
            wired = false;
            if (string.IsNullOrEmpty(code)) return code ?? string.Empty;

            // Match: using (var frame = something.DrawFrame())  followed by an opening brace.
            var rx = new Regex(
                @"using\s*\(\s*var\s+(?<frame>\w+)\s*=\s*[^)]*?\.DrawFrame\(\s*\)\s*\)\s*\r?\n?\s*\{",
                RegexOptions.Singleline);
            var m = rx.Match(code);
            if (!m.Success) return code;
            wired = true;

            int insertAt = m.Index + m.Length; // just past the opening brace
            string frameName = m.Groups["frame"].Value;
            string indent = GetIndent(code, m.Index) + "    ";

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(indent + WireStart);
            sb.AppendLine(indent + $"AddRigSprites({frameName}, _rigTime);");
            sb.Append(indent + WireEnd);

            return code.Insert(insertAt, sb.ToString());
        }

        /// <summary>
        /// Removes the standalone <c>DrawRig(IMyTextSurface, float)</c> method (and the
        /// comment header that introduces it) from a freshly-generated rig snippet.
        /// Used when the injector has wired <c>AddRigSprites</c> into an existing
        /// <c>DrawFrame</c> — the standalone entry point would otherwise become a
        /// second render method that the editor's executor invokes, producing a
        /// duplicate (and lagging) set of sprites on screen.
        /// </summary>
        private static string StripStandaloneDrawRig(string snippet, string methodName)
        {
            if (string.IsNullOrEmpty(snippet) || string.IsNullOrEmpty(methodName)) return snippet;

            // Anchor on the comment header so we strip the explanatory comments too.
            const string header = "// ── Draw entry point";
            int headerIdx = snippet.IndexOf(header, StringComparison.Ordinal);

            // Locate the method signature.
            var sig = new Regex(
                @"public\s+void\s+" + Regex.Escape(methodName) + @"\s*\(\s*IMyTextSurface\s+\w+\s*,\s*float\s+\w+\s*\)\s*\r?\n?\s*\{",
                RegexOptions.Singleline);
            var ms = sig.Match(snippet);
            if (!ms.Success) return snippet;

            int braceStart = ms.Index + ms.Length - 1; // position of opening '{'
            int depth = 0;
            int i = braceStart;
            for (; i < snippet.Length; i++)
            {
                char c = snippet[i];
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { i++; break; } }
            }
            if (depth != 0) return snippet; // unbalanced — bail without modifying

            int start = headerIdx >= 0 && headerIdx < ms.Index ? headerIdx : ms.Index;

            // Trim a single trailing newline so we don't leave a hole.
            int end = i;
            if (end < snippet.Length && snippet[end] == '\r') end++;
            if (end < snippet.Length && snippet[end] == '\n') end++;

            return snippet.Remove(start, end - start);
        }

        /// <summary>
        /// Comments out static <c>frame.Add(new MySprite // ⟦id:GUID⟧ { … });</c> blocks
        /// whose GUID is in <paramref name="boundIds"/>, and tags them with
        /// <see cref="MuteTag"/> so a later re-injection can revert them.
        /// </summary>
        private static string MuteBoundSpriteAdds(string code, System.Collections.Generic.HashSet<string> boundIds)
        {
            if (string.IsNullOrEmpty(code) || boundIds == null || boundIds.Count == 0) return code;

            // Match a full Add block: from "frame.Add(new MySprite // ⟦id:GUID⟧" through the
            // matching "});" line. The id comment is what makes mute targeted and reversible.
            var rx = new Regex(
                @"(?<lead>[ \t]*)frame\.Add\(new\s+MySprite\s*//\s*⟦id:(?<id>[a-fA-F0-9\-]+)⟧[\s\S]*?\}\);",
                RegexOptions.Singleline);

            return rx.Replace(code, m =>
            {
                string id = m.Groups["id"].Value;
                if (!boundIds.Contains(id)) return m.Value;

                string lead = m.Groups["lead"].Value;
                // Comment every line of the matched block, prefix with mute tag for round-trip.
                var lines = m.Value.Replace("\r\n", "\n").Split('\n');
                var sb = new StringBuilder();
                sb.AppendLine(lead + MuteTag);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    // Preserve original leading whitespace; insert "// " right after it.
                    int j = 0;
                    while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
                    if (i == lines.Length - 1 && line.Length == 0)
                        sb.Append(string.Empty);
                    else
                        sb.AppendLine(line.Substring(0, j) + "// " + line.Substring(j));
                }
                // Remove the trailing newline AppendLine added past the last line so we sit
                // exactly where the original block ended.
                string output = sb.ToString();
                if (output.EndsWith("\r\n")) output = output.Substring(0, output.Length - 2);
                else if (output.EndsWith("\n")) output = output.Substring(0, output.Length - 1);
                return output;
            });
        }

        /// <summary>
        /// Reverses <see cref="MuteBoundSpriteAdds"/>: finds blocks tagged with
        /// <see cref="MuteTag"/> and uncomments them, restoring the original frame.Add.
        /// Run before re-applying mutes so unbinding a sprite re-enables its static draw.
        /// </summary>
        private static string UnmuteBoundSpriteAdds(string code)
        {
            if (string.IsNullOrEmpty(code)) return code ?? string.Empty;

            var rx = new Regex(
                @"[ \t]*" + Regex.Escape(MuteTag) + @"\r?\n(?<body>(?:[ \t]*//[^\n]*\r?\n?)+)",
                RegexOptions.Singleline);

            return rx.Replace(code, m =>
            {
                string body = m.Groups["body"].Value;
                // Strip the "// " prefix from each line, preserving leading whitespace.
                var lines = body.Replace("\r\n", "\n").Split('\n');
                var sb = new StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int j = 0;
                    while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
                    string lead = line.Substring(0, j);
                    string rest = line.Substring(j);
                    if (rest.StartsWith("// ")) rest = rest.Substring(3);
                    else if (rest.StartsWith("//")) rest = rest.Substring(2);
                    if (i < lines.Length - 1) sb.AppendLine(lead + rest);
                    else sb.Append(lead + rest);
                }
                return sb.ToString();
            });
        }

        /// <summary>
        /// Finds the best place to insert the rig block: inside the containing type
        /// of any method that calls <c>.DrawFrame()</c>, falling back to before the
        /// first method or top of file.
        /// </summary>
        private static bool FindInsertionPoint(string code, out int insertPos, out string indent)
        {
            insertPos = -1;
            indent = string.Empty;

            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(code).GetRoot();
            }
            catch
            {
                return false;
            }

            // Prefer: top of the type that hosts a method calling DrawFrame().
            var drawMethod = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.ToString().IndexOf(".DrawFrame(", StringComparison.Ordinal) >= 0);

            if (drawMethod != null)
            {
                var typeDecl = drawMethod.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (typeDecl != null && typeDecl.OpenBraceToken.Span.Length > 0)
                {
                    int afterBrace = typeDecl.OpenBraceToken.Span.End;
                    // Move past the newline that typically follows the opening brace.
                    while (afterBrace < code.Length && code[afterBrace] != '\n') afterBrace++;
                    if (afterBrace < code.Length) afterBrace++;
                    insertPos = afterBrace;
                    indent = GetIndent(code, drawMethod.SpanStart);
                    return true;
                }

                // No enclosing type (helper/DrawLayout style): insert just before the
                // method itself so the rig block lives at file/script scope alongside it.
                insertPos = LineStartOf(code, drawMethod.SpanStart);
                indent = GetIndent(code, drawMethod.SpanStart);
                return true;
            }

            // Fallback: before Program() ctor.
            var ctor = root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == "Program");
            if (ctor != null)
            {
                insertPos = LineStartOf(code, ctor.SpanStart);
                indent = GetIndent(code, ctor.SpanStart);
                return true;
            }

            // Fallback: before first method.
            var firstMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (firstMethod != null)
            {
                insertPos = LineStartOf(code, firstMethod.SpanStart);
                indent = GetIndent(code, firstMethod.SpanStart);
                return true;
            }

            // Last resort: top of file (no indent).
            insertPos = 0;
            indent = string.Empty;
            return true;
        }

        private static int LineStartOf(string code, int pos)
        {
            int i = Math.Min(pos, code.Length);
            while (i > 0 && code[i - 1] != '\n') i--;
            return i;
        }

        private static string GetIndent(string code, int pos)
        {
            int lineStart = LineStartOf(code, pos);
            var sb = new StringBuilder();
            for (int i = lineStart; i < code.Length; i++)
            {
                char c = code[i];
                if (c == ' ' || c == '\t') sb.Append(c);
                else break;
            }
            return sb.ToString();
        }
    }
}
