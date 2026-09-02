using System.Windows;

namespace FC;

public partial class PairDeviceWindow : Window
{
    public string InviteCode { get; private set; } = string.Empty;
    public PairDeviceWindow() => InitializeComponent();
    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text)) return;
        InviteCode = CodeBox.Text.Trim();
        DialogResult = true;
    }
}
