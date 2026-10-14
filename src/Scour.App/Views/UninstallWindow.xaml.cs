using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is UninstallWizardViewModel vm)
            {
                vm.CloseRequested += Close;
                await vm.StartAsync();
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is UninstallWizardViewModel vm && !vm.ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    private void Leftovers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Leftovers.SelectedItem is LeftoverItemViewModel item && DataContext is UninstallWizardViewModel vm)
            vm.OpenItemCommand.Execute(item);
    }
}
