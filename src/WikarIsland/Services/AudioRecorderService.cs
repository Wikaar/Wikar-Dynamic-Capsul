using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WikarIsland.Services;

public enum AudioSourceKind
{
    Speakers,
    Microphone,
    Both,
}

/// <summary>
/// Запись звука: колонки (WASAPI loopback), микрофон или оба сразу, смешанные в один файл.
/// Пишем WAV, после остановки сжимаем встроенным кодировщиком Windows в .m4a (AAC).
/// </summary>
public sealed class AudioRecorderService
{
    private static readonly WaveFormat Target = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private volatile bool _stopRequested;

    public bool IsRecording { get; private set; }
    public DateTime StartedAt { get; private set; }
    public AudioSourceKind Source { get; private set; }

    public event Action<string?, string?>? Finished;
    public event Action? StateChanged;

    public static string Folder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Wikar Island");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SourceName(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Speakers => "колонки",
        AudioSourceKind.Microphone => "микрофон",
        _ => "колонки + микрофон",
    };

    public void Start(AudioSourceKind source)
    {
        if (IsRecording) return;
        Source = source;
        _stopRequested = false;
        IsRecording = true;
        StartedAt = DateTime.Now;
        StateChanged?.Invoke();

        string name = $"Звук ({SourceName(source)}) {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        string wav = Path.Combine(Folder, name + ".wav");
        string m4a = Path.Combine(Folder, name + ".m4a");
        System.Windows.Threading.Dispatcher ui = System.Windows.Application.Current.Dispatcher;

        Task.Run(async () =>
        {
            string? result = null, error = null;
            try
            {
                Record(source, wav);
                result = await CompressAsync(wav, m4a) ? m4a : wav;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                error = ex.Message;
                try { if (File.Exists(wav) && new FileInfo(wav).Length < 1024) File.Delete(wav); } catch { }
            }

            await ui.InvokeAsync(() =>
            {
                IsRecording = false;
                StateChanged?.Invoke();
                Finished?.Invoke(result, error);
            });
        });
    }

    public void Stop() => _stopRequested = true;

    private void Record(AudioSourceKind source, string wavPath)
    {
        var captures = new List<IWaveIn>();
        var mixer = new MixingSampleProvider(Target) { ReadFully = true };

        void AddCapture(IWaveIn capture)
        {
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                ReadFully = true,                // нет данных — тишина
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };
            capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            mixer.AddMixerInput(ToTarget(buffer.ToSampleProvider()));
            captures.Add(capture);
        }

        try
        {
            if (source is AudioSourceKind.Speakers or AudioSourceKind.Both) AddCapture(new WasapiLoopbackCapture());
            if (source is AudioSourceKind.Microphone or AudioSourceKind.Both)
            {
                try { AddCapture(new WasapiCapture()); }
                catch (Exception ex)
                {
                    if (source == AudioSourceKind.Microphone) throw new InvalidOperationException("Микрофон не найден", ex);
                    App.LogError(ex); // колонки всё равно пишем
                }
            }

            using var writer = new WaveFileWriter(wavPath, Target);
            foreach (IWaveIn c in captures) c.StartRecording();

            var clock = Stopwatch.StartNew();
            long written = 0; // в отсчётах (float), оба канала
            float[] chunk = new float[Target.SampleRate * Target.Channels];
            while (!_stopRequested)
            {
                Thread.Sleep(50);
                long due = (long)(clock.Elapsed.TotalSeconds * Target.SampleRate) * Target.Channels;
                while (written < due)
                {
                    int count = (int)Math.Min(chunk.Length, due - written);
                    count -= count % Target.Channels;
                    if (count == 0) break;
                    int read = mixer.Read(chunk, 0, count);
                    writer.WriteSamples(chunk, 0, read);
                    written += read;
                }
            }
        }
        finally
        {
            foreach (IWaveIn c in captures)
            {
                try { c.StopRecording(); } catch { }
                c.Dispose();
            }
        }
    }

    /// <summary>Приводим любой источник к 48 кГц стерео float.</summary>
    private static ISampleProvider ToTarget(ISampleProvider input)
    {
        ISampleProvider sp = input;
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
        {
            var stereo = new MultiplexingSampleProvider(new[] { sp }, 2);
            stereo.ConnectInputToOutput(0, 0);
            stereo.ConnectInputToOutput(1, 1);
            sp = stereo;
        }
        if (sp.WaveFormat.SampleRate != Target.SampleRate) sp = new WdlResamplingSampleProvider(sp, Target.SampleRate);
        return sp;
    }

    /// <summary>WAV → .m4a (AAC). Если не вышло — оставляем WAV.</summary>
    private static async Task<bool> CompressAsync(string wav, string m4a)
    {
        try
        {
            StorageFile source = await StorageFile.GetFileFromPathAsync(wav);
            File.WriteAllBytes(m4a, Array.Empty<byte>());
            StorageFile target = await StorageFile.GetFileFromPathAsync(m4a);

            var transcoder = new MediaTranscoder();
            PrepareTranscodeResult prepared = await transcoder.PrepareFileTranscodeAsync(
                source, target, MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High));
            if (!prepared.CanTranscode)
            {
                File.Delete(m4a);
                return false;
            }
            await prepared.TranscodeAsync();
            File.Delete(wav);
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            try { if (File.Exists(m4a) && new FileInfo(m4a).Length == 0) File.Delete(m4a); } catch { }
            return false;
        }
    }
}
