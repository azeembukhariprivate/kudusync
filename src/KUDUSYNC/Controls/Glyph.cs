using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kudusync.Controls
{
    /// <summary>A 24x24 stroke icon that takes its colour from the surrounding text colour.</summary>
    public class Glyph : Viewbox
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(Geometry), typeof(Glyph),
            new PropertyMetadata(null, (d, e) => ((Glyph)d)._path.Data = (Geometry)e.NewValue));

        private readonly Path _path;

        public Glyph()
        {
            Width = 16;
            Height = 16;
            Stretch = Stretch.Uniform;
            Focusable = false;
            IsHitTestVisible = false;

            _path = new Path
            {
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            _path.SetBinding(Shape.StrokeProperty, new Binding
            {
                Path = new PropertyPath(TextElement.ForegroundProperty),
                RelativeSource = RelativeSource.Self
            });

            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(_path);
            Child = canvas;
        }

        public Geometry Data
        {
            get => (Geometry)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }
    }
}
