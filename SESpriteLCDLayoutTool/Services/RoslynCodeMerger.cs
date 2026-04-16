using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Uses Roslyn syntax trees to properly merge sprite code into existing source.
    /// This provides accurate, structure-aware code manipulation that preserves
    /// comments, formatting, expressions, and all surrounding code.
    /// </summary>
    public static class RoslynCodeMerger
    {
        /// <summary>
        /// Result of a merge operation containing the updated code and metadata.
        /// </summary>
        public class MergeResult
        {
            public bool Success { get; set; }
            public string Code { get; set; }
            public string Error { get; set; }
            public int SpritesInserted { get; set; }
            public int SpritesUpdated { get; set; }
        }

        /// <summary>
        /// Inserts new sprite code into the source at the appropriate location.
        /// Finds the last frame.Add() or sprites.Add() call and inserts after it.
        /// Preserves all existing code structure, comments, and formatting.
        /// </summary>
        public static MergeResult InsertSprites(string sourceCode, IEnumerable<SpriteEntry> newSprites, string listVarName = "frame")
        {
            var result = new MergeResult { Success = false };

            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                result.Error = "Source code is empty";
                return result;
            }

            // Only insert sprites that are:
            // 1. Not tracked in source (SourceStart < 0)
            // 2. NOT from execution (those already exist in the code as runtime-generated)
            // 3. Visible (hidden sprites are intentionally excluded from code generation)
            var spritesToInsert = newSprites?
                .Where(s => s.SourceStart < 0 && !s.IsFromExecution && !s.IsHidden)
                .ToList();

            if (spritesToInsert == null || spritesToInsert.Count == 0)
            {
                // Nothing to insert - return original code unchanged
                result.Success = true;
                result.Code = sourceCode;
                return result;
            }

            try
            {
                // Parse the source code into a syntax tree
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = tree.GetRoot();

                // Find the best insertion point
                var insertionInfo = FindInsertionPoint(root, listVarName);
                if (insertionInfo.InsertionPoint == null)
                {
                    // Fallback: try to find any method body
                    insertionInfo = FindAnyMethodInsertionPoint(root);
                }

                if (insertionInfo.InsertionPoint == null)
                {
                    result.Error = "Could not find a suitable insertion point (no frame.Add or method body found)";
                    return result;
                }

                // Generate sprite code for each new sprite
                var spriteStatements = new List<StatementSyntax>();
                foreach (var sprite in spritesToInsert)
                {
                    var spriteCode = GenerateSpriteStatement(sprite, listVarName, insertionInfo.Indentation);
                    var statement = SyntaxFactory.ParseStatement(spriteCode);
                    spriteStatements.Add(statement);
                }

                // Insert the new statements after the insertion point
                var newRoot = root.InsertNodesAfter(insertionInfo.InsertionPoint, spriteStatements);

                // Get the modified code with preserved formatting
                result.Code = newRoot.ToFullString();
                result.Success = true;
                result.SpritesInserted = spritesToInsert.Count;

                // Update source tracking on the inserted sprites
                UpdateSourceTracking(result.Code, spritesToInsert, listVarName);
            }
            catch (Exception ex)
            {
                result.Error = $"Roslyn parse/merge error: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Updates property values in existing sprite code using Roslyn.
        /// Finds the sprite's code block by source position and updates specific properties.
        /// </summary>
        public static MergeResult UpdateSpriteProperties(string sourceCode, SpriteEntry sprite)
        {
            var result = new MergeResult { Success = false };

            if (string.IsNullOrWhiteSpace(sourceCode) || sprite == null)
            {
                result.Error = "Invalid parameters";
                return result;
            }

            if (sprite.SourceStart < 0 || sprite.SourceEnd <= sprite.SourceStart)
            {
                result.Error = "Sprite has no source tracking";
                return result;
            }

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = tree.GetRoot();

                // Find the syntax node at the sprite's source position
                var targetNode = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(sprite.SourceStart, sprite.SourceEnd - sprite.SourceStart));
                
                if (targetNode == null)
                {
                    result.Error = "Could not find sprite code at tracked position";
                    return result;
                }

                // Find the object initializer expression
                var initializer = targetNode.DescendantNodes()
                    .OfType<InitializerExpressionSyntax>()
                    .FirstOrDefault();

                if (initializer == null)
                {
                    // Try to find it in parent nodes
                    initializer = targetNode.Ancestors()
                        .SelectMany(a => a.DescendantNodes())
                        .OfType<InitializerExpressionSyntax>()
                        .FirstOrDefault(i => i.SpanStart >= sprite.SourceStart && i.Span.End <= sprite.SourceEnd);
                }

                if (initializer == null)
                {
                    result.Error = "Could not find object initializer in sprite code";
                    return result;
                }

                // Update the initializer with new property values
                var newInitializer = UpdateInitializerProperties(initializer, sprite);
                var newRoot = root.ReplaceNode(initializer, newInitializer);

                result.Code = newRoot.ToFullString();
                result.Success = true;
                result.SpritesUpdated = 1;
            }
            catch (Exception ex)
            {
                result.Error = $"Roslyn update error: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Detects the list variable name used in the code (frame, sprites, list, etc.)
        /// </summary>
        public static string DetectListVariable(string sourceCode)
        {
            if (string.IsNullOrWhiteSpace(sourceCode))
                return "frame";

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = tree.GetRoot();

                // Look for .Add(new MySprite patterns
                var invocations = root.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                                  ma.Name.Identifier.Text == "Add");

                foreach (var inv in invocations)
                {
                    if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        // Check if argument is new MySprite
                        var args = inv.ArgumentList.Arguments;
                        if (args.Count > 0 && args[0].ToString().Contains("MySprite"))
                        {
                            // Get the identifier (frame, sprites, list, etc.)
                            if (memberAccess.Expression is IdentifierNameSyntax identifier)
                            {
                                return identifier.Identifier.Text;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore parse errors
            }

            return "frame";
        }

        // ── Internal helpers ───────────────────────────────────────────────────────

        private class InsertionPointInfo
        {
            public SyntaxNode InsertionPoint { get; set; }
            public string Indentation { get; set; } = "            ";
        }

        private static InsertionPointInfo FindInsertionPoint(SyntaxNode root, string listVarName)
        {
            var info = new InsertionPointInfo();

            // Find all .Add() invocations on the list variable
            var addCalls = root.DescendantNodes()
                .OfType<ExpressionStatementSyntax>()
                .Where(stmt =>
                {
                    if (stmt.Expression is InvocationExpressionSyntax inv &&
                        inv.Expression is MemberAccessExpressionSyntax ma &&
                        ma.Name.Identifier.Text == "Add")
                    {
                        // Check if it's our list variable
                        if (ma.Expression is IdentifierNameSyntax id)
                            return string.Equals(id.Identifier.Text, listVarName, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(id.Identifier.Text, "frame", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(id.Identifier.Text, "sprites", StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                })
                .ToList();

            if (addCalls.Count > 0)
            {
                // Use the last Add call as insertion point
                var lastAdd = addCalls.Last();
                info.InsertionPoint = lastAdd;
                info.Indentation = GetIndentation(lastAdd);
            }

            return info;
        }

        private static InsertionPointInfo FindAnyMethodInsertionPoint(SyntaxNode root)
        {
            var info = new InsertionPointInfo();

            // Find any method that might be a render method
            var methods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Body != null && m.Body.Statements.Count > 0);

            foreach (var method in methods)
            {
                // Prefer methods with List<MySprite> or IMyTextSurface parameters
                var hasListParam = method.ParameterList.Parameters
                    .Any(p => p.Type?.ToString().Contains("List") == true ||
                              p.Type?.ToString().Contains("IMyTextSurface") == true);

                if (hasListParam || method.Identifier.Text.Contains("Render") ||
                    method.Identifier.Text.Contains("Draw") || method.Identifier.Text.Contains("Build"))
                {
                    // Insert after the last statement in the method body
                    var lastStatement = method.Body.Statements.Last();
                    info.InsertionPoint = lastStatement;
                    info.Indentation = GetIndentation(lastStatement);
                    return info;
                }
            }

            // Fallback: just use the last statement in any method
            var anyMethod = methods.FirstOrDefault();
            if (anyMethod != null)
            {
                var lastStatement = anyMethod.Body.Statements.Last();
                info.InsertionPoint = lastStatement;
                info.Indentation = GetIndentation(lastStatement);
            }

            return info;
        }

        private static string GetIndentation(SyntaxNode node)
        {
            var leadingTrivia = node.GetLeadingTrivia();
            foreach (var trivia in leadingTrivia.Reverse())
            {
                if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    return trivia.ToString();
                }
            }
            return "            "; // Default 3-level indent
        }

        private static string GenerateSpriteStatement(SpriteEntry sprite, string listVarName, string indent)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.Append(indent);
            sb.Append(listVarName);
            sb.AppendLine(".Add(new MySprite {");

            string innerIndent = indent + "    ";

            // Type
            sb.Append(innerIndent);
            sb.Append("Type = SpriteType.");
            sb.Append(sprite.Type == SpriteEntryType.Text ? "TEXT" : "TEXTURE");
            sb.AppendLine(",");

            // Data
            sb.Append(innerIndent);
            sb.Append("Data = \"");
            sb.Append(sprite.Type == SpriteEntryType.Text ? EscapeString(sprite.Text ?? "") : EscapeString(sprite.SpriteName ?? "SquareSimple"));
            sb.AppendLine("\",");

            // Position
            sb.Append(innerIndent);
            sb.Append("Position = new Vector2(");
            sb.Append(sprite.X.ToString("F1"));
            sb.Append("f, ");
            sb.Append(sprite.Y.ToString("F1"));
            sb.AppendLine("f),");

            // Size
            sb.Append(innerIndent);
            sb.Append("Size = new Vector2(");
            sb.Append(sprite.Width.ToString("F1"));
            sb.Append("f, ");
            sb.Append(sprite.Height.ToString("F1"));
            sb.AppendLine("f),");

            // Color
            sb.Append(innerIndent);
            sb.Append("Color = new Color(");
            sb.Append(sprite.ColorR);
            sb.Append(", ");
            sb.Append(sprite.ColorG);
            sb.Append(", ");
            sb.Append(sprite.ColorB);
            if (sprite.ColorA != 255)
            {
                sb.Append(", ");
                sb.Append(sprite.ColorA);
            }
            sb.AppendLine(",");

            // RotationOrScale (text uses scale, texture uses rotation)
            if (sprite.Type == SpriteEntryType.Text)
            {
                sb.Append(innerIndent);
                sb.Append("RotationOrScale = ");
                sb.Append(sprite.Scale.ToString("F2"));
                sb.AppendLine("f,");

                // Font
                if (!string.IsNullOrEmpty(sprite.FontId))
                {
                    sb.Append(innerIndent);
                    sb.Append("FontId = \"");
                    sb.Append(sprite.FontId);
                    sb.AppendLine("\",");
                }

                // Alignment
                sb.Append(innerIndent);
                sb.Append("Alignment = TextAlignment.");
                sb.Append(sprite.Alignment.ToString().ToUpperInvariant());
                sb.AppendLine(",");
            }
            else
            {
                // Always emit Alignment and RotationOrScale for texture sprites —
                // omitting them loses data when the sprite is later animated or
                // round-tripped through code merge.
                sb.Append(innerIndent);
                sb.AppendLine("Alignment = TextAlignment.CENTER,");
                sb.Append(innerIndent);
                sb.Append("RotationOrScale = ");
                sb.Append(sprite.Rotation.ToString("F4"));
                sb.AppendLine("f,");
            }

            sb.Append(indent);
            sb.Append("});");

            return sb.ToString();
        }

        private static string EscapeString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static void UpdateSourceTracking(string newCode, List<SpriteEntry> sprites, string listVarName)
        {
            // After insertion, find the new positions of inserted sprites
            // This allows future edits to track them
            try
            {
                var tree = CSharpSyntaxTree.ParseText(newCode);
                var root = tree.GetRoot();

                var addCalls = root.DescendantNodes()
                    .OfType<ExpressionStatementSyntax>()
                    .Where(stmt =>
                        stmt.Expression is InvocationExpressionSyntax inv &&
                        inv.Expression is MemberAccessExpressionSyntax ma &&
                        ma.Name.Identifier.Text == "Add")
                    .ToList();

                // Match sprites to their code by position/properties
                // This is approximate - we match by the last N add calls
                int addIndex = addCalls.Count - sprites.Count;
                foreach (var sprite in sprites)
                {
                    if (addIndex >= 0 && addIndex < addCalls.Count)
                    {
                        var stmt = addCalls[addIndex];
                        sprite.SourceStart = stmt.SpanStart;
                        sprite.SourceEnd = stmt.Span.End;
                        sprite.ImportBaseline = sprite.CloneValues();
                        addIndex++;
                    }
                }
            }
            catch
            {
                // If tracking fails, leave sprites untracked
            }
        }

        private static InitializerExpressionSyntax UpdateInitializerProperties(
            InitializerExpressionSyntax initializer,
            SpriteEntry sprite)
        {
            var newExpressions = new List<ExpressionSyntax>();

            foreach (var expr in initializer.Expressions)
            {
                if (expr is AssignmentExpressionSyntax assignment &&
                    assignment.Left is IdentifierNameSyntax propName)
                {
                    string name = propName.Identifier.Text;
                    ExpressionSyntax newValue = null;

                    // Update properties based on sprite values
                    switch (name)
                    {
                        case "Position":
                            if (sprite.ImportBaseline != null &&
                                (Math.Abs(sprite.X - sprite.ImportBaseline.X) > 0.05f ||
                                 Math.Abs(sprite.Y - sprite.ImportBaseline.Y) > 0.05f))
                            {
                                newValue = SyntaxFactory.ParseExpression(
                                    $"new Vector2({sprite.X:F1}f, {sprite.Y:F1}f)");
                            }
                            break;

                        case "Size":
                            if (sprite.ImportBaseline != null &&
                                (Math.Abs(sprite.Width - sprite.ImportBaseline.Width) > 0.05f ||
                                 Math.Abs(sprite.Height - sprite.ImportBaseline.Height) > 0.05f))
                            {
                                newValue = SyntaxFactory.ParseExpression(
                                    $"new Vector2({sprite.Width:F1}f, {sprite.Height:F1}f)");
                            }
                            break;

                        case "Color":
                            if (sprite.ImportBaseline != null &&
                                (sprite.ColorR != sprite.ImportBaseline.ColorR ||
                                  sprite.ColorG != sprite.ImportBaseline.ColorG ||
                                  sprite.ColorB != sprite.ImportBaseline.ColorB ||
                                  sprite.ColorA != sprite.ImportBaseline.ColorA))
                            {
                                newValue = sprite.ColorA != 255
                                    ? SyntaxFactory.ParseExpression($"new Color({sprite.ColorR}, {sprite.ColorG}, {sprite.ColorB}, {sprite.ColorA})")
                                    : SyntaxFactory.ParseExpression($"new Color({sprite.ColorR}, {sprite.ColorG}, {sprite.ColorB})");
                            }
                            break;

                        case "Data":
                            // For TEXT sprites, Data contains the text content
                            // For TEXTURE sprites, Data contains the sprite name
                            if (sprite.ImportBaseline != null)
                            {
                                string currentData = sprite.Type == SpriteEntryType.Text ? sprite.Text : sprite.SpriteName;
                                string baselineData = sprite.ImportBaseline.Type == SpriteEntryType.Text 
                                    ? sprite.ImportBaseline.Text 
                                    : sprite.ImportBaseline.SpriteName;

                                if (currentData != baselineData)
                                {
                                    string escaped = EscapeString(currentData ?? "");
                                    newValue = SyntaxFactory.ParseExpression("\"" + escaped + "\"");
                                }
                            }
                            break;
                    }

                    if (newValue != null)
                    {
                        // Create updated assignment
                        var newAssignment = assignment.WithRight(newValue);
                        newExpressions.Add(newAssignment);
                        continue;
                    }
                }

                // Keep original expression
                newExpressions.Add(expr);
            }

            return initializer.WithExpressions(
                SyntaxFactory.SeparatedList(newExpressions, 
                    Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken), newExpressions.Count - 1)));
        }
    }
}
