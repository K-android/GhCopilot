using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Eto.Drawing;
using Eto.Forms;

namespace GhCopilot.UI
{
    /// <summary>
    /// Hardware-accelerated WhatsApp-style dark chat canvas rendered inside Eto.Forms.Drawable.
    /// Supports right-aligned emerald user bubbles, left-aligned slate Copilot bubbles, inset schematic
    /// diagram blueprint cards, embedded live Rhino viewport thumbnails, and a live thinking bubble.
    /// </summary>
    internal sealed class WhatsAppChatControl : Drawable
    {
        private static readonly Color WaCanvasBg = Color.FromArgb(0x0B, 0x14, 0x1A);
        private static readonly Color WaDotPattern = Color.FromArgb(0x13, 0x20, 0x29);
        private static readonly Color WaUserBubbleFill = Color.FromArgb(0x00, 0x5C, 0x4B);
        private static readonly Color WaUserBubbleBorder = Color.FromArgb(0x0D, 0x7A, 0x63);
        private static readonly Color WaBotBubbleFill = Color.FromArgb(0x20, 0x2C, 0x33);
        private static readonly Color WaBotBubbleBorder = Color.FromArgb(0x2E, 0x3F, 0x4A);
        private static readonly Color WaDiagramFill = Color.FromArgb(0x0B, 0x15, 0x20);
        private static readonly Color WaViewportCardFill = Color.FromArgb(0x11, 0x1B, 0x21);
        private static readonly Color WaCheckCyan = Color.FromArgb(0x53, 0xBD, 0xEB);
        private static readonly Color WaTimestampColor = Color.FromArgb(0x94, 0xA3, 0xB8);

        private enum SegmentKind
        {
            Prose,
            Diagram
        }

        private sealed class ContentSegment
        {
            public SegmentKind Kind;
            public List<string> RawLines = new List<string>();
        }

        private sealed class LayoutSegment
        {
            public SegmentKind Kind;
            public List<string> WrappedLines = new List<string>();
            public float BlockWidth;
            public float BlockHeight;
            public float LineHeight;
        }

        private sealed class ChatBubbleItem
        {
            public string Role = string.Empty;
            public string RawText = string.Empty;
            public string? Thought;
            public string Timestamp = string.Empty;
            public double? ElapsedSeconds;
            public bool IsPlanProposal;
            public string ViewportSummary = string.Empty;
            public Bitmap? ViewportThumbnail;
            public List<ContentSegment> Segments = new List<ContentSegment>();

            // Cached layout measurements (recomputed when control width changes)
            public float MeasuredForWidth = -1f;
            public float BubbleWidth;
            public float BubbleHeight;
            public List<LayoutSegment> LayoutSegments = new List<LayoutSegment>();
            public List<string> WrappedThought = new List<string>();
            public List<string> WrappedViewportSummary = new List<string>();
            public float ThumbDrawWidth;
            public float ThumbDrawHeight;
            public RectangleF LastPaintedRect;
        }

        private readonly List<ChatBubbleItem> _items = new List<ChatBubbleItem>();
        private Font? _bodyFont;
        private Font? _smallFont;
        private Font? _smallBoldFont;
        private Font? _monoFont;
        private float _scrollOffsetY;
        private float _totalContentHeight;
        private bool _stickToBottom = true;
        private bool _isThinking;
        private string _thinkingStage = string.Empty;
        private string _thinkingTrail = string.Empty;
        private string _thinkingEta = string.Empty;

        public WhatsAppChatControl()
        {
            CanFocus = true;
            BackgroundColor = WaCanvasBg;
            MinimumSize = new Size(200, 140);
        }

        public event EventHandler<string>? StatusHintRequested;

        public bool HasMessages => _items.Count > 0;

