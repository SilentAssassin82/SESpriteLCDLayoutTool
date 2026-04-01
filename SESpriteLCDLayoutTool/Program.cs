using System;
using System.Windows.Forms;

namespace SESpriteLCDLayoutTool
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
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
