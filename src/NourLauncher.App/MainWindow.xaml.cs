using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using NourLauncher.Core.Models;
using NourLauncher.Storage;
using NourLauncher.Runtimes;
using NourLauncher.Minecraft.Installation;

namespace NourLauncher.App;

public partial class MainWindow : Window
{
    private static readonly Regex ValidUsername = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private readonly LauncherProfileStore _profileStore = new();
    private readonly JavaRuntimeManager _javaRuntimeManager = new();
    private readonly ObservableCollection<ProfileListItem> _profiles = new();
    private readonly ObservableCollection<VersionListItem> _visibleVersions = new();
    private readonly List<VersionListItem> _allVersions = new();
    private readonly MinecraftInstallationService _installationService = new();
    private bool _versionsLoaded;
    private bool _isInstallingVersion;

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        VersionList.ItemsSource = _visibleVersions;
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
        VersionsPanel.Visibility = section == "Versions" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = section is "Home" or "Profiles" or "Settings" or "Versions" ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderTitle.Text = section;
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowSection("Home");
    private void Profiles_Click(object sender, RoutedEventArgs e) => ShowSection("Profiles");
    private async void Versions_Click(object sender, RoutedEventArgs e)
    {
        ShowSection("Versions");
        if (!_versionsLoaded) await LoadVersionsAsync();
    }
    private void Instances_Click(object sender, RoutedEventArgs e) => ShowSection("Instances");
    private void Mods_Click(object sender, RoutedEventArgs e) => ShowSection("Mods");
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSection("Settings");

    private async Task LoadVersionsAsync()
    {
        VersionsMessage.Text = "Loading official Minecraft versions from Mojang…";
        try
        {
            var versions = await _installationService.GetVersionsAsync();
            _allVersions.Clear();
            _allVersions.AddRange(versions.Select(v => new VersionListItem(
                v.Id, v.Type, v.ReleaseTime, _installationService.IsInstalled(v.Id))));
            _versionsLoaded = true;
            ApplyVersionFilters();
            VersionsMessage.Text = $"Loaded {_allVersions.Count:N0} official versions. Select one to install.";
        }
        catch (Exception ex)
        {
            VersionsMessage.Text = "Could not load Minecraft versions: " + ex.Message;
        }
    }

    private void ApplyVersionFilters()
    {
        if (_allVersions.Count == 0) return;
        var query = VersionSearchInput.Text.Trim();
        var selectedFilter = (VersionTypeFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var filtered = _allVersions.Where(v =>
            (selectedFilter == "all" || v.Type == selectedFilter) &&
            (query.Length == 0 || v.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        _visibleVersions.Clear();
        foreach (var version in filtered) _visibleVersions.Add(version);
        VersionsMessage.Text = $"Showing {_visibleVersions.Count:N0} of {_allVersions.Count:N0} versions.";
    }

    private void VersionSearchInput_TextChanged(object sender, TextChangedEventArgs e) => ApplyVersionFilters();
    private void VersionTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyVersionFilters();

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedVersionLabel is null || InstallVersionButton is null) return;
        if (VersionList.SelectedItem is not VersionListItem selected)
        {
            SelectedVersionLabel.Text = "Select a version above";
            InstallVersionButton.IsEnabled = false;
            return;
        }
        SelectedVersionLabel.Text = selected.Id;
        InstallVersionButton.Content = selected.IsInstalled ? "REINSTALL VERSION" : "INSTALL VERSION";
        InstallVersionButton.IsEnabled = !_isInstallingVersion;
    }

    private async void RefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        await LoadVersionsAsync();
    }

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionListItem selected || _isInstallingVersion) return;
        _isInstallingVersion = true;
        InstallVersionButton.IsEnabled = false;
        VersionInstallProgress.Visibility = Visibility.Visible;
        VersionInstallProgress.Value = 0;
        VersionInstallMessage.Text = $"Preparing Minecraft {selected.Id}…";
        var progress = new Progress<InstallationProgress>(update =>
        {
            VersionInstallProgress.Value = Math.Clamp(update.Percentage * 100, 0, 100);
            VersionInstallMessage.Text = update.Message;
        });

        try
        {
            await _installationService.InstallAsync(selected.Id, progress);
            selected.IsInstalled = true;
            ApplyVersionFilters();
            VersionList.SelectedItem = _visibleVersions.FirstOrDefault(v => v.Id == selected.Id);
            VersionInstallProgress.Value = 100;
            VersionInstallMessage.Text = $"Minecraft {selected.Id} is installed. Game files are cached; launching is the next milestone.";
        }
        catch (Exception ex)
        {
            VersionInstallMessage.Text = $"Installation failed: {ex.Message}";
        }
        finally
        {
            _isInstallingVersion = false;
            InstallVersionButton.IsEnabled = VersionList.SelectedItem is VersionListItem;
            if (VersionList.SelectedItem is VersionListItem current)
                InstallVersionButton.Content = current.IsInstalled ? "REINSTALL VERSION" : "INSTALL VERSION";
        }
    }

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
            Java25Message.Text = "Java 25 installed successfully. It is now managed by Sky Client.";
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

    private sealed class VersionListItem : INotifyPropertyChanged
    {
        private bool _isInstalled;

        public VersionListItem(string id, string type, DateTimeOffset releaseTime, bool isInstalled)
        {
            Id = id;
            Type = type;
            ReleaseTime = releaseTime;
            _isInstalled = isInstalled;
        }

        public string Id { get; }
        public string Type { get; }
        public DateTimeOffset ReleaseTime { get; }
        public string TypeLabel => Type.Replace('_', ' ').ToUpperInvariant();
        public string ReleaseDateLabel => ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");
        public string StatusLabel => IsInstalled ? "INSTALLED" : "AVAILABLE";
        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (_isInstalled == value) return;
                _isInstalled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusLabel));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
