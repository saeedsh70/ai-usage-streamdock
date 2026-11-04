// Draws the key images with GDI+ and returns them as PNG data URLs.
//
// Designed for small LCD keys: the SS-550 shows each key at about 100x100 px on a
// ~14 mm screen, so every image is drawn at 144x144 with at most three elements,
// bold type only, and nothing smaller than ~24 px (about 17 px on the device).
// Text is shrunk to fit its box instead of being clipped.
//
// Look: dark graphite gradient background, a tinted label chip, large digits with
// a smaller % sign, and status colour carried by a liquid fill, a glowing ring or a
// glowing bar.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;

namespace AiUsageDeck
{
    internal static class KeyRenderer
    {
        public const int Size = 144;

        private static readonly Color BgTop = Color.FromArgb(34, 35, 41);
        private static readonly Color BgBottom = Color.FromArgb(10, 10, 13);
        private static readonly Color Track = Color.FromArgb(48, 50, 58);
        private static readonly Color Ink = Color.FromArgb(255, 255, 255);
        private static readonly Color Soft = Color.FromArgb(214, 216, 224);
        private static readonly Color Dim = Color.FromArgb(140, 143, 155);
        public static readonly Color Accent = Color.FromArgb(240, 140, 105);   // Claude-like coral
        private static readonly Color Green = Color.FromArgb(52, 221, 160);
        private static readonly Color Amber = Color.FromArgb(255, 190, 50);
        private static readonly Color Red = Color.FromArgb(255, 92, 92);

        private const float MinPx = 18f;

        private static string _family;

        public static Color ColorFor(double pct, int warnAt, int dangerAt)
        {
            if (pct >= dangerAt) return Red;
            if (pct >= warnAt) return Amber;
            return Green;
        }

        #region tiles

        /// <summary>
        /// One limit. Styles:
        ///   fill   - liquid fills the key from the bottom; label chip, big %, reset time
        ///   number - big % in a colour gradient; label chip and reset time
        ///   bar    - label chip, big %, glowing bar
        ///   ring   - glowing ring around the edge, label and % inside
        /// </summary>
        public static string Meter(string label, double pct, DateTime? resetsAt, string style,
                                   bool showReset, bool stale, int warnAt, int dangerAt)
        {
            return Draw(delegate(Graphics g)
            {
                Color color = ColorFor(pct, warnAt, dangerAt);
                bool reset = showReset && resetsAt.HasValue;

                if (style == "ring")
                {
                    Ring(g, pct, color);
                    Text(g, label, 26f, color, new RectangleF(30, 30, Size - 60, 30), false);
                    Percent(g, pct, 46f, new RectangleF(22, 56, Size - 44, 54), Ink, Ink, false);
                }
                else if (style == "bar")
                {
                    Chip(g, label, color);
                    Percent(g, pct, 62f, new RectangleF(4, 38, Size - 8, 62), Ink, Ink, false);
                    Bar(g, new RectangleF(14, 110, Size - 28, 20), pct, color);
                }
                else if (style == "number")
                {
                    Glow(g, new PointF(Size / 2f, reset ? 72 : 86), 62f, color, 36);
                    Chip(g, label, color);
                    Color top = Lighten(color, 0.45);
                    if (reset)
                    {
                        Percent(g, pct, 64f, new RectangleF(0, 38, Size, 66), top, color, false);
                        Reset(g, resetsAt.Value, Soft, 106);
                    }
                    else Percent(g, pct, 74f, new RectangleF(0, 42, Size, 92), top, color, false);
                }
                else // fill
                {
                    Liquid(g, pct, color);
                    Chip(g, label, color);
                    if (reset)
                    {
                        Percent(g, pct, 62f, new RectangleF(0, 38, Size, 66), Ink, Ink, true);
                        Reset(g, resetsAt.Value, Ink, 106);
                    }
                    else Percent(g, pct, 72f, new RectangleF(0, 42, Size, 92), Ink, Ink, true);
                }
                if (stale) Stale(g);
            });
        }

        /// <summary>5-hour and weekly together: provider chip, then two rows of name, % and a bar.</summary>
        public static string Overview(string label, Limit session, Limit weekly, bool stale, int warnAt, int dangerAt)
        {
            return Draw(delegate(Graphics g)
            {
                double worst = Math.Max(session == null ? 0 : session.Percent, weekly == null ? 0 : weekly.Percent);
                Chip(g, label, ColorFor(worst, warnAt, dangerAt));
                Row(g, "5H", session, 44, warnAt, dangerAt);
                Row(g, "WK", weekly, 94, warnAt, dangerAt);
                if (stale) Stale(g);
            });
        }

