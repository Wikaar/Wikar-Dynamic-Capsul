using System;
using System.IO;
using System.Text;

namespace WikarIsland.Services;

/// <summary>Проверка сборки без окон (запускается в CI): токен голоса и загрузка модели распознавания.</summary>
public static class SelfTest
{
    public static int Run(bool online = false)
    {
        var report = new StringBuilder();
        bool ok = EdgeTtsDrm.SelfTest(out string drm);
        report.Append("Edge TTS DRM v2:\n").Append(drm);

        try
        {
            if (!VoiceService.ModelAvailable) throw new DirectoryNotFoundException(VoiceService.ModelPath);
            Vosk.Vosk.SetLogLevel(-1);
            using var model = new Vosk.Model(VoiceService.ModelPath);
            using var recognizer = new Vosk.VoskRecognizer(model, 16000);
            recognizer.AcceptWaveform(new byte[3200], 3200); // 0,1 с тишины
            report.AppendLine("Vosk: OK " + recognizer.FinalResult().Replace("\n", " "));
        }
        catch (Exception ex)
        {
            ok = false;
            report.AppendLine("Vosk: FAIL " + ex.GetType().Name + ": " + ex.Message);
        }

        if (online)
        {
            // настоящий запрос к голосу Microsoft: принимает ли сервер наш токен; пробуем варианты подключения
            bool any = false;
            // основная настройка пять раз подряд (как в программе), затем со сжатием для сравнения
            foreach ((bool deflate, bool enc) in new[] { (false, true), (false, true), (false, true), (false, true), (false, true), (true, true) })
            {
                SpeechService.UseDeflate = deflate;
                SpeechService.SendAcceptEncoding = enc;
                string label = $"deflate={deflate} accept-encoding={enc}";
                try
                {
                    // Task.Run — без контекста WPF, иначе синхронное ожидание на потоке окна взаимно блокируется
                    byte[] mp3 = System.Threading.Tasks.Task.Run(() =>
                            SpeechService.SynthesizeAsync("Проверка связи.", "ru-RU-DmitryNeural", 0, System.Threading.CancellationToken.None))
                        .GetAwaiter().GetResult();
                    bool voiceOk = mp3.Length > 1000;
                    any |= voiceOk;
                    report.AppendLine($"Edge TTS online [{label}]: {(voiceOk ? "OK" : "FAIL")} {mp3.Length} bytes, шаг: {SpeechService.LastStage}");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"Edge TTS online [{label}]: FAIL на шаге «{SpeechService.LastStage}» {ex.GetType().Name}: {ex.Message} | {ex.InnerException?.Message}");
                }
            }
            ok &= any;
        }

        report.AppendLine(ok ? "RESULT: OK" : "RESULT: FAIL");
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "selftest.txt"), report.ToString());
        return ok ? 0 : 1;
    }
}
