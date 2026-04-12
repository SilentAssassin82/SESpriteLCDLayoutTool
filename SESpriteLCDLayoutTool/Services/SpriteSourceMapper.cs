using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Represents a single sprite creation location found in source code.
    /// Used to map runtime sprites back to their exact source location.
    /// </summary>
    public class SpriteSourceLocation
    {
        /// <summary>Method containing this sprite creation.</summary>
        public string MethodName { get; set; }

        /// <summary>Line number (1-based) where the sprite is created.</summary>
        public int LineNumber { get; set; }

        /// <summary>Character position (0-based) within the line.</summary>
        public int CharPosition { get; set; }

        /// <summary>
        /// Index of this sprite creation within the method (0-based).
        /// Sprites are indexed in order of appearance in the source code.
        /// </summary>
        public int CreationIndex { get; set; }

        /// <summary>The code snippet (e.g. "frame.Add(new MySprite(...))").</summary>
        public string CodeSnippet { get; set; }

        /// <summary>Full text span in the document (for precise navigation).</summary>
        public int SpanStart { get; set; }
        public int SpanLength { get; set; }
    }

    /// <summary>
    /// Uses Roslyn to parse source code and map sprite creation calls to their exact locations.
    /// This enables precise code navigation when users click sprites in the layer list.
    /// </summary>
    public static class SpriteSourceMapper
    {
        /// <summary>
        /// Parses source code and builds a map of sprite creation locations grouped by method.
        /// </summary>
        /// <param name="sourceCode">The user's C# source code</param>
        /// <returns>Dictionary mapping method names to ordered lists of sprite creation locations</returns>
        public static Dictionary<string, List<SpriteSourceLocation>> MapSpriteCreationSites(string sourceCode)
        {
            var result = new Dictionary<string, List<SpriteSourceLocation>>();

            if (string.IsNullOrWhiteSpace(sourceCode))
                return result;

            try
            {
                // Parse the source code into a syntax tree
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = tree.GetCompilationUnitRoot();

                // Find all method declarations
                var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

                foreach (var method in methods)
                {
                    string methodName = method.Identifier.Text;
                    var spriteLocations = new List<SpriteSourceLocation>();

                    // Find all sprite creation expressions within this method
                    var creations = FindSpriteCreations(method);

                    int index = 0;
                    foreach (var creation in creations)
                    {
                        var location = creation.GetLocation();
                        var lineSpan = location.GetLineSpan();
                        var span = creation.Span;

                        spriteLocations.Add(new SpriteSourceLocation
                        {
                            MethodName = methodName,
                            LineNumber = lineSpan.StartLinePosition.Line + 1, // Convert to 1-based
                            CharPosition = lineSpan.StartLinePosition.Character,
                            CreationIndex = index++,
                            CodeSnippet = creation.ToString().Trim(),
                            SpanStart = span.Start,
                            SpanLength = span.Length
                        });
                    }

                    if (spriteLocations.Count > 0)
                    {
                        result[methodName] = spriteLocations;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[SpriteSourceMapper] Mapped {result.Sum(kvp => kvp.Value.Count)} sprite creation sites across {result.Count} methods");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpriteSourceMapper] Error parsing source: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Finds all sprite creation expressions within a method.
        /// Looks for: new MySprite(...), frame.Add(...), sprites.Add(...), AddTextSprite(...), etc.
        /// </summary>
        private static List<SyntaxNode> FindSpriteCreations(MethodDeclarationSyntax method)
        {
            return FindSpriteCreationsInNode(method);
        }

        /// <summary>
        /// Attempts to match a runtime sprite to its source location by method name and creation index.
        /// </summary>
        /// <param name="methodName">The method that created the sprite</param>
        /// <param name="creationIndex">The index of the sprite within that method (0-based)</param>
        /// <param name="sourceMap">The map built by MapSpriteCreationSites</param>
        /// <returns>The source location, or null if not found</returns>
        public static SpriteSourceLocation GetSourceLocation(
            string methodName,
            int creationIndex,
            Dictionary<string, List<SpriteSourceLocation>> sourceMap)
        {
            if (sourceMap == null || string.IsNullOrEmpty(methodName))
                return null;

            if (sourceMap.TryGetValue(methodName, out var locations))
            {
                if (creationIndex >= 0 && creationIndex < locations.Count)
                {
                    return locations[creationIndex];
                }
            }

            return null;
        }

        /// <summary>
        /// Maps sprite creation sites within a specific case block.
        /// Used for case-based virtual methods like "RenderHeader" extracted from switch statements.
        /// </summary>
        /// <param name="sourceCode">The user's C# source code</param>
        /// <param name="caseName">The case name (e.g., "Header" for "case Header:")</param>
        /// <returns>List of sprite creation locations within that case block, or null if not found</returns>
        public static List<SpriteSourceLocation> MapCaseBlockSpriteCreations(string sourceCode, string caseName)
        {
            if (string.IsNullOrWhiteSpace(sourceCode) || string.IsNullOrWhiteSpace(caseName))
                return null;

            try
            {
                // Parse the source code into a syntax tree
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = tree.GetCompilationUnitRoot();

                // Find all switch statements
                var switchStatements = root.DescendantNodes().OfType<SwitchStatementSyntax>();

                foreach (var switchStmt in switchStatements)
                {
                    // Find the case section matching our case name
                    foreach (var section in switchStmt.Sections)
                    {
                        foreach (var label in section.Labels)
                        {
                            if (label is CaseSwitchLabelSyntax caseLabel)
                            {
                                // Extract the case value (e.g., "Header" from "case Header:" or "case LcdSpriteRow.Kind.Header:")
                                string caseValue = ExtractCaseValue(caseLabel);

                                if (caseValue == caseName)
                                {
                                    // Found the matching case block! Now find sprite creations within it
                                    var spriteLocations = new List<SpriteSourceLocation>();
                                    var creations = FindSpriteCreationsInNode(section);

                                    int index = 0;
                                    foreach (var creation in creations)
                                    {
                                        var location = creation.GetLocation();
                                        var lineSpan = location.GetLineSpan();
                                        var span = creation.Span;

                                        spriteLocations.Add(new SpriteSourceLocation
                                        {
                                            MethodName = "Render" + caseName, // Virtual method name
                                            LineNumber = lineSpan.StartLinePosition.Line + 1,
                                            CharPosition = lineSpan.StartLinePosition.Character,
                                            CreationIndex = index++,
                                            CodeSnippet = creation.ToString().Trim(),
                                            SpanStart = span.Start,
                                            SpanLength = span.Length
                                        });
                                    }

                                    System.Diagnostics.Debug.WriteLine($"[SpriteSourceMapper] Found {spriteLocations.Count} sprite creations in case {caseName}");
                                    return spriteLocations;
                                }
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[SpriteSourceMapper] Case block '{caseName}' not found in source");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpriteSourceMapper] Error parsing case block: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Extracts the case value from a case label.
        /// Handles both simple cases (case Header:) and qualified cases (case LcdSpriteRow.Kind.Header:)
        /// </summary>
        private static string ExtractCaseValue(CaseSwitchLabelSyntax caseLabel)
        {
            var value = caseLabel.Value;

            // Handle qualified names like "LcdSpriteRow.Kind.Header" → extract "Header"
            if (value is MemberAccessExpressionSyntax memberAccess)
            {
                return memberAccess.Name.Identifier.Text;
            }

            // Handle simple names like "Header"
            if (value is IdentifierNameSyntax identifier)
            {
                return identifier.Identifier.Text;
            }

            return null;
        }

        /// <summary>
        /// Finds all sprite creation expressions within a syntax node (method or case block).
        /// </summary>
        private static List<SyntaxNode> FindSpriteCreationsInNode(SyntaxNode node)
        {
            var creations = new List<SyntaxNode>();

            var invocations = node.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                // Check if this is an .Add() call
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    if (memberAccess.Name.Identifier.Text == "Add")
                    {
                        creations.Add(invocation);
                        continue;
                    }
                }

                // Check for helper methods
                if (invocation.Expression is IdentifierNameSyntax identifier)
                {
                    string name = identifier.Identifier.Text;
                    if (name.Contains("Sprite") || name.StartsWith("Add"))
                    {
                        creations.Add(invocation);
                        continue;
                    }
                }
            }

            // Find MySprite object creations
            var objectCreations = node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
            foreach (var objCreation in objectCreations)
            {
                if (objCreation.Type.ToString().Contains("MySprite"))
                {
                    if (!creations.Any(c => c.Span.Contains(objCreation.Span)))
                    {
                        creations.Add(objCreation);
                    }
                }
            }

            // Sort by position in source
            return creations.OrderBy(c => c.SpanStart).ToList();
        }

        /// <summary>
        /// Applies source location metadata to a sprite entry.
        /// </summary>
        public static void ApplySourceLocation(Models.SpriteEntry sprite, SpriteSourceLocation location)
        {
            if (sprite == null || location == null)
                return;

            sprite.SourceLineNumber = location.LineNumber;
            sprite.SourceCharacterPosition = location.CharPosition;
            sprite.SourceCodeSnippet = location.CodeSnippet;
            sprite.SourceStart = location.SpanStart;
            sprite.SourceEnd = location.SpanStart + location.SpanLength;
        }
    }
}
