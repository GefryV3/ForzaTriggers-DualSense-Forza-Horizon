// User interface: live telemetry panel (left) + settings cards (right).
// Pure WinForms + GDI+, custom-drawn controls so the dark theme looks the same on every Windows version.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ForzaTriggers
{
    // ============================================================ ТЕМА
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(14, 16, 20);
        public static readonly Color Panel = Color.FromArgb(21, 24, 30);
        public static readonly Color Card = Color.FromArgb(26, 30, 38);
        public static readonly Color Panel2 = Color.FromArgb(39, 44, 56);
        public static readonly Color Border = Color.FromArgb(46, 52, 66);
        public static readonly Color Hover = Color.FromArgb(52, 58, 72);
        public static readonly Color Text = Color.FromArgb(232, 234, 240);
        public static readonly Color Muted = Color.FromArgb(138, 146, 164);
        public static readonly Color Brake = Color.FromArgb(255, 84, 84);
        public static readonly Color Throttle = Color.FromArgb(53, 208, 127);
        public static readonly Color Vibe = Color.FromArgb(255, 176, 32);
        public static readonly Color PedalC = Color.FromArgb(77, 163, 255);
        public static readonly Color Haptic = Color.FromArgb(190, 120, 255);
        public static readonly Color Accent = Color.FromArgb(255, 122, 26);

        public static float K = 1f;                       // DPI scale (system DPI aware)
        public static int Px(float v) { return (int)Math.Round(v * K); }

        public static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // The mouse wheel goes to the focused control; send it to the scrolling settings list instead,
        // so scrolling never silently changes a slider/combo/number value.
        public static void ScrollParent(Control c, MouseEventArgs e)
        {
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
            for (var p = c.Parent; p != null; p = p.Parent)
            {
                var sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll)
                {
                    var pos = sc.AutoScrollPosition;
                    sc.AutoScrollPosition = new Point(-pos.X, Math.Max(0, -pos.Y - e.Delta));
                    return;
                }
            }
        }
    }

    // ============================================================ КОНТРОЛЫ
    class FlatSlider : Control
    {
        int min, max = 100, val;
        bool drag, hover;
        public event EventHandler ValueChanged;

        public FlatSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand;
        }

        public int Minimum { get { return min; } set { min = value; if (max < min) max = min; SetValue(val, false); } }
        public int Maximum { get { return max; } set { max = Math.Max(min, value); SetValue(val, false); } }
        public int Value { get { return val; } set { SetValue(value, true); } }

        void SetValue(int v, bool notify)
        {
            v = Util.Clamp(v, min, max);
            if (v == val) return;
            val = v; Invalidate();
            if (notify && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        float X0 { get { return Theme.Px(9); } }
        float X1 { get { return Width - Theme.Px(9); } }
        int FromX(int x) { return min + (int)Math.Round(Util.Clamp01((x - X0) / Math.Max(1f, X1 - X0)) * (max - min)); }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); drag = true; Value = FromX(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) Value = FromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { Theme.ScrollParent(this, e); }
        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int big = Math.Max(1, (max - min) / 10);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value = val - 1; break;
                case Keys.Right: case Keys.Up: Value = val + 1; break;
                case Keys.PageDown: Value = val - big; break;
                case Keys.PageUp: Value = val + big; break;
                case Keys.Home: Value = min; break;
                case Keys.End: Value = max; break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cy = Height / 2f, th = Theme.Px(4), x0 = X0, x1 = X1;
            float f = max > min ? (val - min) / (float)(max - min) : 0f;
            float x = x0 + (x1 - x0) * f;
            using (var p = Theme.Rounded(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2))
            using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
            if (x > x0 + 0.5f)
                using (var p = Theme.Rounded(new RectangleF(x0, cy - th / 2, x - x0, th), th / 2))
                using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
            float r = Theme.Px(hover || drag ? 8 : 7);
            if (Focused)
                using (var b = new SolidBrush(Color.FromArgb(60, Theme.Accent))) g.FillEllipse(b, x - r - 4, cy - r - 4, 2 * r + 8, 2 * r + 8);
            using (var b = new SolidBrush(Color.FromArgb(245, 246, 250))) g.FillEllipse(b, x - r, cy - r, 2 * r, 2 * r);
            using (var pen = new Pen(Theme.Accent, Math.Max(2f, Theme.K * 2))) g.DrawEllipse(pen, x - r, cy - r, 2 * r, 2 * r);
        }
    }

    class Toggle : Control
    {
        bool on, hover;
        public event EventHandler CheckedChanged;

        public Toggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Checked = !Checked; }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) Checked = !Checked; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { Theme.ScrollParent(this, e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float sw = Theme.Px(36), sh = Theme.Px(20), y = (Height - sh) / 2f;
            var track = new RectangleF(1, y, sw, sh);
            using (var p = Theme.Rounded(track, sh / 2))
            {
                using (var b = new SolidBrush(on ? Theme.Accent : (hover ? Theme.Hover : Theme.Panel2))) g.FillPath(b, p);
                if (Focused) using (var pen = new Pen(Color.FromArgb(150, Theme.Accent), 2)) g.DrawPath(pen, p);
            }
            float d = sh - Theme.Px(6);
            float kx = on ? track.Right - d - Theme.Px(3) : track.X + Theme.Px(3);
            using (var b = new SolidBrush(on ? Color.White : Color.FromArgb(200, 205, 215))) g.FillEllipse(b, kx, y + Theme.Px(3), d, d);
            var tr = new Rectangle((int)(sw + Theme.Px(12)), 0, Width - (int)(sw + Theme.Px(12)), Height);
            TextRenderer.DrawText(g, Text, Font, tr, on ? Theme.Text : Color.FromArgb(200, 204, 214),
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    class DarkCombo : ComboBox
    {
        public DarkCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Panel2; ForeColor = Theme.Text; ItemHeight = Theme.Px(22);
        }

        public string Placeholder;

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                using (var b = new SolidBrush(Theme.Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                if (Placeholder != null)
                    TextRenderer.DrawText(e.Graphics, Placeholder, Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                                          Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                return;
            }
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = !edit && (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, r, sel ? Color.White : Theme.Text,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseWheel(MouseEventArgs e) { if (DroppedDown) base.OnMouseWheel(e); else Theme.ScrollParent(this, e); }
    }

    class DarkNum : NumericUpDown
    {
        public DarkNum() { BackColor = Theme.Panel2; ForeColor = Theme.Text; BorderStyle = BorderStyle.FixedSingle; }
        protected override void OnMouseWheel(MouseEventArgs e) { Theme.ScrollParent(this, e); }
    }

    // Rounded card that stacks its rows vertically.
    class Card : FlowLayoutPanel
    {
        public Card()
        {
            FlowDirection = FlowDirection.TopDown; WrapContents = false;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Theme.Card;
            Padding = new Padding(Theme.Px(18), Theme.Px(14), Theme.Px(18), Theme.Px(12));
            Margin = new Padding(0, 0, 0, Theme.Px(14));
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Bg)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.Px(12)))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            }
        }
    }

    // ============================================================ ЖИВАЯ ПАНЕЛЬ (слева)
    class LivePanel : Control
    {
        public Status St;
        public string HapState, HapWarn, RecNote; public bool HapOk, RecBad; public float HapL, HapR;

        public LivePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        static Font Px(float px, FontStyle s) { return new Font("Segoe UI", px, s, GraphicsUnit.Pixel); }
        readonly Font fTitle = Px(21, FontStyle.Bold), fSmall = Px(12, FontStyle.Regular), fSmallB = Px(12, FontStyle.Bold),
                      fBig = Px(56, FontStyle.Bold), fGear = Px(34, FontStyle.Bold), fLabel = Px(13, FontStyle.Bold);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float k = g.DpiX / 96f;
            g.ScaleTransform(k, k);
            float W = Width / k, H = Height / k;
            var st = St ?? new Status();

            using (var tb = new SolidBrush(Theme.Text))
            using (var mb = new SolidBrush(Theme.Muted))
            using (var ab = new SolidBrush(Theme.Accent))
            {
                // --- заголовок
                g.DrawString("FORZA", fTitle, ab, 18, 14);
                float fw = g.MeasureString("FORZA", fTitle).Width;
                g.DrawString("→ DUALSENSE", fTitle, tb, 14 + fw, 14);
                g.DrawString(Loc.T("Адаптивные курки + хаптика · Forza Horizon", "Adaptive triggers + haptics · Forza Horizon"), fSmall, mb, 20, 44);

                // --- карточка состояния
                float cardY = 70, cardH = HapWarn != null ? 112 : 78;
                using (var cp = Theme.Rounded(new RectangleF(14, cardY, W - 28, cardH), 10))
                using (var cb = new SolidBrush(Theme.Card)) g.FillPath(cb, cp);

                float y = cardY + 8;
                bool fOk = st.Fresh && st.RxError == null;
                Dot(g, 28, y + 8, fOk ? Theme.Throttle : Theme.Brake);
                string fTxt = st.RxError != null ? st.RxError
                    : fOk ? ("Forza: " + st.Pps + Loc.T(" пак/с · ", " pkt/s · ") + st.Len + Loc.T(" байт", " bytes") + (st.RaceOn ? "" : Loc.T(" · меню", " · menu")))
                          : Loc.T("Forza: нет данных — включи Data Out (127.0.0.1)", "Forza: no data — enable Data Out (127.0.0.1)");
                g.DrawString(fTxt, fSmall, tb, new RectangleF(38, y, W - 62, 18));
                y += 21;
                if (st.Direct != null)
                {
                    Dot(g, 28, y + 8, st.DirectReady ? Theme.Throttle : Theme.Vibe);
                    g.DrawString(Loc.T("Без DSX: ", "No DSX: ") + st.Direct + (st.DirectReady && !string.IsNullOrEmpty(st.DirectButtons) && st.DirectButtons != "—" ? " · " + st.DirectButtons : ""),
                                 fSmall, tb, new RectangleF(38, y, W - 62, 18));
                }
                else
                {
                    Dot(g, 28, y + 8, st.DsxReplies ? Theme.Throttle : Theme.Vibe);
                    g.DrawString("DSX :" + st.DsxPort + " · " + st.SendPs + Loc.T(" пак/с", " pkt/s") +
                                 (st.DsxReplies ? Loc.T(" · отвечает", " · responding") + (st.Battery >= 0 ? Loc.T(" · батарея ", " · battery ") + st.Battery + "%" : "")
                                                : Loc.T(" · ответа нет", " · no reply")),
                                 fSmall, tb, new RectangleF(38, y, W - 62, 18));
                }
                y += 21;
                Dot(g, 28, y + 8, HapOk && HapWarn == null ? Theme.Haptic : Theme.Vibe);
                g.DrawString(Loc.T("Хаптика: ", "Haptics: ") + (HapState ?? "—"), fSmall, tb, new RectangleF(38, y, W - 150, 18));
                Meter(g, W - 104, y + 4, 34, HapL); Meter(g, W - 64, y + 4, 34, HapR);
                if (HapWarn != null)
                {
                    using (var wb = new SolidBrush(Theme.Vibe))
                        g.DrawString("⚠ " + HapWarn, fSmall, wb, new RectangleF(38, y + 20, W - 62, 32));
                }

                // --- скорость, передача, обороты
                y = cardY + cardH + 14;
                g.DrawString(((int)Math.Round(st.Kmh)).ToString(), fBig, tb, 14, y - 6);
                string unit = Loc.T("км/ч", "km/h");
                g.DrawString(unit, fSmall, mb, 20, y + 62);
                if (st.Fresh && st.EngineName != null)
                    using (var hb = new SolidBrush(Theme.Haptic))
                        g.DrawString("· " + st.EngineName, fSmall, hb, 24 + g.MeasureString(unit, fSmall).Width, y + 62);
                var gr = new RectangleF(W - 92, y + 4, 72, 64);
                using (var gp = Theme.Rounded(gr, 12)) using (var b2 = new SolidBrush(Theme.Card)) g.FillPath(b2, gp);
                string gear = st.Gear == 0 ? "R" : (st.Gear > 10 ? "N" : st.Gear.ToString());
                if (!st.Fresh) gear = "–";
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(gear, fGear, tb, gr, sf);

                float rpmY = y + 86;
                var rr = new RectangleF(20, rpmY, W - 40, 8);
                using (var gp = Theme.Rounded(rr, 4)) using (var b2 = new SolidBrush(Theme.Panel2)) g.FillPath(b2, gp);
                float rf = st.MaxRpm > 0 ? Util.Clamp01(st.Rpm / st.MaxRpm) : 0;
                if (rf > 0.01f)
                {
                    var rfill = new RectangleF(20, rpmY, (W - 40) * rf, 8);
                    Color rc = rf > 0.97f ? Theme.Brake : (rf > 0.85f ? Theme.Vibe : Theme.Throttle);
                    using (var gp = Theme.Rounded(rfill, 4)) using (var b2 = new SolidBrush(rc)) g.FillPath(b2, gp);
                }
                g.DrawString(((int)st.Rpm) + Loc.T(" об/мин", " rpm"), fSmall, mb, 20, rpmY + 12);
                g.DrawString(string.Format(CultureInfo.InvariantCulture, Loc.T("скольж. торм {0:0.00} · вед {1:0.00}", "slip brake {0:0.00} · driven {1:0.00}"), st.BrakeSlip, st.DrivenSlip),
                             fSmall, mb, W - 20, rpmY + 12, new StringFormat { Alignment = StringAlignment.Far });

                // --- курки
                float gy = rpmY + 42;
                float gw = (W - 60) / 2f;
                DrawTrigger(g, 20, gy, gw, Loc.T("L2 · ТОРМОЗ", "L2 · BRAKE"), st.Brk, st.L, Theme.Brake);
                DrawTrigger(g, 40 + gw, gy, gw, Loc.T("R2 · ГАЗ", "R2 · THROTTLE"), st.Thr, st.R, Theme.Throttle);

                if (!string.IsNullOrEmpty(RecNote))
                    using (var rb = new SolidBrush(RecBad ? Theme.Vibe : Theme.Muted))
                        g.DrawString((RecBad ? "⚠ " : "● ") + RecNote, fSmall, rb, new RectangleF(20, H - 24, W - 40, 18));
            }
        }

        void Meter(Graphics g, float x, float y, float w, float v)
        {
            using (var p = Theme.Rounded(new RectangleF(x, y, w, 10), 3)) using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
            if (v > 0.01f)
                using (var p = Theme.Rounded(new RectangleF(x, y, w * Util.Clamp01(v), 10), 3)) using (var b = new SolidBrush(Theme.Haptic)) g.FillPath(b, p);
        }

        void DrawTrigger(Graphics g, float x, float y, float w, string title, float input, Eff e, Color col)
        {
            using (var tb = new SolidBrush(Theme.Text))
            using (var mb = new SolidBrush(Theme.Muted))
            {
                g.DrawString(title, fSmallB, mb, x, y);
                float top = y + 22, h = 200, barW = 26;
                var bar = new RectangleF(x, top, barW, h);
                using (var gp = Theme.Rounded(bar, 8)) using (var b2 = new SolidBrush(Theme.Panel2)) g.FillPath(b2, gp);
                if (input > 0.005f)
                {
                    var fill = new RectangleF(x, top, barW, h * input);
                    using (var gp = Theme.Rounded(fill, 8)) using (var b2 = new SolidBrush(col)) g.FillPath(b2, gp);
                }
                g.DrawString(((int)(input * 100)) + "%", fSmall, tb, x - 2, top + h + 6);

                // профиль по 10 зонам хода
                float px = x + barW + 10, pw = w - barW - 10, zh = h / 10f;
                for (int z = 0; z < 10; z++)
                {
                    var zr = new RectangleF(px, top + z * zh + 1, pw, zh - 2);
                    using (var gp = Theme.Rounded(zr, 3)) using (var b2 = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillPath(b2, gp);
                    float s = 0; Color c = Theme.PedalC;
                    if (e.Kind == EffKind.Curve && e.Zones != null) s = e.Zones[z];
                    else if (e.Kind == EffKind.Vibe && z >= e.Pos) { s = e.Amp; c = Theme.Vibe; }
                    else if (e.Kind == EffKind.Machine && z >= e.Pos) { s = Math.Max(e.Amp, e.AmpB) + 1; c = Theme.Vibe; }
                    else if (e.Kind == EffKind.Gallop && z >= e.Pos) { s = 6; c = Theme.Vibe; }
                    if (s > 0)
                    {
                        var fr = new RectangleF(px, zr.Y, Math.Max(6f, pw * s / 8f), zr.Height);
                        using (var gp = Theme.Rounded(fr, 3)) using (var b2 = new SolidBrush(c)) g.FillPath(b2, gp);
                    }
                }
                using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, px - 4, top + h * input, px + pw, top + h * input);

                Color lc = e.Kind == EffKind.Curve ? Theme.PedalC : (e.Kind == EffKind.Off ? Theme.Muted : Theme.Vibe);
                using (var lb = new SolidBrush(lc))
                    g.DrawString(e.Label ?? "", fLabel, lb, new RectangleF(x, top + h + 26, w + 10, 40));
                if (e.Kind == EffKind.Vibe || e.Kind == EffKind.Machine || e.Kind == EffKind.Gallop)
                    g.DrawString(e.Freq + Loc.T(" Гц", " Hz") + (e.Kind == EffKind.Machine ? " · " + e.Amp + "/" + e.AmpB : ""), fSmall, mb, x, top + h + 46);
            }
        }

        static void Dot(Graphics g, float cx, float cy, Color c)
        {
            using (var b = new SolidBrush(Color.FromArgb(50, c))) g.FillEllipse(b, cx - 7, cy - 7, 14, 14);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 4, cy - 4, 8, 8);
        }
    }

    // ============================================================ ГЛАВНОЕ ОКНО
    class MainForm : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int ContentW = 620;            // ширина карточек настроек (логические px)
        readonly Settings S;
        Engine engine;
        readonly HapticSynth synth = new HapticSynth();
        readonly HapticOutput hout;
        readonly LivePanel live;
        readonly FlowLayoutPanel flow;
        readonly Label headerTitle, headerSub;
        readonly Button bRu, bEn, bTestL, bTestR, bTestH, recButton, recStop;
        readonly LinkLabel ghLink;
        readonly List<Action> refreshers = new List<Action>();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200 };
        readonly System.Windows.Forms.Timer uiTimer, saveTimer;
        bool loading;
        Card card;
        DirectController direct;

        static int Px(float v) { return Theme.Px(v); }
        int RowW { get { return Px(ContentW - 36); } }

        public MainForm()
        {
            S = Settings.Load();
            Loc.Apply(S.Language);
            using (var g0 = CreateGraphics()) Theme.K = g0.DpiX / 96f;

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;   // всё масштабируем сами через Theme.Px — так работает и пересборка UI при смене языка
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(Px(1100), Px(800));
            MinimumSize = new Size(Px(980), Px(680));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // --- левая колонка: живые данные + кнопки тестов
            var left = new Panel { Dock = DockStyle.Left, Width = Px(440), BackColor = Theme.Panel };
            live = new LivePanel { Dock = DockStyle.Fill };
            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Px(62), BackColor = Theme.Panel, Padding = new Padding(Px(14), Px(12), Px(4), Px(8)), WrapContents = false };
            bTestL = MakeButton(60, delegate { engine.StartTest(1); });
            bTestR = MakeButton(60, delegate { engine.StartTest(2); });
            bTestH = MakeButton(90, delegate { engine.StartTest(3); });
            recButton = MakeButton(80, delegate { engine.StartRecording(); });
            recButton.Margin = new Padding(0, 0, Px(2), 0);
            recStop = MakeButton(34, delegate { engine.StopRecording(); });
            recStop.Text = "⏸";
            btnRow.Controls.AddRange(new Control[] { bTestL, bTestR, bTestH, recButton, recStop });
            left.Controls.Add(live);
            left.Controls.Add(btnRow);

            // --- шапка справа: заголовок + язык + ссылка на GitHub
            var header = new Panel { Dock = DockStyle.Top, Height = Px(74), BackColor = Theme.Bg };
            headerTitle = new Label { AutoSize = true, Left = Px(24), Top = Px(14), Font = new Font("Segoe UI Semibold", 15f), ForeColor = Theme.Text };
            headerSub = new Label { AutoSize = true, Left = Px(26), Top = Px(46), ForeColor = Theme.Muted };
            bEn = LangButton("EN", 1);
            bRu = LangButton("RU", 0);
            ghLink = new LinkLabel
            {
                Text = "GitHub ↗", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right,
                LinkColor = Theme.Muted, ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Muted, LinkBehavior = LinkBehavior.HoverUnderline
            };
            ghLink.LinkClicked += delegate { try { Process.Start(Program.RepoUrl); } catch { } };
            header.Controls.AddRange(new Control[] { headerTitle, headerSub, bRu, bEn, ghLink });
            header.Resize += delegate { LayoutHeader(header); };

            flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(Px(24), Px(2), Px(16), Px(24))
            };
            BuildSettings();

            // порядок важен для Dock: левая колонка во всю высоту, шапка — только над настройками
            Controls.Add(flow);
            Controls.Add(header);
            Controls.Add(left);
            ApplyTexts();
            ResumeLayout(false);
            PerformLayout();
            LayoutHeader(header);

            hout = new HapticOutput(synth);
            hout.PreferDsx = () => S.HapticViaDsx && !S.DirectMode;
