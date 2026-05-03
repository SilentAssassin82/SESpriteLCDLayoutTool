using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SESpriteLCDLayoutTool.Forms
{
    /// <summary>
    /// Lightweight dark-themed slider used by the parameter inspector. WinForms'
    /// built-in <see cref="TrackBar"/> ignores <c>BackColor</c> for its track and
    /// always paints a bright Win32 channel, which clashes badly with the dark UI.
    /// This control is fully owner-drawn so the track and thumb match the
    /// surrounding theme.
    /// </summary>
    public class DarkSlider : Control
    {
        private int _min;
        private int _max = 1000;
        private int _value;
        private bool _dragging;

        public event EventHandler ValueChanged;

        public DarkSlider()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 22;
            BackColor = Color.FromArgb(35, 35, 38);
            ForeColor = Color.FromArgb(0, 122, 204);
        }

        public int Minimum
        {
            get { return _min; }
            set { _min = value; Clamp(); Invalidate(); }
        }

        public int Maximum
        {
            get { return _max; }
            set { _max = value; Clamp(); Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(_min, Math.Min(_max, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private void Clamp()
        {
            if (_max < _min) _max = _min;
            if (_value < _min) _value = _min;
            if (_value > _max) _value = _max;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, ClientRectangle);

            int margin = 8;
            int trackY = ClientSize.Height / 2;
            int trackLeft = margin;
            int trackRight = ClientSize.Width - margin;
            // Track
            using (var trackBrush = new SolidBrush(Color.FromArgb(70, 70, 74)))
                g.FillRectangle(trackBrush, trackLeft, trackY - 2, trackRight - trackLeft, 4);

            // Filled portion up to thumb
            int thumbX = ValueToX(_value, trackLeft, trackRight);
            using (var fillBrush = new SolidBrush(ForeColor))
                g.FillRectangle(fillBrush, trackLeft, trackY - 2, thumbX - trackLeft, 4);

            // Thumb
            var thumbRect = new Rectangle(thumbX - 5, trackY - 8, 10, 16);
            using (var thumbBrush = new SolidBrush(Color.FromArgb(220, 220, 225)))
                g.FillRectangle(thumbBrush, thumbRect);
            using (var pen = new Pen(Color.FromArgb(40, 40, 40)))
                g.DrawRectangle(pen, thumbRect);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            Capture = true;
            UpdateValueFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) UpdateValueFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = Math.Max(1, (_max - _min) / 100);
            Value = _value + (e.Delta > 0 ? step : -step);
        }

        private void UpdateValueFromMouse(int mouseX)
        {
            int margin = 8;
            int trackLeft = margin;
            int trackRight = ClientSize.Width - margin;
            if (trackRight <= trackLeft) return;
            double t = (mouseX - trackLeft) / (double)(trackRight - trackLeft);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            Value = _min + (int)Math.Round(t * (_max - _min));
        }

        private int ValueToX(int value, int trackLeft, int trackRight)
        {
            if (_max <= _min) return trackLeft;
            double t = (value - _min) / (double)(_max - _min);
            return trackLeft + (int)Math.Round(t * (trackRight - trackLeft));
        }
    }
}
