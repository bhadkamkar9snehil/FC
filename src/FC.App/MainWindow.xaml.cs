using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace FC;

public partial class MainWindow : Window
{
    private readonly StateStore _store;
    private readonly PairingService _pairing;
    private readonly PeerClient _peerClient;
    private readonly SharingService _sharing;
    private readonly SyncEngine _sync;
    private readonly StartupService _startup;
    private bool _refreshing;

    public MainWindow(StateStore store, PairingService pairing, PeerClient peerClient, SharingService sharing, SyncEngine sync, StartupService startup)
    {
        InitializeComponent();
        _store = store; _pairing = pairing; _peerClient = peerClient; _sharing = sharing; _sync = sync; _startup = startup;
        _store.Changed += Store_Changed;
        Loaded += async (_, _) => await RefreshAsync();
        Closing += MainWindow_Closing;
    }

    private void Store_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RefreshAsync);

    private async Task RefreshAsync()
    {
        _refreshing = true;
        try
        {
            var state = await _store.GetSnapshotAsync();
            DeviceSummaryText.Text = $"{state.Identity.DeviceName}  •  {LanAddressService.GetBestLanAddress()}:{state.Identity.ListenPort}";
            StartupCheck.IsChecked = state.RunAtStartup && _startup.IsRegistered();

            var peersById = state.Peers.ToDictionary(p => p.DeviceId);
            var folderRows = state.Folders.Select(f => new FolderRowVm
            {
                FolderId = f.FolderId,
                Name = f.Name,
                Path = f.LocalPath,
                Status = f.SafetyPaused ? "SAFETY PAUSED" : f.Enabled ? "ACTIVE" : "PAUSED",
                PeerText = f.PeerDeviceIds.Count == 0 ? "Not shared" : "Shared with " + string.Join(", ", f.PeerDeviceIds.Select(id => peersById.TryGetValue(id, out var p) ? p.DeviceName : id.ToString("N")[..8])),
                SafetyText = f.SafetyPaused ? f.SafetyReason : string.Empty,
                ToggleText = f.SafetyPaused ? "Allow once" : f.Enabled ? "Pause" : "Resume",
                FileCountText = BuildFileCount(state, f.FolderId),
                LastStateText = Directory.Exists(f.LocalPath) ? "Folder available" : "Local path unavailable"
            }).ToList();
            FoldersList.ItemsSource = folderRows;
            EmptyFoldersPanel.Visibility = folderRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FolderSummaryText.Text = $"{folderRows.Count} folder{(folderRows.Count == 1 ? "" : "s")}  •  {state.Peers.Count(p => p.IsOnline)}/{state.Peers.Count} peers online";

            var pending = state.PendingShares.OrderByDescending(p => p.ReceivedUtc).Select(p => new PendingRowVm
            {
                PendingId = p.PendingShareId,
                FolderName = p.FolderName,
                FromText = $"From {p.FromPeerName} • {LocalAge(p.ReceivedUtc)}"
            }).ToList();
            PendingSharesList.ItemsSource = pending;
            NoPendingText.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var peerRows = state.Peers.OrderByDescending(p => p.IsOnline).ThenBy(p => p.DeviceName).Select(p => new PeerRowVm
            {
                PeerId = p.DeviceId,
                Name = p.DeviceName,
                Endpoint = p.Endpoint,
                Status = p.IsOnline ? "ONLINE" : "OFFLINE",
                LastSeen = p.LastSeenUtc is null ? "Never seen" : LocalAge(p.LastSeenUtc.Value)
            }).ToList();
            PeersList.ItemsSource = peerRows;
            NoPeersText.Visibility = peerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            ActivityList.ItemsSource = state.Activity.Take(30).Select(a => new ActivityRowVm
            {
                Message = a.Message,
                Meta = $"{a.Kind} • {LocalAge(a.TimestampUtc)}"
            }).ToList();
        }
        finally { _refreshing = false; }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to synchronize", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _sharing.AddFolderAsync(dialog.FolderName);
            _sync.Signal();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not add folder", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Invite_Click(object sender, RoutedEventArgs e)
    {
        var code = await _pairing.CreateInviteCodeAsync();
        Clipboard.SetText(code);
        new InviteWindow(code) { Owner = this }.ShowDialog();
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PairDeviceWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InviteCode)) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            await _peerClient.PairAsync(dialog.InviteCode);
            _sync.Signal();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Pairing failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Mouse.OverrideCursor = null; }
    }

    private async void ShareFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid folderId }) return;
        var state = await _store.GetSnapshotAsync();
        if (state.Peers.Count == 0)
        {
            MessageBox.Show(this, "Pair a teammate first, then share this folder.", "No paired devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ShareFolderWindow(state.Peers) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPeerId is null) return;
        await _sharing.ShareWithPeerAsync(folderId, dialog.SelectedPeerId.Value);
        _sync.Signal();
    }

    private async void AcceptShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid pendingId }) return;
        var state = await _store.GetSnapshotAsync();
        var pending = state.PendingShares.FirstOrDefault(p => p.PendingShareId == pendingId);
        if (pending is null) return;
        var dialog = new OpenFolderDialog { Title = $"Choose where to store {pending.FolderName}", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await _sharing.AcceptPendingShareAsync(pendingId, dialog.FolderName);
        _sync.Signal();
    }

    private async void DeclineShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid pendingId }) await _sharing.DeclinePendingShareAsync(pendingId);
    }

    private async void ToggleFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid folderId }) return;
        var state = await _store.GetSnapshotAsync();
        var folder = state.Folders.FirstOrDefault(f => f.FolderId == folderId);
        if (folder is null) return;
        if (folder.SafetyPaused)
        {
            var result = MessageBox.Show(this, $"{folder.SafetyReason}\n\nAllow this incoming deletion batch once? Deleted files are moved to .fc-recycle first.", "Deletion safety pause", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes) await _sync.AllowLargeDeleteOnceAsync(folderId);
            return;
        }
        await _store.MutateAsync(s =>
        {
            var f = s.Folders.First(x => x.FolderId == folderId);
            f.Enabled = !f.Enabled;
        });
        _sync.Signal();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid folderId }) return;
        var state = await _store.GetSnapshotAsync();
        var folder = state.Folders.FirstOrDefault(f => f.FolderId == folderId);
        if (folder is null || !Directory.Exists(folder.LocalPath)) return;
        Process.Start(new ProcessStartInfo(folder.LocalPath) { UseShellExecute = true });
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            await _sync.SyncNowAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Sync failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Mouse.OverrideCursor = null; }
    }

    private async void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _startup.SetEnabledAsync(StartupCheck.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
        }
    }

    private static string BuildFileCount(AppState state, Guid folderId)
    {
        var files = state.Files.Where(f => f.FolderId == folderId && !f.Deleted).ToList();
        var bytes = files.Sum(f => f.Length);
        return $"{files.Count:N0} files • {FormatBytes(bytes)} tracked";
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double n = value; var i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.#} {units[i]}";
    }

    private static string LocalAge(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hr ago";
        return utc.ToLocalTime().ToString("dd MMM, HH:mm");
    }

    private sealed class FolderRowVm { public Guid FolderId { get; set; } public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string Status { get; set; } = ""; public string PeerText { get; set; } = ""; public string SafetyText { get; set; } = ""; public string ToggleText { get; set; } = ""; public string FileCountText { get; set; } = ""; public string LastStateText { get; set; } = ""; }
    private sealed class PendingRowVm { public Guid PendingId { get; set; } public string FolderName { get; set; } = ""; public string FromText { get; set; } = ""; }
    private sealed class PeerRowVm { public Guid PeerId { get; set; } public string Name { get; set; } = ""; public string Endpoint { get; set; } = ""; public string Status { get; set; } = ""; public string LastSeen { get; set; } = ""; }
    private sealed class ActivityRowVm { public string Message { get; set; } = ""; public string Meta { get; set; } = ""; }
}
