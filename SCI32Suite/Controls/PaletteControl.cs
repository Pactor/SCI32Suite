using SCI32Suite.Palette;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SCI32Suite.Controls
{
    public partial class PaletteControl : UserControl
    {
        public PaletteData Palette { get; set; }

        public PaletteControl()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Palette == null) return;

            const int grid = 16;
            int cell = Math.Min((Width - 10) / grid, (Height - 10) / grid);
            if (cell < 8) cell = 8;

            var g = e.Graphics;
            for (int i = 0; i < 256; i++)
            {
                int x = (i % grid) * cell + 5;
                int y = (i / grid) * cell + 5;

                var c = Palette.GetColor(i);
                if (i == 255) c = Color.White; // transparent index shown as white

                using (var b = new SolidBrush(c))
                {
                    g.FillRectangle(b, x + 2, y + 2, cell - 4, cell - 4);
                }
                using (var p = new Pen(Color.White, 2))
                {
                    g.DrawRectangle(p, x + 1, y + 1, cell - 2, cell - 2);
                }
                using (var p2 = new Pen(Color.Black, 1))
                {
                    g.DrawRectangle(p2, x, y, cell, cell);
                }
            }
        }
    }
}
