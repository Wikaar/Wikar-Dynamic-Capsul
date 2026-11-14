using System;
using System.Collections.Generic;
using NAudio.Wave;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using WF = System.Windows.Forms;

namespace WikarIsland.Services;

/// <summary>
/// Запись экрана в MP4 без сторонних программ (работает и в Windows 10):
/// кадры снимаются с экрана (вместе с курсором), системный звук — через WASAPI loopback (что слышно в колонках),
/// всё кодирует встроенный кодировщик Windows (H.264 + AAC).
/// </summary>
public sealed class RecorderService
{
    private const int Fps = 24;

    private volatile bool _stopRequested;
    private Task? _task;

    public bool IsRecording { get; private set; }
    public DateTime StartedAt { get; private set; }

    /// <summary>Запись закончилась: путь к файлу или null, если не вышло.</summary>
    public event Action<string?, string?>? Finished;
    public event Action? StateChanged;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public NativeMethods.POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    public static string Folder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Wikar Island");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Начать запись экрана, на котором стоит окно-ориентир.</summary>
    public void Start(WF.Screen screen)
    {
        if (IsRecording) return;
        Rectangle area = screen.Bounds;
        // кодировщику нужны чётные размеры
        area.Width -= area.Width % 2;
        area.Height -= area.Height % 2;

        string path = Path.Combine(Folder, $"Запись {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        _stopRequested = false;
        IsRecording = true;
        StartedAt = DateTime.Now;
        StateChanged?.Invoke();

        System.Windows.Threading.Dispatcher ui = System.Windows.Application.Current.Dispatcher;
        _task = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                await RecordAsync(area, path);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                error = ex.Message;
            }

            await ui.InvokeAsync(() =>
            {
                IsRecording = false;
                StateChanged?.Invoke();
                bool ok = error == null && File.Exists(path) && new FileInfo(path).Length > 0;
                if (!ok && File.Exists(path)) { try { File.Delete(path); } catch { } }
                Finished?.Invoke(ok ? path : null, error);
            });
        });
    }

    public void Stop() => _stopRequested = true;

    private async Task RecordAsync(Rectangle area, string path)
    {
        int w = area.Width, h = area.Height;

        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Rgb32, (uint)w, (uint)h);
        input.FrameRate.Numerator = Fps;
        input.FrameRate.Denominator = 1;
        // MF_MT_DEFAULT_STRIDE > 0 — кадры идут сверху вниз (без этого RGB считается перевёрнутым)
        input.Properties[new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")] = (uint)(w * 4);

        var videoDescriptor = new VideoStreamDescriptor(input);

        // системный звук; если устройства вывода нет — пишем без звука
        SystemAudio? loopback = null;
        try { loopback = new SystemAudio(); }
        catch (Exception ex) { App.LogError(ex); }
        using SystemAudio? audioOwner = loopback;

        MediaStreamSource source = loopback != null
            ? new MediaStreamSource(videoDescriptor, new AudioStreamDescriptor(
                AudioEncodingProperties.CreatePcm((uint)loopback.SampleRate, (uint)loopback.Channels, 16)))
            : new MediaStreamSource(videoDescriptor);
        source.BufferTime = TimeSpan.Zero;
        source.CanSeek = false;
        source.IsLive = true;

        using var frame = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        // несколько буферов по кругу: кодировщик может ещё читать прошлый кадр
        byte[][] pool = { new byte[w * h * 4], new byte[w * h * 4], new byte[w * h * 4], new byte[w * h * 4] };
        int poolIndex = 0;
        var clock = Stopwatch.StartNew();
        TimeSpan frameTime = TimeSpan.FromSeconds(1.0 / Fps);
        TimeSpan next = TimeSpan.Zero;
        TimeSpan audioChunk = TimeSpan.FromMilliseconds(20);
        TimeSpan nextAudio = TimeSpan.Zero;
        loopback?.Start(clock);

        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            if (_stopRequested)
            {
                e.Request.Sample = null; // конец потока
                return;
            }

            if (e.Request.StreamDescriptor is AudioStreamDescriptor)
            {
                // звук кусками по 20 мс; если в колонках тишина — подставляем тишину
                TimeSpan waitAudio = nextAudio + audioChunk - clock.Elapsed;
                if (waitAudio > TimeSpan.Zero) Thread.Sleep(waitAudio);
                byte[] pcm = loopback!.Take(audioChunk);
                MediaStreamSample audioSample = MediaStreamSample.CreateFromBuffer(pcm.AsBuffer(), nextAudio);
                audioSample.Duration = audioChunk;
                nextAudio += audioChunk;
                e.Request.Sample = audioSample;
                return;
            }

            // держим ровный темп кадров
            TimeSpan wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            TimeSpan stamp = clock.Elapsed;
            next = stamp + frameTime;

            byte[] pixels = pool[poolIndex++ % pool.Length];
            CaptureFrame(frame, area, pixels);
            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(pixels.AsBuffer(), stamp);
            sample.Duration = frameTime;
            e.Request.Sample = sample;
        };

        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = loopback != null
            ? AudioEncodingProperties.CreateAac((uint)loopback.SampleRate, (uint)loopback.Channels, 192_000)
            : null;
        profile.Video.Width = (uint)w;
        profile.Video.Height = (uint)h;
        profile.Video.FrameRate.Numerator = Fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(w * h * 4, 4_000_000, 24_000_000);

        File.WriteAllBytes(path, Array.Empty<byte>());
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream output = await file.OpenAsync(FileAccessMode.ReadWrite);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        PrepareTranscodeResult prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException("Кодировщик Windows не смог начать запись: " + prepared.FailureReason);

        await prepared.TranscodeAsync();
    }

    /// <summary>Снимок экрана с курсором → байты BGRA сверху вниз.</summary>
    private static void CaptureFrame(Bitmap frame, Rectangle area, byte[] pixels)
    {
        using (Graphics g = Graphics.FromImage(frame))
        {
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(g, area);
        }

        BitmapData data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = frame.Width * 4;
            for (int y = 0; y < frame.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }

    private static void DrawCursor(Graphics g, Rectangle area)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || info.flags != 1 /* CURSOR_SHOWING */ || info.hCursor == IntPtr.Zero) return;

        int hotX = 0, hotY = 0;
        if (GetIconInfo(info.hCursor, out ICONINFO icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }

        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, info.ptScreenPos.X - area.Left - hotX, info.ptScreenPos.Y - area.Top - hotY,
                info.hCursor, 0, 0, 0, IntPtr.Zero, 0x0003 /* DI_NORMAL */);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }
}

