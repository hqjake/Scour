using System.Collections.Specialized;
using System.Windows.Controls;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class RepairView : UserControl
{
    public RepairView()
    {
        InitializeComponent();
        // Keep the newest output line in view.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is RepairViewModel vm)
                vm.Output.CollectionChanged += (_, args) =>
                {
                    if (args.Action == NotifyCollectionChangedAction.Add && OutputList.Items.Count > 0)
                        OutputList.ScrollIntoView(OutputList.Items[^1]);
                };
        };
    }
}
