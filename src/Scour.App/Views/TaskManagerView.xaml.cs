using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class TaskManagerView : UserControl
{
    public TaskManagerView() => InitializeComponent();

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && Tabs.SelectedIndex == 1 && DataContext is TaskManagerViewModel vm) vm.EnsureServices();
    }

    /// <summary>Buttons whose choices are in a drop-down menu.</summary>
    private void Priority_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