        public void AppendMessage(
            string role,
            string content,
            string? thought = null,
            double? elapsedSeconds = null,
            bool isPlanProposal = false,
            string? viewportImageBase64Png = null,
            string? viewportVerificationSummary = null)
        {
            Bitmap? thumbnail = TryDecodeThumbnail(viewportImageBase64Png);
            var item = new ChatBubbleItem
            {
                Role = role ?? "GH Copilot",
                RawText = (content ?? string.Empty).Replace("\r\n", "\n"),
                Thought = string.IsNullOrWhiteSpace(thought) ? null : thought!.Trim(),
                Timestamp = DateTime.Now.ToString("HH:mm"),
                ElapsedSeconds = elapsedSeconds,
                IsPlanProposal = isPlanProposal,
                ViewportSummary = viewportVerificationSummary ?? string.Empty,
                ViewportThumbnail = thumbnail
            };
            item.Segments = ParseSegments(item.RawText);

            _items.Add(item);
            _stickToBottom = true;
            Invalidate();
        }

        public void SetThinkingState(bool isThinking, string stage = "", string trail = "", string eta = "")
        {
            _isThinking = isThinking;
            _thinkingStage = stage ?? string.Empty;
            _thinkingTrail = trail ?? string.Empty;
            _thinkingEta = eta ?? string.Empty;
            if (isThinking)
            {
                _stickToBottom = true;
            }
            Invalidate();
        }

        public void ClearMessages()
        {
            DisposeThumbnails();
            _items.Clear();
            _scrollOffsetY = 0f;
            _totalContentHeight = 0f;
            _stickToBottom = true;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeThumbnails();
                _bodyFont?.Dispose();
                _smallFont?.Dispose();
                _smallBoldFont?.Dispose();
                _monoFont?.Dispose();
                _bodyFont = null;
                _smallFont = null;
                _smallBoldFont = null;
                _monoFont = null;
            }

            base.Dispose(disposing);
        }

        private void DisposeThumbnails()
        {
            foreach (ChatBubbleItem item in _items)
            {
                item.ViewportThumbnail?.Dispose();
                item.ViewportThumbnail = null;
            }
        }