#if !UIPREVIEW
            hout.Start();      // превью-сборка для скриншотов не трогает аудио геймпада
#endif
            StartBackend();

            uiTimer = new System.Windows.Forms.Timer { Interval = 33 };
            int warnTick = 0;
            live.HapWarn = S.DirectMode ? null : DsxProfile.RumbleWarning();
            uiTimer.Tick += delegate
            {
                if (++warnTick >= 90) { warnTick = 0; live.HapWarn = S.HapticEnabled && !S.DirectMode ? DsxProfile.RumbleWarning() : null; } // раз в 3 с
                var eng = engine;
                live.St = eng.Stat;
                recButton.Text = eng.Recording ? "● " + (eng.RecElapsedMs / 1000) + Loc.T(" с", " s")
                               : (eng.RecStatus != null ? (eng.RecFailed ? "▶ ✗" : "▶ ✓") : Loc.T("▶ Запись", "▶ Record"));
                live.RecNote = eng.Recording ? null : eng.RecStatus; live.RecBad = eng.RecFailed;
                live.HapOk = hout.Ok; live.HapState = S.HapticEnabled ? hout.State : Loc.T("выключена", "off");
                live.HapL = synth.PeakL; live.HapR = synth.PeakR;
                live.Invalidate();
            };
            uiTimer.Start();

            saveTimer = new System.Windows.Forms.Timer { Interval = 600 };
            saveTimer.Tick += delegate { saveTimer.Stop(); S.Save(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // тёмный заголовок окна (Windows 10 20H1+ / Windows 11); на старых системах просто ничего не произойдёт
            try { int on = 1; if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch { }
        }

        void LayoutHeader(Panel header)
        {
            int right = header.ClientSize.Width - Px(18);
            bEn.Left = right - bEn.Width; bEn.Top = Px(20);
            bRu.Left = bEn.Left - bRu.Width; bRu.Top = Px(20);
            ghLink.Left = bRu.Left - ghLink.Width - Px(16); ghLink.Top = Px(26);
        }

        Button LangButton(string text, int lang)
        {
            var b = new Button
            {
                Text = text, Width = Px(42), Height = Px(30), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 9f), TabStop = false
            };
            b.FlatAppearance.BorderColor = Theme.Border;
            b.Click += delegate { SetLanguage(lang); };
            return b;
        }

        void ApplyTexts()
        {
            Text = Loc.T("ForzaTriggers 2 — адаптивные курки и хаптика DualSense для Forza Horizon",
                         "ForzaTriggers 2 — DualSense adaptive triggers & haptics for Forza Horizon");
            headerTitle.Text = Loc.T("Настройки", "Settings");
            headerSub.Text = Loc.T("Всё сохраняется автоматически", "Everything is saved automatically");
            bTestL.Text = Loc.T("Тест L2", "Test L2");
            bTestR.Text = Loc.T("Тест R2", "Test R2");
            bTestH.Text = Loc.T("Тест хаптики", "Haptics test");
            recButton.Text = Loc.T("▶ Запись", "▶ Record");
            tips.SetToolTip(recButton, Loc.T("Записать телеметрию игры в файл telemetry-*.bin рядом с exe (для настройки и отчётов об ошибках)",
                                             "Record game telemetry to telemetry-*.bin next to the exe (for tuning and bug reports)"));
            tips.SetToolTip(recStop, Loc.T("Остановить запись и сохранить", "Stop recording and save"));
            tips.SetToolTip(bTestL, Loc.T("6 секунд: педаль тормоза, ABS, блокировка — без игры", "6 seconds: brake pedal, ABS, lock-up — no game needed"));
            tips.SetToolTip(bTestR, Loc.T("6 секунд: педаль газа, пробуксовка, отсечка, переключения — без игры", "6 seconds: throttle pedal, wheelspin, rev limiter, shifts — no game needed"));
            tips.SetToolTip(bTestH, Loc.T("Демо хаптики: разгон, переключения, поворот, удары, приземление", "Haptics demo: acceleration, shifts, cornering, impacts, landing"));
            StyleLang(bRu, !Loc.En); StyleLang(bEn, Loc.En);
        }

        static void StyleLang(Button b, bool active)
        {
            b.BackColor = active ? Theme.Accent : Theme.Panel2;
            b.ForeColor = active ? Color.White : Theme.Muted;
            b.FlatAppearance.MouseOverBackColor = active ? Theme.Accent : Theme.Hover;
        }

        void SetLanguage(int lang)
        {
            if ((lang == 1) == Loc.En && S.Language == lang) return;
            S.Language = lang;
            Loc.Apply(lang);
            S.Save();
            int scroll = -flow.AutoScrollPosition.Y;
            flow.SuspendLayout();
            while (flow.Controls.Count > 0) flow.Controls[0].Dispose();
            tips.RemoveAll();
            refreshers.Clear();
            BuildSettings();
            flow.ResumeLayout();
            flow.AutoScrollPosition = new Point(0, scroll);
            ApplyTexts();
            // статусы в фоновых потоках формируются при подключении — переподключаемся, чтобы они тоже сменили язык
            DsxProfile.Invalidate();
            live.HapWarn = S.HapticEnabled && !S.DirectMode ? DsxProfile.RumbleWarning() : null;
#if !UIPREVIEW
            hout.Stop(); hout.Start();
#endif
            RestartBackend();
        }

        void StartBackend()
        {
            if (S.DirectMode) { direct = new DirectController(); direct.Start(); }
            engine = new Engine(S, synth) { Direct = direct };
            engine.Start();
        }

        void StopBackend()
        {
            if (engine != null) engine.Stop(direct == null);
            if (direct != null) { direct.Stop(); direct = null; }
        }

        void RestartBackend()
        {
            S.Save();
            StopBackend();
            StartBackend();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            uiTimer.Stop();
            S.Save();
            StopBackend();
            synth.P = new HapticParams();
            Thread.Sleep(60);
            hout.Stop();
            base.OnFormClosing(e);
        }

        void Changed()
        {
            if (loading) return;
            saveTimer.Stop(); saveTimer.Start();
        }

        Button MakeButton(int w, EventHandler onClick)
        {
            var b = new Button
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(Px(w), Px(36)), Height = Px(36),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel2, ForeColor = Theme.Text, Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, Px(8), 0), Padding = new Padding(Px(4), 0, Px(4), 0)
            };
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Theme.Hover;
            b.FlatAppearance.MouseDownBackColor = Theme.Border;
            b.Click += onClick;
            return b;
        }

        Button Primary(Button b)
        {
            b.BackColor = Theme.Accent; b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Theme.Accent;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 145, 60);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(220, 100, 15);
            b.Font = new Font("Segoe UI Semibold", 9.5f);
            return b;
        }

        // ---------- строители строк настроек
        void Section(string title, string hint)
        {
            card = new Card { MinimumSize = new Size(Px(ContentW), 0) };
            flow.Controls.Add(card);
            card.Controls.Add(new Label
            {
                Text = title, AutoSize = true, ForeColor = Theme.Accent, BackColor = Theme.Card,
                Font = new Font("Segoe UI Semibold", 11.5f), Margin = new Padding(0, 0, 0, Px(hint != null ? 2 : 8))
            });
            if (hint != null)
                card.Controls.Add(new Label
                {
                    Text = hint, AutoSize = true, MaximumSize = new Size(RowW, 0), ForeColor = Theme.Muted, BackColor = Theme.Card,
                    Margin = new Padding(0, 0, 0, Px(10))
                });
        }

        void Slider(string caption, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt, string tip)
        {
            var row = new Panel { Width = RowW, Height = Px(36), Margin = new Padding(0), BackColor = Theme.Card };
            var lbl = new Label { Text = caption, Left = 0, Top = 0, Width = Px(230), Height = Px(36), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var sl = new FlatSlider { Minimum = min, Maximum = max, Left = Px(236), Top = Px(4), Width = RowW - Px(236 + 80), Height = Px(28), BackColor = Theme.Card };
            var val = new Label
            {
                Left = RowW - Px(74), Top = 0, Width = Px(74), Height = Px(36), ForeColor = Theme.Vibe,
                TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI Semibold", 9.5f)
            };
            Action refresh = delegate { sl.Value = Util.Clamp(get(), min, max); val.Text = fmt(sl.Value); };
            refresh();
            refreshers.Add(refresh);
            sl.ValueChanged += delegate { set(sl.Value); val.Text = fmt(sl.Value); Changed(); };
            if (tip != null) { tips.SetToolTip(lbl, tip); tips.SetToolTip(sl, tip); }
            row.Controls.Add(lbl); row.Controls.Add(sl); row.Controls.Add(val);
            card.Controls.Add(row);
        }

        void Check(string caption, Func<bool> get, Action<bool> set, string tip)
        {
            var tg = new Toggle { Text = caption, Width = RowW, Height = Px(34), ForeColor = Theme.Text, BackColor = Theme.Card, Margin = new Padding(0) };
            Action refresh = delegate { tg.Checked = get(); };
            refresh();
            refreshers.Add(refresh);
            tg.CheckedChanged += delegate { set(tg.Checked); Changed(); };
            if (tip != null) tips.SetToolTip(tg, tip);
            card.Controls.Add(tg);
        }

        void Combo(string caption, string[] items, Func<int> get, Action<int> onSel, string tip = null)
        {
            var row = new Panel { Width = RowW, Height = Px(38), Margin = new Padding(0, Px(1), 0, Px(1)), BackColor = Theme.Card };
            var lbl = new Label { Text = caption, Left = 0, Top = 0, Width = Px(230), Height = Px(38), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var cb = new DarkCombo { Left = Px(236), Top = Px(5), Width = RowW - Px(236) };
            if (get == null) cb.Placeholder = Loc.T("Выбери пресет…", "Choose a preset…");
            cb.Items.AddRange(items);
            if (get != null)
            {
                Action refresh = delegate { int v = get(); if (v >= 0 && v < items.Length) cb.SelectedIndex = v; };
                refresh(); refreshers.Add(refresh);
            }
            cb.SelectedIndexChanged += delegate { if (cb.SelectedIndex >= 0 && !loading) onSel(cb.SelectedIndex); };
            if (tip != null) { tips.SetToolTip(lbl, tip); tips.SetToolTip(cb, tip); }
            row.Controls.Add(lbl); row.Controls.Add(cb);
            card.Controls.Add(row);
        }

        static string Pct(int v) { return v + "%"; }
        static string Num(int v) { return v.ToString(); }
        static string Slip(int v) { return (v / 100f).ToString("0.00", CultureInfo.InvariantCulture); }
        static string Hz(int v) { return v + Loc.T(" Гц", " Hz"); }

        void RefreshAll()
        {
            loading = true;
            foreach (var r in refreshers) r();
            loading = false;
            Changed();
        }

        void BuildSettings()
        {
            Section(Loc.T("ОБЩЕЕ", "GENERAL"), null);
            Check(Loc.T("Отдача включена", "Force feedback enabled"), () => S.Enabled, v => S.Enabled = v, null);
            Combo(Loc.T("Пресет", "Preset"), Presets.Names, null, i => { Presets.Apply(S, i); RefreshAll(); },
                  Loc.T("Загружает набор значений ниже. Режим работы, порты и язык не меняются.", "Loads a set of values below. Mode, ports and language are kept."));
            Combo(Loc.T("Режим работы", "Mode"),
                  new[] { Loc.T("Через DSX (Bluetooth или USB)", "Through DSX (Bluetooth or USB)"),
                          Loc.T("Без DSX — USB напрямую (свой Xbox-геймпад, хаптика 16 бит)", "No DSX — direct USB (own Xbox pad, 16-bit haptics)") },
                  () => S.DirectMode ? 1 : 0, i => { S.DirectMode = i == 1; RestartBackend(); },
                  Loc.T("Без DSX нужны драйверы ViGEmBus и HidHide, геймпад — только по кабелю.", "Direct mode needs the ViGEmBus and HidHide drivers and a USB cable."));
            Combo(Loc.T("Режим DSX", "DSX protocol"),
                  new[] { Loc.T("DSX v3 — точный (кривые педалей, насос ABS)", "DSX v3 — precise (pedal curves, ABS pump)"),
                          Loc.T("Совместимый (DSX v2 / старые версии)", "Compatible (DSX v2 / older versions)") },
                  () => S.AdvancedModes ? 0 : 1, i => { S.AdvancedModes = i == 0; Changed(); });
            Slider(Loc.T("Сила курков", "Trigger strength"), 0, 150, () => S.MasterStrength, v => S.MasterStrength = v, Pct,
                   Loc.T("Масштаб всех эффектов курков", "Scales every trigger effect"));
            Slider(Loc.T("Плавность", "Smoothing"), 0, 100, () => S.Smoothing, v => S.Smoothing = v, Pct,
                   Loc.T("Больше — мягче переходы, меньше — резче отклик", "Higher — softer transitions, lower — snappier response"));
            Check(Loc.T("Подсветка = шифт-лайт (по оборотам)", "Light bar = shift light (by RPM)"), () => S.LightBar, v => S.LightBar = v,
                  Loc.T("Световая полоса: зелёный → красный, мигает на отсечке", "Light bar goes green → red and flashes at the limiter"));
            Check(Loc.T("Шифт-лайт на 5 индикаторах игрока (без DSX)", "Shift light on the 5 player LEDs (no-DSX mode)"), () => S.ShiftLeds, v => S.ShiftLeds = v,
                  Loc.T("Светодиоды под тачпадом заполняются по оборотам и мигают на отсечке", "The LEDs under the touchpad fill up with RPM and flash at the limiter"));

            Section(Loc.T("ТОРМОЗ · L2", "BRAKE · L2"),
                    Loc.T("Люфт → точка схватывания → педаль «стекленеет» к концу хода. У предела сцепления сразу за пальцем вырастает «стена».",
                          "Free play → bite point → the pedal firms up towards the end of travel. Near the grip limit a \"wall\" rises right behind your finger."));
            Slider(Loc.T("Люфт (свободный ход)", "Free play"), 0, 4, () => S.BrakeFreeTravel, v => S.BrakeFreeTravel = v, v => (v * 10) + "%", null);
            Slider(Loc.T("Жёсткость схватывания", "Bite-point stiffness"), 1, 8, () => S.BrakeMinForce, v => S.BrakeMinForce = v, Num, null);
            Slider(Loc.T("Жёсткость в конце", "End-of-travel stiffness"), 1, 8, () => S.BrakeMaxForce, v => S.BrakeMaxForce = v, Num, null);
            Slider(Loc.T("Прогрессия кривой", "Curve progression"), 80, 300, () => S.BrakeProgression, v => S.BrakeProgression = v,
                   v => (v / 100f).ToString("0.0", CultureInfo.InvariantCulture) + "×",
                   Loc.T("1.0 — линейно; 2+ — мягко в начале и резко твердеет в конце", "1.0 — linear; 2+ — soft at first, then firms up sharply"));
            Slider(Loc.T("Плотность от замедления", "Firmness from deceleration"), 0, 100, () => S.BrakeDecelWeight, v => S.BrakeDecelWeight = v, Pct,
                   Loc.T("Чем сильнее машина реально замедляется, тем плотнее вся педаль", "The harder the car actually decelerates, the firmer the whole pedal"));
            Check(Loc.T("«Стена» перед блокировкой (чувство предела)", "\"Wall\" before lock-up (feel the limit)"), () => S.BrakeLimitFeel, v => S.BrakeLimitFeel = v,
                  Loc.T("Когда колёса близки к срыву, дальше давить тяжелее — легко держать порог", "When the wheels are about to lock, pressing further gets harder — easy to trail-brake at the threshold"));
            Check("ABS", () => S.AbsEnabled, v => S.AbsEnabled = v, null);
            Combo(Loc.T("Характер ABS", "ABS feel"),
                  new[] { Loc.T("Насос — пульсирующие толчки", "Pump — pulsing kicks"), Loc.T("Пульс — ровная вибрация", "Pulse — even vibration"),
                          Loc.T("Стук — двойные удары клапанов", "Knock — double valve hits") },
                  () => S.AbsStyle == 1 ? 0 : (S.AbsStyle == 0 ? 1 : 2), i => { S.AbsStyle = i == 0 ? 1 : (i == 1 ? 0 : 2); Changed(); });
            Slider(Loc.T("Порог ABS (скольжение)", "ABS threshold (slip)"), 20, 150, () => S.AbsThreshold, v => S.AbsThreshold = v, Slip,
                   Loc.T("Меньше — ABS раньше. Текущее скольжение видно слева.", "Lower — ABS kicks in earlier. Live slip is shown on the left."));
            Slider(Loc.T("Сила ABS", "ABS strength"), 0, 100, () => S.AbsStrength, v => S.AbsStrength = v, Pct, null);
            Slider(Loc.T("Частота ABS", "ABS frequency"), 6, 40, () => S.AbsFrequency, v => S.AbsFrequency = v, Hz,
                   Loc.T("Реальные ABS ≈ 12–20 Гц", "Real ABS units run at ≈ 12–20 Hz"));
            Check(Loc.T("Блокировка колёс (если ABS в игре выключен)", "Wheel lock-up (when in-game ABS is off)"), () => S.LockupEnabled, v => S.LockupEnabled = v, null);

            Section(Loc.T("ГАЗ · R2", "THROTTLE · R2"),
                    Loc.T("Лёгкая пружина. Пробуксовка, отсечка с «подпрыгиванием», толчок переключения.",
                          "A light spring. Wheelspin, a bouncing rev limiter and a kick on every gear change."));
            Slider(Loc.T("Жёсткость в начале", "Initial stiffness"), 1, 8, () => S.ThrottleMinForce, v => S.ThrottleMinForce = v, Num, null);
            Slider(Loc.T("Жёсткость в конце", "End-of-travel stiffness"), 1, 8, () => S.ThrottleMaxForce, v => S.ThrottleMaxForce = v, Num, null);
            Slider(Loc.T("Тяжелеет при разгоне", "Heavier under acceleration"), 0, 100, () => S.ThrottleLoadWeight, v => S.ThrottleLoadWeight = v, Pct, null);
            Check(Loc.T("Кикдаун — упор у пола, который надо продавить", "Kickdown — a detent near the floor you push through"), () => S.ThrottleKickdown, v => S.ThrottleKickdown = v, null);
            Check(Loc.T("Пробуксовка ведущих колёс", "Driven-wheel spin"), () => S.WheelspinEnabled, v => S.WheelspinEnabled = v, null);
            Slider(Loc.T("Порог пробуксовки", "Wheelspin threshold"), 20, 200, () => S.WheelspinThreshold, v => S.WheelspinThreshold = v, Slip,
                   Loc.T("Для дрифта ставь выше", "Set it higher for drifting"));
            Slider(Loc.T("Сила пробуксовки", "Wheelspin strength"), 0, 100, () => S.WheelspinStrength, v => S.WheelspinStrength = v, Pct, null);
            Check(Loc.T("Отсечка (рев-лимитер)", "Rev limiter"), () => S.LimiterEnabled, v => S.LimiterEnabled = v, null);
            Check(Loc.T("Толчок при переключении передачи", "Kick on gear change"), () => S.ShiftKickEnabled, v => S.ShiftKickEnabled = v, null);
            Slider(Loc.T("Сила толчка", "Kick strength"), 0, 100, () => S.ShiftKickStrength, v => S.ShiftKickStrength = v, Pct, null);

            Section(Loc.T("ХАПТИКА · вибромоторы", "HAPTICS · voice-coil motors"),
                    Loc.T("Настоящие тактильные сигналы на моторы геймпада, левый и правый борт отдельно. Только по USB-проводу.",
                          "Real tactile signals on the controller's motors, left and right side separately. USB cable only."));
            Check(Loc.T("Хаптика включена", "Haptics enabled"), () => S.HapticEnabled, v => S.HapticEnabled = v, null);
            Check(Loc.T("Через DSX (рекомендуется)", "Through DSX (recommended)"), () => S.HapticViaDsx, v => S.HapticViaDsx = v,
                  Loc.T("DSX 3.2 сам включает моторы, только когда стримит хаптику. Приложение пишет сигнал в выход, который DSX слушает (Haptics → Audio → System Audio). Выключи — будет прямой вывод в каналы 3/4 геймпада.",
                        "DSX 3.2 only powers the motors while it streams haptics itself. The app writes into the output DSX captures (Haptics → Audio → System Audio). Turn off to write straight to the controller's channels 3/4."));
            Slider(Loc.T("Общая сила хаптики", "Overall haptics strength"), 0, 150, () => S.HapMaster, v => S.HapMaster = v, Pct, null);
            Slider(Loc.T("Мотор (гул по оборотам)", "Engine (RPM rumble)"), 0, 100, () => S.HapEngine, v => S.HapEngine = v, Pct,
                   Loc.T("Частота растёт с оборотами, характер зависит от числа цилиндров. Под нагрузкой сильнее.", "Pitch follows RPM, character depends on the cylinder count. Stronger under load."));
            Slider(Loc.T("Перенос веса в поворотах", "Weight transfer in corners"), 0, 200, () => S.HapWeight, v => S.HapWeight = v, Pct,
                   Loc.T("Мотор смещается на внешний борт по боковой перегрузке. 200% — внутренний борт почти молчит", "The engine feel shifts to the outer side with lateral G. 200% — the inner side goes almost silent"));
            Slider(Loc.T("Нырок и старт", "Brake dive & launch"), 0, 100, () => S.HapDive, v => S.HapDive = v, Pct,
                   Loc.T("Резко ударил по тормозам — кузов клюёт носом; старт с места в пол — удар сцепления и рывок R2", "Stamp on the brakes and the nose dives; full-throttle launch — a clutch bite and an R2 jolt"));
            Slider(Loc.T("Удары, прыжки, аварии", "Impacts, jumps, crashes"), 0, 100, () => S.HapImpact, v => S.HapImpact = v, Pct,
                   Loc.T("Приземление тем сильнее, чем дольше был в воздухе", "The longer you're airborne, the harder the landing"));
            Slider(Loc.T("Скольжение шин", "Tyre slip"), 0, 100, () => S.HapSlip, v => S.HapSlip = v, Pct, null);
            Slider(Loc.T("Переключение передач", "Gear shifts"), 0, 100, () => S.HapShift, v => S.HapShift = v, Pct,
                   Loc.T("Вверх — жёсткий клац с провалом тяги, вниз — двойной толчок с перегазовкой", "Upshift — a hard clunk with a torque cut; downshift — a double kick with a throttle blip"));
            Slider(Loc.T("Щелчки ABS", "ABS clicks"), 0, 100, () => S.HapAbs, v => S.HapAbs = v, Pct, null);

            Section(Loc.T("ПОДКЛЮЧЕНИЕ", "CONNECTION"),
                    Loc.T("Forza: Настройки → HUD и геймплей → Data Out = Вкл, IP 127.0.0.1, порт как ниже.",
                          "Forza: Settings → HUD and Gameplay → Data Out = On, IP 127.0.0.1, port as below."));
            var row = new Panel { Width = RowW, Height = Px(40), BackColor = Theme.Card, Margin = new Padding(0) };
            var nForza = NumBox(Loc.T("Порт Forza", "Forza port"), 0, S.ForzaPort, 1, 65535, row);
            var nDsx = NumBox(Loc.T("Порт DSX", "DSX port"), Px(200), S.DsxPort, 1, 65535, row);
            var nIdx = NumBox(Loc.T("Геймпад №", "Pad #"), Px(400), S.ControllerIndex, 0, 7, row);
            tips.SetToolTip(nIdx, Loc.T("Номер геймпада в DSX (0 — первый)", "Controller index in DSX (0 — the first one)"));
            card.Controls.Add(row);
            var btns = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Card, Margin = new Padding(0, Px(8), 0, Px(2)), WrapContents = false };
            var apply = Primary(MakeButton(200, delegate
            {
                S.ForzaPort = (int)nForza.Value; S.DsxPort = (int)nDsx.Value; S.ControllerIndex = (int)nIdx.Value;
                RestartBackend();
            }));
            apply.Text = Loc.T("Применить и переподключить", "Apply & reconnect");
            var reset = MakeButton(200, delegate
            {
                if (MessageBox.Show(this, Loc.T("Сбросить все настройки эффектов к значениям по умолчанию?", "Reset all effect settings to defaults?"),
                                    "ForzaTriggers", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { Presets.Apply(S, 0); RefreshAll(); }
            });
            reset.Text = Loc.T("Сбросить всё по умолчанию", "Reset to defaults");
            btns.Controls.Add(apply); btns.Controls.Add(reset);
            card.Controls.Add(btns);

            // подвал
            flow.Controls.Add(new Label
            {
                Text = "ForzaTriggers " + Program.Version + Loc.T(" · бесплатно и с открытым кодом (MIT) · не связано с Microsoft, Playground Games и Sony",
                                                                   " · free & open source (MIT) · not affiliated with Microsoft, Playground Games or Sony"),
                AutoSize = true, MaximumSize = new Size(Px(ContentW), 0), ForeColor = Color.FromArgb(96, 103, 120), Margin = new Padding(Px(4), Px(2), 0, 0)
            });
        }

        NumericUpDown NumBox(string caption, int x, int value, int min, int max, Panel row)
        {
            var lbl = new Label { Text = caption, Left = x, Top = 0, Width = Px(96), Height = Px(40), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var n = new DarkNum { Left = x + Px(98), Top = Px(9), Width = Px(84), Minimum = min, Maximum = max, Value = Util.Clamp(value, min, max) };
            row.Controls.Add(lbl); row.Controls.Add(n);
            return n;
        }
    }
}
