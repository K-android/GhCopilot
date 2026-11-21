using System;
using Eto.Drawing;
using Eto.Forms;

namespace GhCopilot.UI
{
    /// <summary>
    /// Modern dark-slate visual design system for GH Copilot (Rhino 8 / Eto.Forms WPF).
    /// Pure Eto controls and drawing primitives — no XAML or external theme dependencies.
    /// </summary>
    internal static class CopilotTheme
    {
        // Core dark studio surfaces
        public static readonly Color CanvasBg = Color.FromArgb(0x14, 0x17, 0x1D);
        public static readonly Color Surface = Color.FromArgb(0x1C, 0x21, 0x2B);
        public static readonly Color SurfaceElevated = Color.FromArgb(0x24, 0x2B, 0x38);
        public static readonly Color InputBg = Color.FromArgb(0x0F, 0x12, 0x18);
        public static readonly Color BorderSubtle = Color.FromArgb(0x2E, 0x37, 0x48);
        public static readonly Color BorderStrong = Color.FromArgb(0x3D, 0x4A, 0x61);

        // Typography colors
        public static readonly Color TextPrimary = Color.FromArgb(0xF1, 0xF5, 0xF9);
        public static readonly Color TextSecondary = Color.FromArgb(0x94, 0xA3, 0xB8);
        public static readonly Color TextMuted = Color.FromArgb(0x64, 0x74, 0x8B);

        // Semantic accents (Grasshopper Emerald + Studio Cyan + Amber + Rose)
        public static readonly Color AccentEmerald = Color.FromArgb(0x10, 0xB9, 0x81);
        public static readonly Color AccentEmeraldDark = Color.FromArgb(0x0B, 0x3B, 0x2E);
        public static readonly Color AccentCyan = Color.FromArgb(0x38, 0xBD, 0xF8);
        public static readonly Color AccentCyanDark = Color.FromArgb(0x10, 0x2A, 0x3C);
        public static readonly Color WarningAmber = Color.FromArgb(0xF5, 0x9E, 0x0B);
        public static readonly Color WarningAmberDark = Color.FromArgb(0x3B, 0x2A, 0x0B);
        public static readonly Color DangerRose = Color.FromArgb(0xEF, 0x44, 0x44);
        public static readonly Color DangerRoseDark = Color.FromArgb(0x3B, 0x16, 0x1B);

        // Fonts (lazily cached via factory helpers so Eto Platform is initialized first)
        public static Font TitleFont() => new Font(SystemFont.Bold, 11.5f);
        public static Font SectionFont() => new Font(SystemFont.Bold, 9.0f);
        public static Font BodyFont() => new Font(SystemFont.Default, 9.0f);
        public static Font SmallFont() => new Font(SystemFont.Default, 8.2f);
        public static Font SmallBoldFont() => new Font(SystemFont.Bold, 8.2f);
        public static Font MonoFont() => new Font(FontFamilies.Monospace, 8.5f);

        /// <summary>
        /// Wraps content in a crisp 1px bordered studio card.
        /// </summary>
        public static Panel CreateCard(Control inner, Color? borderColor = null, Color? fillColor = null, int padding = 10)
        {
            var innerHost = new Panel
            {
                BackgroundColor = fillColor ?? Surface,
                Padding = new Padding(padding),
                Content = inner
            };

            return new Panel
            {
                BackgroundColor = borderColor ?? BorderSubtle,
                Padding = new Padding(1),
                Content = innerHost
            };
        }

        /// <summary>
        /// Updates the border color of a card created by <see cref="CreateCard"/>.
        /// </summary>
        public static void SetCardBorder(Panel card, Color borderColor)
        {
            card.BackgroundColor = borderColor;
        }

        public static Label CreateLabel(string text, Color? color = null, Font? font = null)
        {
            return new Label
            {
                Text = text,
                TextColor = color ?? TextPrimary,
                Font = font ?? BodyFont(),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static Label CreateSectionHeader(string text)
        {
            return new Label
            {
                Text = text.ToUpperInvariant(),
                TextColor = TextSecondary,
                Font = SmallBoldFont(),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static Panel CreateBadge(out Label label, string text, Color background, Color foreground)
        {
            label = new Label
            {
                Text = text,
                TextColor = foreground,
                Font = SmallBoldFont(),
                VerticalAlignment = VerticalAlignment.Center
            };

            return new Panel
            {
                BackgroundColor = background,
                Padding = new Padding(8, 3),
                Content = label
            };
        }

        public static Button CreatePrimaryButton(string text, string? toolTip = null)
        {
            var button = new Button
            {
                Text = text,
                BackgroundColor = AccentEmerald,
                TextColor = Color.FromArgb(0x07, 0x1A, 0x14),
                Font = SmallBoldFont(),
                MinimumSize = new Size(72, 28)
            };
            if (!string.IsNullOrEmpty(toolTip)) button.ToolTip = toolTip;
            return button;
        }

        public static Button CreateSecondaryButton(string text, string? toolTip = null)
        {
            var button = new Button
            {
                Text = text,
                BackgroundColor = SurfaceElevated,
                TextColor = TextPrimary,
                Font = SmallFont(),
                MinimumSize = new Size(64, 26)
            };
            if (!string.IsNullOrEmpty(toolTip)) button.ToolTip = toolTip;
            return button;
        }

        public static Button CreateChipButton(string text, string? toolTip = null)
        {
            var button = new Button
            {
                Text = text,
                BackgroundColor = AccentCyanDark,
                TextColor = AccentCyan,
                Font = SmallFont(),
                MinimumSize = new Size(56, 23)
            };
            if (!string.IsNullOrEmpty(toolTip)) button.ToolTip = toolTip;
            return button;
        }

        public static Button CreateDangerButton(string text, string? toolTip = null)
        {
            var button = new Button
            {
                Text = text,
                BackgroundColor = DangerRoseDark,
                TextColor = Color.FromArgb(0xFC, 0xA5, 0xA5),
                Font = SmallBoldFont(),
                MinimumSize = new Size(68, 28)
            };
            if (!string.IsNullOrEmpty(toolTip)) button.ToolTip = toolTip;
            return button;
        }

        public static Button CreateTabButton(string text, bool active)
        {
            var button = new Button
            {
                Text = text,
                Font = SmallBoldFont(),
                MinimumSize = new Size(88, 28)
            };
            ApplyTabButtonState(button, active);
            return button;
        }

        public static void ApplyTabButtonState(Button button, bool active)
        {
            button.BackgroundColor = active ? AccentEmeraldDark : Surface;
            button.TextColor = active ? AccentEmerald : TextSecondary;
        }

        public static void StyleTextArea(TextArea area, bool monospace = false)
        {
            area.BackgroundColor = InputBg;
            area.TextColor = TextPrimary;
            area.Font = monospace ? MonoFont() : BodyFont();
        }

        public static void StyleTextBox(TextBox box)
        {
            box.BackgroundColor = InputBg;
            box.TextColor = TextPrimary;
            box.Font = BodyFont();
        }

        public static void StylePasswordBox(PasswordBox box)
        {
            box.BackgroundColor = InputBg;
            box.TextColor = TextPrimary;
            box.Font = BodyFont();
        }

        public static void StyleCheckBox(CheckBox checkBox)
        {
            checkBox.TextColor = TextPrimary;
            checkBox.Font = SmallFont();
        }

        public static Panel CreateHorizontalDivider()
        {
            return new Panel
            {
                BackgroundColor = BorderSubtle,
                Height = 1
            };
        }
    }
}
