using System.Collections.Specialized;
using System.Windows.Controls;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class RepairView : UserControl
{
    public RepairView()
    {
        InitializeComponent();
        // Keep the newest output line in view. A new view is made each time the page is opened, so unhook the
        // old one or every earlier view keeps listening.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is RepairViewModel old) old.Output.CollectionChanged -= OnOutputChanged;
            if (e.NewValue is RepairViewModel vm && IsLoaded) vm.Output.CollectionChanged += OnOutputChanged;
        };
        Loaded += (_, _) =>
        {
            if (DataContext is RepairViewModel vm)
            {
                vm.Output.CollectionChanged -= OnOutputChanged;
                vm.Output.CollectionChanged += OnOutputChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is RepairViewModel vm) vm.Output.CollectionChanged -= OnOutputChanged;
        };
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Add && OutputList.Items.Count > 0)
            OutputList.ScrollIntoView(OutputList.Items[^1]);
    }
}
