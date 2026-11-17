using System.Text.RegularExpressions;

namespace WikarIsland.Services;

/// <summary>
/// Как Капсулка обращается к пользователю: «сэр», «мэм», «мистер Илья», «мисс Олеся», «госпожа Олеся», просто по имени
/// или своё обращение. Все готовые фразы написаны с «сэр» — при озвучке и показе оно заменяется на выбранное.
/// </summary>
public static class UserAddress
{
    private static readonly Regex Sir = new(@"(?<![\p{L}])([Сс])эр(?![\p{L}])", RegexOptions.Compiled);

    /// <summary>Обращение по настройкам. Без настроек — «сэр», как раньше.</summary>
    public static string Word => Build(App.Settings?.UserName ?? "", App.Settings?.UserGender ?? "",
        App.Settings?.AddressStyle ?? "sir", App.Settings?.AddressCustom ?? "");

    public static string Build(string name, string gender, string style, string custom)
    {
        name = name.Trim();
        bool female = gender == "f";
        bool known = gender is "m" or "f";
        string sirWord = female ? "мэм" : "сэр";
        switch (style)
        {
            case "custom" when custom.Trim().Length > 0:
                return custom.Trim();
            case "name":
                return name.Length > 0 ? name : sirWord;
            case "title":
                if (name.Length == 0) return sirWord;
                if (!known) return name;
                return (female ? "мисс " : "мистер ") + name;
            case "gos":
                if (!known) return name.Length > 0 ? name : sirWord;
                string g = female ? "госпожа" : "господин";
                return name.Length > 0 ? g + " " + name : g;
            default:
                // пол не указан, но есть имя — нейтрально, по имени
                if (!known && name.Length > 0) return name;
                return sirWord;
        }
    }

    /// <summary>Заменить «сэр» в готовой фразе на выбранное обращение.</summary>
    public static string Apply(string text) => Apply(text, Word);

    public static string Apply(string text, string word)
    {
        if (string.IsNullOrEmpty(text) || word == "сэр") return text;
        return Sir.Replace(text, m => m.Groups[1].Value == "С" ? Capitalize(word) : word);
    }

    /// <summary>Строка для системной подсказки ИИ: как обращаться и в каком роде говорить о пользователе.</summary>
    public static string PromptRule()
    {
        string word = Word;
        string name = (App.Settings?.UserName ?? "").Trim();
        string gender = App.Settings?.UserGender ?? "";
        string rule = word == "сэр" ? "" : $"Обращаешься к пользователю «{word}» (вместо «сэр» везде говори «{word}»). ";
        if (name.Length > 0) rule += $"Пользователя зовут {name}. ";
        if (gender == "m") rule += "Пользователь — мужчина: говоря о нём, используй мужской род. ";
        else if (gender == "f") rule += "Пользователь — женщина: говоря о ней, используй женский род. ";
        return rule;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
