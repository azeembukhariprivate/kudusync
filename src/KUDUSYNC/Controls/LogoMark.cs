using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kudusync.Controls
{
    /// <summary>The KUDUSYNC mark: two arrows chasing each other in a circle. Uses the text colour.</summary>
    public class LogoMark : Viewbox
    {
        public LogoMark(double size = 40)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;

            var canvas = new Canvas { Width = 64, Height = 64 };
            var arcs = new Path
            {
                Data = Geometry.Parse("M17.43,26.70 A15.5,15.5 0 0 1 46.05,25.45 M46.57,37.30 A15.5,15.5 0 0 1 17.95,38.55"),
                StrokeThickness = 5.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            Bind(arcs, Shape.StrokeProperty);
            canvas.Children.Add(arcs);

            var heads = new Path
            {
                Data = Geometry.Parse("M48.90,31.57 L50.26,23.48 L41.83,27.41 Z M15.10,32.43 L13.74,40.52 L22.17,36.59 Z"),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round
            };
            Bind(heads, Shape.FillProperty);
            Bind(heads, Shape.StrokeProperty);
            canvas.Children.Add(heads);

            Child = canvas;
        }

        private void Bind(Shape shape, DependencyProperty property) =>
            shape.SetBinding(property, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
    }

    /// <summary>The app tile: the mark in white on a teal rounded square.</summary>
    public static class LogoTile
    {
        public static Border Create(double size)
        {
            var mark = new LogoMark(size * 0.86) { Margin = new Thickness(size * 0.07) };
            mark.SetValue(TextElement.ForegroundProperty, Brushes.White);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.234),
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0xA3, 0x96)),
                Child = mark
            };
        }
    }
}