        public static string Extra(string label, ExtraUsage x, bool stale, int warnAt, int dangerAt)
        {
            return Draw(delegate(Graphics g)
            {
                if (x == null || !x.Enabled)
                {
                    Chip(g, label, Dim);
                    Text(g, "OFF", 52f, Dim, new RectangleF(0, 46, Size, 80), false);
                }
                else
                {
                    double pct = x.Percent ?? 0;
                    Color color = x.Percent.HasValue ? ColorFor(pct, warnAt, dangerAt) : Green;
                    if (x.Percent.HasValue) Liquid(g, pct, color);
                    Chip(g, label, color);
                    bool sub = !string.IsNullOrEmpty(x.Sub);
                    Text(g, string.IsNullOrEmpty(x.Value) ? "-" : x.Value, sub ? 56f : 66f, Ink,
                         sub ? new RectangleF(0, 38, Size, 66) : new RectangleF(0, 42, Size, 92), true);
                    if (sub) Text(g, x.Sub, 28f, Ink, new RectangleF(4, 106, Size - 8, 34), true);
                }
                if (stale) Stale(g);
            });
        }

        /// <summary>A short status word or two ("SIGN IN", "OFFLINE") under the key label.</summary>
        public static string Message(string label, string title, Color color)
        {
            return Draw(delegate(Graphics g)
            {
                Chip(g, label, Dim);
                if (title == "...")
                {
                    // loading: three dots
                    for (int i = 0; i < 3; i++)
                    {
                        Color c = Color.FromArgb(255 - i * 60, color);
                        using (var b = new SolidBrush(c)) g.FillEllipse(b, 38 + i * 26, 82, 16, 16);
                    }
                    return;
                }
                Glow(g, new PointF(Size / 2f, 90), 52f, color, 28);
                string[] lines = title.Split('\n');
                if (lines.Length == 1)
                    Text(g, lines[0], 40f, color, new RectangleF(2, 46, Size - 4, 84), false);
                else
                {
                    Text(g, lines[0], 34f, color, new RectangleF(2, 48, Size - 4, 44), false);
                    Text(g, lines[1], 34f, color, new RectangleF(2, 92, Size - 4, 44), false);
                }
            });
        }

        #endregion

        #region pieces

        /// <summary>Rounded, colour-tinted chip with the key label, at the top centre.</summary>
        private static void Chip(Graphics g, string label, Color color)
        {
            if (string.IsNullOrEmpty(label)) return;
            using (Font font = Fit(g, label, 29f, Size - 20))
            {
                label = Ellipsize(g, label, font, Size - 20);
                SizeF size = Measure(g, label, font);
                float w = Math.Min(Size - 4, size.Width + 18), h = 36;
                var r = new RectangleF((Size - w) / 2f, 4, w, h);
                using (GraphicsPath p = Rounded(r, h / 2f))
                {
                    using (var fill = new SolidBrush(Color.FromArgb(150, Darken(color, 0.25))))
                        g.FillPath(fill, p);
                    using (var edge = new Pen(Color.FromArgb(150, color), 1.6f))
                        g.DrawPath(edge, p);
                }
                using (var brush = new SolidBrush(Lighten(color, 0.55)))
                    g.DrawString(label, font, brush, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), Align(StringAlignment.Center, label));
            }
        }

        /// <summary>Liquid filling the key from the bottom: gradient body, wavy surface, a darker wave behind.</summary>
        private static void Liquid(Graphics g, double pct, Color color)
        {
            double p = Math.Min(Math.Max(pct, 0), 100);
            if (p < 0.5) return;
            float level = (float)(Size - Size * p / 100.0);
            float amp = p >= 99.5 ? 0f : 4.5f;

            using (GraphicsPath back = Wave(level - 3, amp, 1.7f))
            using (var b = new SolidBrush(Color.FromArgb(120, Darken(color, 0.45))))
                g.FillPath(b, back);

            using (GraphicsPath front = Wave(level, amp, 0f))
            {
                var bounds = new RectangleF(0, level - amp - 1, Size, Size - level + amp + 2);
                using (var body = new LinearGradientBrush(bounds, Darken(color, 0.52), Darken(color, 0.82), 90f))
                    g.FillPath(body, front);
                using (var surface = new Pen(Color.FromArgb(210, color), 3f))
                    g.DrawPath(surface, SurfaceLine(level, amp, 0f));
            }
        }

