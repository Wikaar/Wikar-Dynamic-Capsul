using System;
using System.Runtime.InteropServices;

namespace WikarIsland.Services;

/// <summary>
/// Очистка корзины по команде «Капсула, очисти корзину». Капсула сначала переспрашивает голосом,
/// а после «да» сама подтверждает системный вопрос «Удалить все объекты безвозвратно?» — окно не всплывает.
/// </summary>
public static class RecycleBin
{
    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>Сколько лежит в корзине на всех дисках.</summary>
    public static (long items, long bytes) Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (-1, 0);
    }

    /// <summary>Вопрос перед очисткой («Очистить корзину, сэр? Там 12 файлов, 1,4 ГБ.») или null, если корзина пуста.</summary>
    public static string? Question(out string emptyReply)
    {
        emptyReply = "Корзина уже пуста, сэр.";
        (long items, long bytes) = Query();
        if (items == 0) return null;
        return items < 0 ? "Очистить корзину, сэр? Восстановить файлы будет нельзя."
            : $"Очистить корзину, сэр? Там {Files(items)}, {Size(bytes)}. Восстановить их будет нельзя.";
    }

    /// <summary>Очистить корзину на всех дисках. Возвращает фразу для ответа (со «сэр» — заменится на обращение пользователя).</summary>
    public static string Empty()
    {
        (long items, long bytes) = Query();
        if (items == 0) return "Корзина уже пуста, сэр.";

        // NOCONFIRMATION — системное «Вы действительно хотите…» подтверждаем сами; звук очистки оставляем
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI);
        if (hr != 0 && Query().items != 0)
            return "Не получилось очистить корзину, сэр: какой-то файл занят другой программой.";

        if (items < 0) return "Корзина очищена, сэр.";
        return $"Корзина очищена, сэр: {Files(items)}, {Size(bytes)}.";
    }

    private static string Files(long n)
    {
        long d = n % 100, u = n % 10;
        string word = d is >= 11 and <= 14 ? "файлов" : u == 1 ? "файл" : u is >= 2 and <= 4 ? "файла" : "файлов";
        return $"{n} {word}";
    }

    private static string Size(long bytes)
    {
        double mb = bytes / 1048576.0;
        if (mb < 1) return "меньше мегабайта";
        if (mb < 1024) return $"{Math.Round(mb)} МБ";
        return $"{mb / 1024:0.#} ГБ";
    }
}
