using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AFKLocker.Core;

namespace AFKLocker.Setup
{
    /// <summary>
    /// One line of the readiness list: a status dot, a title and a detail line.
    /// Drawn rather than composed from labels so the dot aligns with the text
    /// baseline at any DPI.
    /// </summary>
    internal sealed class StatusRow : Control
    {
        private const int DotDiameter = 10;
        private const int DotColumnWidth = 26;

        private readonly ReadinessCheck _check;

        public StatusRow(ReadinessCheck check)
        {
            _check = check;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            TabStop = false;
        }

        internal static Color ColorFor(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Ready: return Color.FromArgb(16, 124, 16);
                case CheckStatus.NeedsConfiguration: return Color.FromArgb(196, 43, 28);
                case CheckStatus.Warning: return Color.FromArgb(157, 93, 0);
                case CheckStatus.Optional: return Color.FromArgb(97, 97, 97);
                default: return Color.FromArgb(168, 168, 168);
            }
        }

        private static string LabelFor(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Ready: return "Ready";
                case CheckStatus.NeedsConfiguration: return "Needs configuration";
                case CheckStatus.Warning: return "Note";
                case CheckStatus.Optional: return "Optional";
                default: return "Not applicable";
            }
        }

        /// <summary>Measures the height this row needs for the given width.</summary>
        public int MeasureHeight(int width)
        {
            using (Graphics g = CreateGraphics())
            using (Font titleFont = new Font(Font, FontStyle.Bold))
            {
                int textWidth = Math.Max(60, width - DotColumnWidth);
                int titleHeight = (int)Math.Ceiling(
                    g.MeasureString(TitleText, titleFont, textWidth).Height);
                int detailHeight = (int)Math.Ceiling(
                    g.MeasureString(_check.Detail ?? string.Empty, Font, textWidth).Height);
                return titleHeight + detailHeight + 16;
            }
        }

        private string TitleText
        {
            get { return _check.Title + "  -  " + LabelFor(_check.Status); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var dot = new SolidBrush(ColorFor(_check.Status)))
                g.FillEllipse(dot, 6, 6, DotDiameter, DotDiameter);

            int textLeft = DotColumnWidth;
            int textWidth = Math.Max(60, Width - textLeft);

            using (var titleFont = new Font(Font, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(Color.FromArgb(32, 32, 32)))
            using (var detailBrush = new SolidBrush(Color.FromArgb(94, 94, 94)))
            {
                var titleRect = new RectangleF(textLeft, 0, textWidth, 100);
                g.DrawString(TitleText, titleFont, titleBrush, titleRect);

                float titleHeight = g.MeasureString(TitleText, titleFont, textWidth).Height;
                var detailRect = new RectangleF(textLeft, titleHeight + 2, textWidth, 200);
                g.DrawString(_check.Detail ?? string.Empty, Font, detailBrush, detailRect);
            }
        }
    }
}
