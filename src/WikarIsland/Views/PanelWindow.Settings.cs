using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>Настройки: меню разделов слева, поиск по ним и голос Fish Audio.</summary>
public partial class PanelWindow
{
    private readonly Dictionary<string, string> _sectionText = new();

    private IEnumerable<StackPanel> SettingsSections =>
        new[] { SetSec_general, SetSec_profile, SetSec_look, SetSec_voice, SetSec_ai, SetSec_weather, SetSec_notify, SetSec_games, SetSec_focus, SetSec_data };

    private IEnumerable<RadioButton> SettingsNavItems => SettingsNavPanel.Children.OfType<RadioButton>();

    /// <summary>Открыть раздел, который был открыт в прошлый раз.</summary>
    private void RestoreSettingsSection()
    {
        string key = App.Settings.SettingsSection;
        RadioButton nav = SettingsNavItems.FirstOrDefault(r => (string)r.Tag == key) ?? SetNav_general;
        if (nav.IsChecked == true) ShowSettingsSection((string)nav.Tag);
        else nav.IsChecked = true;
    }

    private void SettingsNav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;
        ShowSettingsSection(key);
        if (App.Settings.SettingsSection != key)
        {
            App.Settings.SettingsSection = key;
            App.Settings.Save();
        }
    }

    private void ShowSettingsSection(string key)
    {
        foreach (StackPanel sec in SettingsSections)
            sec.Visibility = (string)sec.Tag == key ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ScrollToTop();
    }

    // ---------- поиск ----------

    private void SettingsSearch_Changed(object sender, TextChangedEventArgs e)
    {
        string q = SettingsSearchBox.Text.Trim();
        SettingsSearchHint.Visibility = SettingsSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RadioButton? firstVisible = null;
        foreach (RadioButton nav in SettingsNavItems)
        {
            bool match = q.Length == 0 || SectionText((string)nav.Tag).Contains(q, StringComparison.CurrentCultureIgnoreCase);
            nav.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) firstVisible ??= nav;
        }
        SettingsSearchEmpty.Visibility = firstVisible == null ? Visibility.Visible : Visibility.Collapsed;
        // открытый раздел скрылся фильтром — показываем первый подходящий
        RadioButton? current = SettingsNavItems.FirstOrDefault(r => r.IsChecked == true);
        if (firstVisible != null && (current == null || current.Visibility != Visibility.Visible))
            firstVisible.IsChecked = true;
    }

    private void SettingsSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SettingsSearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RadioButton? first = SettingsNavItems.FirstOrDefault(r => r.Visibility == Visibility.Visible);
            if (first != null) first.IsChecked = true;
            e.Handled = true;
        }
    }

    /// <summary>Все подписи раздела (заголовки, галочки, кнопки, пояснения) — для поиска.</summary>
    private string SectionText(string key)
    {
        if (_sectionText.TryGetValue(key, out string? cached)) return cached;
        StackPanel? sec = SettingsSections.FirstOrDefault(s => (string)s.Tag == key);
        RadioButton? nav = SettingsNavItems.FirstOrDefault(r => (string)r.Tag == key);
        var parts = new List<string>();
        if (nav != null) Collect(nav, parts);
        if (sec != null) Collect(sec, parts);
        string text = string.Join(" ", parts);
        _sectionText[key] = text;
        return text;
    }

    private static void Collect(object node, List<string> parts)
    {
        switch (node)
        {
            case TextBlock tb:
                parts.Add(tb.Text);
                break;
            case ContentControl { Content: string s }:
                parts.Add(s);
                break;
        }
        if (node is DependencyObject d)
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                Collect(child, parts);
    }

    // ---------- Fish Audio ----------

    private void LoadFishSettings()
    {
        AppSettings s = App.Settings;
        FishKeyBox.Password = s.FishKey;
        FishVoiceBox.Text = s.FishVoiceId;
        FishModelBox.Text = s.FishModel.Length > 0 ? s.FishModel : FishAudioTts.DefaultModel;
        FishStatus.Text = SpeechService.IsFish(s.VoiceName)
            ? FishAudioTts.Configured ? "Капсулка говорит вашим голосом Fish Audio." : "Голос Fish Audio выбран, но нет ключа или ID голоса."
            : "Используйте голоса, на которые у вас есть права.";
    }

    private void SaveFishFields()
    {
        AppSettings s = App.Settings;
        s.FishKey = FishKeyBox.Password;
        s.FishVoiceId = FishAudioTts.VoiceId(FishVoiceBox.Text);
        s.FishModel = FishModelBox.Text.Trim().Length > 0 ? FishModelBox.Text.Trim() : FishAudioTts.DefaultModel;
        FishVoiceBox.Text = s.FishVoiceId; // из вставленной ссылки остаётся только ID
        FishModelBox.Text = s.FishModel;
    }

    private void FishSave_Click(object sender, RoutedEventArgs e)
    {
        SaveFishFields();
        AppSettings s = App.Settings;
        if (!FishAudioTts.Configured)
        {
            s.Save();
            FishStatus.Text = "Нужны ключ API и ID голоса. Ключ — кнопка «Получить ключ», ID — хвост ссылки на голос.";
            return;
        }
        s.VoiceName = SpeechService.FishVoice;
        s.Save();
        LoadVoiceSettings(); // в списке голосов отметится «Свой голос»
        LoadFishSettings();
        FishStatus.Text = "Сохранено. Теперь Капсулка говорит голосом Fish Audio (ключ зашифрован).";
    }

    private async void FishTest_Click(object sender, RoutedEventArgs e)
    {
        SaveFishFields();
        App.Settings.Save();
        AppSettings s = App.Settings;
        FishTestButton.IsEnabled = false;
        FishStatus.Text = "Синтезирую…";
        try
        {
            byte[] mp3 = await FishAudioTts.SynthesizeAsync(UserAddress.Apply("Добрый день, сэр. Капсулка на связи и готова помочь."),
                s.FishKey, s.FishVoiceId, s.FishModel, s.VoiceRate, CancellationToken.None);
            FishStatus.Text = SpeechService.IsFish(s.VoiceName)
                ? "Это ваш голос Fish Audio."
                : "Это ваш голос Fish Audio. Нажмите «Сохранить и выбрать», чтобы Капсулка говорила им.";
            await App.Speech.PlayClipAsync(mp3);
        }
        catch (System.Threading.Tasks.TaskCanceledException)
        {
            FishStatus.Text = "Fish Audio не ответил вовремя. Попробуйте ещё раз.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            FishStatus.Text = "Не получилось: " + ex.Message;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            FishStatus.Text = "Не получилось: " + ex.Message;
        }
        finally
        {
            FishTestButton.IsEnabled = true;
        }
    }

    private void FishGetKey_Click(object sender, RoutedEventArgs e) => ShellHelper.Open(FishAudioTts.KeysUrl);
}
