using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Autarx.Gui.ViewModels;

namespace Autarx.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.PickFile = PickFileAsync;
        };
    }

    private async Task<string?> PickFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open ARXML",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ARXML") { Patterns = ["*.arxml", "*.xml"] },
            ],
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
