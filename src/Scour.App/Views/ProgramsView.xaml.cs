using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class ProgramsView : UserControl
{
    public ProgramsView() => InitializeComponent();

    private ProgramsViewModel? Vm => DataContext as ProgramsViewModel;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(Grid.SelectedItems.OfType<ProgramItemViewModel>());

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows, not headers or scrollbars.
        var source = e.OriginalSource as System.Windows.DependencyObject;
        while (source is not null and not DataGridRow)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : System.Windows.LogicalTreeHelper.GetParent(source);
        if (source is DataGridRow && Vm?.UninstallCommand.CanExecute(null) == true) Vm.UninstallCommand.Execute(null);
    }
}
