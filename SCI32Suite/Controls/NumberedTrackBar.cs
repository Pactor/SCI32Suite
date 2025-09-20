using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SCI32Suite.Controls
{
    public partial class NumberedTrackBar : TrackBar
    {
        public string RightText { get; set; } = string.Empty; // e.g. total loops
        public int FixedWidth { get; set; } = 160;            // keeps all bars same size

        public NumberedTrackBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.OptimizedDoubleBuffer, true);
            AutoSize = false;      // we control the size
            Width = FixedWidth;
            Height = 45;           // a little taller for the numbers
            LargeChange = 1;
            SmallChange = 1;
        }
        // Map a mouse X to an exact Value (respects the right margin)
        private void SetValueFromMouse(int mouseX, bool capture)
        {
            int range = Maximum - Minimum;
            if (range <= 0) return;

            const int LeftPadding = 8;
            const int RightPadding = 16;
            const int RightMargin = 30;

            float trackWidth = Width - (LeftPadding + RightPadding + RightMargin);
            if (trackWidth <= 0) return;

            // Clamp position to the track
            float pos = mouseX - LeftPadding;
            if (pos < 0) pos = 0;
            if (pos > trackWidth) pos = trackWidth;

            // Normalize 0..1 and map to [Minimum..Maximum]
            float t = pos / trackWidth;
            int newValue = Minimum + (int)Math.Round(t * range);

            // Make clicks in the right margin reliably select Maximum
            if (mouseX >= LeftPadding + trackWidth) newValue = Maximum;
            if (mouseX <= LeftPadding) newValue = Minimum;

            if (newValue != Value)
            {
                Value = newValue;
                Invalidate();
            }

            if (capture) Capture = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);   // keep focus behavior
            if (e.Button == MouseButtons.Left)
                SetValueFromMouse(e.X, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left)
                SetValueFromMouse(e.X, false);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var g = e.Graphics;
            int range = Maximum - Minimum;
            if (range <= 0) return;

            const int LeftPadding = 8;    // track left padding
            const int RightPadding = 16;   // internal border padding
            const int RightMargin = 30;   // reserved space for the total label

            // width available for the track (leaves room for the label)
            float trackWidth = Width - (LeftPadding + RightPadding + RightMargin);
            float step = trackWidth / range;

            // ---- track ----
            using (var trackBrush = new SolidBrush(SystemColors.ControlDark))
                g.FillRectangle(trackBrush,
                    new Rectangle(LeftPadding, 15, (int)trackWidth, 4));

            // ---- ticks and numbers ----
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            using (var tickPen = new Pen(ForeColor))
            using (var font = new Font(Font.FontFamily, 8))
            {
                for (int i = 0; i <= range; i++)
                {
                    float x = LeftPadding + i * step;
                    g.DrawLine(tickPen, x, 15, x, 20);
                    g.DrawString((Minimum + i).ToString(), font, Brushes.Black, x, 22, sf);
                }
            }

            // ---- yellow triangle thumb ----
            float thumbX = LeftPadding + (Value - Minimum) * step;
            float thumbY = 7;
            float size = 12;

            PointF[] outer =
            {
        new PointF(thumbX, thumbY + size),      // bottom center
        new PointF(thumbX - size / 2f, thumbY), // top left
        new PointF(thumbX + size / 2f, thumbY)  // top right
    };

            using (var fillOuter = new SolidBrush(Color.Yellow))
            using (var outline = new Pen(Color.Black, 1.5f))
            {
                g.FillPolygon(fillOuter, outer);
                g.DrawPolygon(outline, outer);
            }

            // ---- total loops text ----
            if (!string.IsNullOrEmpty(RightText))
            {
                using (var font = new Font(Font.FontFamily, 8, FontStyle.Bold))
                using (var sf = new StringFormat
                {
                    Alignment = StringAlignment.Far,
                    LineAlignment = StringAlignment.Center
                })
                {
                    float trackCenterY = 15 + 2f; // vertical center of the 4-pixel track
                    const int offsetDown = 2;     // move text down 2 px
                    const int offsetLeft = 5;     // move text left 5 px

                    g.DrawString(RightText,
                                 font,
                                 Brushes.Black,
                                 new RectangleF(Width - RightMargin - offsetLeft,
                                                trackCenterY - font.Height / 2f + offsetDown,
                                                RightMargin,
                                                font.Height),
                                 sf);
                }
            }
        }


    }
}
