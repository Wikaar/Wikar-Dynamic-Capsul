using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Онлайн-распознавание команды через Whisper на Groq (тот же ключ, что у ИИ).
/// Офлайн-Vosk только ловит слово «Капсулка» и конец фразы; саму фразу отправляем сюда:
/// Whisper знает названия программ и игр (Discord, Telegram, Valheim), которых нет в словаре Vosk.
/// В интернет уходит только записанная команда, а не постоянный звук с микрофона.
/// </summary>
public static class CloudStt
{
    private const string Model = "whisper-large-v3-turbo";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Есть ключ Groq — распознаём онлайн.</summary>
    public static bool Available =>
        Key.Length > 0;

    /// <summary>Свой ключ Groq для слуха; если его нет, а «мозг» — Groq, то ключ ИИ.</summary>
    private static string Key =>
        App.Settings.GroqKey.Length > 0 ? App.Settings.GroqKey
        : App.Settings.AiBaseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase) ? App.Settings.AiKey : "";

    private const string GroqUrl = "https://api.groq.com/openai/v1/audio/transcriptions";

    // Whisper на тишине и шуме иногда «дописывает» титры из обучающих видео
    private static readonly string[] Hallucinations =
    {
        "субтитр", "продолжение следует", "спасибо за просмотр", "подписывайтесь", "dimatorzok", "редактор субтитров",
    };

    /// <summary>PCM 16 кГц моно 16 бит → текст. null — не получилось (тогда берём текст Vosk).</summary>
    public static async Task<string?> TranscribeAsync(byte[] pcm16k, CancellationToken token = default)
    {
        if (!Available || pcm16k.Length < 16000 * 2 / 4) return null; // меньше четверти секунды — нечего слать
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Wav(pcm16k));
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "command.wav");
            form.Add(new StringContent(Model), "model");
            form.Add(new StringContent("ru"), "language");
            form.Add(new StringContent("0"), "temperature");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent(Prompt()), "prompt");

            using var request = new HttpRequestMessage(HttpMethod.Post, GroqUrl) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using HttpResponseMessage response = await Http.SendAsync(request, token);
            string json = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                App.LogError(new InvalidOperationException($"Whisper ответил {(int)response.StatusCode}: {json[..Math.Min(json.Length, 300)]}"));
                return null;
            }
            using JsonDocument doc = JsonDocument.Parse(json);
            string text = doc.RootElement.TryGetProperty("text", out JsonElement t) ? t.GetString()?.Trim() ?? "" : "";
            if (text.Length == 0) return null;
            string lower = text.ToLowerInvariant();
            if (Hallucinations.Any(h => lower.Contains(h, StringComparison.Ordinal))) return null;
            if (lower.Contains("названия:", StringComparison.Ordinal)) return null; // повторил подсказку вместо речи
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            App.LogError(new InvalidOperationException("Whisper не ответил, беру офлайн-распознавание", ex));
            return null;
        }
    }

    /// <summary>Подсказка словаря: имя помощника и названия игр и программ этого компьютера (до ~200 токенов).</summary>
    private static string Prompt()
    {
        var sb = new StringBuilder("Капсулка. Названия: Discord, Telegram, Steam, YouTube, ");
        try
        {
            foreach (string name in AppIndex.Entries.Where(e => e.Kind != "Пуск").Select(e => e.Name)
                         .Concat(AppIndex.Entries.Where(e => e.Kind == "Пуск").Select(e => e.Name)).Distinct())
            {
                if (sb.Length + name.Length > 600) break;
                sb.Append(name).Append(", ");
            }
        }
        catch
        {
            // список программ ещё строится — хватит примера
        }
        return sb.ToString().TrimEnd(' ', ',');
    }

    private static byte[] Wav(byte[] pcm)
    {
        const int rate = 16000;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm.Length);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
