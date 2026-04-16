using System.Drawing;
using System.Windows.Forms;

namespace SESpriteLCDLayoutTool
{
    public partial class MainForm
    {
        // ── Dark theme menu renderer ──────────────────────────────────────────────
        private class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer()
                : base(new DarkColorTable()) { }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected         => Color.FromArgb(60, 60, 62);
            public override Color MenuItemBorder           => Color.FromArgb(80, 80, 80);
            public override Color MenuBorder               => Color.FromArgb(60, 60, 60);
            public override Color ToolStripDropDownBackground => Color.FromArgb(45, 45, 48);
            public override Color MenuStripGradientBegin   => Color.FromArgb(45, 45, 48);
            public override Color MenuStripGradientEnd     => Color.FromArgb(45, 45, 48);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(60, 60, 62);
            public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(60, 60, 62);
            public override Color MenuItemPressedGradientBegin  => Color.FromArgb(70, 70, 72);
            public override Color MenuItemPressedGradientEnd    => Color.FromArgb(70, 70, 72);
            public override Color ImageMarginGradientBegin => Color.FromArgb(45, 45, 48);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(45, 45, 48);
            public override Color ImageMarginGradientEnd   => Color.FromArgb(45, 45, 48);
        }
    }
}
