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
            //   --run         <path>    Compile + execute; emits sprite count, echo log, timings.
            //   --dump-stubs            Emit the SE API stub source so the LLM has ground truth.
            //   --version               Emit {"version":"...","stubHash":"..."} so MCP can detect drift.
            if (args.Length >= 1 && (args[0] == "--compile" || args[0] == "--run" || args[0] == "--dump-stubs" || args[0] == "--version"))
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
