using System.Windows;

namespace FC;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private PeerApiHost? _apiHost;
    private SyncEngine? _sync;
    private TrayIconService? _tray;
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var store = new StateStore();
            await store.InitializeAsync();
            var certificates = new CertificateService(store);
            var certificate = await certificates.EnsureAsync();
            var pairing = new PairingService(store);
            var manifest = new ManifestService(store);
            var peerClient = new PeerClient(store);
            var sharing = new SharingService(store, peerClient);
            var startup = new StartupService(store);
            _apiHost = new PeerApiHost(store, pairing, manifest);
            _sync = new SyncEngine(store, manifest, peerClient);

            await _apiHost.StartAsync(certificate, _lifetime.Token);
            _sync.Start(_lifetime.Token);

            var window = new MainWindow(store, pairing, peerClient, sharing, _sync, startup);
            MainWindow = window;
            _tray = new TrayIconService(
                open: () => Dispatcher.Invoke(() => ShowMainWindow()),
                exit: () => Dispatcher.Invoke(ExitApplication));

            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
                window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"FC could not start.\n\n{ex.Message}", "FC", MessageBoxButton.OK, MessageBoxImage.Error);
            IsExiting = true;
            Shutdown(1);
        }
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _lifetime.Cancel();
        try { _apiHost?.StopAsync().GetAwaiter().GetResult(); } catch { }
        _sync?.Dispose();
        _tray?.Dispose();
        _lifetime.Dispose();
        base.OnExit(e);
    }
}
