using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Свой голос Капсулки через Fish Audio: пользователь сам вставляет ключ API и ID голоса
/// (хвост ссылки fish.audio/…/m/&lt;id&gt;). Никаких голосов в программе не зашито.
/// POST https://api.fish.audio/v1/tts → mp3. Модель задаётся заголовком model:
/// s2.1-pro-free доступна на бесплатном аккаунте.
/// </summary>
public static class FishAudioTts
{
    public const string DefaultModel = "s2.1-pro-free";
    public const string KeysUrl = "https://fish.audio/app/api-keys";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static bool Configured => App.Settings.FishKey.Length > 0 && VoiceId(App.Settings.FishVoiceId).Length > 0;

    /// <summary>Из ссылки на голос — только ID: «https://fish.audio/ru/m/abc123/» → «abc123».</summary>
    public static string VoiceId(string input)
    {
        string s = (input ?? "").Trim().TrimEnd('/');
        int q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s[..q].TrimEnd('/');
        int slash = s.LastIndexOf('/');
        return slash >= 0 ? s[(slash + 1)..] : s;
    }

    public static Task<byte[]> SynthesizeAsync(string text, CancellationToken token) =>
        SynthesizeAsync(text, App.Settings.FishKey, App.Settings.FishVoiceId, App.Settings.FishModel, App.Settings.VoiceRate, token);

    public static async Task<byte[]> SynthesizeAsync(string text, string key, string voice, string model, int ratePercent, CancellationToken token)
    {
        string id = VoiceId(voice);
        if (key.Trim().Length == 0) throw new InvalidOperationException("Нет ключа Fish Audio");
        if (id.Length == 0) throw new InvalidOperationException("Не указан ID голоса Fish Audio");

        var body = new JsonObject
        {
            ["text"] = text,
            ["reference_id"] = id,
            ["format"] = "mp3",
            ["mp3_bitrate"] = 128,
            ["latency"] = "balanced",
            ["normalize"] = true,
            ["prosody"] = new JsonObject { ["speed"] = Math.Clamp(1 + ratePercent / 100.0, 0.5, 2.0) },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.fish.audio/v1/tts")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        request.Headers.TryAddWithoutValidation("model", string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(8 + text.Length / 20, 12, 30)));
        using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new HttpRequestException(Explain((int)response.StatusCode, error), null, response.StatusCode);
        }
        byte[] mp3 = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        if (mp3.Length < 200) throw new InvalidOperationException("Fish Audio вернул пустой звук");
        return mp3;
    }

    /// <summary>Понятная причина ошибки для подсказки в настройках.</summary>
    private static string Explain(int status, string body)
    {
        string detail = body.Length > 160 ? body[..160] : body;
        return status switch
        {
            401 or 403 => "Fish Audio не принял ключ",
            402 => "Эта модель Fish Audio платная — оставьте s2.1-pro-free или пополните баланс",
            404 or 400 when body.Contains("reference", StringComparison.OrdinalIgnoreCase) => "Голос с таким ID не найден",
            400 => "Fish Audio отклонил запрос: " + detail,
            429 => "Лимит Fish Audio исчерпан, попробуйте позже",
            _ => $"Fish Audio ответил {status}: {detail}",
        };
    }
}
