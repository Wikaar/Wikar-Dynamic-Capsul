using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vosk;

namespace WikarIsland.Services;

public enum VoiceState
{
    Off,       // помощник выключен
    Waiting,   // ждём «Капсулка»
    Listening, // услышали слово, слушаем команду
    Thinking,  // выполняем или спрашиваем ИИ
    Speaking,  // отвечаем голосом
}

/// <summary>
/// Слух Капсулки. Микрофон → 16 кГц моно → Vosk (офлайн, русская модель в папке vosk\model).
/// В фоне ищем в речи «капсулка/капсула»; после неё ловим команду и отдаём её AssistantService.
/// В интернет отсюда не уходит ничего.
/// </summary>
public sealed partial class VoiceService : IDisposable
{
    private const int SampleRate = 16000;

    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private readonly object _lock = new();
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _pipeline;
    private float[] _floats = new float[SampleRate];
    private byte[] _pcm = new byte[SampleRate * 2];
    private bool _command;           // сейчас слушаем команду, а не слово
    private DateTime _commandUntil;
    private bool _heardSomething;    // в режиме команды уже что-то сказали
    private bool _deaf;              // пока думаем и говорим — не слушаем себя
    private bool _starting;
    private string? _deviceId;       // какой микрофон открыт
    private volatile bool _lost;     // Windows отключила микрофон (0x88890004 и т. п.)
    private int _reconnecting;
    private System.Threading.Timer? _watch;
    private readonly MemoryStream _utterance = new(); // звук текущей фразы (16 кГц моно) — для онлайн-распознавания
    private const int MaxUtteranceBytes = SampleRate * 2 * 20;
    private const int FollowUpSeconds = 10;

    // Gemini Live: после слова «Капсулка» звук идёт в Gemini потоком, ответ звучит сразу
    private GeminiLiveService? _live;
    private volatile bool _liveMode;
    private volatile bool _liveEnding;
    private bool _liveWasSpeaking;
    private DateTime _liveSpokeAt, _liveUserAt;
    private DateTime _liveOffUntil = DateTime.MinValue; // после сбоя — обычный режим на 10 минут
    private string _liveModelText = "";
    private const int LiveIdleSeconds = 12;

    public VoiceState State { get; private set; } = VoiceState.Off;

    /// <summary>Что показать в капсуле: распознанная речь или ответ.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Громкость микрофона 0..1 — капсула «дышит» в такт.</summary>
    public float MicLevel { get; private set; }

    public string? Problem { get; private set; }

