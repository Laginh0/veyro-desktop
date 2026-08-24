using System.Windows;
using System.Windows.Input;

namespace Veyro.Desktop.Views;

public partial class DecisionDialog : Window
{
    private DecisionDialog(
        string eyebrow,
        string title,
        string message,
        string acceptText,
        string declineText,
        string icon)
    {
        InitializeComponent();
        EyebrowText.Text = eyebrow.ToUpperInvariant();
        TitleText.Text = title;
        MessageText.Text = message;
        AcceptButton.Content = acceptText;
        DeclineButton.Content = declineText;
        IconText.Text = icon;
    }

    public static bool Ask(
        Window owner,
        string eyebrow,
        string title,
        string message,
        string acceptText = "Permitir",
        string declineText = "Agora não",
        string icon = "\uE72E")
    {
        var dialog = new DecisionDialog(
            eyebrow,
            title,
            message,
            acceptText,
            declineText,
            icon)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