        private static GraphicsPath Wave(float level, float amp, float phase)
        {
            var path = new GraphicsPath();
            path.AddLines(SurfacePoints(level, amp, phase));
            path.AddLine(Size + 2, level, Size + 2, Size + 2);
            path.AddLine(Size + 2, Size + 2, -2, Size + 2);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath SurfaceLine(float level, float amp, float phase)
        {
            var path = new GraphicsPath();
            path.AddLines(SurfacePoints(level, amp, phase));
            return path;
        }

        private static PointF[] SurfacePoints(float level, float amp, float phase)
        {
            var pts = new PointF[37];
            for (int i = 0; i < pts.Length; i++)
            {
                float x = -2 + i * (Size + 4) / 36f;
                pts[i] = new PointF(x, level + amp * (float)Math.Sin(x / Size * 2 * Math.PI * 1.5 + phase));
            }
            return pts;
        }

        private static void Ring(Graphics g, double pct, Color color)
        {
            var ring = new RectangleF(12, 12, Size - 24, Size - 24);
            using (var track = new Pen(Track, 13f)) g.DrawEllipse(track, ring);
            if (pct <= 0.4) return;
            float sweep = (float)(360.0 * Math.Min(pct, 100) / 100.0);
            // glow
            for (int i = 3; i >= 1; i--)
                using (var halo = new Pen(Color.FromArgb(22, color), 13f + i * 6f))
                {
                    halo.StartCap = LineCap.Round; halo.EndCap = LineCap.Round;
                    g.DrawArc(halo, ring, -90f, sweep);
                }
            using (var brush = new LinearGradientBrush(new RectangleF(0, 0, Size, Size), Lighten(color, 0.35), color, 90f))
            using (var pen = new Pen(brush, 13f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawArc(pen, ring, -90f, sweep);
            }
            // bright head at the end of the arc
            double a = (-90 + sweep) * Math.PI / 180.0;
            float cx = Size / 2f + (float)(Math.Cos(a) * ring.Width / 2), cy = Size / 2f + (float)(Math.Sin(a) * ring.Height / 2);
            using (var b = new SolidBrush(Lighten(color, 0.7))) g.FillEllipse(b, cx - 4.5f, cy - 4.5f, 9, 9);
        }

        private static void Row(Graphics g, string name, Limit limit, float top, int warnAt, int dangerAt)
        {
            double pct = limit == null ? 0 : limit.Percent;
            Color color = ColorFor(pct, warnAt, dangerAt);
            using (var nameFont = Fit(g, name, 26f, 44))
            using (var brush = new SolidBrush(Lighten(color, 0.5)))
                g.DrawString(name, nameFont, brush, new RectangleF(10, top, 46, 36), Align(StringAlignment.Near, name));
            if (limit == null)
                Text(g, "--", 32f, Dim, new RectangleF(54, top, Size - 64, 36), false);
            else
                Percent(g, pct, 36f, new RectangleF(54, top, Size - 64, 36), Ink, Ink, false, StringAlignment.Far);
            Bar(g, new RectangleF(10, top + 37, Size - 20, 10), pct, color);
        }

        private static void Bar(Graphics g, RectangleF r, double pct, Color color)
        {
            using (var track = new SolidBrush(Track))
            using (GraphicsPath p = Rounded(r, r.Height / 2f))
                g.FillPath(track, p);
            float w = (float)(r.Width * Math.Min(pct, 100) / 100.0);
            if (w <= 0.5f) return;
            if (w < r.Height) w = r.Height;
            var fill = new RectangleF(r.X, r.Y, w, r.Height);
            for (int i = 2; i >= 1; i--)
                using (var halo = new SolidBrush(Color.FromArgb(28, color)))
                using (GraphicsPath p = Rounded(RectangleF.Inflate(fill, i * 3, i * 3), r.Height / 2f + i * 3))
                    g.FillPath(halo, p);
            using (var brush = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height),
                                                       Darken(color, 0.15), Lighten(color, 0.3), 0f))
            using (GraphicsPath p = Rounded(fill, r.Height / 2f))
                g.FillPath(brush, p);
            // glossy top highlight
            using (var gloss = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            using (GraphicsPath p = Rounded(new RectangleF(fill.X + 3, fill.Y + 2, Math.Max(0, fill.Width - 6), fill.Height * 0.32f), fill.Height * 0.16f))
                g.FillPath(gloss, p);
        }

        /// <summary>Time until reset with a small circular-arrow icon.</summary>
        private static void Reset(Graphics g, DateTime resetsAtUtc, Color color, float top)
        {
            string text = Countdown(resetsAtUtc);
            using (Font font = Fit(g, text, 28f, Size - 40))
            {
                SizeF size = Measure(g, text, font);
                float icon = 20f, gap = 6f;
                float x = (Size - (icon + gap + size.Width)) / 2f;
                float cy = top + 18f;
                using (var pen = new Pen(color, 3f))
                {
                    pen.StartCap = LineCap.Round;
                    g.DrawArc(pen, x + 1.5f, cy - icon / 2f + 1.5f, icon - 3, icon - 3, -60, 290);
                }
                // arrow head at the start of the arc
                double a = -60 * Math.PI / 180.0;
                float ax = x + icon / 2f + (float)Math.Cos(a) * (icon - 3) / 2f;
                float ay = cy + (float)Math.Sin(a) * (icon - 3) / 2f;
                using (var b = new SolidBrush(color))
                    g.FillPolygon(b, new[] { new PointF(ax - 1, ay - 6), new PointF(ax + 6, ay + 1.5f), new PointF(ax - 3.5f, ay + 3.5f) });
                Shadowed(g, text, font, color, new RectangleF(x + icon + gap, top, size.Width + 6, 36), StringAlignment.Near, true);
            }
        }

        /// <summary>Soft radial glow made of stacked translucent circles.</summary>
        private static void Glow(Graphics g, PointF c, float radius, Color color, int alpha)
        {
            for (int i = 6; i >= 1; i--)
            {
                float r = radius * i / 6f;
                using (var b = new SolidBrush(Color.FromArgb(alpha / 6, color)))
                    g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            }
        }

        /// <summary>The last refresh failed and the numbers are old: a dashed amber frame.</summary>
        private static void Stale(Graphics g)
        {
            using (var pen = new Pen(Amber, 5f))
            {
                pen.DashStyle = DashStyle.Dash;
                using (GraphicsPath p = Rounded(new RectangleF(3, 3, Size - 6, Size - 6), 14))
                    g.DrawPath(pen, p);
            }
        }

        #endregion

        #region text

        /// <summary>Big digits with a smaller, raised % sign; shrinks to fit the box.</summary>
        private static void Percent(Graphics g, double pct, float maxPx, RectangleF area, Color top, Color bottom,
                                    bool shadow, StringAlignment align = StringAlignment.Center)
        {
            string digits = Math.Round(pct).ToString("0", CultureInfo.InvariantCulture);
            float px = Math.Min(maxPx, area.Height * 0.98f);
            Font big = null, small = null;
            SizeF d = SizeF.Empty, s = SizeF.Empty;
            while (true)
            {
                big = MakeFont(px);
                small = MakeFont(Math.Max(MinPx, px * 0.46f));
                d = Measure(g, digits, big);
                s = Measure(g, "%", small);
                if (d.Width + s.Width <= area.Width - 2 || px <= MinPx) break;
                big.Dispose(); small.Dispose();
                px -= 2f;
            }
            using (big)
            using (small)
            {
                const float gap = -1f; // typographic boxes already leave a little side bearing
                float total = d.Width + gap + s.Width;
                float x = align == StringAlignment.Far ? area.Right - total
                        : align == StringAlignment.Near ? area.X
                        : area.X + (area.Width - total) / 2f;
                // Vertically centre on the digits' cap height rather than the line box.
                float capTop = area.Y + (area.Height - px * 0.72f) / 2f;
                float lineY = capTop - px * 0.20f;
                var digitsBox = new RectangleF(x, lineY, d.Width + 4, px * 1.3f);
                var pctBox = new RectangleF(x + d.Width + gap, lineY + px * 0.10f, s.Width + 4, px);

                StringFormat fmt = Align(StringAlignment.Near, digits);
                fmt.LineAlignment = StringAlignment.Near;
                if (shadow)
                    using (var sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    {
                        g.DrawString(digits, big, sh, Offset(digitsBox, 2, 3), fmt);
                        g.DrawString("%", small, sh, Offset(pctBox, 2, 3), fmt);
                    }
                using (Brush brush = top == bottom
                    ? (Brush)new SolidBrush(top)
                    : new LinearGradientBrush(new RectangleF(x, capTop - 1, total + 1, px * 0.72f + 2), top, bottom, 90f))
                {
                    g.DrawString(digits, big, brush, digitsBox, fmt);
                    using (var pctBrush = new SolidBrush(Color.FromArgb(225, bottom == Ink ? Soft : bottom)))
                        g.DrawString("%", small, pctBrush, pctBox, fmt);
                }
            }
        }

        /// <summary>Draws text centred in its box, shrinking the font until it fits the width.</summary>
        private static void Text(Graphics g, string text, float maxPx, Color color, RectangleF area, bool shadow)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (Font font = Fit(g, text, Math.Min(maxPx, area.Height * 0.95f), area.Width))
                Shadowed(g, Ellipsize(g, text, font, area.Width), font, color, area, StringAlignment.Center, shadow);
        }

        private static void Shadowed(Graphics g, string text, Font font, Color color, RectangleF area,
                                     StringAlignment align, bool shadow)
        {
            StringFormat fmt = Align(align, text);
            if (shadow)
                using (var sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    g.DrawString(text, font, sh, Offset(area, 2, 2), fmt);
            using (var brush = new SolidBrush(color))
                g.DrawString(text, font, brush, area, fmt);
        }

        /// <summary>Shortens text that still does not fit at the smallest font size, ending it with an ellipsis.</summary>
        private static string Ellipsize(Graphics g, string text, Font font, float width)
        {
            if (Measure(g, text, font).Width <= width - 2) return text;
            string t = text;
            while (t.Length > 1)
            {
                t = t.Substring(0, t.Length - 1).TrimEnd();
                if (Measure(g, t + "\u2026", font).Width <= width - 2) return t + "\u2026";
            }
            return t;
        }

        private static RectangleF Offset(RectangleF r, float dx, float dy)
        {
            return new RectangleF(r.X + dx, r.Y + dy, r.Width, r.Height);
        }

        private static StringFormat Align(StringAlignment horizontal, string text)
        {
            var fmt = new StringFormat(StringFormat.GenericTypographic);
            fmt.Alignment = horizontal;
            fmt.LineAlignment = StringAlignment.Center;
            // GenericTypographic sets LineLimit, which drops a line taller than its box instead of overflowing.
            fmt.FormatFlags = (fmt.FormatFlags & ~StringFormatFlags.LineLimit) | StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            fmt.Trimming = StringTrimming.None;
            if (IsRtl(text)) fmt.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            return fmt;
        }

        private static SizeF Measure(Graphics g, string text, Font font)
        {
            return g.MeasureString(text, font, new PointF(0, 0), StringFormat.GenericTypographic);
        }

        private static Font Fit(Graphics g, string text, float maxPx, float width)
        {
            float px = maxPx;
            while (true)
            {
                Font f = MakeFont(px);
                if (px <= MinPx) return f;
                if (Measure(g, text, f).Width <= width - 2) return f;
                f.Dispose();
                px -= 2f;
            }
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Lighten(Color c, double t)
        {
            return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t));
        }

        private static Color Darken(Color c, double t)
        {
            return Color.FromArgb(c.A, (int)(c.R * (1 - t)), (int)(c.G * (1 - t)), (int)(c.B * (1 - t)));
        }

        public static string Pct(double pct)
        {
            return Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public static string Money(double dollars)
        {
            string fmt = dollars >= 100 ? "0" : "0.##";
            return "$" + dollars.ToString(fmt, CultureInfo.InvariantCulture);
        }

        /// <summary>Compact time until reset: 37m, 2h13m, 3d4h.</summary>
        public static string Countdown(DateTime resetsAtUtc)
        {
            TimeSpan left = resetsAtUtc - DateTime.UtcNow;
            if (left.TotalSeconds <= 0) return "now";
            if (left.TotalMinutes < 60) return Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + "m";
            if (left.TotalHours < 24) return (int)left.TotalHours + "h" + left.Minutes.ToString("00") + "m";
            return (int)left.TotalDays + "d" + left.Hours + "h";
        }

        private static bool IsRtl(string s)
        {
            foreach (char c in s)
                if ((c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFEFC')) return true;
            return false;
        }

        private static Font MakeFont(float px)
        {
            if (_family == null)
            {
                _family = FontFamily.GenericSansSerif.Name;
                foreach (string name in new[] { "Segoe UI", "Arial", "Tahoma" })
                {
                    try
                    {
                        using (var f = new FontFamily(name)) { _family = f.Name; break; }
                    }
                    catch (ArgumentException) { }
                }
            }
            return new Font(_family, px, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        #endregion

        private delegate void Painter(Graphics g);

        private static string Draw(Painter paint)
        {
            using (var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Size, Size), BgTop, BgBottom, 90f))
                        g.FillRectangle(bg, 0, 0, Size, Size);
                    paint(g);
                }
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }
    }
}
