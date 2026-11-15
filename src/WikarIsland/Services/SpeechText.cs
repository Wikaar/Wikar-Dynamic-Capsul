using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WikarIsland.Services;

/// <summary>
/// Подготовка текста к озвучке: без символов, которые голос читает вслух, и с английскими словами,
/// записанными так, как их произносят по-русски (русский голос читает латиницу по буквам или с сильным акцентом).
/// </summary>
public static partial class SpeechText
{
    /// <summary>Частые слова — точное произношение. Остальное транслитерируется по правилам.</summary>
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discord"] = "дискорд", ["steam"] = "стим", ["youtube"] = "ютуб", ["telegram"] = "телеграм", ["google"] = "гугл",
        ["chrome"] = "хром", ["windows"] = "виндоус", ["microsoft"] = "майкрософт", ["spotify"] = "спотифай",
        ["valheim"] = "вальхейм", ["minecraft"] = "майнкрафт", ["counter"] = "каунтер", ["strike"] = "страйк",
        ["epic"] = "эпик", ["games"] = "геймс", ["game"] = "гейм", ["launcher"] = "лаунчер", ["store"] = "стор",
        ["whatsapp"] = "вотсап", ["twitch"] = "твич", ["github"] = "гитхаб", ["netflix"] = "нетфликс", ["zoom"] = "зум",
        ["teams"] = "тимс", ["word"] = "ворд", ["excel"] = "эксель", ["powerpoint"] = "пауэрпоинт", ["photoshop"] = "фотошоп",
        ["office"] = "офис", ["edge"] = "эдж", ["firefox"] = "файрфокс", ["opera"] = "опера", ["yandex"] = "яндекс",
        ["obs"] = "о би эс", ["studio"] = "студио", ["visual"] = "вижуал", ["code"] = "код", ["notepad"] = "нотпад",
        ["explorer"] = "эксплорер", ["settings"] = "сеттингс", ["xbox"] = "иксбокс", ["playstation"] = "плейстейшн",
        ["ark"] = "арк", ["survival"] = "сёрвайвал", ["evolved"] = "эволвд", ["cyberpunk"] = "киберпанк",
        ["witcher"] = "ведьмак", ["dota"] = "дота", ["warface"] = "варфейс", ["world"] = "ворлд", ["of"] = "оф",
        ["tanks"] = "танкс", ["the"] = "зе", ["and"] = "энд", ["online"] = "онлайн", ["wi-fi"] = "вай-фай", ["wifi"] = "вай-фай",
        ["bluetooth"] = "блютуз", ["email"] = "имейл", ["mail"] = "мейл", ["ok"] = "окей", ["okay"] = "окей",
        ["jarvis"] = "джарвис", ["iron"] = "айрон", ["man"] = "мэн", ["stark"] = "старк", ["tony"] = "тони",
        ["groq"] = "грок", ["gemini"] = "джемини", ["chatgpt"] = "чат джи пи ти", ["openai"] = "оупен эй ай", ["claude"] = "клод",
        ["whisper"] = "виспер", ["zapret"] = "запрет", ["wikar"] = "викар", ["island"] = "айленд", ["nvidia"] = "энвидиа",
        ["geforce"] = "джифорс", ["radeon"] = "радеон", ["intel"] = "интел", ["amd"] = "эй эм ди", ["ryzen"] = "райзен",
        ["iphone"] = "айфон", ["android"] = "андроид", ["apple"] = "эпл", ["amazon"] = "амазон", ["reddit"] = "реддит",
        ["wikipedia"] = "википедия", ["deepl"] = "дипл", ["deepseek"] = "дипсик", ["ollama"] = "оллама",
    };

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['A'] = "эй", ['B'] = "би", ['C'] = "си", ['D'] = "ди", ['E'] = "и", ['F'] = "эф", ['G'] = "джи", ['H'] = "эйч",
        ['I'] = "ай", ['J'] = "джей", ['K'] = "кей", ['L'] = "эл", ['M'] = "эм", ['N'] = "эн", ['O'] = "оу", ['P'] = "пи",
        ['Q'] = "кью", ['R'] = "ар", ['S'] = "эс", ['T'] = "ти", ['U'] = "ю", ['V'] = "ви", ['W'] = "дабл ю", ['X'] = "экс",
        ['Y'] = "уай", ['Z'] = "зед",
    };

    /// <summary>Убрать то, что голос произносит как символы, и превратить знаки в слова или паузы.</summary>
    public static string Clean(string text)
    {
        string s = Regex.Replace(text, @"https?://\S+", "ссылка");
        s = Regex.Replace(s, @"```.*?```", " ", RegexOptions.Singleline); // код не читаем
        s = Regex.Replace(s, @"[\p{So}\p{Cs}]", "");                      // эмодзи
        s = Regex.Replace(s, @"(\d)\s*°\s*[CС]\b", "$1 градусов");
        s = s.Replace("°", " градусов").Replace("№", "номер ").Replace("&", " и ").Replace("+", " плюс ")
             .Replace("=", " равно ").Replace("%", " процентов");
        s = Regex.Replace(s, @"(\d)\s*-\s*(\d)", "$1 до $2");               // 5-10 → 5 до 10
        s = Regex.Replace(s, @"\s+[—–-]+\s+", ", ");                        // тире — пауза
        s = Regex.Replace(s, @"\.{2,}|…", ".");
        s = Regex.Replace(s, @"[*_`#>|~^\[\]{}()<>""'«»„“”‘’/\\@$]", " ");    // разметка, кавычки, скобки, слэши
        s = Regex.Replace(s, @"(?m)^\s*[-•]\s*", "");                       // маркеры списков
        s = Regex.Replace(s, @"\s+([,.!?;:])", "$1");
        s = Regex.Replace(s, @"([,.!?;:]){2,}", "$1");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        foreach (char c in s.ToCharArray())
            if (c < 32) s = s.Replace(c, ' ');
        return s.Length > 1500 ? s[..1500] : s;
    }

    /// <summary>Латиница → как её произносят по-русски (для русских голосов).</summary>
    public static string EnglishForRussianVoice(string text) =>
        LatinWord().Replace(text, m =>
        {
            string w = m.Value;
            if (Words.TryGetValue(w, out string? known)) return known;
            // аббревиатуры: VPN, GPU, FPS → по буквам
            if (w.Length is >= 2 and <= 5 && w.ToUpperInvariant() == w && !w.Contains('-'))
            {
                var parts = new List<string>();
                foreach (char c in w)
                    parts.Add(Letters.TryGetValue(c, out string? l) ? l : c.ToString());
                return string.Join(" ", parts);
            }
            // составное через дефис: Counter-Strike
            if (w.Contains('-'))
            {
                var parts = new List<string>();
                foreach (string p in w.Split('-', StringSplitOptions.RemoveEmptyEntries))
                    parts.Add(Words.TryGetValue(p, out string? kp) ? kp : Transliterate(p));
                return string.Join(" ", parts);
            }
            return Transliterate(w);
        });

    [GeneratedRegex(@"[A-Za-z]+(?:-[A-Za-z]+)*")]
    private static partial Regex LatinWord();

    /// <summary>Правила английского чтения в упрощённом виде: лучше, чем русский голос читает латиницу сам.</summary>
    private static string Transliterate(string word)
    {
        string w = word.ToLowerInvariant();
        // немое e на конце: game, code, time
        if (w.Length > 3 && w.EndsWith('e') && !w.EndsWith("ee") && !w.EndsWith("ie")) w = w[..^1] + "\u0001";
        var rules = new (string from, string to)[]
        {
            ("tion", "шн"), ("sion", "жн"), ("igh", "ай"), ("ough", "оу"), ("augh", "о"), ("ture", "чер"),
            ("sch", "ск"), ("tch", "ч"), ("th", "з"), ("sh", "ш"), ("ch", "ч"), ("ph", "ф"), ("wh", "у"), ("ck", "к"),
            ("qu", "кв"), ("kn", "н"), ("wr", "р"), ("ng", "нг"), ("oo", "у"), ("ee", "и"), ("ea", "и"), ("ie", "и"),
            ("ai", "эй"), ("ay", "эй"), ("ey", "эй"), ("ei", "эй"), ("oa", "оу"), ("ow", "оу"), ("ou", "ау"), ("oi", "ой"),
            ("oy", "ой"), ("au", "о"), ("aw", "о"), ("ew", "ью"), ("er", "ер"), ("ar", "ар"), ("or", "ор"), ("ir", "ёр"),
            ("ur", "ёр"), ("ce", "се"), ("ci", "си"), ("cy", "си"), ("ge", "дже"), ("gi", "джи"), ("x", "кс"),
        };
        var sb = new StringBuilder();
        int i = 0;
        while (i < w.Length)
        {
            if (w[i] == '\u0001')
            {
                i++;
                continue;
            }
            bool done = false;
            foreach ((string from, string to) in rules)
            {
                if (string.CompareOrdinal(w, i, from, 0, from.Length) != 0) continue;
                sb.Append(to);
                i += from.Length;
                done = true;
                break;
            }
            if (done) continue;
            char c = w[i];
            bool first = i == 0;
            bool beforeSilentE = i + 1 < w.Length && w[i + 1] != '\u0001' && i + 2 < w.Length && w[i + 2] == '\u0001';
            sb.Append(c switch
            {
                // гласная перед «немым e» читается по имени буквы: game → гейм, time → тайм
                'a' => beforeSilentE ? "эй" : first ? "э" : "а",
                'i' => beforeSilentE ? "ай" : "и",
                'o' => beforeSilentE ? "оу" : "о",
                'u' => beforeSilentE ? "ю" : "а",
                'e' => first ? "э" : "е",
                'y' => first ? "й" : i == w.Length - 1 ? "и" : "ай",
                'b' => "б", 'c' => "к", 'd' => "д", 'f' => "ф", 'g' => "г", 'h' => "х", 'j' => "дж", 'k' => "к", 'l' => "л",
                'm' => "м", 'n' => "н", 'p' => "п", 'q' => "к", 'r' => "р", 's' => "с", 't' => "т", 'v' => "в", 'w' => "у",
                'z' => "з",
                _ => c.ToString(),
            });
            i++;
        }
        return sb.ToString();
    }
}
