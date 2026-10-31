using System.Windows;
using System.Windows.Media;

namespace OAIRouter;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText = "确定", bool danger = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = confirmText;

        if (danger)
        {
            OkButton.Background = new SolidColorBrush(Accent.Error);
            OkButton.Foreground = Brushes.White;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.Apply(this);
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
