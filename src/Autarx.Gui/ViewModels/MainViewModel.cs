using System.Collections.ObjectModel;
using Autarx.Core.Models;
using Autarx.Core.Parsing;
using Autarx.Core.Validation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Autarx.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>Set by the view so the VM can open a file picker without
    /// referencing Avalonia controls.</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private string _statusText = "Open an ARXML file to begin.";

    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    public ObservableCollection<TreeNodeViewModel> Modules { get; } = [];

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (PickFile is null)
            return;

        var path = await PickFile();
        if (path is not null)
            await LoadFileAsync(path);
    }

    public async Task LoadFileAsync(string path)
    {
        FilePath = path;
        StatusText = $"Parsing {Path.GetFileName(path)}…";

        try
        {
            var (modules, diagnostics) = await Task.Run(() =>
            {
                var root = ArxmlParser.ParseFile(path);
                var mods = EcucReader.ReadModules(root);
                return (mods, ValidationEngine.Validate(mods));
            });

            Modules.Clear();
            foreach (var module in modules)
                Modules.Add(TreeNodeViewModel.FromModule(module));

            var errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = diagnostics.Count - errors;
            StatusText = $"{Path.GetFileName(path)} — {modules.Count} module(s), " +
                         $"{errors} error(s), {warnings} warning(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load {Path.GetFileName(path)}: {ex.Message}";
        }
    }
}
