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
using NourLauncher.Minecraft.Launching;
using NourLauncher.App.Games;

namespace NourLauncher.App;

public partial class MainWindow : Window
{
    private static readonly Regex ValidUsername = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private readonly LauncherProfileStore _profileStore = new();
    private readonly LauncherSettingsStore _settingsStore = new();
    private readonly JavaRuntimeManager _javaRuntimeManager = new();
    private readonly ObservableCollection<ProfileListItem> _profiles = new();
    private readonly ObservableCollection<VersionListItem> _visibleVersions = new();
    private readonly List<VersionListItem> _allVersions = new();
    private readonly MinecraftInstallationService _installationService = new();
    private readonly MinecraftLaunchService _launchService = new();
    private readonly LuantiManager _luantiManager = new();
    private LauncherSettings _settings = new();
    private bool _isInstallingLuanti;
    private bool _versionsLoaded;
    private bool _isInstallingVersion;
    private bool _isLaunching;
    private bool _suppressSettingsEvents;

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        VersionList.ItemsSource = _visibleVersions;
        Loaded += async (_, _) =>
        {
            await LoadSettingsAsync();
            await RefreshProfilesAsync();
            RefreshManagedJavaStatus();
        };
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
        }
        catch (Exception ex)
        {
            _settings = new LauncherSettings();
            ProfileMessage.Text = $"Could not load launcher settings: {ex.Message}";
        }
        RefreshOfflineModeUi();
    }

    private async Task RefreshProfilesAsync()
    {
        try
        {
            var saved = await _profileStore.LoadAsync();
            _profiles.Clear();
            foreach (var profile in saved)
                _profiles.Add(new ProfileListItem(
                    profile.Id, profile.Username, profile.CreatedAt, profile.IsActive, profile.AccountType));
            if (_profiles.Count > 0 && !_profiles.Any(p => p.IsActive))
                _profiles[0].IsActive = true;
            RefreshProfileLabels();
            LuantiUsernameInput.Text = _profiles.FirstOrDefault(p => p.IsActive)?.Username ?? "Player";
        }
        catch (Exception ex)
        {
            ProfileMessage.Text = $"Could not load saved profiles: {ex.Message}";
        }
    }

    private Task PersistProfilesAsync() => _profileStore.SaveAsync(_profiles.Select(p =>
        new LauncherProfile(p.Id, p.Username, p.CreatedAt, p.IsActive, p.AccountType)));

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

        var profile = new ProfileListItem(
            Guid.NewGuid(), username, DateTimeOffset.UtcNow, _profiles.Count == 0, LauncherAccountType.Offline);
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
        LuantiPanel.Visibility = section == "OfflineGame" ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = section is "Home" or "Profiles" or "Settings" or "Versions" or "OfflineGame" ? Visibility.Collapsed : Visibility.Visible;
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
    private void OfflineGame_Click(object sender, RoutedEventArgs e)
    {
        ShowSection("OfflineGame");
        LuantiUsernameInput.Text = _profiles.FirstOrDefault(p => p.IsActive)?.Username ?? LuantiUsernameInput.Text;
        LuantiStatus.Text = _luantiManager.IsInstalled
            ? "Luanti and Minetest Game are installed. Ready to launch."
            : "Not installed yet. Press INSTALL & PLAY to download the game files.";
        InstallPlayLuantiButton.Content = _luantiManager.IsInstalled ? "PLAY LUANTI" : "INSTALL & PLAY";
    }

    private async void InstallPlayLuanti_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstallingLuanti) return;
        _isInstallingLuanti = true;
        InstallPlayLuantiButton.IsEnabled = false;
        var username = LuantiUsernameInput.Text.Trim();
        var progress = new Progress<string>(message => LuantiStatus.Text = message);
        try
        {
            if (!_luantiManager.IsInstalled)
                await _luantiManager.InstallAsync(username, progress);
            LuantiStatus.Text = "Starting Luanti…";
            await _luantiManager.LaunchAsync(username);
            LuantiStatus.Text = "Luanti launched. Create a singleplayer world from its main menu to start playing.";
            InstallPlayLuantiButton.Content = "PLAY LUANTI";
        }
        catch (Exception ex)
        {
            LuantiStatus.Text = "Could not start the offline game: " + ex.Message;
        }
        finally
        {
            _isInstallingLuanti = false;
            InstallPlayLuantiButton.IsEnabled = true;
        }
    }

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
            RefreshLaunchState();
            return;
        }
        SelectedVersionLabel.Text = selected.Id;
        InstallVersionButton.Content = selected.IsInstalled ? "REINSTALL VERSION" : "INSTALL VERSION";
        InstallVersionButton.IsEnabled = !_isInstallingVersion;
        RefreshLaunchState();
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
            var javaMajor = await _installationService.GetJavaMajorVersionAsync(selected.Id);
            VersionInstallMessage.Text = $"Minecraft files installed. Preparing private Java {javaMajor}…";
            var runtimeProgress = new Progress<double>(value =>
            {
                VersionInstallProgress.Value = 50 + Math.Clamp(value * 50, 0, 50);
                VersionInstallMessage.Text = $"Installing private Java {javaMajor}…";
            });
            await _javaRuntimeManager.EnsureRuntimeAsync(javaMajor, runtimeProgress);
            RefreshManagedJavaStatus();
            selected.IsInstalled = true;
            ApplyVersionFilters();
            VersionList.SelectedItem = _visibleVersions.FirstOrDefault(v => v.Id == selected.Id);
            VersionInstallProgress.Value = 100;
            VersionInstallMessage.Text = $"Minecraft {selected.Id} and Java {javaMajor} are ready. " +
                (_settings.AllowOfflineMode
                    ? "Press PLAY OFFLINE to start."
                    : "Enable offline mode under Settings to play it.");
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
            RefreshLaunchState();
        }
    }

    /// <summary>
    /// Enables PLAY OFFLINE only when the selected version is fully installed, the user has
    /// opted in to offline mode, and no other launch/install is in flight.
    /// </summary>
    private void RefreshLaunchState()
    {
        if (PlayOfflineButton is null) return;
        var selected = VersionList?.SelectedItem as VersionListItem;
        PlayOfflineButton.IsEnabled = !_isLaunching && !_isInstallingVersion &&
                                      selected?.IsInstalled == true && _settings.AllowOfflineMode;
    }

    private async void PlayOffline_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionListItem selected || _isLaunching) return;
        if (!selected.IsInstalled)
        {
            VersionInstallMessage.Text = $"Install Minecraft {selected.Id} first.";
            return;
        }
        if (!_settings.AllowOfflineMode)
        {
            VersionInstallMessage.Text = "Offline mode is turned off. Enable it under Settings to launch without an account.";
            return;
        }

        var profile = _profiles.FirstOrDefault(p => p.IsActive);
        if (profile is null)
        {
            VersionInstallMessage.Text = "Add a profile on the Profiles page first; offline play needs a player name.";
            return;
        }

        _isLaunching = true;
        RefreshLaunchState();
        VersionInstallProgress.Visibility = Visibility.Visible;
        VersionInstallProgress.IsIndeterminate = true;
        VersionInstallMessage.Text = $"Preparing Minecraft {selected.Id} for {profile.Username}…";

        try
        {
            var javaMajor = await _installationService.GetJavaMajorVersionAsync(selected.Id);
            VersionInstallMessage.Text = $"Checking private Java {javaMajor}…";
            var javaExecutable = await _javaRuntimeManager.EnsureRuntimeAsync(javaMajor);
            RefreshManagedJavaStatus();

            var session = MinecraftLaunchSession.CreateOffline(profile.Username);
            var process = _launchService.Launch(
                new MinecraftLaunchRequest(selected.Id), session, javaExecutable);

            VersionInstallMessage.Text =
                $"Minecraft {selected.Id} started as {session.Username} (offline, PID {process.Id})." +
                Environment.NewLine +
                "Singleplayer and LAN worlds work; servers that require Mojang/Microsoft authentication will refuse the connection.";
        }
        catch (Exception ex)
        {
            VersionInstallMessage.Text = $"Offline launch failed: {ex.Message}";
        }
        finally
        {
            VersionInstallProgress.IsIndeterminate = false;
            VersionInstallProgress.Visibility = Visibility.Collapsed;
            _isLaunching = false;
            RefreshLaunchState();
        }
    }

    private async void OfflineMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        var enabled = OfflineModeCheckBox.IsChecked == true;

        if (enabled && !_settings.OfflineModeNoticeAccepted)
        {
            var answer = MessageBox.Show(
                this,
                "Offline mode launches Minecraft with a locally generated player identity instead of a Microsoft account.\n\n" +
                "It works for singleplayer worlds and LAN games. It does not provide skins, Realms, account sync, " +
                "or access to servers that verify accounts.\n\n" +
                "You are responsible for holding a legitimate Minecraft licence for any copy you play.\n\n" +
                "Enable offline mode?",
                "Enable offline mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                _suppressSettingsEvents = true;
                OfflineModeCheckBox.IsChecked = false;
                _suppressSettingsEvents = false;
                return;
            }

            _settings = _settings with { OfflineModeNoticeAccepted = true };
        }

        _settings = _settings with { AllowOfflineMode = enabled };
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            OfflineModeStatus.Text = $"Could not save launcher settings: {ex.Message}";
            return;
        }
        RefreshOfflineModeUi();
    }

    private void RefreshOfflineModeUi()
    {
        if (OfflineModeCheckBox is not null)
        {
            _suppressSettingsEvents = true;
            OfflineModeCheckBox.IsChecked = _settings.AllowOfflineMode;
            _suppressSettingsEvents = false;
        }

        if (OfflineModeStatus is not null)
            OfflineModeStatus.Text = _settings.AllowOfflineMode
                ? "Offline mode is ON. Installed versions can be launched with the selected profile's local identity — no account sign-in takes place."
                : "Offline mode is OFF. Launching Minecraft requires a Microsoft session, which Sky Client does not support yet.";

        RefreshLaunchState();
    }

    private void RefreshManagedJavaStatus()
    {
        var installed = _javaRuntimeManager.GetInstalledMajors();
        ManagedJavaStatus.Text = installed.Count == 0
            ? "No managed Java runtimes installed yet. Sky Client installs the correct one when you install a Minecraft version."
            : "Installed runtimes: " + string.Join(", ", installed.Select(v => $"Java {v}")) +
              Environment.NewLine + string.Join(Environment.NewLine, installed.Select(v => $"Java {v}: {_javaRuntimeManager.GetJavaExecutablePath(v)}"));
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

        public ProfileListItem(
            Guid id,
            string username,
            DateTimeOffset createdAt,
            bool isActive,
            LauncherAccountType accountType = LauncherAccountType.Offline)
        {
            Id = id;
            Username = username;
            CreatedAt = createdAt;
            AccountType = accountType;
            _isActive = isActive;
        }

        public Guid Id { get; }
        public string Username { get; }
        public DateTimeOffset CreatedAt { get; }
        public LauncherAccountType AccountType { get; }
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

        public string AccountTypeLabel => AccountType == LauncherAccountType.Offline
            ? "OFFLINE ACCOUNT"
            : "MICROSOFT ACCOUNT";

        public string StatusLabel => IsActive ? "Selected · " + AccountTypeLabel : AccountTypeLabel;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void RefreshStatus() => OnPropertyChanged(nameof(StatusLabel));
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
