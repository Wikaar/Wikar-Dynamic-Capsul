using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WikarIsland.Controls;

/// <summary>Три подпрыгивающих столбика, пока играет музыка.</summary>
public sealed class EqualizerControl : StackPanel
{
    private readonly ScaleTransform[] _bars = new ScaleTransform[3];
    private readonly double[] _durations = { 0.42, 0.31, 0.37 };
    private bool _playing;

    public Brush BarBrush
    {
        set
        {
            foreach (UIElement child in Children)
                if (child is Rectangle r) r.Fill = value;
        }
    }

    public EqualizerControl()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Height = 14;

        for (int i = 0; i < 3; i++)
        {
            _bars[i] = new ScaleTransform(1, 0.35);
            Children.Add(new Rectangle
            {
                Width = 3,
                Height = 14,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                Fill = Brushes.White,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = _bars[i],
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
    }

    public void SetPlaying(bool playing)
    {
        if (playing == _playing) return;
        _playing = playing;

        for (int i = 0; i < 3; i++)
        {
            if (playing)
            {
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1.0,
                    TimeSpan.FromSeconds(_durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase(),
                    BeginTime = TimeSpan.FromSeconds(i * 0.12),
                });
            }
            else
            {
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(250)));
            }
        }
    }
}
