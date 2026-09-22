using System.Windows;
using System.Windows.Input;
using CVmaker.App.Localization;

namespace CVmaker.App;

public sealed class ShortcutRow
{
    public string Keys { get; init; } = "";
    public string Action { get; init; } = "";
}

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        if (owner.Icon != null) Icon = owner.Icon;
        Rows.ItemsSource = Loc.ShortcutRows().Select(r => new ShortcutRow { Keys = r.Keys, Action = r.Action }).ToList();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape || e.Key == Key.Enter) Close(); };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
