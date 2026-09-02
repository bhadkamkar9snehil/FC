using System.Windows;

namespace FC;

public partial class InviteWindow : Window
{
    public InviteWindow(string code)
    {
        InitializeComponent();
        InviteCodeBox.Text = code;
    }
    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(InviteCodeBox.Text);
    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
