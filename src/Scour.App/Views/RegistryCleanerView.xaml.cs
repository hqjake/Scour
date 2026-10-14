using System.Windows.Controls;
using System.Windows.Input;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class RegistryCleanerView : UserControl
{
    public RegistryCleanerView() => InitializeComponent();

    private void IssuesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IssuesGrid.SelectedItem is RegistryIssueViewModel issue && DataContext is RegistryCleanerViewModel vm)
            vm.OpenCommand.Execute(issue);
    }
}
