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

public sealed record HourForecast(DateTime Time, double Temp, int Code);

public sealed record DayForecast(DateTime Date, double Max, double Min, int Code);

public sealed class WeatherData
{
    public string City { get; init; } = "";
    public double Temp { get; init; }
    public double Feels { get; init; }
    public int Code { get; init; }
    public bool IsDay { get; init; } = true;
    public double Wind { get; init; }
    public double Precipitation { get; init; }
    public List<HourForecast> Hours { get; init; } = new();
    public List<DayForecast> Days { get; init; } = new();
    public DateTime Updated { get; init; }

    public string Icon => WeatherCodes.Icon(Code, IsDay);
    public string Text => WeatherCodes.Text(Code);
    public string Short => $"{Icon} {Math.Round(Temp):0}°";
}

/// <summary>Погода из Open-Meteo: бесплатно, без ключа и регистрации.</summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(20) };

    public WeatherData? Current { get; private set; }
    public event Action? Changed;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WikarIsland/0.2");
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
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,precipitation,is_day" +
                "&hourly=temperature_2m,weather_code" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                "&wind_speed_unit=ms&timezone=auto&forecast_days=6", lat, lon);

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
        JsonElement root = doc.RootElement;
        JsonElement cur = root.GetProperty("current");

        var hours = new List<HourForecast>();
        JsonElement hourly = root.GetProperty("hourly");
        JsonElement hTime = hourly.GetProperty("time");
        JsonElement hTemp = hourly.GetProperty("temperature_2m");
        JsonElement hCode = hourly.GetProperty("weather_code");
        DateTime fromHour = DateTime.Parse(cur.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
        fromHour = new DateTime(fromHour.Year, fromHour.Month, fromHour.Day, fromHour.Hour, 0, 0);
        for (int i = 0; i < hTime.GetArrayLength() && hours.Count < 12; i++)
        {
            DateTime t = DateTime.Parse(hTime[i].GetString()!, CultureInfo.InvariantCulture);
            if (t < fromHour) continue;
            hours.Add(new HourForecast(t, Num(hTemp[i]), (int)Num(hCode[i])));
        }

        var days = new List<DayForecast>();
        JsonElement daily = root.GetProperty("daily");
        JsonElement dTime = daily.GetProperty("time");
        for (int i = 0; i < dTime.GetArrayLength(); i++)
        {
            days.Add(new DayForecast(
                DateTime.Parse(dTime[i].GetString()!, CultureInfo.InvariantCulture),
                Num(daily.GetProperty("temperature_2m_max")[i]),
                Num(daily.GetProperty("temperature_2m_min")[i]),
                (int)Num(daily.GetProperty("weather_code")[i])));
        }

        return new WeatherData
        {
            City = city,
            Temp = Num(cur.GetProperty("temperature_2m")),
            Feels = Num(cur.GetProperty("apparent_temperature")),
            Code = (int)Num(cur.GetProperty("weather_code")),
            IsDay = Num(cur.GetProperty("is_day")) > 0,
            Wind = Num(cur.GetProperty("wind_speed_10m")),
            Precipitation = Num(cur.GetProperty("precipitation")),
            Hours = hours,
            Days = days,
            Updated = DateTime.Now,
        };
    }

    private static double Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

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

/// <summary>Коды погоды WMO → значок и описание.</summary>
public static class WeatherCodes
{
    public static string Icon(int code, bool isDay = true) => code switch
    {
        0 => isDay ? "☀" : "☾",
        1 or 2 => isDay ? "⛅" : "☁",
        3 => "☁",
        45 or 48 => "🌫",
        >= 51 and <= 57 => "🌦",
        >= 61 and <= 67 or >= 80 and <= 82 => "🌧",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⛈",
        _ => "☁",
    };

    public static string Text(int code) => code switch
    {
        0 => "Ясно",
        1 => "Преимущественно ясно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 or 48 => "Туман",
        >= 51 and <= 55 => "Морось",
        56 or 57 => "Ледяная морось",
        61 or 63 => "Дождь",
        65 => "Сильный дождь",
        66 or 67 => "Ледяной дождь",
        71 or 73 => "Снег",
        75 => "Сильный снег",
        77 => "Снежные зёрна",
        80 or 81 => "Ливень",
        82 => "Сильный ливень",
        85 or 86 => "Снегопад",
        95 => "Гроза",
        96 or 99 => "Гроза с градом",
        _ => "—",
    };
}
