using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using SESpriteLCDLayoutTool.Services;

namespace SESpriteLCDLayoutTool
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // ── Headless compile mode ─────────────────────────────────────────
            // Usage: SESpriteLCDLayoutTool.exe --compile <path-to-script.cs>
            // Writes a single JSON line to stdout and exits — no window is shown.
            //
            // Output (success):  {"success":true,"errors":null,"diagnostics":[],"scriptType":"ProgrammableBlock"}
            // Output (failure):  {"success":false,"errors":"(3,1): error CS0246: ...","diagnostics":[{"line":3,"column":1,"severity":"error","code":"CS0246","message":"..."}],"scriptType":"ProgrammableBlock"}
            //
            // Other headless modes:
            //   --run                 <path>                                   Compile + execute; emits sprite count, echo log, timings.
            //   --dump-stubs                                                   Emit the SE API stub source so the LLM has ground truth.
            //   --version                                                      Emit {"version":"...","stubHash":"..."} so MCP can detect drift.
            //
            // Rig MCP modes (all stateless; layout passed by .seld path):
            //   --layout-info         <layout.seld>                            Surface size, sprite/rig/clip/bone counts.
            //   --rig-validate        <layout.seld>                            Parent integrity, cycle, binding, clip-track checks.
            //   --rig-clips           <layout.seld>                            List clips per rig with duration/loop/track count.
            //   --rig-preview         <layout.seld> <rigId|first> <clipId|active> <time>
            //                                                                  Sample a clip and emit per-bone + per-binding world poses.
            //   --rig-snippet         <layout.seld> [methodName] [surfaceParam]
            //                                                                  Emit standalone rig runtime snippet (no source modification).
            //   --rig-inject          <layout.seld> <source.cs> <output.cs> [methodName] [surfaceParam]
            //                                                                  Idempotently inject rig region into a user script.
            //   --rig-inject-inplace  <layout.seld> [outputLayout.seld] [methodName] [surfaceParam]
            //                                                                  Inject into layout.OriginalSourceCode and persist back.
            //   --layout-export-source <layout.seld> <output.cs>               Write layout.OriginalSourceCode to a .cs file.
            if (args.Length >= 1 && (args[0] == "--compile" || args[0] == "--run" || args[0] == "--dump-stubs" || args[0] == "--version"
                || args[0] == "--layout-info" || args[0] == "--rig-validate" || args[0] == "--rig-clips"
                || args[0] == "--rig-preview" || args[0] == "--rig-snippet" || args[0] == "--rig-inject"
                || args[0] == "--rig-inject-inplace" || args[0] == "--layout-export-source"))
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

                if (args[0] == "--dump-stubs")
                {
                    Console.Write(CodeExecutor.GetStubsSource());
                    return;
                }

                if (args[0] == "--version")
                {
                    string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                    string stubs = CodeExecutor.GetStubsSource() ?? string.Empty;
                    string stubHash;
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(stubs));
                        StringBuilder sb = new StringBuilder(hash.Length * 2);
                        for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                        stubHash = sb.ToString();
                    }
                    Console.WriteLine("{\"version\":\"" + version + "\",\"stubHash\":\"" + stubHash + "\"}");
                    return;
                }

                if (args.Length < 2)
                {
                    Console.WriteLine("{\"success\":false,\"errors\":\"Missing <path> argument.\",\"diagnostics\":[],\"scriptType\":null}");
                    return;
                }

                // Rig MCP dispatch (all stateless; emits a single JSON line and exits).
                if (args[0] == "--layout-info")        { Console.WriteLine(Services.RigMcpService.LayoutInfo(args[1]).ToJson()); return; }
                if (args[0] == "--rig-validate")       { Console.WriteLine(Services.RigMcpService.ValidateRigs(args[1]).ToJson()); return; }
                if (args[0] == "--rig-clips")          { Console.WriteLine(Services.RigMcpService.ListClips(args[1]).ToJson()); return; }
                if (args[0] == "--rig-preview")
                {
                    string rigId  = args.Length > 2 ? args[2] : "first";
                    string clipId = args.Length > 3 ? args[3] : "active";
                    float t = 0f;
                    if (args.Length > 4) float.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t);
                    Console.WriteLine(Services.RigMcpService.PreviewPose(args[1], rigId, clipId, t).ToJson());
                    return;
                }
                if (args[0] == "--rig-snippet")
                {
                    string method  = args.Length > 2 ? args[2] : null;
                    string surface = args.Length > 3 ? args[3] : null;
                    Console.WriteLine(Services.RigMcpService.GenerateRigSnippet(args[1], method, surface).ToJson());
                    return;
                }
                if (args[0] == "--rig-inject")
                {
                    if (args.Length < 4) { Console.WriteLine("{\"success\":false,\"error\":\"Usage: --rig-inject <layout.seld> <source.cs> <output.cs> [methodName] [surfaceParam]\"}"); return; }
                    string method  = args.Length > 4 ? args[4] : null;
                    string surface = args.Length > 5 ? args[5] : null;
                    Console.WriteLine(Services.RigMcpService.InjectRig(args[1], args[2], args[3], method, surface).ToJson());
                    return;
                }
                if (args[0] == "--rig-inject-inplace")
                {
                    string outLayout = args.Length > 2 ? args[2] : null;
                    string method    = args.Length > 3 ? args[3] : null;
                    string surface   = args.Length > 4 ? args[4] : null;
                    Console.WriteLine(Services.RigMcpService.InjectRigInPlace(args[1], outLayout, method, surface).ToJson());
                    return;
                }
                if (args[0] == "--layout-export-source")
                {
                    if (args.Length < 3) { Console.WriteLine("{\"success\":false,\"error\":\"Usage: --layout-export-source <layout.seld> <output.cs>\"}"); return; }
                    Console.WriteLine(Services.RigMcpService.ExportSource(args[1], args[2]).ToJson());
                    return;
                }

                string filePath = args[1];
                try
                {
                    string code = File.ReadAllText(filePath, Encoding.UTF8);
                    if (args[0] == "--run")
                    {
                        CodeExecutor.RunResult run = CodeExecutor.RunHeadless(code);
                        Console.WriteLine(run.ToJson());
                    }
                    else
                    {
                        CodeExecutor.CompileResult result = CodeExecutor.CompileOnly(code);
                        Console.WriteLine(result.ToJson());
                    }
                }
                catch (Exception ex)
                {
                    // File I/O or other unexpected error — still emit valid JSON
                    string escaped = ex.Message
                        .Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\r\n", "\\n").Replace("\n", "\\n");
                    Console.WriteLine("{\"success\":false,\"errors\":\"" + escaped + "\",\"diagnostics\":[],\"scriptType\":null}");
                }
                return;
            }

            // ── Normal GUI mode ───────────────────────────────────────────────
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Show a dialog for any unhandled UI-thread exception instead of crashing silently.
            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.ToString(), "Unhandled Exception",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.Run(new MainForm());
        }
    }
}