        private void EnsureFonts()
        {
            _bodyFont ??= CopilotTheme.BodyFont();
            _smallFont ??= CopilotTheme.SmallFont();
            _smallBoldFont ??= CopilotTheme.SmallBoldFont();
            _monoFont ??= new Font(FontFamilies.Monospace, 8.0f);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float viewHeight = Math.Max(40f, Height);
            float maxScroll = Math.Max(0f, _totalContentHeight - viewHeight);
            if (maxScroll <= 0f)
            {
                return;
            }

            _scrollOffsetY = Clamp(_scrollOffsetY - e.Delta.Height * 38f, 0f, maxScroll);
            _stickToBottom = _scrollOffsetY >= maxScroll - 6f;
            Invalidate();
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e.Buttons == MouseButtons.Alternate)
            {
                string? copyText = null;
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i].LastPaintedRect.Contains(e.Location))
                    {
                        copyText = _items[i].RawText;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(copyText) && _items.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (ChatBubbleItem item in _items)
                    {
                        sb.AppendLine($"[{item.Role} • {item.Timestamp}]");
                        sb.AppendLine(item.RawText);
                        sb.AppendLine();
                    }
                    copyText = sb.ToString().TrimEnd();
                }

                if (!string.IsNullOrEmpty(copyText))
                {
                    try
                    {
                        Clipboard.Instance.Text = copyText;
                        StatusHintRequested?.Invoke(this, "Copied chat message to clipboard.");
                    }
                    catch (Exception)
                    {
                        // Ignore clipboard contention errors.
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            float viewW = Math.Max(160f, Width);
            float viewH = Math.Max(100f, Height);

            EnsureFonts();

            // 1. WhatsApp dark wallpaper + subtle grid dots
            g.FillRectangle(WaCanvasBg, 0f, 0f, viewW, viewH);
            for (float dy = 14f; dy < viewH; dy += 28f)
            {
                for (float dx = 14f; dx < viewW; dx += 28f)
                {
                    g.FillRectangle(WaDotPattern, dx, dy, 2f, 2f);
                }
            }

            if (_items.Count == 0 && !_isThinking)
            {
                DrawWelcomeCard(g, viewW, viewH);
                _totalContentHeight = viewH;
                _scrollOffsetY = 0f;
                return;
            }

            // 2. Measure all bubbles for current width
            const float outerPad = 10f;
            const float bubbleGap = 10f;
            float contentW = Math.Max(120f, viewW - outerPad * 2f - 6f);
            float cursorY = outerPad;

            foreach (ChatBubbleItem item in _items)
            {
                MeasureBubbleIfNeeded(g, item, contentW);
                cursorY += item.BubbleHeight + bubbleGap;
            }

            float thinkingHeight = 0f;
            List<string>? thinkingTrailLines = null;
            List<string>? thinkingStageLines = null;
            if (_isThinking)
            {
                float maxThinkW = Math.Max(140f, contentW * 0.88f);
                float innerThinkW = Math.Max(90f, maxThinkW - 22f);
                thinkingStageLines = WrapParagraph(g, _smallBoldFont!, string.IsNullOrWhiteSpace(_thinkingStage) ? "Thinking..." : _thinkingStage, innerThinkW);
                thinkingTrailLines = WrapParagraph(g, _smallFont!, string.IsNullOrWhiteSpace(_thinkingTrail) ? "Inspecting Rhino viewport & workspace..." : _thinkingTrail, innerThinkW);
                thinkingHeight = 32f + thinkingStageLines.Count * 15f + thinkingTrailLines.Count * 14f + 12f;
                cursorY += thinkingHeight + bubbleGap;
            }

            _totalContentHeight = cursorY;
            float maxScroll = Math.Max(0f, _totalContentHeight - viewH);
            if (_stickToBottom)
            {
                _scrollOffsetY = maxScroll;
            }
            else
            {
                _scrollOffsetY = Clamp(_scrollOffsetY, 0f, maxScroll);
            }

            // 3. Paint visible bubbles
            float drawY = outerPad - _scrollOffsetY;
            foreach (ChatBubbleItem item in _items)
            {
                item.LastPaintedRect = RectangleF.Empty;
                if (drawY + item.BubbleHeight >= -16f && drawY <= viewH + 16f)
                {
                    bool isUser = string.Equals(item.Role, "You", StringComparison.OrdinalIgnoreCase);
                    float bubbleX = isUser
                        ? Math.Max(outerPad, viewW - outerPad - 6f - item.BubbleWidth)
                        : outerPad;

                    var rect = new RectangleF(bubbleX, drawY, item.BubbleWidth, item.BubbleHeight);
                    item.LastPaintedRect = rect;
                    DrawBubble(g, item, rect, isUser);
                }

                drawY += item.BubbleHeight + bubbleGap;
            }

            // 4. Paint live typing / thinking bubble when active
            if (_isThinking && thinkingHeight > 0f && drawY + thinkingHeight >= 0f)
            {
                float maxThinkW = Math.Max(140f, contentW * 0.88f);
                var thinkRect = new RectangleF(outerPad, drawY, maxThinkW, thinkingHeight);
                DrawThinkingBubble(g, thinkRect, thinkingStageLines!, thinkingTrailLines!);
            }

            // 5. Sleek scrollbar indicator
            if (maxScroll > 1f)
            {
                float trackH = Math.Max(20f, viewH - 8f);
                float thumbH = Math.Max(26f, trackH * (viewH / _totalContentHeight));
                float thumbY = 4f + (trackH - thumbH) * (_scrollOffsetY / maxScroll);
                var thumbRect = new RectangleF(viewW - 5f, thumbY, 3f, thumbH);
                using (IGraphicsPath thumbPath = GraphicsPath.GetRoundRect(thumbRect, 1.5f, 1.5f, 1.5f, 1.5f))
                {
                    g.FillPath(CopilotTheme.BorderStrong, thumbPath);
                }
            }
        }

        private void DrawWelcomeCard(Graphics g, float viewW, float viewH)
        {
            float cardW = Math.Min(360f, Math.Max(170f, viewW - 24f));
            float innerW = Math.Max(120f, cardW - 24f);
            string[] bullets =
            {
                "• Rhino Direct: Build boxes, spheres, curves, move/array, layers, colors & viewports without Grasshopper.",
                "• Plan-First Architect: Discusses your requirements, outlines a plan & questions (with a diagram when useful) before building. Tap Approve & Build or reply to refine.",
                "• Live Viewport Vision: Sees your Rhino 8 viewport before & after every change so follow-ups stay accurate.",
                "Tip: Press Ctrl+Enter to send • Right-click any bubble to copy."
            };

            var wrapped = new List<List<string>>(bullets.Length);
            float textH = 0f;
            foreach (string b in bullets)
            {
                List<string> lines = WrapParagraph(g, _smallFont!, b, innerW);
                wrapped.Add(lines);
                textH += lines.Count * 15f + 6f;
            }

            float cardH = 44f + textH + 12f;
            float cardX = Math.Max(10f, (viewW - cardW) * 0.5f);
            float cardY = Math.Max(14f, Math.Min(36f, (viewH - cardH) * 0.35f));
            var rect = new RectangleF(cardX, cardY, cardW, cardH);

            using (IGraphicsPath path = GraphicsPath.GetRoundRect(rect, 10f, 10f, 10f, 10f))
            {
                g.FillPath(WaBotBubbleFill, path);
                using var border = new Pen(CopilotTheme.BorderStrong, 1f);
                g.DrawPath(border, path);
            }

            g.DrawText(_smallBoldFont!, CopilotTheme.AccentEmerald, cardX + 12f, cardY + 12f, "✦ Welcome to GH Copilot");
            float y = cardY + 34f;
            foreach (List<string> block in wrapped)
            {
                foreach (string line in block)
                {
                    g.DrawText(_smallFont!, CopilotTheme.TextSecondary, cardX + 12f, y, line);
                    y += 15f;
                }
                y += 6f;
            }
        }

        private void MeasureBubbleIfNeeded(Graphics g, ChatBubbleItem item, float contentW)
        {
            if (Math.Abs(item.MeasuredForWidth - contentW) < 1.5f)
            {
                return;
            }

            item.MeasuredForWidth = contentW;
            item.LayoutSegments.Clear();
            item.WrappedThought.Clear();
            item.WrappedViewportSummary.Clear();

            bool isUser = string.Equals(item.Role, "You", StringComparison.OrdinalIgnoreCase);
            float maxBubbleW = Math.Max(140f, contentW * (isUser ? 0.78f : 0.88f));
            const float padX = 11f;
            float maxTextW = Math.Max(80f, maxBubbleW - padX * 2f);

            float usedWidth = isUser ? 96f : 155f;
            float totalH = 26f; // Header row height

            if (!string.IsNullOrWhiteSpace(item.Thought))
            {
                item.WrappedThought = WrapParagraph(g, _smallFont!, "💭 " + item.Thought, maxTextW);
                foreach (string line in item.WrappedThought)
                {
                    usedWidth = Math.Max(usedWidth, MeasureWidth(g, _smallFont!, line));
                }
                totalH += item.WrappedThought.Count * 14f + 5f;
            }

            foreach (ContentSegment seg in item.Segments)
            {
                var lseg = new LayoutSegment { Kind = seg.Kind };
                if (seg.Kind == SegmentKind.Diagram)
                {
                    lseg.LineHeight = 14f;
                    float diagramTextW = Math.Max(60f, maxTextW - 14f);
                    float maxLineW = 60f;
                    foreach (string rawLine in seg.RawLines)
                    {
                        string clean = rawLine.TrimEnd();
                        // Keep diagram lines intact unless wider than box; hard-wrap only if strictly necessary
                        if (MeasureWidth(g, _monoFont!, clean) <= diagramTextW)
                        {
                            lseg.WrappedLines.Add(clean);
                            maxLineW = Math.Max(maxLineW, MeasureWidth(g, _monoFont!, clean));
                        }
                        else
                        {
                            foreach (string part in WrapParagraph(g, _monoFont!, clean, diagramTextW))
                            {
                                lseg.WrappedLines.Add(part);
                                maxLineW = Math.Max(maxLineW, MeasureWidth(g, _monoFont!, part));
                            }
                        }
                    }

                    lseg.BlockWidth = Math.Min(maxTextW, maxLineW + 14f);
                    lseg.BlockHeight = 22f + lseg.WrappedLines.Count * lseg.LineHeight + 8f;
                    usedWidth = Math.Max(usedWidth, lseg.BlockWidth);
                    totalH += lseg.BlockHeight + 6f;
                }
                else
                {
                    lseg.LineHeight = 16f;
                    foreach (string rawLine in seg.RawLines)
                    {
                        List<string> wrapped = WrapParagraph(g, _bodyFont!, rawLine, maxTextW);
                        foreach (string wLine in wrapped)
                        {
                            lseg.WrappedLines.Add(wLine);
                            usedWidth = Math.Max(usedWidth, MeasureWidth(g, _bodyFont!, wLine));
                        }
                    }

                    lseg.BlockWidth = usedWidth;
                    lseg.BlockHeight = Math.Max(16f, lseg.WrappedLines.Count * lseg.LineHeight);
                    totalH += lseg.BlockHeight + 5f;
                }

                item.LayoutSegments.Add(lseg);
            }

            if (item.ViewportThumbnail != null || !string.IsNullOrWhiteSpace(item.ViewportSummary))
            {
                float cardMaxW = maxTextW;
                if (item.ViewportThumbnail != null)
                {
                    float tw = Math.Max(1f, item.ViewportThumbnail.Width);
                    float th = Math.Max(1f, item.ViewportThumbnail.Height);
                    float targetW = Math.Min(cardMaxW - 12f, 240f);
                    float scale = Math.Min(1f, targetW / tw);
                    item.ThumbDrawWidth = Math.Max(80f, tw * scale);
                    item.ThumbDrawHeight = Math.Max(50f, th * scale);
                    if (item.ThumbDrawHeight > 140f)
                    {
                        float hScale = 140f / item.ThumbDrawHeight;
                        item.ThumbDrawHeight = 140f;
                        item.ThumbDrawWidth *= hScale;
                    }
                    usedWidth = Math.Max(usedWidth, item.ThumbDrawWidth + 14f);
                    totalH += item.ThumbDrawHeight + 24f;
                }

                if (!string.IsNullOrWhiteSpace(item.ViewportSummary))
                {
                    item.WrappedViewportSummary = WrapParagraph(g, _smallFont!, item.ViewportSummary, cardMaxW - 14f);
                    foreach (string line in item.WrappedViewportSummary)
                    {
                        usedWidth = Math.Max(usedWidth, MeasureWidth(g, _smallFont!, line) + 14f);
                    }
                    totalH += item.WrappedViewportSummary.Count * 14f + (item.ViewportThumbnail != null ? 8f : 24f);
                }
            }

            totalH += 18f; // Bottom timestamp footer
            item.BubbleWidth = Math.Min(maxBubbleW, Math.Max(isUser ? 120f : 185f, usedWidth + padX * 2f));
            item.BubbleHeight = totalH;
        }

        private void DrawBubble(Graphics g, ChatBubbleItem item, RectangleF rect, bool isUser)
        {
            Color fill = isUser ? WaUserBubbleFill : WaBotBubbleFill;
            Color border = isUser
                ? WaUserBubbleBorder
                : (item.IsPlanProposal ? CopilotTheme.AccentCyan : WaBotBubbleBorder);

            float nw = isUser ? 10f : 2f;
            float ne = isUser ? 2f : 10f;
            using (IGraphicsPath path = GraphicsPath.GetRoundRect(rect, nw, ne, 10f, 10f))
            {
                g.FillPath(fill, path);
                using var pen = new Pen(border, item.IsPlanProposal ? 1.5f : 1f);
                g.DrawPath(pen, path);
            }

            float x = rect.X + 11f;
            float y = rect.Y + 8f;
            float innerW = Math.Max(60f, rect.Width - 22f);

            // Header row
            string headerText = isUser
                ? "You"
                : (item.IsPlanProposal ? "✦ GH Copilot • Plan" : $"✦ {item.Role}");
            Color headerColor = isUser
                ? Color.FromArgb(0xA7, 0xF3, 0xD0)
                : (item.IsPlanProposal ? CopilotTheme.AccentCyan : CopilotTheme.AccentEmerald);
            g.DrawText(_smallBoldFont!, headerColor, x, y, headerText);
            y += 18f;

            // Thought trail
            if (item.WrappedThought.Count > 0)
            {
                foreach (string tLine in item.WrappedThought)
                {
                    g.DrawText(_smallFont!, CopilotTheme.AccentCyan, x, y, tLine);
                    y += 14f;
                }
                y += 4f;
            }

            // Segments (prose vs schematic diagram blueprint cards)
            foreach (LayoutSegment seg in item.LayoutSegments)
            {
                if (seg.Kind == SegmentKind.Diagram)
                {
                    var cardRect = new RectangleF(x, y, innerW, seg.BlockHeight);
                    using (IGraphicsPath dPath = GraphicsPath.GetRoundRect(cardRect, 6f, 6f, 6f, 6f))
                    {
                        g.FillPath(WaDiagramFill, dPath);
                        using var dPen = new Pen(CopilotTheme.AccentCyan, 1f);
                        g.DrawPath(dPen, dPath);
                    }

                    g.DrawText(_smallBoldFont!, CopilotTheme.AccentCyan, cardRect.X + 7f, cardRect.Y + 4f, "📐 DIAGRAM / ILLUSTRATION");
                    float dy = cardRect.Y + 21f;
                    foreach (string dLine in seg.WrappedLines)
                    {
                        g.DrawText(_monoFont!, Color.FromArgb(0xA7, 0xF3, 0xD0), cardRect.X + 7f, dy, dLine);
                        dy += seg.LineHeight;
                    }
                    y += seg.BlockHeight + 6f;
                }
                else
                {
                    foreach (string pLine in seg.WrappedLines)
                    {
                        g.DrawText(_bodyFont!, CopilotTheme.TextPrimary, x, y, pLine);
                        y += seg.LineHeight;
                    }
                    y += 5f;
                }
            }

            // Live Viewport thumbnail + verification card
            if (item.ViewportThumbnail != null || item.WrappedViewportSummary.Count > 0)
            {
                float cardH = (item.ViewportThumbnail != null ? item.ThumbDrawHeight + 22f : 20f)
                            + item.WrappedViewportSummary.Count * 14f + 6f;
                var vRect = new RectangleF(x, y, innerW, cardH);
                using (IGraphicsPath vPath = GraphicsPath.GetRoundRect(vRect, 6f, 6f, 6f, 6f))
                {
                    g.FillPath(WaViewportCardFill, vPath);
                    using var vPen = new Pen(CopilotTheme.BorderStrong, 1f);
                    g.DrawPath(vPen, vPath);
                }

                g.DrawText(_smallBoldFont!, CopilotTheme.AccentCyan, vRect.X + 7f, vRect.Y + 4f, "👁️ LIVE RHINO VIEWPORT");
                float vy = vRect.Y + 20f;
                if (item.ViewportThumbnail != null)
                {
                    g.DrawImage(item.ViewportThumbnail, vRect.X + 7f, vy, item.ThumbDrawWidth, item.ThumbDrawHeight);
                    vy += item.ThumbDrawHeight + 5f;
                }

                foreach (string sLine in item.WrappedViewportSummary)
                {
                    g.DrawText(_smallFont!, CopilotTheme.TextSecondary, vRect.X + 7f, vy, sLine);
                    vy += 14f;
                }
            }

            // Bottom-right WhatsApp timestamp (+ double checkmarks on user bubbles)
            string meta = item.ElapsedSeconds.HasValue && item.ElapsedSeconds.Value > 0
                ? $"{item.Timestamp} ({item.ElapsedSeconds.Value:F1}s)"
                : item.Timestamp;
            string check = isUser ? " ✓✓" : string.Empty;
            float metaW = MeasureWidth(g, _smallFont!, meta + check);
            float metaX = Math.Max(x, rect.Right - 10f - metaW);
            float metaY = rect.Bottom - 16f;
            g.DrawText(_smallFont!, WaTimestampColor, metaX, metaY, meta);
            if (isUser)
            {
                float metaOnlyW = MeasureWidth(g, _smallFont!, meta);
                g.DrawText(_smallBoldFont!, WaCheckCyan, metaX + metaOnlyW, metaY, " ✓✓");
            }
        }

        private void DrawThinkingBubble(
            Graphics g,
            RectangleF rect,
            List<string> stageLines,
            List<string> trailLines)
        {
            using (IGraphicsPath path = GraphicsPath.GetRoundRect(rect, 2f, 10f, 10f, 10f))
            {
                g.FillPath(WaBotBubbleFill, path);
                using var pen = new Pen(CopilotTheme.AccentCyan, 1.5f);
                g.DrawPath(pen, path);
            }

            float x = rect.X + 11f;
            float y = rect.Y + 8f;

            g.DrawText(_smallBoldFont!, CopilotTheme.AccentEmerald, x, y, "✦ GH Copilot");
            if (!string.IsNullOrWhiteSpace(_thinkingEta))
            {
                float etaW = MeasureWidth(g, _smallFont!, _thinkingEta);
                g.DrawText(_smallFont!, CopilotTheme.AccentCyan, Math.Max(x + 90f, rect.Right - 10f - etaW), y, _thinkingEta);
            }
            y += 18f;

            foreach (string sLine in stageLines)
            {
                g.DrawText(_smallBoldFont!, CopilotTheme.TextPrimary, x, y, sLine);
                y += 15f;
            }

            foreach (string tLine in trailLines)
            {
                g.DrawText(_smallFont!, CopilotTheme.TextSecondary, x, y, tLine);
                y += 14f;
            }
        }

        private static List<ContentSegment> ParseSegments(string text)
        {
            var segments = new List<ContentSegment>();
            string[] lines = text.Split('\n');
            ContentSegment? current = null;
            bool inFence = false;

            foreach (string rawLine in lines)
            {
                string trimmed = rawLine.Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    if (!inFence)
                    {
                        inFence = true;
                        current = new ContentSegment { Kind = SegmentKind.Diagram };
                        segments.Add(current);
                    }
                    else
                    {
                        inFence = false;
                        current = null;
                    }
                    continue;
                }

                if (inFence && current != null)
                {
                    current.RawLines.Add(rawLine);
                    continue;
                }

                bool looksLikeBoxDrawing = ContainsBoxDrawing(rawLine);
                SegmentKind targetKind = looksLikeBoxDrawing ? SegmentKind.Diagram : SegmentKind.Prose;
                if (current is null || current.Kind != targetKind)
                {
                    current = new ContentSegment { Kind = targetKind };
                    segments.Add(current);
                }

                current.RawLines.Add(rawLine);
            }

            return segments;
        }

        private static bool ContainsBoxDrawing(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            foreach (char c in line)
            {
                if (c == '┌' || c == '┐' || c == '└' || c == '┘' || c == '├' || c == '┤' || c == '┬' || c == '┴' || c == '┼' || c == '│' || c == '►' || c == '▼')
                {
                    return true;
                }
            }
            return false;
        }

        private static List<string> WrapParagraph(Graphics g, Font font, string text, float maxWidth)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                result.Add(string.Empty);
                return result;
            }

            string[] words = text.Split(' ');
            var line = new StringBuilder();
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : line.ToString() + " " + word;
                if (MeasureWidth(g, font, candidate) <= maxWidth)
                {
                    line.Clear();
                    line.Append(candidate);
                    continue;
                }

                if (line.Length > 0)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }

                if (MeasureWidth(g, font, word) <= maxWidth)
                {
                    line.Append(word);
                }
                else
                {
                    // Split overly long unspaced token character by character
                    var chunk = new StringBuilder();
                    foreach (char c in word)
                    {
                        string next = chunk.ToString() + c;
                        if (chunk.Length > 0 && MeasureWidth(g, font, next) > maxWidth)
                        {
                            result.Add(chunk.ToString());
                            chunk.Clear();
                        }
                        chunk.Append(c);
                    }
                    if (chunk.Length > 0)
                    {
                        line.Append(chunk.ToString());
                    }
                }
            }

            if (line.Length > 0)
            {
                result.Add(line.ToString());
            }

            return result;
        }

        private static float MeasureWidth(Graphics g, Font font, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            return g.MeasureString(font, text).Width;
        }

        private static Bitmap? TryDecodeThumbnail(string? base64Png)
        {
            if (string.IsNullOrWhiteSpace(base64Png))
            {
                return null;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(base64Png!.Trim());
                return bytes.Length > 0 ? new Bitmap(bytes) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static float Clamp(float value, float min, float max) =>
            value < min ? min : (value > max ? max : value);
    }
}
