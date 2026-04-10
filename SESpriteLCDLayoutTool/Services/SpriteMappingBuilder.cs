using System;
using System.Collections.Generic;
using System.Linq;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Builds an ElementSpriteMapping by running N frames of animation
    /// and collecting all sprites produced by each method across the animation lifecycle.
    /// </summary>
    public static class SpriteMappingBuilder
    {
        /// <summary>
        /// Default number of frames to capture when building the mapping.
        /// Should be enough to capture most animation states without excessive delay.
        /// </summary>
        public const int DefaultFrameCount = 30;

        /// <summary>
        /// Result of building a sprite mapping.
        /// </summary>
        public class BuildResult
        {
            public ElementSpriteMapping Mapping { get; set; }
            public string Error { get; set; }
            public int FramesExecuted { get; set; }
            public int TotalSpritesCollected { get; set; }
            public bool Success => string.IsNullOrEmpty(Error);
        }

        /// <summary>
        /// Builds an element-sprite mapping by running each detected method individually
        /// and collecting the sprites it produces in isolation.
        /// </summary>
        /// <param name="userCode">The user's source code</param>
        /// <param name="callExpression">Optional call expression (null for auto-detect all)</param>
        /// <param name="frameCount">Number of frames to run per method</param>
        /// <param name="capturedRows">Optional snapshot data for realistic rendering</param>
        /// <returns>BuildResult containing the mapping or error information</returns>
        public static BuildResult BuildMapping(
            string userCode,
            string callExpression = null,
            int frameCount = DefaultFrameCount,
            List<SnapshotRowData> capturedRows = null)
        {
            var result = new BuildResult
            {
                Mapping = new ElementSpriteMapping()
            };

            if (string.IsNullOrWhiteSpace(userCode))
            {
                result.Error = "No code provided.";
                return result;
            }

            // STEP 1: Detect all rendering methods
            System.Diagnostics.Debug.WriteLine($"[SpriteMappingBuilder] === STEP 1: Detect Entry Points ===");
            var allCalls = CodeExecutor.DetectAllCallExpressions(userCode, capturedRows);
            System.Diagnostics.Debug.WriteLine($"DetectAllCallExpressions returned {allCalls.Count} entry points:");
            foreach (var c in allCalls)
                System.Diagnostics.Debug.WriteLine($"  - {c}");

            if (allCalls.Count == 0)
            {
                result.Error = "No rendering entry points detected.";
                return result;
            }

            // STEP 2: Run each method individually and collect its sprites
            System.Diagnostics.Debug.WriteLine($"\n[SpriteMappingBuilder] === STEP 2: Run Each Method Individually ===");
            int totalSprites = 0;
            int successfulMethods = 0;

            // STEP 2a: First run the FULL orchestrator across multiple frames to capture all sprite positions
            System.Diagnostics.Debug.WriteLine($"\n[SpriteMappingBuilder] === STEP 2a: Run Full Orchestrator ===");
            CodeExecutor.AnimationContext fullCtx = null;
            // INDEX BY X POSITION (the invariant!) - X doesn't change between isolated and full scene, only Y does
            var fullSceneSpritesByX = new Dictionary<int, List<SpriteEntry>>();
            try
            {
                // Run full orchestrator (null call) to get sprites with correct positions
                fullCtx = CodeExecutor.CompileForAnimation(userCode, null, capturedRows);
                CodeExecutor.InitAnimation(fullCtx);

                // Run multiple frames to capture animated sprites
                for (int frame = 0; frame < frameCount; frame++)
                {
                    double elapsed = frame * 0.016;
                    var fullResult = CodeExecutor.RunAnimationFrame(fullCtx, 32, frame, elapsed);
                    if (fullResult.Success && fullResult.Sprites != null)
                    {
                        foreach (var sprite in fullResult.Sprites)
                        {
                            // Index by X position (rounded to nearest pixel)
                            // X is the invariant - it doesn't change between isolated and full scene!
                            int xKey = (int)Math.Round(sprite.X);

                            if (!fullSceneSpritesByX.ContainsKey(xKey))
                                fullSceneSpritesByX[xKey] = new List<SpriteEntry>();

                            // Only add if not already present (based on exact position)
                            bool exists = fullSceneSpritesByX[xKey].Any(s => 
                                Math.Abs(s.X - sprite.X) < 1f && Math.Abs(s.Y - sprite.Y) < 1f);
                            if (!exists)
                                fullSceneSpritesByX[xKey].Add(sprite);
                        }
                    }
                }

                int totalUnique = fullSceneSpritesByX.Sum(kvp => kvp.Value.Count);
                System.Diagnostics.Debug.WriteLine($"Full orchestrator produced {totalUnique} unique sprites across {frameCount} frames");
                System.Diagnostics.Debug.WriteLine($"Indexed by {fullSceneSpritesByX.Count} unique X positions (O(1) lookup!)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Full orchestrator failed: {ex.Message}");
            }
            finally
            {
                fullCtx?.Dispose();
            }

            foreach (var call in allCalls)
            {
                string methodName = ExtractMethodName(call);
                if (string.IsNullOrEmpty(methodName))
                {
                    System.Diagnostics.Debug.WriteLine($"  Skipping (can't extract method name): {call}");
                    continue;
                }

                System.Diagnostics.Debug.WriteLine($"\n  Running: {methodName}...");

                CodeExecutor.AnimationContext ctx = null;
                try
                {
                    // Compile and run THIS specific method in isolation
                    ctx = CodeExecutor.CompileForAnimation(userCode, call, capturedRows);
                    CodeExecutor.InitAnimation(ctx);

                    // Collect unique sprites from this method across animation frames
                    var uniqueSprites = new HashSet<string>();
                    var methodSprites = new List<SpriteEntry>();

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        double elapsed = frame * 0.016;
                        var frameResult = CodeExecutor.RunAnimationFrame(ctx, 32, frame, elapsed);

                        if (!string.IsNullOrEmpty(frameResult.Error))
                        {
                            System.Diagnostics.Debug.WriteLine($"    Frame {frame} error: {frameResult.Error}");
                            break;
                        }

                        if (frameResult.Sprites != null)
                        {
                            foreach (var sprite in frameResult.Sprites)
                            {
                                // Create signature based on type + name/text + approximate position
                                // Position helps distinguish sprites that use same texture in different locations
                                string signature = $"{sprite.Type}|{sprite.SpriteName}|{sprite.Text}|{(int)(sprite.X / 10) * 10},{(int)(sprite.Y / 10) * 10}";
                                if (!uniqueSprites.Contains(signature))
                                {
                                    uniqueSprites.Add(signature);
                                    methodSprites.Add(sprite);
                                }
                            }
                        }
                    }

                    // Add all sprites from this method to the mapping
                    foreach (var sprite in methodSprites)
                    {
                        result.Mapping.AddSprite(methodName, sprite);
                        totalSprites++;
                    }

                    // Calculate Y offset by EXACT PROPERTY MATCHING with O(1) X-coordinate lookup
                    // Match isolated sprites to full-scene sprites by comparing ALL properties EXCEPT Y
                    if (fullSceneSpritesByX != null && fullSceneSpritesByX.Count > 0 && methodSprites.Count > 0)
                    {
                        // Match each isolated sprite to its full-scene counterpart
                        var matchedOffsets = new List<float>();
                        int matchedCount = 0;

                        foreach (var isolatedSprite in methodSprites)
                        {
                            // O(1) lookup: find all sprites at the same X coordinate
                            int xKey = (int)Math.Round(isolatedSprite.X);

                            if (fullSceneSpritesByX.TryGetValue(xKey, out var candidatesAtX))
                            {
                                // Now search only the 5-10 sprites at this X position (not 1700+ sprites!)
                                SpriteEntry bestMatch = null;
                                foreach (var fullSprite in candidatesAtX)
                                {
                                    if (SpritesMatchExceptY(isolatedSprite, fullSprite))
                                    {
                                        bestMatch = fullSprite;
                                        break; // Found exact match
                                    }
                                }

                                if (bestMatch != null)
                                {
                                    float offset = bestMatch.Y - isolatedSprite.Y;
                                    matchedOffsets.Add(offset);
                                    matchedCount++;
                                }
                            }
                        }

                        if (matchedOffsets.Count > 0)
                        {
                            // Take the median offset (more robust than mean against outliers)
                            matchedOffsets.Sort();
                            float yOffset = matchedOffsets[matchedOffsets.Count / 2];

                            float isolatedMinY = methodSprites.Min(s => s.Y);
                            float fullSceneMinY = isolatedMinY + yOffset;

                            result.Mapping.MethodYOffsets[methodName] = yOffset;
                            System.Diagnostics.Debug.WriteLine($"    → Isolated Y: {isolatedMinY:F1}, Full scene Y: {fullSceneMinY:F1}, Offset: {yOffset:F1} ({matchedCount}/{methodSprites.Count} exact matches)");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"    → WARNING: Could not find exact property matches in full scene");
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"    → Collected {methodSprites.Count} unique sprites");
                    successfulMethods++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"    × Error: {ex.Message}");
                    // Continue with other methods
                }
                finally
                {
                    ctx?.Dispose();
                }
            }

            System.Diagnostics.Debug.WriteLine($"\n[SpriteMappingBuilder] === FINAL RESULTS ===");
            System.Diagnostics.Debug.WriteLine($"Successfully mapped {successfulMethods} methods");
            System.Diagnostics.Debug.WriteLine($"Total sprite attributions: {totalSprites}");
            System.Diagnostics.Debug.WriteLine($"Method breakdown:");
            foreach (var kvp in result.Mapping.MethodToSprites.OrderBy(kvp => kvp.Key))
            {
                System.Diagnostics.Debug.WriteLine($"  - {kvp.Key}: {kvp.Value.Count} unique sprites");
            }

            result.TotalSpritesCollected = totalSprites;
            result.FramesExecuted = frameCount * successfulMethods;
            result.Mapping.FramesCaptured = frameCount;
            result.Mapping.LastBuilt = DateTime.Now;

            return result;
        }

        /// <summary>
        /// Checks if two sprites are identical EXCEPT for Y position.
        /// This allows us to match isolated sprites to their full-scene counterparts deterministically.
        /// </summary>
        private static bool SpritesMatchExceptY(SpriteEntry isolated, SpriteEntry fullScene)
        {
            // Type must match (Texture vs Text)
            if (isolated.Type != fullScene.Type) return false;

            // Sprite name/text must match
            if (isolated.Type == SpriteEntryType.Texture)
            {
                if (isolated.SpriteName != fullScene.SpriteName) return false;
            }
            else // Text
            {
                if (isolated.Text != fullScene.Text) return false;
                if (isolated.FontId != fullScene.FontId) return false;
            }

            // X position must match (horizontal position doesn't change)
            if (Math.Abs(isolated.X - fullScene.X) > 1f) return false;

            // Size must match
            if (Math.Abs(isolated.Width - fullScene.Width) > 1f) return false;
            if (Math.Abs(isolated.Height - fullScene.Height) > 1f) return false;

            // Color must match
            if (isolated.ColorR != fullScene.ColorR) return false;
            if (isolated.ColorG != fullScene.ColorG) return false;
            if (isolated.ColorB != fullScene.ColorB) return false;
            if (isolated.ColorA != fullScene.ColorA) return false;

            // Rotation/Scale must match
            if (Math.Abs(isolated.Rotation - fullScene.Rotation) > 0.01f) return false;

            // All properties match except Y → This is the same sprite!
            return true;
        }

        /// <summary>
        /// Extracts the method name from a call expression like "RenderPanel(sprites, ...)"
        /// or "sprites = BuildSprites(...)".
        /// </summary>
        private static string ExtractMethodName(string callExpression)
        {
            if (string.IsNullOrWhiteSpace(callExpression)) return null;

            int parenPos = callExpression.IndexOf('(');
            if (parenPos <= 0) return null;

            string beforeParen = callExpression.Substring(0, parenPos).Trim();
            int eqPos = beforeParen.LastIndexOf('=');
            if (eqPos >= 0)
                beforeParen = beforeParen.Substring(eqPos + 1).Trim();

            return beforeParen;
        }

        /// <summary>
        /// Gets the path for the mapping file associated with a layout file.
        /// </summary>
        public static string GetMappingFilePath(string layoutFilePath)
        {
            if (string.IsNullOrEmpty(layoutFilePath))
                return null;

            // Store alongside the layout file with .sprmap extension
            return System.IO.Path.ChangeExtension(layoutFilePath, ".sprmap");
        }

        /// <summary>
        /// Saves a mapping to the appropriate location for a layout file.
        /// </summary>
        public static void SaveMapping(ElementSpriteMapping mapping, string layoutFilePath)
        {
            var path = GetMappingFilePath(layoutFilePath);
            if (path != null)
            {
                mapping.SaveToFile(path);
            }
        }

        /// <summary>
        /// Loads a mapping for a layout file if it exists.
        /// </summary>
        public static ElementSpriteMapping LoadMapping(string layoutFilePath)
        {
            var path = GetMappingFilePath(layoutFilePath);
            if (path != null && System.IO.File.Exists(path))
            {
                return ElementSpriteMapping.LoadFromFile(path);
            }
            return null;
        }
    }
}
