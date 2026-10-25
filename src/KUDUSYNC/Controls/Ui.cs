// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Kudusync.Core;

namespace Kudusync.Controls
{
    public enum Tone { Accent, Neutral, Ok, Warn, Bad }

    /// <summary>Small helpers that build the app's look in code, using the theme's resource keys.</summary>
    public static partial class Ui
    {
        public static T Res<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
        {
            element.SetResourceReference(property, key);
            return element;
        }

        public static T Styled<T>(this T element, string styleKey) where T : FrameworkElement
        {
            element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
            return element;
        }

        public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
        {
            element.Margin = new Thickness(left, top, right, bottom);
            return element;
        }

        public static T Tip<T>(this T element, string tip) where T : FrameworkElement
        {
            element.ToolTip = tip;
            return element;
        }

        public static T At<T>(this T element, int row, int column = 0, int columnSpan = 1) where T : UIElement
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            if (columnSpan > 1) Grid.SetColumnSpan(element, columnSpan);
            return element;
        }

        public static TextBlock Text(string text, double size = 13, string brush = "Text", bool bold = false, bool wrap = false)
        {
            var t = new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            return t.Res(TextBlock.ForegroundProperty, brush);
        }

        public static TextBlock Subtle(string text, double size = 12, bool wrap = true) => Text(text, size, "SubtleText", wrap: wrap);

        public static TextBlock Heading(string text, double size = 15) => Text(text, size, bold: true);

        public static TextBlock Mono(string text, double size = 12, string brush = "Text")
        {
            var t = Text(text, size, brush);
            t.Res(TextBlock.FontFamilyProperty, "MonoFont");
            return t;
        }

        public static Glyph Icon(string key, double size = 16)
        {
            var g = new Glyph { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(Glyph.DataProperty, key);
            return g;
        }

        public static object Label(string icon, string text, double iconSize = 16)
        {
            if (icon == null) return text;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(Icon(icon, iconSize));
            if (!string.IsNullOrEmpty(text))
                panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        public static Button Button(string text, string icon = null, string style = "SecondaryButton", RoutedEventHandler click = null)
        {
            var b = new Button { Content = Label(icon, text) }.Styled(style);
            if (click != null) b.Click += click;
            // Screen readers can't read a name from an icon and text in a panel, so the button is given one.
            if (!string.IsNullOrEmpty(text)) System.Windows.Automation.AutomationProperties.SetName(b, text);
            if (string.IsNullOrEmpty(text) && icon != null)
            {
                b.Padding = new Thickness(10, 0, 10, 0);
                b.Loaded += (s, e) =>
                {
                    if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b)) && b.ToolTip is string tip)
                        System.Windows.Automation.AutomationProperties.SetName(b, tip);
                };
            }
            return b;
        }

        public static Button Small(Button b)
        {
            b.MinHeight = 30;
            b.FontSize = 12;
            b.Padding = new Thickness(10, 0, 10, 0);
            return b;
        }

        public static Button Link(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Styled("LinkButton");
            b.Click += click;
            return b;
        }

        public static Border Card(UIElement child, double padding = 18)
        {
            var border = new Border { Child = child, Padding = new Thickness(padding) }.Styled("Card");
            border.Padding = new Thickness(padding);
            return border;
        }

        public static Border Chip(string text, Tone tone = Tone.Accent, string icon = null)
        {
            string bg, fg;
            switch (tone)
            {
                case Tone.Ok: bg = "OkBg"; fg = "OkText"; break;
                case Tone.Warn: bg = "WarnBg"; fg = "WarnText"; break;
                case Tone.Bad: bg = "BadBg"; fg = "BadText"; break;
                case Tone.Neutral: bg = "NeutralChipBg"; fg = "SubtleText"; break;
                default: bg = "ChipBg"; fg = "ChipText"; break;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null) panel.Children.Add(Icon(icon, 12).Margin(0, 0, 4, 0));
            panel.Children.Add(new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            TextElement.SetFontSize(panel, 11.5);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 3),
                Child = panel,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            }.Res(Border.BackgroundProperty, bg);
            panel.Res(TextElement.ForegroundProperty, fg);
            return chip;
        }

        public static Ellipse Dot(string brush = "OkText", double size = 8) =>
            new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }.Res(Shape.FillProperty, brush);

        public static Border Banner(string icon, UIElement content, string background = "BannerBg", string iconBrush = "Text")
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var glyph = Icon(icon, 16).Margin(0, 1, 10, 0);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            glyph.Res(TextElement.ForegroundProperty, iconBrush);
            grid.Children.Add(glyph);
            grid.Children.Add(content.At(0, 1));
            return new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Child = grid }
                .Res(Border.BackgroundProperty, background);
        }

        public static StackPanel Row(params UIElement[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in children) p.Children.Add(c);
            return p;
        }

        public static StackPanel Column(params UIElement[] children)
        {
            var p = new StackPanel();
            foreach (var c in children) p.Children.Add(c);
            return p;
        }

        public static Grid Columns(params GridLength[] widths)
        {
            var g = new Grid();
            foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            return g;
        }

        public static GridLength Px(double v) => new GridLength(v);
        public static GridLength Star(double v = 1) => new GridLength(v, GridUnitType.Star);
        public static GridLength Auto => GridLength.Auto;

        public static Border Divider(bool vertical = false)
        {
            var b = vertical
                ? new Border { Width = 1, Margin = new Thickness(6, 4, 6, 4) }
                : new Border { Height = 1 };
            return b.Res(Border.BackgroundProperty, "BorderBrush");
        }

        public static TextBox MonoBox(bool wrap)
        {
            var box = new TextBox().Styled("MonoBox");
            if (wrap) box.TextWrapping = TextWrapping.Wrap;
            return box;
        }

        /// <summary>A stat: big number with a small label underneath.</summary>
        public static StackPanel Stat(string value, string label, string brush = "Text")
        {
            var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            p.Children.Add(Text(value, 20, brush, bold: true));
            p.Children.Add(Subtle(label, 12, false));
            return p;
        }

        public static void Copy(string text)
        {
            try { Clipboard.SetText(text ?? string.Empty); }
            catch { /* clipboard busy */ }
        }

        public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}
