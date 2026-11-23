using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Preflight.App.ViewModels;

namespace Preflight.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Log.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add)
                        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
                };
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private async void OnAddSite(object? sender, RoutedEventArgs e)
    {
        var dialogVm = new AddSiteViewModel();
        var dialog = new AddSiteWindow { DataContext = dialogVm };
        dialogVm.Saved = (path, manifest) =>
        {
            Vm.AddSite(path, manifest);
            dialog.Close();
        };
        await dialog.ShowDialog(this);
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } cb) await cb.SetTextAsync(Vm.LogText);
    }
}
