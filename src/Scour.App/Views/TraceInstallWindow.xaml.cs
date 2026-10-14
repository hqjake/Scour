using System.ComponentModel;
using System.Windows;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class TraceInstallWindow : Window
{
    private bool _closingFromVm;

    public TraceInstallWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is TraceInstallViewModel vm)
                vm.CloseRequested += () =>
                {
                    _closingFromVm = true;
                    Close();
                };
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingFromVm && DataContext is TraceInstallViewModel vm && !vm.ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }
}
