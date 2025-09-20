using SCI32Suite.Controls;
using SCI32Suite.P56;
using SCI32Suite.Palette;
using SCI32Suite.V56;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SCI32Suite
{
    public partial class MainForm : Form
    {
        private PaletteControl _topPalette;
        private PaletteControl _bottomPalette;

        private Button _btnLoadRiff;
        private Button _btnExtractFromImage;
        private Button _btnLoadSci32;
        private Button _btnExportTopRiff;
        private Button _btnExportBottomSci32;

        private Label _lblTop;
        private Label _lblBottom;
        private Label _lblStatus;

        private PaletteData _top;
        private PaletteData _bottom;

        // --- View tab controls ---
        //private TrackBar _loopSlider;
        private NumberedTrackBar _loopSlider;
        Label _loopsNumLabel;
        Label _loopsTotalLabel;
        FlowLayoutPanel _loopSliderPanel;
        private NumberedTrackBar _cellSlider;
        private Panel _keyColorPanel;
        private PaletteControl _miniPalette;
        private PictureBox _viewPictureBox;
        private PictureBox _p56PictureBox;
        private Button _btnLoadProject;
        private Button _btnCreateProject;
        private Button _btnPlay;
        // Holds the currently loaded P56 if any
        private P56File _currentP56;
        // Covers most formats the .NET GDI+ Bitmap class can open
        private const string ImageFileFilter =
            "All Image Files|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|" +
            "Bitmap (*.bmp)|*.bmp|" +
            "PNG (*.png)|*.png|" +
            "JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|" +
            "TIFF (*.tif;*.tiff)|*.tif;*.tiff|" +
            "All Files (*.*)|*.*";

        private bool _fillImage = true;

        private TabPage _viewTab;
        private TabPage _paletteTab;
        public MainForm(string startupFile)
        {
            Text = "SCI32PaletteConvertor";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900,620);
            MinimumSize = new Size(800, 500);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(CreateViewTab());
            tabs.TabPages.Add(CreatePictureTab());
            tabs.TabPages.Add(CreatePaletteTab());

            Controls.Add(tabs);
            if (!string.IsNullOrEmpty(startupFile))
            {
                string ext = Path.GetExtension(startupFile).ToLowerInvariant();
                if (ext == ".sci32")
                {
                    tabs.SelectedTab = _viewTab;
                    // TODO: LoadProject(startupFile);
                }
                else if (ext == ".scpal")
                {
                    tabs.SelectedTab = _paletteTab;
                    // TODO: LoadPalette(startupFile);
                }
            }
            // initialise palette data
            _top = PaletteData.CreateDefault();
            _bottom = PaletteData.CreateDefault();
            RefreshViews();
        }
        private TabPage CreatePictureTab()
        {
            // Buttons
            var btnLoadP56 = new Button { Text = "Load P56", AutoSize = true };
            var btnLoadImage = new Button { Text = "Load Image", AutoSize = true };
            var btnExportImage = new Button { Text = "Export Image (SCI → Image)", AutoSize = true };
            var btnExportP56 = new Button { Text = "Export P56 (Image → SCI)", AutoSize = true };
            // TODO: hook events when ready
            btnLoadP56.Click += BtnLoadP56_Click;
            btnLoadImage.Click += BtnLoadImage_Click;
            btnExportImage.Click += BtnExportImage_Click;
            btnExportP56.Click += BtnExportP56_Click;
            
            var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            var buttonStack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            buttonStack.Controls.AddRange(new Control[]
            {
                btnLoadP56,
                btnLoadImage,
                btnExportImage,
                btnExportP56
            });
            leftPanel.Controls.Add(buttonStack);

            _p56PictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.Controls.Add(leftPanel, 0, 0);
            layout.Controls.Add(_p56PictureBox, 1, 0);

            var page = new TabPage("Picture");
            page.Controls.Add(layout);
            return page;
        }
        private TabPage CreateViewTab()
        {
            // ----------------------------
            // Left button column
            // ----------------------------
            _btnLoadProject = new Button { Text = "Load Project", AutoSize = true };
            _btnCreateProject = new Button { Text = "Create Project", AutoSize = true };
            _btnPlay = new Button { Text = "Play", AutoSize = true };

            var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var buttonStack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            buttonStack.Controls.AddRange(new Control[]
            {
                _btnLoadProject,
                _btnCreateProject,
                _btnPlay
            });
            leftPanel.Controls.Add(buttonStack);

            // ----------------------------
            // Sliders
            // ----------------------------
            _loopSlider = new NumberedTrackBar
            {
                Minimum = 0,
                Maximum = 10,
                TickFrequency = 1,
                RightText = "10",
                FixedWidth = 180
            };

            _loopsTotalLabel = new Label
            {
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(4, 0, 0, 0)
            };

            _loopSliderPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true
            };
            _loopSliderPanel.Controls.Add(_loopSlider);
            _loopSliderPanel.Controls.Add(_loopsTotalLabel);

            _cellSlider = new NumberedTrackBar
            {
                Minimum = 0,
                Maximum = 10,
                TickFrequency = 1,
                RightText = "10",
                FixedWidth = 180
            };

            // ----------------------------
            // Key color panel and palette
            // ----------------------------
            _keyColorPanel = new Panel
            {
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                Height = 24,
                Width = 80,
                Margin = new Padding(0, 4, 0, 6),
                Anchor = AnchorStyles.None
            };

            _miniPalette = new PaletteControl
            {
                Width = 150,
                Height = 150,
                Margin = new Padding(0, 2, 0, 0),
                Anchor = AnchorStyles.None
            };

            // ============================
            // Key + Palette layout block
            // ============================
            var keyPaletteStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(6)
            };
            keyPaletteStack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Key label
            keyPaletteStack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // key panel

            // Key row
            Label keyLabel = new Label
            {
                Text = "Key",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 6, 0)
            };
            keyPaletteStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            keyPaletteStack.Controls.Add(keyLabel, 0, 0);
            keyPaletteStack.Controls.Add(_keyColorPanel, 1, 0);

            // Spacer row (adjust Height to move palette further down)
            Panel spacer = new Panel { Height = 255 };   // increase/decrease as needed
            keyPaletteStack.RowStyles.Add(new RowStyle(SizeType.Absolute, spacer.Height));
            keyPaletteStack.Controls.Add(spacer, 0, 1);
            keyPaletteStack.SetColumnSpan(spacer, 2);

            // Palette row
            keyPaletteStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            keyPaletteStack.Controls.Add(_miniPalette, 0, 2);
            keyPaletteStack.SetColumnSpan(_miniPalette, 2);

            // ----------------------------
            // Overall control stack
            // ----------------------------
            var controlStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(6)
            };
            controlStack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // loop slider
            controlStack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // cell slider
            controlStack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // key + palette block
            controlStack.Controls.Add(_loopSliderPanel, 0, 0);
            controlStack.Controls.Add(_cellSlider, 0, 1);
            controlStack.Controls.Add(keyPaletteStack, 0, 2);

            var controlPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            controlPanel.Controls.Add(controlStack);

            // ----------------------------
            // Right-side picture box
            // ----------------------------
            _viewPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            var rightPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2
            };
            rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            rightPanel.Controls.Add(controlPanel, 0, 0);
            rightPanel.Controls.Add(_viewPictureBox, 1, 0);

            // ----------------------------
            // Main layout: left buttons / right area
            // ----------------------------
            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            mainLayout.Controls.Add(leftPanel, 0, 0);
            mainLayout.Controls.Add(rightPanel, 1, 0);

            _viewTab = new TabPage("View");
            _viewTab.Controls.Add(mainLayout);
            return _viewTab;
        }
        private TabPage CreatePaletteTab()
        {
            _lblTop = new Label
            {
                Text = "Windows RIFF (Top)",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Padding = new Padding(0, 4, 0, 4)
            };

            _lblBottom = new Label
            {
                Text = "SCI32 (Bottom)",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Padding = new Padding(0, 4, 0, 4)
            };

            _topPalette = new PaletteControl { Dock = DockStyle.Fill };
            _bottomPalette = new PaletteControl { Dock = DockStyle.Fill };

            _btnLoadRiff = new Button { Text = "Load RIFF", AutoSize = true };
            _btnExtractFromImage = new Button { Text = "Extract From Image", AutoSize = true };
            _btnLoadSci32 = new Button { Text = "Load SCI32 / V56", AutoSize = true };
            _btnExportTopRiff = new Button { Text = "Export Top as RIFF", AutoSize = true };
            _btnExportBottomSci32 = new Button { Text = "Export Bottom as SCI32", AutoSize = true };

            _lblStatus = new Label
            {
                Text = "Ready.",
                AutoSize = true,
                Padding = new Padding(4, 6, 4, 6)
            };

            // wire events
            _btnLoadRiff.Click += OnLoadRiff;
            _btnExtractFromImage.Click += OnExtractFromImage;
            _btnLoadSci32.Click += OnLoadSci32;
            _btnExportTopRiff.Click += OnExportTopRiff;
            _btnExportBottomSci32.Click += OnExportBottomSci32;

            var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            var buttonStack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true
            };
            buttonStack.Controls.AddRange(new Control[]
            {
                _btnLoadRiff,
                _btnExtractFromImage,
                _btnLoadSci32,
                _btnExportTopRiff,
                _btnExportBottomSci32
            });
            _lblStatus.Dock = DockStyle.Bottom;
            leftPanel.Controls.Add(buttonStack);
            leftPanel.Controls.Add(_lblStatus);

            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(10)
            };
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var topArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            topArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            topArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            topArea.Controls.Add(_lblTop, 0, 0);
            topArea.Controls.Add(_topPalette, 0, 1);

            var bottomArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            bottomArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottomArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bottomArea.Controls.Add(_lblBottom, 0, 0);
            bottomArea.Controls.Add(_bottomPalette, 0, 1);

            rightLayout.Controls.Add(topArea, 0, 0);
            rightLayout.Controls.Add(bottomArea, 0, 1);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.Controls.Add(leftPanel, 0, 0);
            layout.Controls.Add(rightLayout, 1, 0);

            _paletteTab = new TabPage("Palette");
            _paletteTab.Controls.Add(layout);
            return _paletteTab;
        }


        private void RefreshViews()
        {
            _topPalette.Palette = _top;
            _bottomPalette.Palette = _bottom;
            _topPalette.Invalidate();
            _bottomPalette.Invalidate();
        }
        // helper to refresh tick numbers and total label
      
        private void OnLoadRiff(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "RIFF Palette (*.pal)|*.pal|All Files (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var riff = RiffPalette.Read(ofd.FileName);
                        _top = PaletteData.FromRiff(riff);
                        _bottom = Sci32PaletteConverter.FromRiff(_top, 0); // remap=0 default
                        _lblStatus.Text = $"Loaded RIFF: {_top.Count} colors -> SCI32 converted.";
                        RefreshViews();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Load RIFF failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnExtractFromImage(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "Images (*.png;*.jpg;*.bmp)|*.png;*.jpg;*.bmp|All Files (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        using (var bmp = (Bitmap)Image.FromFile(ofd.FileName))
                        {
                            var palette = MedianCutQuantizer.ExtractPalette(bmp, 256);
                            _top = PaletteData.FromRgbList(palette);
                            _bottom = Sci32PaletteConverter.FromRiff(_top, 0);
                            _lblStatus.Text = $"Extracted 256-color palette from image: {Path.GetFileName(ofd.FileName)}";
                            RefreshViews();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Quantize failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnLoadSci32(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "SCI32 Palette Block (*.bin;*.pal32)|*.bin;*.pal32|V56 View (*.v56)|*.v56|All Files (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        PaletteData sciPal;
                        if (Path.GetExtension(ofd.FileName).Equals(".v56", StringComparison.OrdinalIgnoreCase))
                        {
                            sciPal = V56PaletteExtractor.ExtractFirst256(ofd.FileName);
                        }
                        else
                        {
                            sciPal = Sci32PaletteConverter.ReadSci32Block(ofd.FileName);
                        }
                        _bottom = sciPal;
                        _top = Sci32PaletteConverter.ToRiff(_bottom);
                        _lblStatus.Text = $"Loaded SCI32 palette from: {Path.GetFileName(ofd.FileName)}";
                        RefreshViews();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Load SCI32 failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnExportTopRiff(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "RIFF Palette (*.pal)|*.pal";
                sfd.FileName = "palette_riff.pal";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var riff = _top.ToRiff();
                        RiffPalette.Write(sfd.FileName, riff);
                        _lblStatus.Text = $"Saved RIFF: {Path.GetFileName(sfd.FileName)}";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Save RIFF failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnExportBottomSci32(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "SCI32 Palette Block (*.pal32;*.bin)|*.pal32;*.bin";
                sfd.FileName = "palette_sci32.pal32";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        Sci32PaletteConverter.WriteSci32Block(sfd.FileName, _bottom);
                        _lblStatus.Text = $"Saved SCI32: {Path.GetFileName(sfd.FileName)}";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Save SCI32 failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        // Export the currently loaded P56 image to a standard file (PNG/JPG/etc.)
        private void BtnExportImage_Click(object sender, EventArgs e)
        {
            if (_p56PictureBox.Image == null)
            {
                MessageBox.Show(this, "No image loaded.", "Export Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Export Image";
                sfd.Filter = ImageFileFilter;
                sfd.DefaultExt = "png";

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        // If a P56 is loaded we can use its ExportImage helper,
                        // otherwise just save the PictureBox image directly.
                        if (_currentP56 != null)
                            _currentP56.ExportImage(sfd.FileName, null);
                        else
                            _p56PictureBox.Image.Save(sfd.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Save Image Failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // Load a standard image (PNG/JPG/BMP/…) into the PictureBox
        private void BtnLoadImage_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Open Image";
                ofd.Filter = ImageFileFilter;
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var bmp = new Bitmap(ofd.FileName);
                        _p56PictureBox.Image = bmp;
                        _currentP56 = null;    // we loaded a plain image, not a P56
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Open Image Failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        /*
        // Save the current PictureBox image (or loaded P56 image) as a P56 file
        private void BtnExportP56_Click(object sender, EventArgs e)
        {
            if (_p56PictureBox.Image == null)
            {
                MessageBox.Show(this, "No image to save.", "Export P56",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Save as P56";
                sfd.Filter = "SCI32 P56 (*.p56)|*.p56|All Files (*.*)|*.*";
                sfd.DefaultExt = "p56";

                if (sfd.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    using (var src = new Bitmap(_p56PictureBox.Image))
                    using (var resized = new Bitmap(src, new Size(640, 480)))
                    {
                        // build palette
                        var colors = SCI32Suite.Palette.MedianCutQuantizer.ExtractPalette(resized, 256);
                        var palData = SCI32Suite.Palette.PaletteData.FromRgbList(colors);

                        // map pixels (reserve 255)
                        var palColors = new System.Drawing.Color[256];
                        for (int i = 0; i < 256; i++) palColors[i] = palData.GetColor(i);

                        byte NearestIndex(System.Drawing.Color c)
                        {
                            int bestI = 0, bestD = int.MaxValue;
                            for (int i = 0; i < 255; i++)
                            {
                                var k = palColors[i];
                                int dr = c.R - k.R, dg = c.G - k.G, db = c.B - k.B;
                                int d = dr * dr + dg * dg + db * db;
                                if (d < bestD) { bestD = d; bestI = i; }
                            }
                            return (byte)bestI;
                        }

                        var pix = new byte[640 * 480];
                        int p = 0;
                        for (int y = 0; y < 480; y++)
                            for (int x = 0; x < 640; x++, p++)
                            {
                                var c = resized.GetPixel(x, y);
                                byte idx = NearestIndex(c);
                                pix[p] = (idx == 255) ? (byte)254 : idx;
                            }

                        // Build using GOODCLIENT framing (0x0322 size=14 + tail, pixels at 870)
                        byte[] p56Bytes = P56File.BuildLegacyP56(pix, palData, 640, 480);

                        System.IO.File.WriteAllBytes(sfd.FileName, p56Bytes);
                    }

                    MessageBox.Show(this, "P56 saved successfully.", "Export P56",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Export P56 Failed",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        */
        private void BtnExportP56_Click(object sender, EventArgs e)
        {
            if (_p56PictureBox.Image == null)
            {
                MessageBox.Show(this, "No image to save.", "Export P56",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Save as P56";
                sfd.Filter = "SCI32 P56 (*.p56)|*.p56|All Files (*.*)|*.*";
                sfd.DefaultExt = "p56";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    P56File.SaveImageAsV56(_p56PictureBox.Image, sfd.FileName, false);
                    MessageBox.Show(this, "P56 saved successfully.", "Export P56",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Export P56 Failed",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }




        // Load a P56 file and display it in the PictureBox
        private void BtnLoadP56_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Open P56";
                ofd.Filter = "SCI32 P56 (*.p56)|*.p56|All Files (*.*)|*.*";

                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        _currentP56 = P56File.Load(ofd.FileName);
                        _p56PictureBox.Image = _currentP56.Image;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Open P56 Failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

    }
}
