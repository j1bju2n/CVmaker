using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CVmaker.App.Localization;

namespace CVmaker.App;

/// <summary>Dark-themed replacement for MessageBox: information or a yes/no question.</summary>
public partial class MessageDialog : Window
{
    private MessageDialog(Window? owner, string title, string heading, string message, bool question, ImageSource? icon)
    {
        InitializeComponent();
        Owner = owner;
        if (owner?.Icon != null) Icon = owner.Icon;
        Title = title;
        HeadingText.Text = heading;
        HeadingText.Visibility = string.IsNullOrEmpty(heading) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;
        if (icon != null)
        {
            IconImage.Source = icon;
            IconImage.Visibility = Visibility.Visible;
        }
        if (question)
        {
            YesButton.Content = Loc.T("Confirm.Yes");
            NoButton.Content = Loc.T("Confirm.No");
            NoButton.Visibility = Visibility.Visible;
        }
        else
        {
            YesButton.Content = Loc.T("Dialog.OK");
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        };
        Loaded += (_, _) => YesButton.Focus();
    }

    /// <summary>Dev helper for the screenshot mode: a non-modal question dialog.</summary>
    internal static MessageDialog CreateForScreenshot(Window owner, string title, string heading, string message) =>
        new(owner, title, heading, message, true, null);

    public static bool Confirm(Window? owner, string title, string heading, string message)
    {
        var d = new MessageDialog(owner, title, heading, message, true, null);
        return d.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string heading, string message, ImageSource? icon = null)
    {
        var d = new MessageDialog(owner, title, heading, message, false, icon);
        d.ShowDialog();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void NoButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
