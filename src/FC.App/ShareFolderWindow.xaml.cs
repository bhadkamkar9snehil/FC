using System.Windows;

namespace FC;

public partial class ShareFolderWindow : Window
{
    public Guid? SelectedPeerId { get; private set; }
    public ShareFolderWindow(IEnumerable<PeerDevice> peers)
    {
        InitializeComponent();
        PeerCombo.ItemsSource = peers.OrderBy(p => p.DeviceName).Select(p => new PeerChoice(p.DeviceId, $"{p.DeviceName}  —  {p.Endpoint}")).ToList();
        PeerCombo.SelectedIndex = 0;
    }
    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (PeerCombo.SelectedValue is not Guid id) return;
        SelectedPeerId = id;
        DialogResult = true;
    }
    private sealed record PeerChoice(Guid Id, string Label);
}
