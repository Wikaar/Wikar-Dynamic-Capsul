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

        // каталог системных программ: все шаблоны собираются, коды не повторяются, частые фразы находятся
        try
        {
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SystemCatalog.Entry e in SystemCatalog.All)
                if (!ids.Add(e.Id)) throw new InvalidOperationException("повтор кода " + e.Id);
            (string phrase, string id)[] cases =
            {
                ("диспетчер задач", "taskmgr"), ("диспетчера задач", "taskmgr"), ("микшер громкости", "mixer"), ("службы", "services"),
                ("параметры wi-fi", "wifi"), ("вайфай", "wifi"), ("папку автозагрузки", "startupfolder"), ("автозагрузку", "startup"),
                ("просмотр событий", "eventvwr"), ("эмодзи", "emoji"), ("окно микшера громкости", "mixer"), ("панель управления", "control"),
                ("программы и компоненты", "appwiz"), ("редактор реестра", "regedit"), ("диспетчер устройств", "devmgmt"),
            };
            foreach ((string phrase, string id) in cases)
            {
                string got = SystemCatalog.Find(phrase)?.Id ?? "—";
                if (got != id) throw new InvalidOperationException($"«{phrase}» → {got}, ждали {id}");
            }
            foreach (string notSystem in new[] { "дискорд", "ворд", "стим", "телеграм" })
                if (SystemCatalog.Find(notSystem) is { } wrong) throw new InvalidOperationException($"«{notSystem}» ошибочно → {wrong.Id}");
            report.AppendLine($"System catalog: OK {SystemCatalog.All.Length} пунктов");
        }
        catch (Exception ex)
        {
            ok = false;
            report.AppendLine("System catalog: FAIL " + ex.GetType().Name + ": " + ex.Message);
        }

        // VPN: конфиги Xray (прокси и TUN) и sing-box (TUN) проверяют сами ядра — как они их поймут у пользователя
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "wikar-selftest");
            Directory.CreateDirectory(dir);
            string xray = Path.Combine(AppContext.BaseDirectory, "xray", "xray.exe");
            string[] links =
            {
                "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=tcp&security=reality&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw&sid=abcd&sni=www.microsoft.com&fp=chrome&flow=xtls-rprx-vision#reality",
                "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=xhttp&security=tls&sni=example.com&path=%2Fx&mode=auto&extra=%7B%22xPaddingBytes%22%3A%22100-1000%22%7D#xhttp",
                "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=ws&security=tls&path=%2Fws#ws",
            };
            if (File.Exists(xray))
            {
                int n = 0;
                foreach (string link in links)
                {
                    VlessLink l = VlessLink.Parse(link);
                    foreach (bool tun in new[] { false, true })
                    {
                        string cfg = Path.Combine(dir, $"xray-{n++}.json");
                        File.WriteAllText(cfg, l.ToXrayConfig(10809, 10808, new[] { "https://1.1.1.1/dns-query" }, tun ? "93.184.215.14" : null, tun));
                        (int code, string output) = RunTool(xray, $"run -test -c \"{cfg}\"", Path.GetDirectoryName(xray)!, ("XRAY_LOCATION_ASSET", Path.GetDirectoryName(xray)!));
                        if (code != 0) throw new InvalidOperationException($"Xray не принял конфиг {l.Name} (tun={tun}): {Tail(output)}");
                    }
                }
                report.AppendLine($"Xray configs: OK {n}");
            }
            else report.AppendLine("Xray configs: пропущено (нет xray.exe)");

            if (VpnTun.Available)
            {
                foreach (string[] dns in new[] { Array.Empty<string>(), new[] { "https://dns.google/dns-query" }, new[] { "9.9.9.9" } })
                {
                    string cfg = Path.Combine(dir, "sing-box.json");
                    File.WriteAllText(cfg, VpnTun.Config(10808, dns));
                    (int code, string output) = RunTool(VpnTun.Exe, $"check -c \"{cfg}\"", VpnTun.Dir);
                    if (code != 0) throw new InvalidOperationException($"sing-box не принял конфиг (DNS {string.Join(",", dns)}): {Tail(output)}");
                    if (output.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
                        report.AppendLine("sing-box: предупреждение: " + Tail(output));
                }
                (_, string version) = RunTool(VpnTun.Exe, "version", VpnTun.Dir);
                report.AppendLine("sing-box config: OK " + version.Split('\n')[0].Trim());
            }
            else report.AppendLine("sing-box config: пропущено (нет sing-box.exe)");
        }
        catch (Exception ex)
        {
            ok = false;
            report.AppendLine("VPN configs: FAIL " + ex.GetType().Name + ": " + ex.Message);
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

    private static (int code, string output) RunTool(string exe, string args, string workDir, params (string key, string value)[] env)
    {
        var info = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach ((string key, string value) in env) info.Environment[key] = value;
        using var p = System.Diagnostics.Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000))
        {
            p.Kill();
            return (-1, "timeout");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static string Tail(string s)
    {
        s = s.Trim().Replace("\r", "").Replace("\n", " | ");
        return s.Length > 600 ? s[^600..] : s;
    }
}
