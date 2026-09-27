using System;
using System.Windows;
using System.Windows.Media;

namespace SarahsToolkit.Controls
{
    // A tiny dependency-free sparkline: draws a line + soft fill from an
    // array of samples (oldest -> newest). NaN values break the line so
    // missing samples render as gaps, not zeroes.
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register("Values", typeof(double[]), typeof(Sparkline),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineColorProperty =
            DependencyProperty.Register("LineColor", typeof(Color), typeof(Sparkline),
                new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillOpacityProperty =
            DependencyProperty.Register("FillOpacity", typeof(double), typeof(Sparkline),
                new FrameworkPropertyMetadata(0.18, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AutoScaleProperty =
            DependencyProperty.Register("AutoScale", typeof(bool), typeof(Sparkline),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public double[] Values
        {
            get => (double[])GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public Color LineColor
        {
            get => (Color)GetValue(LineColorProperty);
            set => SetValue(LineColorProperty, value);
        }

        public double FillOpacity
        {
            get => (double)GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }

        // false = fixed 0..100 scale (percent vitals); true = scale to the
        // max value in the buffer (network Mbps, ping ms).
        public bool AutoScale
        {
            get => (bool)GetValue(AutoScaleProperty);
            set => SetValue(AutoScaleProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var values = Values;
            if (values == null || values.Length < 2) return;
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double min = 0, max = 100;
            if (AutoScale)
            {
                max = double.Epsilon;
                foreach (var v in values)
                    if (!double.IsNaN(v) && v > max) max = v;
                if (max <= 0) return;
                max *= 1.15; // headroom so peaks don't touch the top edge
            }

            var lineColor = LineColor;
            var linePen = new Pen(new SolidColorBrush(lineColor), 1.6);
            linePen.Freeze();
            var fillBrush = new SolidColorBrush(Color.FromArgb(
                (byte)(255 * Math.Max(0, Math.Min(1, FillOpacity))),
                lineColor.R, lineColor.G, lineColor.B));
            fillBrush.Freeze();

            double xStep = w / (values.Length - 1);
            var line = new PathGeometry();
            var fill = new PathGeometry();
            bool penDown = false;
            PathFigure lineFig = null, fillFig = null;

            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i];
                if (double.IsNaN(v))
                {
                    penDown = false;
                    lineFig = null; fillFig = null;
                    continue;
                }
                double x = i * xStep;
                double y = h - (Math.Max(min, Math.Min(max, v)) - min) / (max - min) * (h - 3) - 1.5;
                var pt = new Point(x, y);
                if (!penDown)
                {
                    lineFig = new PathFigure { StartPoint = pt, IsClosed = false };
                    line.Figures.Add(lineFig);
                    fillFig = new PathFigure { StartPoint = new Point(x, h), IsClosed = true };
                    fillFig.Segments.Add(new LineSegment(pt, true));
                    fill.Figures.Add(fillFig);
                    penDown = true;
                }
                else
                {
                    lineFig.Segments.Add(new LineSegment(pt, true));
                    fillFig.Segments.Add(new LineSegment(pt, true));
                }
            }

            foreach (var fig in fill.Figures)
            {
                // close each fill figure back down to the bottom edge
                var lastSeg = fig.Segments[fig.Segments.Count - 1] as LineSegment;
                if (lastSeg != null)
                    fig.Segments.Add(new LineSegment(new Point(lastSeg.Point.X, h), true));
            }
            dc.DrawGeometry(fillBrush, null, fill);
            dc.DrawGeometry(null, linePen, line);
        }
    }
}
