using System;
using System.IO;
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
            // Output (success):  {"success":true,"errors":null,"scriptType":"ProgrammableBlock"}
            // Output (failure):  {"success":false,"errors":"(3,1): error CS0246: ...","scriptType":"ProgrammableBlock"}
            if (args.Length >= 2 && args[0] == "--compile")
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
                string filePath = args[1];
                try
                {
                    string code = File.ReadAllText(filePath, Encoding.UTF8);
                    CodeExecutor.CompileResult result = CodeExecutor.CompileOnly(code);
                    Console.WriteLine(result.ToJson());
                }
                catch (Exception ex)
                {
                    // File I/O or other unexpected error — still emit valid JSON
                    string escaped = ex.Message
                        .Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\r\n", "\\n").Replace("\n", "\\n");
                    Console.WriteLine("{\"success\":false,\"errors\":\"" + escaped + "\",\"scriptType\":null}");
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
