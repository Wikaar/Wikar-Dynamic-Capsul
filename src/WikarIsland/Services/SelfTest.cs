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
            // настоящий запрос к голосу Microsoft: принимает ли сервер наш токен
            try
            {
                byte[] mp3 = SpeechService.SynthesizeAsync("Проверка связи.", "ru-RU-DmitryNeural", 0, System.Threading.CancellationToken.None)
                    .GetAwaiter().GetResult();
                bool voiceOk = mp3.Length > 1000;
                report.AppendLine($"Edge TTS online: {(voiceOk ? "OK" : "FAIL")} {mp3.Length} bytes");
                ok &= voiceOk;
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("Edge TTS online: FAIL " + ex.GetType().Name + ": " + ex.Message + " " + ex.InnerException?.Message);
            }
        }

        report.AppendLine(ok ? "RESULT: OK" : "RESULT: FAIL");
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "selftest.txt"), report.ToString());
        return ok ? 0 : 1;
    }
}
