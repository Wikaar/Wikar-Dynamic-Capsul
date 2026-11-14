using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WikarIsland.Services;

namespace WikarIsland.Controls;

/// <summary>
/// Столбики визуализатора в капсуле. В режиме «spectrum» двигаются под настоящий звук
/// (басы слева, верхи справа), в «simple» — просто подпрыгивают, пока играет музыка.
/// </summary>
public sealed class EqualizerControl : StackPanel
{
    private const int Count = SpectrumService.Bands;
    private readonly ScaleTransform[] _bars = new ScaleTransform[Count];
    private readonly double[] _durations = { 0.42, 0.31, 0.37, 0.29, 0.45 };
    private bool _playing;
    private bool _animated;
    private bool _rendering;
    private TimeSpan _lastFrame;

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

        for (int i = 0; i < Count; i++)
        {
            _bars[i] = new ScaleTransform(1, 0.25);
            Children.Add(new Rectangle
            {
                Width = 2.6,
                Height = 14,
                RadiusX = 1.3,
                RadiusY = 1.3,
                Margin = new Thickness(i == 0 ? 0 : 1.8, 0, 0, 0),
                Fill = Brushes.White,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = _bars[i],
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
        Unloaded += (_, _) => StopRendering();
    }

    public void SetPlaying(bool playing)
    {
        string mode = App.Settings.Visualizer;
        Visibility = mode == "off" ? Visibility.Collapsed : Visibility.Visible;
        if (mode == "off") playing = false;

        bool spectrum = playing && mode == "spectrum";
        if (spectrum) App.Spectrum.Start();
        else App.Spectrum.Stop();

        if (playing == _playing && (spectrum ? _rendering : !_rendering)) return;
        _playing = playing;

        if (spectrum)
        {
            StopAnimation(fade: false);
            StartRendering();
        }
        else
        {
            StopRendering();
            if (playing) StartAnimation();
            else StopAnimation();
        }
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // ~40 кадров в секунду достаточно
        if (e is RenderingEventArgs r)
        {
            if ((r.RenderingTime - _lastFrame).TotalMilliseconds < 24) return;
            _lastFrame = r.RenderingTime;
        }

        if (!App.Spectrum.Live)
        {
            // звук не ловится (например, другой выход) — подпрыгиваем по-простому
            if (!_animated) StartAnimation();
            return;
        }
        if (_animated) StopAnimation(fade: false);

        float[] levels = App.Spectrum.Levels();
        for (int i = 0; i < Count; i++)
            _bars[i].ScaleY = 0.18 + levels[i] * 0.82;
    }

    private void StartAnimation()
    {
        _animated = true;
        for (int i = 0; i < Count; i++)
        {
            _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1.0,
                TimeSpan.FromSeconds(_durations[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase(),
                BeginTime = TimeSpan.FromSeconds(i * 0.09),
            });
        }
    }

    private void StopAnimation(bool fade = true)
    {
        _animated = false;
        for (int i = 0; i < Count; i++)
        {
            if (fade)
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(250)));
            else
            {
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _bars[i].ScaleY = 0.25;
            }
        }
    }
}