    public event Action? Changed;

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "vosk", "model");
    public static bool ModelAvailable => Directory.Exists(ModelPath);

    // Капсулка, Капсуль, Капсула, Капс; Whisper иногда пишет по-английски — Capsule, Kapsulka, Kapsel
    [GeneratedRegex(@"к[ао][пб]\s?сул\w*|\bкапс\b|\bcapsul\w*|\bkapsul\w*|\bkapsel\w*", RegexOptions.IgnoreCase)]
    private static partial Regex WakeWord();

    // ---------- включение ----------

    public void Start()
    {
        if (State != VoiceState.Off || _starting) return;
        if (!ModelAvailable)
        {
            Problem = "Нет модели распознавания (папка vosk рядом с программой). Поставь свежую сборку целиком.";
            Changed?.Invoke();
            return;
        }
        _starting = true;
        Problem = null;
        Text = "";
        Changed?.Invoke();

        Task.Run(() =>
        {
            try
            {
                Vosk.Vosk.SetLogLevel(-1);
                _model ??= new Model(ModelPath); // 1–2 секунды
                lock (_lock)
                {
                    _recognizer = new VoskRecognizer(_model, SampleRate);
                }
                OpenMicrophone();
                Set(VoiceState.Waiting, "");
                _watch?.Dispose();
                _watch = new System.Threading.Timer(_ => WatchMicrophone(), null, 3000, 3000);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                Problem = ex is DllNotFoundException
                    ? "Не загрузился движок распознавания (libvosk.dll). Поставь свежую сборку целиком."
                    : "Микрофон или распознавание не запустились: " + ex.Message;
                CloseMicrophone();
                Set(VoiceState.Off, "");
            }
            finally
            {
                _starting = false;
            }
        });
    }

    public void Stop()
    {
        EndLive(silent: true);
        _watch?.Dispose();
        _watch = null;
        CloseMicrophone();
        lock (_lock)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        _command = false;
        Set(VoiceState.Off, "");
    }

    public void Dispose()
    {
        Stop();
        _model?.Dispose();
        _model = null;
    }

    /// <summary>Горячая клавиша: слушать команду сразу, без слова.</summary>
    public void ListenNow()
    {
        if (State == VoiceState.Off) return;
        if (_liveMode)
        {
            _live?.StopSpeaking();
            return;
        }
        if (State is VoiceState.Speaking or VoiceState.Thinking) App.Speech.Stop();
        if (TryStartLive()) return;
        if (State is VoiceState.Speaking or VoiceState.Thinking) App.Speech.Stop();
        BeginCommand(chime: true, seconds: 7);
    }

    /// <summary>Клик по капсуле во время разговора — замолчать и перестать слушать.</summary>
    public void Cancel()
    {
        if (_liveMode)
        {
            _live?.StopSpeaking();
            EndLive();
            return;
        }
        App.Speech.Stop();
        _command = false;
        _deaf = false;
        ResetRecognizer();
        if (State != VoiceState.Off) Set(VoiceState.Waiting, "");
    }

    // ---------- микрофон ----------

    private void OpenMicrophone()
    {
        CloseMicrophone();
        MMDevice device = WasapiCapture.GetDefaultCaptureDevice(); // микрофон по умолчанию
        var capture = new WasapiCapture(device);
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(3),
            ReadFully = false,
        };

        ISampleProvider sp = buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
        {
            var mono = new MultiplexingSampleProvider(new[] { sp }, 1);
            mono.ConnectInputToOutput(0, 0);
            sp = mono;
        }
        if (sp.WaveFormat.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);

        capture.DataAvailable += (_, e) => OnAudio(e.Buffer, e.BytesRecorded);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception == null || State == VoiceState.Off || !ReferenceEquals(_capture, capture)) return;
            App.LogError(e.Exception);
            _lost = true; // переподключит WatchMicrophone через пару секунд
            Problem = "Микрофон отключился, переподключаюсь…";
            _ui.BeginInvoke(() => Changed?.Invoke());
        };

        _capture = capture;
        _buffer = buffer;
        _pipeline = sp;
        _deviceId = device.ID;
        _lost = false;
        capture.StartRecording();
    }

    /// <summary>
    /// Раз в 3 секунды: если Windows отключила микрофон (беспроводная гарнитура уснула, другая программа
    /// забрала его) или сменился микрофон по умолчанию — открываем заново. Раньше после такого остров глох навсегда.
    /// </summary>
    private void WatchMicrophone()
    {
        if (State == VoiceState.Off || _starting) return;
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1) return;
        try
        {
            string? current = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID;
            }
            catch
            {
                // микрофонов нет совсем — ждём, пока появится
            }
            if (current == null)
            {
                if (_lost && Problem == null)
                {
                    Problem = "Микрофон не найден — жду, пока подключится.";
                    _ui.BeginInvoke(() => Changed?.Invoke());
                }
                return;
            }
            if (!_lost && _capture != null && current == _deviceId) return;

            try
            {
                OpenMicrophone();
                ResetRecognizer();
                Problem = null;
            }
            catch (Exception ex)
            {
                _lost = true;
                Problem = "Микрофон недоступен, пробую снова: " + ex.Message;
            }
            _ui.BeginInvoke(() => Changed?.Invoke());
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    private void CloseMicrophone()
    {
        WasapiCapture? c = _capture;
        _capture = null;
        if (c == null) return;
        try
        {
            c.StopRecording();
        }
        catch
        {
            // уже остановлен
        }
        try
        {
            c.Dispose();
        }
        catch
        {
            // устройство уже исчезло
        }
    }

    private void OnAudio(byte[] data, int count)
    {
        BufferedWaveProvider? buffer = _buffer;
        ISampleProvider? pipeline = _pipeline;
        if (buffer == null || pipeline == null) return;
        buffer.AddSamples(data, 0, count);

        int read;
        while ((read = pipeline.Read(_floats, 0, _floats.Length)) > 0)
        {
            float peak = 0;
            for (int i = 0; i < read; i++)
            {
                float f = Math.Clamp(_floats[i], -1f, 1f);
                peak = Math.Max(peak, Math.Abs(f));
                short s = (short)(f * 32767);
                _pcm[2 * i] = (byte)s;
                _pcm[2 * i + 1] = (byte)(s >> 8);
            }
            MicLevel = MicLevel * 0.6f + Math.Min(1f, peak * 2.5f) * 0.4f;
            if (_liveMode)
            {
                LiveAudio(read * 2);
                continue;
            }
            if (_deaf) continue; // своё же озвучивание не слушаем
            Recognize(read * 2);
        }

        if (_liveMode)
        {
            LiveTick();
            return;
        }

        // команду так и не сказали — возвращаемся к ожиданию слова
        if (_command && !_heardSomething && DateTime.Now > _commandUntil)
        {
            _command = false;
            ResetRecognizer();
            Set(VoiceState.Waiting, "");
        }
    }

    private void Recognize(int bytes)
    {
        string? final = null, partial = null;
        byte[] audio = Array.Empty<byte>();
        lock (_lock)
        {
            if (_recognizer == null) return;
            if (_utterance.Length + bytes > MaxUtteranceBytes)
            {
                // слишком длинная фраза — оставляем последние 10 секунд
                byte[] all = _utterance.ToArray();
                int keep = MaxUtteranceBytes / 2;
                _utterance.SetLength(0);
                _utterance.Write(all, all.Length - keep, keep);
            }
            _utterance.Write(_pcm, 0, bytes);
            if (_recognizer.AcceptWaveform(_pcm, bytes))
            {
                final = TextOf(_recognizer.Result(), "text");
                audio = _utterance.ToArray();
                _utterance.SetLength(0);
            }
            else partial = TextOf(_recognizer.PartialResult(), "partial");
        }

        if (partial != null) OnPartial(partial);
        if (final != null) OnFinal(final, audio);
    }

    private static string TextOf(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out JsonElement v) ? v.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private void OnPartial(string text)
    {
        if (text.Length == 0) return;
        if (!_command)
        {
            if (!WakeWord().IsMatch(text)) return;
            if (TryStartLive()) return;
            BeginCommand(chime: true, seconds: 8);
        }
        string shown = AfterWake(text);
        _heardSomething = shown.Length > 0;
        Set(VoiceState.Listening, shown);
    }

    private void OnFinal(string text, byte[] audio)
    {
        if (!_command)
        {
            if (!WakeWord().IsMatch(text)) return;
            if (TryStartLive(audio)) return;
            string rest = AfterWake(text);
            if (rest.Length > 0) DispatchHeard(rest, audio);
            else BeginCommand(chime: true, seconds: 8);
            return;
        }

        string command = AfterWake(text);
        if (command.Length < 2) return; // сказали только «Капсулка» или шум — ждём команду дальше
        // в режиме разговора при играющей музыке без имени не слушаем — это слова песни
        if (_followUp && App.Media.Current?.IsPlaying == true && !WakeWord().IsMatch(text))
        {
            App.LogVoice($"Пропущено (играет музыка, без имени): «{text}»");
            return;
        }
        DispatchHeard(command, audio);
    }

    /// <summary>
    /// Фраза закончилась. Если есть ключ Groq — переспрашиваем её у Whisper (он знает названия программ),
    /// офлайн-текст Vosk остаётся запасным, если сеть подвела.
    /// </summary>
    private void DispatchHeard(string voskCommand, byte[] audio)
    {
        if (!CloudStt.Available)
        {
            Dispatch(voskCommand);
            return;
        }
        _command = false;
        _deaf = true;
        ResetRecognizer();
        Set(VoiceState.Thinking, voskCommand);
        Task.Run(async () =>
        {
            string command = voskCommand;
            string? heard = await CloudStt.TranscribeAsync(audio);
            if (heard != null)
            {
                string fromCloud = AfterWake(heard.ToLowerInvariant());
                if (fromCloud.Length > 0) command = fromCloud;
            }
            if (State == VoiceState.Off) return;
            Dispatch(command);
        });
    }

    /// <summary>Всё, что после слова «Капсулка» (если слова нет — весь текст).</summary>
    private static string AfterWake(string text)
    {
        Match m = WakeWord().Match(text);
        string rest = m.Success ? text[(m.Index + m.Length)..] : text;
        rest = Regex.Replace(rest, @"^\s*ка\b", ""); // «капсул ка»
        return rest.Trim().Trim(',', '.', '!', '?', ':', ';', '—', '-', '…', ' ');
    }

    // ---------- Gemini Live ----------

    /// <summary>Начать живой разговор, если он включён. prefill — уже записанная фраза («Капсулка, открой…»).</summary>
    private bool TryStartLive(byte[]? prefill = null)
    {
        if (!GeminiLiveService.Available || DateTime.Now < _liveOffUntil) return false;
        byte[] head;
        lock (_lock)
        {
            head = prefill ?? _utterance.ToArray();
            _recognizer?.Reset();
            _utterance.SetLength(0);
        }
        _command = false;
        _liveEnding = false;
        if (Problem?.StartsWith("Gemini Live", StringComparison.Ordinal) == true) Problem = null;
        _liveWasSpeaking = false;
        _liveModelText = "";
        _liveSpokeAt = _liveUserAt = DateTime.Now;

        var live = new GeminiLiveService();
        live.UserSaid += text =>
        {
            _liveUserAt = DateTime.Now;
            if (!live.IsSpeaking) Set(VoiceState.Listening, text);
        };
        live.ModelSaid += text =>
        {
            _liveModelText = text;
            Set(VoiceState.Speaking, text);
        };
        live.EndRequested += () => _liveEnding = true;
        live.Failed += message => _ui.BeginInvoke(() => OnLiveFailed(live, message));
        try
        {
            live.Open();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            live.Dispose();
            _liveOffUntil = DateTime.Now.AddMinutes(10);
            return false;
        }
        _live = live;
        _liveMode = true;
        if (head.Length > 0) live.SendAudio(head, head.Length);
        Chime.Play();
        Set(VoiceState.Listening, "");
        return true;
    }

    /// <summary>Звук микрофона в живом режиме. Пока Gemini говорит — без перебивания не отправляем (иначе он услышит себя из колонок).</summary>
    private void LiveAudio(int bytes)
    {
        GeminiLiveService? live = _live;
        if (live == null) return;
        bool speaking = live.IsSpeaking;
        if (speaking) _liveSpokeAt = DateTime.Now;
        if (App.Settings.LiveBargeIn || (!speaking && (DateTime.Now - _liveSpokeAt).TotalMilliseconds > 500))
            live.SendAudio(_pcm, bytes);
    }

    /// <summary>Состояние капсулы и конец разговора: прощание, молчание 12 секунд или режим «одна команда».</summary>
    private void LiveTick()
    {
        GeminiLiveService? live = _live;
        if (live == null) return;
        bool speaking = live.IsSpeaking;
        if (speaking && !_liveWasSpeaking) Set(VoiceState.Speaking, _liveModelText);
        if (!speaking && _liveWasSpeaking)
        {
            _liveModelText = "";
            if (!App.Settings.VoiceFollowUp) _liveEnding = true;
            else Set(VoiceState.Listening, "");
        }
        _liveWasSpeaking = speaking;
        if (speaking) return;

        DateTime now = DateTime.Now;
        if (_liveEnding && (now - _liveSpokeAt).TotalMilliseconds > 400)
        {
            EndLive();
            return;
        }
        DateTime last = new[] { live.LastActivity, _liveSpokeAt, _liveUserAt }.Max();
        if ((now - last).TotalSeconds > LiveIdleSeconds) EndLive();
    }

    private void EndLive(bool silent = false)
    {
        GeminiLiveService? live = Interlocked.Exchange(ref _live, null);
        if (live == null) return;
        _liveMode = false;
        _liveEnding = false;
        live.Close();
        ResetRecognizer();
        if (!silent && State != VoiceState.Off) Set(VoiceState.Waiting, "");
    }

    /// <summary>Gemini Live не открылся или оборвался: 10 минут работаем по-старому, команду можно повторить сразу.</summary>
    private async void OnLiveFailed(GeminiLiveService live, string message)
    {
        if (!ReferenceEquals(_live, live)) return;
        EndLive(silent: true);
        bool quota = message.Contains("quota", StringComparison.OrdinalIgnoreCase) || message.Contains("billing", StringComparison.OrdinalIgnoreCase);
        // нет квоты — это не на минуту: не мучаем пользователя каждые 10 минут, ждём до перезапуска или 6 часов
        _liveOffUntil = DateTime.Now.Add(quota ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(10));
        Problem = quota
            ? "Gemini Live: Google ответил «нет квоты» (бесплатный лимит на сегодня исчерпан или модель доступна только с оплатой в AI Studio). 30 минут работаю в обычном режиме."
            : "Gemini Live недоступен (" + message + "). 10 минут работаю в обычном режиме.";
        Changed?.Invoke();
        if (State == VoiceState.Off) return;
        _deaf = true;
        Set(VoiceState.Speaking, "Gemini Live недоступен");
        await App.Speech.SpeakAsync("Живой режим недоступен, сэр. Повторите команду, я выполню её по-старому.");
        await Task.Delay(250);
        ResetRecognizer();
        _deaf = false;
        if (State != VoiceState.Off) BeginCommand(chime: true, seconds: 8);
    }

    private bool _followUp;

    private void BeginCommand(bool chime, int seconds)
    {
        _command = true;
        _followUp = !chime; // без звонка — это режим разговора после команды, а не «Капсулка»/горячая клавиша
        _heardSomething = false;
        _commandUntil = DateTime.Now.AddSeconds(seconds);
        if (chime) Chime.Play();
        Set(VoiceState.Listening, "");
    }

    private void ResetRecognizer()
    {
        lock (_lock)
        {
            _recognizer?.Reset();
            _utterance.SetLength(0);
        }
    }

    // ---------- команда → ответ ----------

    private void Dispatch(string command)
    {
        _command = false;
        _deaf = true;
        ResetRecognizer();
        Set(VoiceState.Thinking, command);

        _ui.BeginInvoke(async () =>
        {
            // ответ ИИ приходит по кусочкам: первое предложение начинает звучать, пока пишется остальное
            var sentences = Channel.CreateUnbounded<string>();
            Task? streaming = null;
            void Speak(string sentence)
            {
                if (State == VoiceState.Off) return;
                streaming ??= App.Speech.SpeakStreamAsync(sentences.Reader, said => Set(VoiceState.Speaking, said));
                sentences.Writer.TryWrite(sentence);
            }

            AssistantReply reply;
            try
            {
                reply = await App.Assistant.HandleAsync(command, Speak);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                reply = new AssistantReply("Что-то пошло не так, сэр. Подробности в журнале ошибок.");
            }
            sentences.Writer.TryComplete();

            if (State == VoiceState.Off) return;
            if (streaming != null) await streaming;
            if (!reply.Spoken && reply.Text.Length > 0)
            {
                Set(VoiceState.Speaking, reply.Text);
                await App.Speech.SpeakAsync(reply.Text);
            }
            if (State == VoiceState.Off) return;

            await Task.Delay(250); // хвост собственного голоса в микрофоне
            ResetRecognizer();
            _deaf = false;

            // ждём ответа («да» на подтверждение) или продолжения разговора — без слова «Капсулка»
            if (reply.ExpectReply)
            {
                BeginCommand(chime: false, seconds: 8);
                _followUp = false; // ждём именно ответа «да/нет» — слушаем даже при музыке
            }
            // режим разговора: после любой команды ещё 10 секунд слушаем следующую без слова «Капсулка»;
            // окно продлевается после каждой команды, «спасибо» или «хватит» его закрывают
            // пока играет музыка — не слушаем без имени: слова песни из колонок иначе становятся «командами»
            else if (App.Settings.VoiceFollowUp && !reply.EndTalk && App.Media.Current?.IsPlaying != true)
                BeginCommand(chime: false, seconds: FollowUpSeconds);
            else Set(VoiceState.Waiting, "");
        });
    }

    private void Set(VoiceState state, string text)
    {
        State = state;
        Text = text;
        _ui.BeginInvoke(() => Changed?.Invoke());
    }
}

/// <summary>Короткий двухтоновый сигнал «слушаю» (генерируем сами, без файлов).</summary>
internal static class Chime
{
    private static byte[]? _wav;

    public static void Play()
    {
        try
        {
            _wav ??= Build();
            var player = new System.Media.SoundPlayer(new MemoryStream(_wav));
            player.Play();
        }
        catch
        {
            // без звука
        }
    }

    private static byte[] Build()
    {
        const int rate = 22050;
        var samples = new System.Collections.Generic.List<short>();
        void Tone(double freq, double seconds)
        {
            int n = (int)(rate * seconds);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i / (rate * 0.01), (n - i) / (rate * 0.04)));
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * i / rate) * 9000 * env));
            }
        }
        Tone(880, 0.08);
        Tone(1320, 0.11);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples.Count * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
