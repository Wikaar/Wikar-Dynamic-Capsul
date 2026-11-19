using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WikarIsland.Services;

/// <summary>
/// Голосом по Steam: поиск в магазине с фильтрами («бесплатные стратегии в стиме», «игры со скидкой до 10 евро»),
/// переключение разделов («открой библиотеку стима», «стим друзья») и поиск по своей библиотеке
/// («есть ли у меня в стиме ведьмак», «найди в библиотеке стима сталкер»).
/// Всё открывается в клиенте Steam через steam://, если его нет — в браузере.
/// </summary>
public static class SteamVoice
{
    private const string Steam = @"(?:стим\w*|steam)";

    private static readonly (string pattern, string uri, string name)[] Sections =
    {
        (@"библиотек\w*|мои игры|игры", "steam://nav/games", "библиотеку"),
        (@"магазин\w*|стор\w*|store", "steam://store", "магазин"),
        (@"загрузк\w*|скачивани\w*|закачк\w*", "steam://open/downloads", "загрузки"),
        (@"друз\w*|друг\w*|чат\w*", "steam://open/friends", "друзей"),
        (@"профил\w*", "steam://url/SteamIDMyProfile", "профиль"),
        (@"сообществ\w*|комьюнити", "steam://url/CommunityHome", "сообщество"),
        (@"(?:список\s+)?желаем\w*|вишлист\w*|wishlist", "steam://openurl/https://store.steampowered.com/wishlist/", "список желаемого"),
        (@"инвентар\w*", "steam://openurl/https://steamcommunity.com/my/inventory", "инвентарь"),
        (@"настройк\w*|параметр\w*", "steam://open/settings", "настройки"),
        (@"скриншот\w*|снимк\w*", "steam://open/screenshots", "скриншоты"),
        (@"новост\w*", "steam://openurl/https://store.steampowered.com/news/", "новости"),
        (@"скидк\w*|распродаж\w*|акци\w*", "steam://openurl/https://store.steampowered.com/specials/", "скидки"),
        (@"очк\w*|магазин очков|points", "steam://openurl/https://store.steampowered.com/points/shop/", "магазин очков"),
        (@"корзин\w*", "steam://openurl/https://store.steampowered.com/cart/", "корзину"),
    };

