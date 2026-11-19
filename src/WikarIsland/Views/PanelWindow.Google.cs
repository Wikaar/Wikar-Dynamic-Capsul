using System;
using System.Windows;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>Настройки → Google: вход, что показывать в капсуле.</summary>
public partial class PanelWindow
{
    private void LoadGoogleSettings()
    {
        AppSettings s = App.Settings;
        GoogleIdBox.Text = s.GoogleClientId;
        GoogleSecretBox.Password = s.GoogleClientSecret;
        GoogleInPillBox.IsChecked = s.GoogleInPill;
        GoogleAlertsBox.IsChecked = s.GoogleEventAlerts;
        GoogleVoiceBox.IsChecked = s.GoogleEventVoice;
        GoogleVoiceBox.IsEnabled = s.GoogleEventAlerts;
        GoogleSyncBox.IsChecked = s.GoogleTasksSync;
        UpdateGoogleStatus();
    }

    private void UpdateGoogleStatus(string? message = null)
    {
        GoogleService g = App.Google;
        GoogleSignOutButton.Visibility = g.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        GoogleSignInButton.Content = g.IsSignedIn ? "Войти заново" : "Войти через Google";
        GoogleStatus.Text = message ?? (g.IsSignedIn
            ? $"Подключено: {(App.Settings.GoogleEmail.Length > 0 ? App.Settings.GoogleEmail : "аккаунт Google")}" +
              (g.LastError != null ? $"\nОшибка: {g.LastError}" : g.LastUpdate != default ? $" · событий: {g.Events.Count}, задач: {g.Tasks.Count}" : "")
            : "Не подключено.");
    }

    private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
    {
        string id = GoogleIdBox.Text.Trim();
        string secret = GoogleSecretBox.Password.Trim();
        if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            UpdateGoogleStatus("Client ID выглядит так: 1234567890-abc….apps.googleusercontent.com — скопируй его из Google Cloud → Credentials.");
            return;
        }
        if (secret.Length < 10)
        {
            UpdateGoogleStatus("Вставь секрет клиента (Client secret, начинается с GOCSPX-).");
            return;
        }
        App.Settings.GoogleClientId = id;
        App.Settings.GoogleClientSecret = secret;
        App.Settings.Save();
        GoogleSignInButton.IsEnabled = false;
        UpdateGoogleStatus("Открыл браузер — войди в Google и разреши доступ. Если появится «Приложение не проверено» — «Дополнительно» → «Перейти».");
        string? error = await App.Google.SignInAsync();
        GoogleSignInButton.IsEnabled = true;
        UpdateGoogleStatus(error);
        if (error == null) UpdateAgenda();
    }

    private void GoogleSignOut_Click(object sender, RoutedEventArgs e)
    {
        App.Google.SignOut();
        UpdateGoogleStatus();
        UpdateAgenda();
        App.Pill.UpdateAgenda();
    }

    private void GoogleConsole_Click(object sender, RoutedEventArgs e) =>
        ShellHelper.Open("https://console.cloud.google.com/apis/credentials");

    private void GoogleOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings s = App.Settings;
        s.GoogleInPill = GoogleInPillBox.IsChecked == true;
        s.GoogleEventAlerts = GoogleAlertsBox.IsChecked == true;
        s.GoogleEventVoice = GoogleVoiceBox.IsChecked == true;
        GoogleVoiceBox.IsEnabled = s.GoogleEventAlerts;
        s.GoogleTasksSync = GoogleSyncBox.IsChecked == true;
        s.Save();
        App.Pill.UpdateAgenda();
    }
}
