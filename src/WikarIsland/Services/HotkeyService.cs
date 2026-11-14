using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WikarIsland.Services;

/// <summary>Глобальная горячая клавиша через RegisterHotKey.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB001;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(Hook);
    }

    public bool Register(string text, out string error)
    {
        Unregister();
        if (!TryParse(text, out uint mods, out uint vk, out error))
            return false;

        if (!NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, mods | MOD_NOREPEAT, vk))
        {
            error = "Это сочетание уже занято другой программой";
            return false;
        }

        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public static bool TryParse(string text, out uint mods, out uint vk, out string error)
    {
        mods = 0;
        vk = 0;
        error = "";
        Key? key = null;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= MOD_CONTROL;
                    break;
                case "alt":
                    mods |= MOD_ALT;
                    break;
                case "shift":
                    mods |= MOD_SHIFT;
                    break;
                case "win":
                    mods |= MOD_WIN;
                    break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0]))
                        key = Key.D0 + (raw[0] - '0');
                    else if (!int.TryParse(raw, out _) && Enum.TryParse(raw, ignoreCase: true, out Key parsed))
                        key = parsed;
                    else
                    {
                        error = $"Не знаю клавишу «{raw}»";
                        return false;
                    }
                    break;
            }
        }

        if (key is null)
        {
            error = "Нужна основная клавиша, например Space или I";
            return false;
        }
        if (mods == 0)
        {
            error = "Нужен хотя бы один модификатор: Ctrl, Alt, Shift или Win";
            return false;
        }

        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return true;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(Hook);
    }
}
