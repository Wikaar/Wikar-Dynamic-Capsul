using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WikarIsland.Services;

public sealed class ClipItem
{
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
}

/// <summary>
/// История буфера обмена. Текст и картинки живут только в памяти;
/// на диск сохраняются лишь закреплённые текстовые записи.
/// Записи, которые менеджеры паролей помечают как «не сохранять», пропускаем.
/// </summary>
public sealed class ClipboardService
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxItems = 40;
    private const byte VK_CONTROL = 0x11, VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x2;

    private static string PinnedPath => Path.Combine(AppSettings.DataDir, "clipboard-pinned.json");

    public List<ClipItem> Items { get; } = new();
    public event Action? Changed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public void Attach(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook(Hook);
        AddClipboardFormatListener(handle);
        LoadPinned();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) _ = CaptureAsync();
        return IntPtr.Zero;
    }

    private async Task CaptureAsync()
    {
        await Task.Delay(80); // даём программе-источнику дописать буфер
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") ||
                    Clipboard.ContainsData("Clipboard Viewer Ignore"))
                    return;

                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text)) AddText(text);
                }
                else if (Clipboard.ContainsImage())
                {
                    BitmapSource? image = Clipboard.GetImage();
                    if (image != null)
                    {
                        image.Freeze();
                        Insert(new ClipItem { Image = image });
                    }
                }
                return;
            }
            catch (COMException)
            {
                await Task.Delay(100); // буфер занят другой программой
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return;
            }
        }
    }

    private void AddText(string text)
    {
        ClipItem? same = Items.FirstOrDefault(i => i.Text == text);
        if (same != null)
        {
            Items.Remove(same);
            same.Time = DateTime.Now;
            Items.Insert(0, same);
            Changed?.Invoke();
            return;
        }
        Insert(new ClipItem { Text = text });
    }

    private void Insert(ClipItem item)
    {
        Items.Insert(0, item);
        while (Items.Count(i => !i.Pinned) > MaxItems)
            Items.Remove(Items.Last(i => !i.Pinned));
        Changed?.Invoke();
    }

    public void Remove(ClipItem item)
    {
        Items.Remove(item);
        if (item.Pinned) SavePinned();
        Changed?.Invoke();
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        SavePinned();
        Changed?.Invoke();
    }

    public void ClearUnpinned()
    {
        Items.RemoveAll(i => !i.Pinned);
        Changed?.Invoke();
    }

    /// <summary>Положить запись в буфер, вернуть фокус окну, где печатали, и нажать Ctrl+V.</summary>
    public async Task PasteAsync(ClipItem item, IntPtr target)
    {
        try
        {
            if (item.Text != null) Clipboard.SetText(item.Text);
            else if (item.Image != null) Clipboard.SetImage(item.Image);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return;
        }

        if (target != IntPtr.Zero) NativeMethods.SetForegroundWindow(target);
        await Task.Delay(140);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private void LoadPinned()
    {
        try
        {
            if (!File.Exists(PinnedPath)) return;
            List<string>? pinned = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PinnedPath));
            if (pinned == null) return;
            foreach (string text in pinned)
                Items.Add(new ClipItem { Text = text, Pinned = true });
            Changed?.Invoke();
        }
        catch
        {
            // файл испорчен — без закреплённых
        }
    }

    private void SavePinned()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            List<string> pinned = Items.Where(i => i.Pinned && i.Text != null).Select(i => i.Text!).ToList();
            File.WriteAllText(PinnedPath, JsonSerializer.Serialize(pinned));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
