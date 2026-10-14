using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.App.ViewModels;

namespace Scour.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayIntro();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void PlayIntro()
    {
        SoundService.Play(UiSound.Startup);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = (ScaleTransform)SplashLogo.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        SplashTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = ease });
        SplashTag.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(450), EasingFunction = ease });

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450)) { BeginTime = TimeSpan.FromMilliseconds(1400), EasingFunction = ease };
        fade.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
        Splash.BeginAnimation(OpacityProperty, fade);
        AnimatePage();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage)) return;
        SoundService.Play(UiSound.Nav);
        AnimatePage();
    }

    /// <summary>New pages fade in and glide up.</summary>
    private void AnimatePage()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        ((TranslateTransform)PageHost.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Vm.CurrentPage = Vm.Settings;

    private void Hunter_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HunterTarget.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        e.Handled = true;
    }

    private void Hunter_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!HunterTarget.IsMouseCaptured) return;
        HunterTarget.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        e.Handled = true;

        var hit = Vm.Hunt();
        if (hit is null) return; // released over Scour itself or nothing
        SoundService.Play(UiSound.Scan);
        ShowHunterMenu(hit);
    }

    private void ShowHunterMenu(HuntResult hit)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var title = hit.Program?.DisplayName ?? (hit.ExecutablePath is null ? $"Process {hit.ProcessId}" : System.IO.Path.GetFileName(hit.ExecutablePath));
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());

        if (hit.Program is { } program)
        {
            Add(menu, "Uninstall…", () => Vm.UninstallFromHunter(program));
            Add(menu, "Show in program list", () => Vm.ShowInList(program));
        }
        else if (hit.ExecutablePath is not null)
        {
            Add(menu, "Not in the installed list — forced uninstall…", () => Vm.ForcedFromHunter(hit));
        }
        if (hit.ExecutablePath is { } exe)
        {
            Add(menu, "Open file location", () => Shell.OpenFolder(exe));
            Add(menu, "Search online", () => Shell.SearchWeb(hit.Program?.DisplayName ?? System.IO.Path.GetFileNameWithoutExtension(exe)));
        }
        Add(menu, "End process", () =>
        {
            if (Dialogs.Confirm($"End \"{title}\"? Unsaved work in it will be lost."))
            {
                try { Hunter.Kill(hit.ProcessId); }
                catch (Exception ex) { Dialogs.Error(ex.Message); }
            }
        });
        menu.IsOpen = true;
    }

    private static void Add(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
