using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WikarIsland.Services;

public sealed record CityResult(string Name, string Region, string Country, double Lat, double Lon)
{
    public string Full => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed record HourForecast(DateTime Time, double Temp, string Symbol);

public sealed record DayForecast(DateTime Date, double Max, double Min, string Symbol);

public sealed class WeatherData
{
    public string City { get; init; } = "";
    public double Temp { get; init; }
    public double Feels { get; init; }
    public string Symbol { get; init; } = "cloudy";
    public double Wind { get; init; }
    public double Humidity { get; init; }
    public double Precipitation { get; init; }
    public List<HourForecast> Hours { get; init; } = new();
    public List<DayForecast> Days { get; init; } = new();
    public DateTime Updated { get; init; }

    public string Text => WeatherSymbols.Text(Symbol);
    public string TempText => $"{Math.Round(Temp):0}°";
}

/// <summary>
/// Погода от MET Norway (api.met.no, Норвежский метеоинститут): бесплатно, без ключа.
/// Их правила требуют представляться в User-Agent. Поиск города — через геокодер Open-Meteo.
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(20) };

    public WeatherData? Current { get; private set; }
    public event Action? Changed;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WikarIsland/0.6 github.com/Wikaar/Wikar-DynamicIsland");
        return http;
    }

    public void Start()
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        AppSettings s = App.Settings;
        if (s.WeatherLat is not double lat || s.WeatherLon is not double lon)
        {
            Current = null;
            Changed?.Invoke();
            return;
        }

        try
        {
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={0:0.####}&lon={1:0.####}", lat, lon);
            string json = await Http.GetStringAsync(url);
            Current = Parse(json, s.WeatherCity ?? "");
        }
        catch (Exception ex)
        {
            App.LogError(ex); // нет сети — оставляем прошлые данные
        }
        Changed?.Invoke();
    }

    private static WeatherData Parse(string json, string city)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement series = doc.RootElement.GetProperty("properties").GetProperty("timeseries");

        var points = new List<(DateTime time, double temp, double wind, double humidity, string? sym1, string? sym6, double precip)>();
        foreach (JsonElement e in series.EnumerateArray())
        {
            DateTime time = DateTime.Parse(e.GetProperty("time").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime();
            JsonElement data = e.GetProperty("data");
            JsonElement details = data.GetProperty("instant").GetProperty("details");
            points.Add((
                time,
                Num(details, "air_temperature"),
                Num(details, "wind_speed"),
                Num(details, "relative_humidity"),
                Symbol(data, "next_1_hours"),
                Symbol(data, "next_6_hours") ?? Symbol(data, "next_12_hours"),
                data.TryGetProperty("next_1_hours", out JsonElement n1) && n1.TryGetProperty("details", out JsonElement d1)
                    ? Num(d1, "precipitation_amount") : 0));
        }

        DateTime now = DateTime.Now;
        var cur = points.OrderBy(p => Math.Abs((p.time - now).TotalMinutes)).First();

        var hours = points
            .Where(p => p.time >= now.AddMinutes(-30) && p.sym1 != null)
            .Take(12)
            .Select(p => new HourForecast(p.time, p.temp, p.sym1!))
            .ToList();

        var days = points
            .GroupBy(p => p.time.Date)
            .Where(g => g.Key >= DateTime.Today)
            .Take(7)
            .Select(g =>
            {
                // значок дня — около полудня
                var noon = g.OrderBy(p => Math.Abs(p.time.Hour - 13)).First();
                string sym = noon.sym6 ?? noon.sym1 ?? "cloudy";
                return new DayForecast(g.Key, g.Max(p => p.temp), g.Min(p => p.temp), sym.Replace("_night", "_day"));
            })
            .ToList();

        return new WeatherData
        {
            City = city,
            Temp = cur.temp,
            Feels = FeelsLike(cur.temp, cur.wind),
            Symbol = cur.sym1 ?? cur.sym6 ?? "cloudy",
            Wind = cur.wind,
            Humidity = cur.humidity,
            Precipitation = cur.precip,
            Hours = hours,
            Days = days,
            Updated = DateTime.Now,
        };
    }

    /// <summary>Ощущаемая температура с учётом ветра (формула ветро-холодового индекса).</summary>
    private static double FeelsLike(double t, double windMs)
    {
        double v = windMs * 3.6;
        if (t > 10 || v < 4.8) return t;
        return 13.12 + 0.6215 * t - 11.37 * Math.Pow(v, 0.16) + 0.3965 * t * Math.Pow(v, 0.16);
    }

    private static string? Symbol(JsonElement data, string block) =>
        data.TryGetProperty(block, out JsonElement b) && b.TryGetProperty("summary", out JsonElement s)
        && s.TryGetProperty("symbol_code", out JsonElement c) ? c.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>Поиск города по названию (на русском или любом другом языке).</summary>
    public static async Task<List<CityResult>> SearchAsync(string query)
    {
        var list = new List<CityResult>();
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=6&language=ru&format=json&name="
                     + Uri.EscapeDataString(query.Trim());
        string json = await Http.GetStringAsync(url);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out JsonElement results)) return list;

        foreach (JsonElement r in results.EnumerateArray())
        {
            list.Add(new CityResult(
                Str(r, "name"), Str(r, "admin1"), Str(r, "country"),
                r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Коды погоды MET Norway (symbol_code) → описание.</summary>
public static class WeatherSymbols
{
    public static string Base(string symbol) => symbol.Split('_')[0];
    public static bool IsNight(string symbol) => symbol.EndsWith("_night", StringComparison.Ordinal);

    public static string Text(string symbol)
    {
        string b = Base(symbol);
        bool thunder = b.Contains("thunder");
        string core = b.Replace("andthunder", "");
        string text = core switch
        {
            "clearsky" => "Ясно",
            "fair" => "Преимущественно ясно",
            "partlycloudy" => "Переменная облачность",
            "cloudy" => "Пасмурно",
            "fog" => "Туман",
            "lightrain" => "Небольшой дождь",
            "rain" => "Дождь",
            "heavyrain" => "Сильный дождь",
            "lightrainshowers" => "Небольшой ливень",
            "rainshowers" => "Ливень",
            "heavyrainshowers" => "Сильный ливень",
            "lightsleet" or "lightsleetshowers" => "Небольшой мокрый снег",
            "sleet" or "sleetshowers" => "Мокрый снег",
            "heavysleet" or "heavysleetshowers" => "Сильный мокрый снег",
            "lightsnow" or "lightsnowshowers" => "Небольшой снег",
            "snow" or "snowshowers" => "Снег",
            "heavysnow" or "heavysnowshowers" => "Сильный снег",
            _ => "—",
        };
        return thunder ? text + " с грозой" : text;
    }
}