/// <summary>
/// Системный звук через WASAPI loopback. Копит 16-битный PCM в очереди;
/// когда звука нет (Windows в тишине не присылает данные), отдаёт тишину.
/// </summary>
internal sealed class SystemAudio : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly Queue<byte> _buffer = new();
    private readonly object _lock = new();
    private readonly bool _float;
    private readonly int _bytesPerSample;

    public int SampleRate { get; }
    public int Channels { get; }

    public SystemAudio()
    {
        _capture = new WasapiLoopbackCapture();
        WaveFormat f = _capture.WaveFormat;
        SampleRate = f.SampleRate;
        Channels = Math.Min(f.Channels, 2);
        _float = f.Encoding == WaveFormatEncoding.IeeeFloat
                 || (f is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        _bytesPerSample = f.BitsPerSample / 8;
        _capture.DataAvailable += OnData;
    }

    public void Start(Stopwatch clock) => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int srcChannels = _capture.WaveFormat.Channels;
        int frameBytes = _bytesPerSample * srcChannels;
        int frames = e.BytesRecorded / frameBytes;
        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                for (int ch = 0; ch < Channels; ch++)
                {
                    int offset = i * frameBytes + ch * _bytesPerSample;
                    short value;
                    if (_float && _bytesPerSample == 4)
                        value = (short)Math.Clamp(BitConverter.ToSingle(e.Buffer, offset) * 32767f, -32768f, 32767f);
                    else if (_bytesPerSample == 2)
                        value = BitConverter.ToInt16(e.Buffer, offset);
                    else if (_bytesPerSample == 4)
                        value = (short)(BitConverter.ToInt32(e.Buffer, offset) >> 16);
                    else
                        value = 0;
                    _buffer.Enqueue((byte)(value & 0xFF));
                    _buffer.Enqueue((byte)((value >> 8) & 0xFF));
                }
            }

            // не даём очереди расти больше чем на секунду (рассинхрон)
            int max = SampleRate * Channels * 2;
            while (_buffer.Count > max) _buffer.Dequeue();
        }
    }

    /// <summary>Отдать ровно столько PCM, сколько помещается в длительность chunk.</summary>
    public byte[] Take(TimeSpan chunk)
    {
        int bytes = (int)(SampleRate * chunk.TotalSeconds) * Channels * 2;
        byte[] result = new byte[bytes]; // по умолчанию — тишина
        lock (_lock)
        {
            int n = Math.Min(bytes, _buffer.Count);
            for (int i = 0; i < n; i++) result[i] = _buffer.Dequeue();
        }
        return result;
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}
