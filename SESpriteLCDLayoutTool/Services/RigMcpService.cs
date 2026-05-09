using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using SESpriteLCDLayoutTool.Models;
using SESpriteLCDLayoutTool.Models.Rig;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Stateless headless helpers exposed to the MCP server. Each method takes file
    /// paths (so the LLM owns the data on disk), performs a single deterministic
    /// transform, and returns a single-line JSON result via <c>ToJson()</c>.
    ///
    /// All results follow the same envelope: <c>{"success":bool,"error":string|null, ... }</c>
    /// so the MCP client can branch on a single field. No external JSON dependency.
    /// </summary>
    public static class RigMcpService
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Esc(string s) => CodeExecutor.CompileDiagnostic.Escape(s ?? string.Empty);

        private static LcdLayout LoadLayout(string path)
        {
            var xs = new XmlSerializer(typeof(LcdLayout));
            using (var fs = File.OpenRead(path))
                return (LcdLayout)xs.Deserialize(fs);
        }

        private static void SaveLayout(LcdLayout layout, string path)
        {
            var xs = new XmlSerializer(typeof(LcdLayout));
            using (var fs = File.Create(path))
                xs.Serialize(fs, layout);
        }

        private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        // ── Result envelope ───────────────────────────────────────────────────

        public sealed class McpResult
        {
            public bool Success { get; set; }
            public string Error { get; set; }
            /// <summary>Pre-built JSON fragment (no surrounding braces) appended to the envelope.</summary>
            public string PayloadJson { get; set; }

            public string ToJson()
            {
                var sb = new StringBuilder(128);
                sb.Append('{').Append("\"success\":").Append(Success ? "true" : "false");
                sb.Append(",\"error\":").Append(Error == null ? "null" : "\"" + Esc(Error) + "\"");
                if (!string.IsNullOrEmpty(PayloadJson))
                    sb.Append(',').Append(PayloadJson);
                sb.Append('}');
                return sb.ToString();
            }

            public static McpResult Fail(string error) => new McpResult { Success = false, Error = error };
        }

        // ── Tools ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Summarise a layout: surface size, sprite count, rig/clip/bone counts.
        /// </summary>
        public static McpResult LayoutInfo(string layoutPath)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                var sb = new StringBuilder();
                sb.Append("\"layout\":{");
                sb.Append("\"name\":\"").Append(Esc(layout.Name)).Append("\"");
                sb.Append(",\"surfaceWidth\":").Append(layout.SurfaceWidth);
                sb.Append(",\"surfaceHeight\":").Append(layout.SurfaceHeight);
                sb.Append(",\"spriteCount\":").Append(layout.Sprites?.Count ?? 0);
                sb.Append(",\"rigCount\":").Append(layout.Rigs?.Count ?? 0);
                sb.Append(",\"hasOriginalSource\":").Append(string.IsNullOrEmpty(layout.OriginalSourceCode) ? "false" : "true");
                sb.Append(",\"isPulsarOrMod\":").Append(layout.IsPulsarOrModLayout ? "true" : "false");
                sb.Append(",\"rigs\":[");
                if (layout.Rigs != null)
                {
                    for (int i = 0; i < layout.Rigs.Count; i++)
                    {
                        var r = layout.Rigs[i];
                        if (i > 0) sb.Append(',');
                        sb.Append('{')
                          .Append("\"id\":\"").Append(Esc(r.Id)).Append("\"")
                          .Append(",\"name\":\"").Append(Esc(r.Name)).Append("\"")
                          .Append(",\"enabled\":").Append(r.Enabled ? "true" : "false")
                          .Append(",\"boneCount\":").Append(r.Bones?.Count ?? 0)
                          .Append(",\"bindingCount\":").Append(r.Bindings?.Count ?? 0)
                          .Append(",\"clipCount\":").Append(r.Clips?.Count ?? 0)
                          .Append('}');
                    }
                }
                sb.Append("]}");
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// Validate a rig: parent integrity, cycle detection, binding sprite indices, clip tracks.
        /// </summary>
        public static McpResult ValidateRigs(string layoutPath)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                var issues = new List<string>();
                int spriteCount = layout.Sprites?.Count ?? 0;

                if (layout.Rigs != null)
                {
                    foreach (var rig in layout.Rigs)
                    {
                        var ids = new HashSet<string>();
                        if (rig.Bones != null)
                            foreach (var b in rig.Bones)
                                if (b != null && !string.IsNullOrEmpty(b.Id) && !ids.Add(b.Id))
                                    issues.Add("Rig '" + rig.Name + "': duplicate bone id '" + b.Id + "'");

                        if (rig.Bones != null)
                            foreach (var b in rig.Bones)
                            {
                                if (b == null) continue;
                                if (!string.IsNullOrEmpty(b.ParentId) && !ids.Contains(b.ParentId))
                                    issues.Add("Rig '" + rig.Name + "': bone '" + b.Id + "' has unknown ParentId '" + b.ParentId + "'");
                            }

                        // Cycle detection (simple parent chain walk).
                        if (rig.Bones != null)
                        {
                            var byId = new Dictionary<string, Bone>();
                            foreach (var b in rig.Bones) if (b != null && b.Id != null) byId[b.Id] = b;
                            foreach (var b in rig.Bones)
                            {
                                if (b == null) continue;
                                var seen = new HashSet<string>();
                                var cur = b;
                                while (cur != null && !string.IsNullOrEmpty(cur.ParentId) && byId.ContainsKey(cur.ParentId))
                                {
                                    if (!seen.Add(cur.Id)) { issues.Add("Rig '" + rig.Name + "': cycle through bone '" + b.Id + "'"); break; }
                                    cur = byId[cur.ParentId];
                                }
                            }
                        }

                        if (rig.Bindings != null)
                            foreach (var bind in rig.Bindings)
                            {
                                if (bind == null) continue;
                                if (bind.SpriteIndex < 0 || bind.SpriteIndex >= spriteCount)
                                    issues.Add("Rig '" + rig.Name + "': binding SpriteIndex " + bind.SpriteIndex + " out of range (0.." + (spriteCount - 1) + ")");
                                if (!string.IsNullOrEmpty(bind.BoneId) && !ids.Contains(bind.BoneId))
                                    issues.Add("Rig '" + rig.Name + "': binding references unknown bone '" + bind.BoneId + "'");
                            }

                        if (rig.Clips != null)
                            foreach (var clip in rig.Clips)
                            {
                                if (clip == null) continue;
                                if (clip.Duration <= 0f)
                                    issues.Add("Rig '" + rig.Name + "', clip '" + clip.Name + "': non-positive Duration");
                                if (clip.Tracks != null)
                                    foreach (var t in clip.Tracks)
                                        if (t != null && !string.IsNullOrEmpty(t.BoneId) && !ids.Contains(t.BoneId))
                                            issues.Add("Rig '" + rig.Name + "', clip '" + clip.Name + "': track references unknown bone '" + t.BoneId + "'");
                            }
                    }
                }

                var sb = new StringBuilder();
                sb.Append("\"valid\":").Append(issues.Count == 0 ? "true" : "false");
                sb.Append(",\"issues\":[");
                for (int i = 0; i < issues.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(issues[i])).Append('"');
                }
                sb.Append(']');
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// List clips on every rig in the layout.
        /// </summary>
        public static McpResult ListClips(string layoutPath)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                var sb = new StringBuilder();
                sb.Append("\"rigs\":[");
                if (layout.Rigs != null)
                {
                    for (int ri = 0; ri < layout.Rigs.Count; ri++)
                    {
                        var rig = layout.Rigs[ri];
                        if (ri > 0) sb.Append(',');
                        sb.Append("{\"id\":\"").Append(Esc(rig.Id)).Append("\"")
                          .Append(",\"name\":\"").Append(Esc(rig.Name)).Append("\"")
                          .Append(",\"activeClipId\":").Append(rig.ActiveClipId == null ? "null" : "\"" + Esc(rig.ActiveClipId) + "\"")
                          .Append(",\"clips\":[");
                        if (rig.Clips != null)
                        {
                            for (int ci = 0; ci < rig.Clips.Count; ci++)
                            {
                                var clip = rig.Clips[ci];
                                if (ci > 0) sb.Append(',');
                                sb.Append("{\"id\":\"").Append(Esc(clip.Id)).Append("\"")
                                  .Append(",\"name\":\"").Append(Esc(clip.Name)).Append("\"")
                                  .Append(",\"duration\":").Append(F(clip.Duration))
                                  .Append(",\"loop\":").Append(clip.Loop ? "true" : "false")
                                  .Append(",\"trackCount\":").Append(clip.Tracks?.Count ?? 0)
                                  .Append('}');
                            }
                        }
                        sb.Append("]}");
                    }
                }
                sb.Append(']');
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// Sample a clip at the given time and emit per-bone world transforms plus per-binding
        /// sprite poses. Use rigId="" or "active" / "first" to pick the first rig.
        /// </summary>
        public static McpResult PreviewPose(string layoutPath, string rigId, string clipId, float time)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                if (layout.Rigs == null || layout.Rigs.Count == 0) return McpResult.Fail("Layout has no rigs.");

                Rig rig = null;
                if (string.IsNullOrEmpty(rigId) || rigId == "first" || rigId == "active")
                    rig = layout.Rigs[0];
                else
                    rig = layout.Rigs.Find(r => r.Id == rigId || r.Name == rigId);
                if (rig == null) return McpResult.Fail("Rig not found: " + rigId);

                Dictionary<string, RigKeyframe> overrides = null;
                if (rig.Clips != null && rig.Clips.Count > 0)
                {
                    RigClip clip = null;
                    if (string.IsNullOrEmpty(clipId) || clipId == "active")
                        clip = rig.Clips.Find(c => c.Id == rig.ActiveClipId) ?? rig.Clips[0];
                    else
                        clip = rig.Clips.Find(c => c.Id == clipId || c.Name == clipId);
                    if (clip != null)
                        overrides = RigClipSampler.Sample(clip, time);
                }

                var bones = RigEvaluator.EvaluateBones(rig, overrides);
                var sprites = RigEvaluator.EvaluateBindings(rig, layout, overrides);

                var sb = new StringBuilder();
                sb.Append("\"rigId\":\"").Append(Esc(rig.Id)).Append("\"");
                sb.Append(",\"time\":").Append(F(time));
                sb.Append(",\"bones\":{");
                bool first = true;
                foreach (var kv in bones)
                {
                    if (!first) sb.Append(','); first = false;
                    var t = kv.Value;
                    sb.Append('"').Append(Esc(kv.Key)).Append("\":{")
                      .Append("\"x\":").Append(F(t.X))
                      .Append(",\"y\":").Append(F(t.Y))
                      .Append(",\"rot\":").Append(F(t.Rotation))
                      .Append(",\"sx\":").Append(F(t.ScaleX))
                      .Append(",\"sy\":").Append(F(t.ScaleY))
                      .Append('}');
                }
                sb.Append("},\"sprites\":[");
                if (sprites != null)
                {
                    for (int i = 0; i < sprites.Count; i++)
                    {
                        var s = sprites[i];
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"index\":").Append(s.SpriteIndex)
                          .Append(",\"boneId\":\"").Append(Esc(s.BoneId)).Append("\"")
                          .Append(",\"x\":").Append(F(s.X))
                          .Append(",\"y\":").Append(F(s.Y))
                          .Append(",\"rot\":").Append(F(s.Rotation))
                          .Append(",\"sx\":").Append(F(s.ScaleX))
                          .Append(",\"sy\":").Append(F(s.ScaleY))
                          .Append('}');
                    }
                }
                sb.Append(']');
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// Generate the standalone rig runtime snippet (without modifying any user source).
        /// Useful for the LLM to inspect what the rig will inject.
        /// </summary>
        public static McpResult GenerateRigSnippet(string layoutPath, string methodName, string surfaceParam)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                string snippet = RigCodeGenerator.Generate(layout,
                    string.IsNullOrEmpty(methodName) ? "DrawRig" : methodName,
                    string.IsNullOrEmpty(surfaceParam) ? "surface" : surfaceParam);
                var sb = new StringBuilder();
                sb.Append("\"snippet\":\"").Append(Esc(snippet ?? string.Empty)).Append('"');
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// Inject the rig snippet for the layout into a user script and write the result to
        /// <paramref name="outputPath"/>. Idempotent: existing rig regions are replaced.
        /// </summary>
        public static McpResult InjectRig(string layoutPath, string sourcePath, string outputPath,
            string methodName, string surfaceParam)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                string source = File.ReadAllText(sourcePath, Encoding.UTF8);
                var res = RigCodeInjector.Inject(source, layout,
                    string.IsNullOrEmpty(methodName) ? "DrawRig" : methodName,
                    string.IsNullOrEmpty(surfaceParam) ? "surface" : surfaceParam);
                if (!res.Success) return McpResult.Fail(res.Error ?? "Injection failed.");

                File.WriteAllText(outputPath, res.Code ?? string.Empty, Encoding.UTF8);
                var sb = new StringBuilder();
                sb.Append("\"output\":\"").Append(Esc(outputPath)).Append('"');
                sb.Append(",\"replaced\":").Append(res.Replaced ? "true" : "false");
                sb.Append(",\"length\":").Append((res.Code ?? "").Length);
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// Export the layout's <see cref="LcdLayout.OriginalSourceCode"/> to a .cs file.
        /// </summary>
        public static McpResult ExportSource(string layoutPath, string outputPath)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                string code = layout.OriginalSourceCode ?? string.Empty;
                File.WriteAllText(outputPath, code, Encoding.UTF8);
                var sb = new StringBuilder();
                sb.Append("\"output\":\"").Append(Esc(outputPath)).Append("\",\"length\":").Append(code.Length);
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// Apply a freshly generated rig+animation pass and write back to the same .seld file
        /// (or to <paramref name="outputLayoutPath"/> when provided). Persists the updated
        /// <c>OriginalSourceCode</c> so the next run sees the wired source.
        /// </summary>
        public static McpResult InjectRigInPlace(string layoutPath, string outputLayoutPath,
            string methodName, string surfaceParam)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                if (string.IsNullOrEmpty(layout.OriginalSourceCode))
                    return McpResult.Fail("Layout has no OriginalSourceCode to inject into.");

                var res = RigCodeInjector.Inject(layout.OriginalSourceCode, layout,
                    string.IsNullOrEmpty(methodName) ? "DrawRig" : methodName,
                    string.IsNullOrEmpty(surfaceParam) ? "surface" : surfaceParam);
                if (!res.Success) return McpResult.Fail(res.Error ?? "Injection failed.");

                layout.OriginalSourceCode = res.Code;
                string target = string.IsNullOrEmpty(outputLayoutPath) ? layoutPath : outputLayoutPath;
                SaveLayout(layout, target);

                var sb = new StringBuilder();
                sb.Append("\"layout\":\"").Append(Esc(target)).Append('"');
                sb.Append(",\"replaced\":").Append(res.Replaced ? "true" : "false");
                sb.Append(",\"length\":").Append((res.Code ?? "").Length);
                return new McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return McpResult.Fail(ex.Message); }
        }
    }
}
