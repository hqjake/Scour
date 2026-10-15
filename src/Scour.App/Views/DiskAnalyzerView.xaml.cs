using System.Windows;
using System.Windows.Controls;
using Scour.App.ViewModels;
using Scour.Core.DiskUsage;

namespace Scour.App.Views;

public partial class DiskAnalyzerView : UserControl
{
    public DiskAnalyzerView()
    {
        InitializeComponent();
        // Built in code so it inherits whatever TreeViewItem style the active theme provides.
        var style = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new System.Windows.Data.Binding("IsExpanded") { Mode = System.Windows.Data.BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new System.Windows.Data.Binding("IsSelected") { Mode = System.Windows.Data.BindingMode.TwoWay }));
        Tree.ItemContainerStyle = style;
        Map.ZoomRequested += node => (DataContext as DiskAnalyzerViewModel)?.ZoomTo(node);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DiskNodeViewModel item && DataContext is DiskAnalyzerViewModel vm) vm.Selected = item.Node;
    }

    private void Largest_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Largest.SelectedItem is DiskNode node && DataContext is DiskAnalyzerViewModel vm) vm.Selected = node;
    }

    private void Changes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChangesGrid.SelectedItem is ChangeRow row && DataContext is DiskAnalyzerViewModel vm) vm.ShowChangeCommand.Execute(row);
    }
}
