using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using NourLauncher.Core.Models;
using NourLauncher.Storage;
using NourLauncher.Runtimes;

namespace NourLauncher.App;

public partial class MainWindow : Window
{
    private static readonly Regex ValidUsername = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private readonly LauncherProfileStore _profileStore = new();
    private readonly JavaRuntimeManager _javaRuntimeManager = new();
    private readonly ObservableCollection<ProfileListItem> _profiles = new();

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        Loaded += async (_, _) =>
        {
            await RefreshProfilesAsync();
            RefreshJava25Status();
        };
    }

    private async Task RefreshProfilesAsync()
    {
        try
        {
            var saved = await _profileStore.LoadAsync();
            _profiles.Clear();
            foreach (var profile in saved)
                _profiles.Add(new ProfileListItem(profile.Id, profile.Username, profile.CreatedAt, profile.IsActive));
            if (_profiles.Count > 0 && !_profiles.Any(p => p.IsActive))
                _profiles[0].IsActive = true;
            RefreshProfileLabels();
        }
        catch (Exception ex)
        {
            ProfileMessage.Text = $"Could not load saved profiles: {ex.Message}";
        }
    }

    private Task PersistProfilesAsync() => _profileStore.SaveAsync(_profiles.Select(p =>
        new LauncherProfile(p.Id, p.Username, p.CreatedAt, p.IsActive)));

    private async void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text.Trim();
        if (!ValidUsername.IsMatch(username))
        {
            ProfileMessage.Text = "Use 3–16 characters: letters, numbers or underscores.";
            return;
        }
        if (_profiles.Any(p => string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            ProfileMessage.Text = "A profile with that username already exists.";
            return;
        }

        var profile = new ProfileListItem(Guid.NewGuid(), username, DateTimeOffset.UtcNow, _profiles.Count == 0);
        _profiles.Add(profile);
        UsernameInput.Clear();
        await SaveAndReportAsync($"Profile '{username}' added.");
    }

    private async void SelectProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        foreach (var profile in _profiles) profile.IsActive = profile.Id == id;
        var selected = _profiles.First(p => p.Id == id);
        await SaveAndReportAsync($"'{selected.Username}' is now the selected local profile.");
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var profile = _profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        var wasActive = profile.IsActive;
        _profiles.Remove(profile);
        if (wasActive && _profiles.Count > 0) _profiles[0].IsActive = true;
        await SaveAndReportAsync($"Removed '{profile.Username}'.");
    }

    private async Task SaveAndReportAsync(string successMessage)
    {
        try
        {
            await PersistProfilesAsync();
            ProfileMessage.Text = successMessage;
        }
        catch (Exception ex)
        {
            ProfileMessage.Text = $"Could not save profiles: {ex.Message}";
        }
    }

    private void RefreshProfileLabels()
    {
        foreach (var profile in _profiles) profile.RefreshStatus();
    }

    private void ShowSection(string section)
    {
        HomePanel.Visibility = section == "Home" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPanel.Visibility = section == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = section is "Home" or "Profiles" or "Settings" ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderTitle.Text = section;
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowSection("Home");
    private void Profiles_Click(object sender, RoutedEventArgs e) => ShowSection("Profiles");
    private void Versions_Click(object sender, RoutedEventArgs e) => ShowSection("Versions");
    private void Instances_Click(object sender, RoutedEventArgs e) => ShowSection("Instances");
    private void Mods_Click(object sender, RoutedEventArgs e) => ShowSection("Mods");
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSection("Settings");

    private void RefreshJava25Status()
    {
        var installed = _javaRuntimeManager.IsJava25Installed;
        Java25Status.Text = installed
            ? "Installed · " + _javaRuntimeManager.JavaExecutablePath
            : "Not installed";
        InstallJava25Button.Content = installed ? "REINSTALL JAVA 25" : "INSTALL JAVA 25";
    }

    private async void InstallJava25_Click(object sender, RoutedEventArgs e)
    {
        InstallJava25Button.IsEnabled = false;
        Java25Progress.Visibility = Visibility.Visible;
        Java25Progress.Value = 0;
        Java25Message.Text = "Downloading Eclipse Temurin 25…";

        var progress = new Progress<double>(value =>
            Java25Progress.Value = Math.Clamp(value * 100, 0, 100));

        try
        {
            await _javaRuntimeManager.InstallJava25Async(progress);
            Java25Message.Text = "Java 25 installed successfully. It is now managed by Nour Launcher.";
            RefreshJava25Status();
        }
        catch (Exception ex)
        {
            Java25Message.Text = "Java 25 installation failed: " + ex.Message;
        }
        finally
        {
            InstallJava25Button.IsEnabled = true;
        }
    }

    private sealed class ProfileListItem : INotifyPropertyChanged
    {
        private bool _isActive;

        public ProfileListItem(Guid id, string username, DateTimeOffset createdAt, bool isActive)
        {
            Id = id;
            Username = username;
            CreatedAt = createdAt;
            _isActive = isActive;
        }

        public Guid Id { get; }
        public string Username { get; }
        public DateTimeOffset CreatedAt { get; }
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusLabel));
            }
        }

        public string StatusLabel => IsActive ? "Selected local profile" : "Local profile";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void RefreshStatus() => OnPropertyChanged(nameof(StatusLabel));
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