    /// <summary>Ответ для голоса или null, если это не команда про Steam.</summary>
    public static string? TryHandle(string text)
    {
        string t = RuNumbers.Digitize(text.ToLowerInvariant().Replace('ё', 'е')).Trim().TrimEnd('.', '!', '?');
        bool steamWord = Regex.IsMatch(t, Steam);
        Match m;

        // --- своя библиотека: «есть ли у меня в стиме сталкер», «найди в библиотеке (стима) ведьмак» ---
        if ((m = Regex.Match(t, @"^(?:есть ли у меня|у меня есть|есть у меня)\s+(?:в\s+" + Steam + @"\s+)?(?:игра\s+)?(.+?)(?:\s+в\s+" + Steam + @")?$")).Success && (steamWord || t.Contains("игр"))
            || (m = Regex.Match(t, @"^(?:найди|поищи|покажи|открой)\s+(?:в\s+)?(?:моей\s+)?библиотек\w*\s+(?:" + Steam + @"\s+)?(.+)$")).Success
            || (m = Regex.Match(t, @"^(?:найди|поищи)\s+(?:у меня\s+)?в\s+" + Steam + @"\s+(?:в\s+)?библиотек\w*\s+(.+)$")).Success)
            return SearchLibrary(m.Groups[1].Value);

        if (!steamWord && !Regex.IsMatch(t, @"(бесплатн\w+|со скидк\w+|по скидк\w+)\s+игр")) return null;

        // --- разделы клиента: «открой библиотеку стима», «стим друзья», «перейди в магазин стим» ---
        if ((m = Regex.Match(t, @"^(?:открой|покажи|перейди\s+(?:в|на)|переключи\s+на|зайди\s+в|включи)?\s*(?:в\s+)?(?:" + Steam + @"\s+)?(.+?)(?:\s+(?:в\s+)?" + Steam + @")?$")).Success)
        {
            string what = m.Groups[1].Value.Trim();
            foreach ((string pattern, string uri, string name) in Sections)
            {
                if (!Regex.IsMatch(what, @"^(?:" + pattern + @")$")) continue;
                Open(uri);
                return $"Открываю {name} Steam, сэр.";
            }
        }

        // --- магазин: «найди в стиме …», «бесплатные игры в стиме», «игры со скидкой до 10 евро» ---
        bool search = Regex.IsMatch(t, @"^(?:найди|поищи|покажи|открой|ищи|подбери|какие)\b") || Regex.IsMatch(t, @"(бесплатн|скидк|до \d|дешевле)");
        if (!search) return null;

        string q = t;
        bool free = Regex.IsMatch(q, @"бесплатн\w*|free to play|фри ту плей");
        bool sale = Regex.IsMatch(q, @"(?:со|по|на|с)\s+скидк\w*|скидочн\w*|акционн\w*|по акции|распродаж\w*");
        int? max = null, min = null;
        if ((m = Regex.Match(q, @"от\s+(\d+)\s*(?:евро|€|eur|долл\w*|\$|руб\w*|₽)?\s+до\s+(\d+)")).Success)
        {
            min = int.Parse(m.Groups[1].Value);
            max = int.Parse(m.Groups[2].Value);
        }
        else if ((m = Regex.Match(q, @"(?:до|дешевле|не дороже|меньше)\s+(\d+)")).Success)
            max = int.Parse(m.Groups[1].Value);
        else if ((m = Regex.Match(q, @"(?:от|дороже|больше)\s+(\d+)")).Success)
            min = int.Parse(m.Groups[1].Value);

        // что искать: убираем служебные слова
        string term = q;
        term = Regex.Replace(term, @"^(?:найди|поищи|покажи|открой|ищи|подбери|какие)\s+(?:мне\s+)?", "");
        term = Regex.Replace(term, @"(?:в|на|по)\s+" + Steam + @"|" + Steam, " ");
        term = Regex.Replace(term, @"(?:в\s+)?магазин\w*", " ");
        term = Regex.Replace(term, @"от\s+\d+\s*\S*\s+до\s+\d+|(?:до|дешевле|не дороже|меньше|от|дороже|больше)\s+\d+", " ");
        term = Regex.Replace(term, @"\b(?:евро|€|eur|долл\w*|руб\w*|₽|бакс\w*)\b", " ");
        term = Regex.Replace(term, @"бесплатн\w*|free to play|фри ту плей|(?:со|по|на|с)\s+скидк\w*|скидочн\w*|акционн\w*|по акции|распродаж\w*", " ");
        term = Regex.Replace(term, @"\b(?:игр\w*|есть|там|сейчас|какие нибудь|какие-нибудь|что нибудь|что-нибудь|про|жанра|типа|в жанре|с|за)\b", " ");
        term = Regex.Replace(term, @"\s+", " ").Trim();

        var parts = new List<string>();
        if (term.Length > 0) parts.Add("term=" + Uri.EscapeDataString(term));
        if (free) parts.Add("maxprice=free");
        else if (max != null) parts.Add("maxprice=" + max.Value.ToString(CultureInfo.InvariantCulture));
        if (sale) parts.Add("specials=1");
        // нижней границы цены у поиска Steam нет: при «от … до …» показываем от дорогих к дешёвым, при «от …» — тоже
        if (min != null) parts.Add("sort_by=Price_DESC");
        else if (max != null && !free) parts.Add("sort_by=Price_ASC");
        if (free || sale) parts.Add("category1=998"); // только игры, без DLC и саундтреков
        if (parts.Count == 0) return null;

        string url = "https://store.steampowered.com/search/?" + string.Join("&", parts);
        Open("steam://openurl/" + url);

        var said = new List<string>();
        if (free) said.Add("бесплатные");
        if (sale) said.Add("со скидкой");
        string what2 = term.Length > 0 ? $"«{term}»" : "игры";
        string price = free ? "" : min != null && max != null ? $" от {min} до {max}" : max != null ? $" до {max}" : min != null ? $" от {min}" : "";
        string note = min != null ? " Нижней границы цены у Steam нет, поэтому сортирую от дорогих к дешёвым." : "";
        return $"Ищу в Steam {(said.Count > 0 ? string.Join(" ", said) + " " : "")}{what2}{price}, сэр.{note}";
    }

    private static string SearchLibrary(string query)
    {
        string q = Regex.Replace(query, @"\b(?:игр\w*|в\s+" + Steam + @"|" + Steam + @"|библиотек\w*)\b", " ");
        q = Regex.Replace(q, @"\s+", " ").Trim();
        if (q.Length == 0) return "Какую игру поискать в библиотеке, сэр?";
        if (App.Steam?.Installed != true) return "Steam на этом компьютере не найден, сэр.";

        var games = App.Steam.InstalledGames().ToList();
        string nq = Norm(q);
        var found = games.Where(g => Norm(g.name).Contains(nq)).ToList();
        if (found.Count == 0)
        {
            string[] words = nq.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            found = games.Where(g => words.All(w => Norm(g.name).Contains(w.Length > 4 ? w[..^1] : w))).ToList();
        }
        if (found.Count == 0)
        {
            // английское название по-русски: «ведьмак» → witcher и т. п. — откроем поиск в библиотеке
            Open("steam://nav/games");
            return $"Среди установленных игр «{q}» не нашёл, сэр. Открыл библиотеку Steam — там видны и неустановленные.";
        }
        if (found.Count == 1)
        {
            Open($"steam://nav/games/details/{found[0].id}");
            return $"Есть: {found[0].name}, установлена. Открыл её в библиотеке Steam. Скажите «запусти {found[0].name}», чтобы играть.";
        }
        Open($"steam://nav/games/details/{found[0].id}");
        return $"Нашёл {found.Count}: {string.Join(", ", found.Take(5).Select(g => g.name))}. Открыл первую.";
    }

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd} ]", " ").Replace("  ", " ").Trim();

    private static void Open(string uri)
    {
        bool steam = App.Steam?.Installed == true;
        string target = !steam && uri.StartsWith("steam://openurl/") ? uri["steam://openurl/".Length..] : uri;
        if (!steam && target.StartsWith("steam://")) target = "https://store.steampowered.com/";
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
