using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using SESpriteLCDLayoutTool.Models;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Stateless headless helpers exposing the unified animation + rig injection
    /// pipeline and an end-to-end orchestrator to the MCP server. Mirrors the shape
    /// of <see cref="RigMcpService"/>: every method takes file paths, performs one
    /// deterministic transform, and returns a single-line JSON envelope via
    /// <c>ToJson()</c> on <see cref="RigMcpService.McpResult"/>.
    ///
    /// Note: <see cref="SpriteEntry.AnimationEffects"/> is <c>[XmlIgnore]</c>, so a
    /// .seld file does not by itself carry per-sprite effects. The anim path still
    /// performs every other pass the editor does (ensure Add blocks, sprite dedup,
    /// rig region, idempotent strip-and-replace), and is the right entry point for
    /// the LLM to (a) drive rigs through the unified pipeline, (b) reconcile a
    /// layout's <c>OriginalSourceCode</c> after layout edits, and (c) compose with
    /// a future effect-spec-from-JSON tool.
    /// </summary>
    public static class AnimMcpService
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

        // ── Tools ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Run the unified <see cref="RoslynAnimationInjector.InjectAnimations(string, System.Collections.Generic.IEnumerable{SpriteEntry}, LcdLayout)"/>
        /// pass on <paramref name="sourcePath"/> against <paramref name="layoutPath"/>'s
        /// sprites and rig, and write the result to <paramref name="outputPath"/>.
        /// </summary>
        public static RigMcpService.McpResult InjectAnimations(string layoutPath, string sourcePath, string outputPath)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                string source = File.ReadAllText(sourcePath, Encoding.UTF8);

                var sprites = layout.Sprites ?? new List<SpriteEntry>();
                var res = RoslynAnimationInjector.InjectAnimations(source, sprites, layout);
                if (!res.Success)
                    return RigMcpService.McpResult.Fail(res.Error ?? "Injection failed.");

                File.WriteAllText(outputPath, res.Code ?? string.Empty, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.Append("\"output\":\"").Append(Esc(outputPath)).Append('"');
                sb.Append(",\"spritesAnimated\":").Append(res.SpritesAnimated);
                sb.Append(",\"length\":").Append((res.Code ?? "").Length);
                return new RigMcpService.McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return RigMcpService.McpResult.Fail(ex.Message); }
        }

        /// <summary>
        /// Same as <see cref="InjectAnimations"/> but reads from / writes to
        /// <see cref="LcdLayout.OriginalSourceCode"/> on the layout itself. The
        /// updated layout is persisted to <paramref name="outputLayoutPath"/> or
        /// back to <paramref name="layoutPath"/> when null.
        /// </summary>
        public static RigMcpService.McpResult InjectAnimationsInPlace(string layoutPath, string outputLayoutPath)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                if (string.IsNullOrEmpty(layout.OriginalSourceCode))
                    return RigMcpService.McpResult.Fail("Layout has no OriginalSourceCode to inject into.");

                var sprites = layout.Sprites ?? new List<SpriteEntry>();
                var res = RoslynAnimationInjector.InjectAnimations(layout.OriginalSourceCode, sprites, layout);
                if (!res.Success)
                    return RigMcpService.McpResult.Fail(res.Error ?? "Injection failed.");

                layout.OriginalSourceCode = res.Code;
                string target = string.IsNullOrEmpty(outputLayoutPath) ? layoutPath : outputLayoutPath;
                SaveLayout(layout, target);

                var sb = new StringBuilder();
                sb.Append("\"layout\":\"").Append(Esc(target)).Append('"');
                sb.Append(",\"spritesAnimated\":").Append(res.SpritesAnimated);
                sb.Append(",\"length\":").Append((res.Code ?? "").Length);
                return new RigMcpService.McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return RigMcpService.McpResult.Fail(ex.Message); }
        }

        // ── Orchestrator ──────────────────────────────────────────────────────

        /// <summary>
        /// End-to-end pipeline: load a layout, run the unified inject pass against
        /// <see cref="LcdLayout.OriginalSourceCode"/>, write the result to
        /// <paramref name="outputSourcePath"/>, then optionally compile and/or run
        /// the produced script. Returns a combined JSON envelope with sub-results
        /// for <c>inject</c>, <c>compile</c>, and <c>run</c> stages.
        /// </summary>
        public static RigMcpService.McpResult Pipeline(string layoutPath, string outputSourcePath,
            bool compile, bool run)
        {
            try
            {
                var layout = LoadLayout(layoutPath);
                if (string.IsNullOrEmpty(layout.OriginalSourceCode))
                    return RigMcpService.McpResult.Fail("Layout has no OriginalSourceCode for the pipeline.");

                var sprites = layout.Sprites ?? new List<SpriteEntry>();
                var inj = RoslynAnimationInjector.InjectAnimations(layout.OriginalSourceCode, sprites, layout);
                if (!inj.Success)
                    return RigMcpService.McpResult.Fail(inj.Error ?? "Injection failed.");

                string code = inj.Code ?? string.Empty;
                if (!string.IsNullOrEmpty(outputSourcePath))
                    File.WriteAllText(outputSourcePath, code, Encoding.UTF8);

                var sb = new StringBuilder();
                sb.Append("\"inject\":{");
                sb.Append("\"output\":").Append(string.IsNullOrEmpty(outputSourcePath) ? "null" : "\"" + Esc(outputSourcePath) + "\"");
                sb.Append(",\"spritesAnimated\":").Append(inj.SpritesAnimated);
                sb.Append(",\"length\":").Append(code.Length);
                sb.Append('}');

                // Run implies compile.
                bool wantCompile = compile || run;

                if (wantCompile)
                {
                    var cr = CodeExecutor.CompileOnly(code);
                    sb.Append(",\"compile\":").Append(cr.ToJson());
                    if (!cr.Success)
                    {
                        // Don't attempt to run a script that didn't compile — but the
                        // envelope still reports success=true because the pipeline ran
                        // to completion. The caller branches on inner compile.success.
                        return new RigMcpService.McpResult { Success = true, PayloadJson = sb.ToString() };
                    }
                }

                if (run)
                {
                    var rr = CodeExecutor.RunHeadless(code);
                    sb.Append(",\"run\":").Append(rr.ToJson());
                }

                return new RigMcpService.McpResult { Success = true, PayloadJson = sb.ToString() };
            }
            catch (Exception ex) { return RigMcpService.McpResult.Fail(ex.Message); }
        }
    }
}
