using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Preflight.App.ViewModels;

namespace Preflight.App.Views;

public partial class AddSiteWindow : Window
{
    public AddSiteWindow() => InitializeComponent();

    private AddSiteViewModel Vm => (AddSiteViewModel)DataContext!;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Local Git folder" });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) Vm.LocalFolder = path;
    }

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "SSH private key" });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) Vm.KeyPath = path;
    }
}
