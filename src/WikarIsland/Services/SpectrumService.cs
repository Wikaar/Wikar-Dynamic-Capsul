using System;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace WikarIsland.Services;

/// <summary>
/// Настоящий визуализатор: слушаем то, что играет в колонках (WASAPI loopback), считаем БПФ
/// и раскладываем по нескольким полосам — от басов к верхам. Работает только пока играет музыка.
/// </summary>
public sealed class SpectrumService : IDisposable
{
    public const int Bands = 5;
    private const int FftLog = 11;            // 2048 отсчётов
    private const int FftSize = 1 << FftLog;

    private readonly object _lock = new();
    private readonly float[] _buffer = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _levels = new float[Bands];
    private readonly float[] _peaks = new float[Bands];
    private int _filled;
    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private DateTime _lastData;

    public bool Running => _capture != null;

    /// <summary>Есть свежие данные (последние полсекунды) — иначе визуализатор показывает простую анимацию.</summary>
    public bool Live => Running && (DateTime.Now - _lastData).TotalMilliseconds < 500;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            WaveFormat f = capture.WaveFormat;
            if (f.Encoding != WaveFormatEncoding.IeeeFloat && f.BitsPerSample != 16 && f.Encoding != WaveFormatEncoding.Extensible)
            {
                capture.Dispose();
                return;
            }
            _sampleRate = f.SampleRate;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, _) => { };
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _capture = null;
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? c = _capture;
        _capture = null;
        if (c == null) return;
        try
        {
            c.DataAvailable -= OnData;
            c.StopRecording();
            c.Dispose();
        }
        catch
        {
            // устройство пропало — не страшно
        }
        lock (_lock) Array.Clear(_levels);
    }

    /// <summary>Уровни полос 0…1 (сглаженные).</summary>
    public float[] Levels()
    {
        lock (_lock) return (float[])_levels.Clone();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;
        WaveFormat f = _capture.WaveFormat;
        int channels = Math.Max(1, f.Channels);
        bool isFloat = f.BitsPerSample == 32;
        int bytesPerSample = f.BitsPerSample / 8;
        int frameBytes = bytesPerSample * channels;
        _lastData = DateTime.Now;

        for (int pos = 0; pos + frameBytes <= e.BytesRecorded; pos += frameBytes)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int at = pos + ch * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(e.Buffer, at) : BitConverter.ToInt16(e.Buffer, at) / 32768f;
            }
            _buffer[_filled++] = sum / channels;
            if (_filled == FftSize)
            {
                Analyze();
                // половинное перекрытие — плавнее
                Array.Copy(_buffer, FftSize / 2, _buffer, 0, FftSize / 2);
                _filled = FftSize / 2;
            }
        }
    }

    private void Analyze()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = (float)(_buffer[i] * FastFourierTransform.HannWindow(i, FftSize));
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftLog, _fft);

        // логарифмические полосы 50 Гц … 12 кГц
        double lo = 50, hi = Math.Min(12000, _sampleRate / 2.0);
        double binHz = (double)_sampleRate / FftSize;
        var bands = new float[Bands];
        for (int b = 0; b < Bands; b++)
        {
            double f1 = lo * Math.Pow(hi / lo, (double)b / Bands);
            double f2 = lo * Math.Pow(hi / lo, (double)(b + 1) / Bands);
            int i1 = Math.Max(1, (int)(f1 / binHz));
            int i2 = Math.Min(FftSize / 2 - 1, Math.Max(i1 + 1, (int)(f2 / binHz)));
            double power = 0;
            for (int i = i1; i < i2; i++)
                power = Math.Max(power, Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
            // в децибелы и в 0…1 (−60 дБ … 0 дБ), верхи подтягиваем — их энергия меньше
            double db = 20 * Math.Log10(power * (1 + b * 0.6) + 1e-9);
            bands[b] = (float)Math.Clamp((db + 62) / 52, 0, 1);
        }

        lock (_lock)
        {
            for (int b = 0; b < Bands; b++)
            {
                // быстрый подъём, плавный спад; плюс авто-усиление по пику полосы
                _peaks[b] = Math.Max(bands[b], _peaks[b] * 0.995f);
                float v = _peaks[b] > 0.05f ? bands[b] / _peaks[b] : bands[b];
                v = Math.Clamp(v * v, 0, 1);
                _levels[b] = v > _levels[b] ? v : _levels[b] * 0.82f + v * 0.18f;
            }
        }
    }

    public void Dispose() => Stop();
}
