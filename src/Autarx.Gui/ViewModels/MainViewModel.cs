using System.Collections.ObjectModel;
using System.Text;
using Autarx.Core.Communication;
using Autarx.Core.Diff;
using Autarx.Core.Index;
using Autarx.Core.Models;
using Autarx.Core.Parsing;
using Autarx.Core.Validation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Autarx.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private WorkspaceIndex? _workspaceIndex;

    /// <summary>Set by the view so the VM can open file/folder pickers without
    /// referencing Avalonia controls.</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private string? _workspacePath;

    [ObservableProperty]
    private string _statusText = "Open an ARXML file or a workspace folder to begin.";

    [ObservableProperty]
    private string _analysisText = "";

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

    [RelayCommand]
    private async Task OpenWorkspaceAsync()
    {
        if (PickFolder is null)
            return;

        var path = await PickFolder();
        if (path is not null)
            await LoadWorkspaceAsync(path);
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

            _workspaceIndex = null;
            WorkspacePath = null;
            Modules.Clear();
            foreach (var module in modules)
                Modules.Add(TreeNodeViewModel.FromModule(module));

            var errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = diagnostics.Count - errors;
            StatusText = $"{Path.GetFileName(path)} — {modules.Count} module(s), " +
                         $"{errors} error(s), {warnings} warning(s)";
            AnalysisText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    public async Task LoadWorkspaceAsync(string path)
    {
        WorkspacePath = path;
        StatusText = $"Indexing {Path.GetFileName(path)}…";

        try
        {
            var index = await Task.Run(() => WorkspaceIndex.Build(path));
            _workspaceIndex = index;
            FilePath = null;

            Modules.Clear();
            var root = new TreeNodeViewModel
            {
                Kind = "Workspace",
                Name = Path.GetFileName(path) + $" ({index.Objects.Count} objects)",
                IsExpanded = true,
            };

            foreach (var group in index.Objects
                .GroupBy(o => o.AbsolutePath[..o.AbsolutePath.LastIndexOf('/')])
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var packageNode = new TreeNodeViewModel
                {
                    Kind = "Package",
                    Name = group.Key,
                };
                foreach (var o in group)
                    packageNode.Children.Add(TreeNodeViewModel.FromObject(o));
                root.Children.Add(packageNode);
            }

            Modules.Add(root);

            var summary = index.Summarize();
            StatusText = $"{Path.GetFileName(path)} — {summary.FileCount} file(s), " +
                         $"{summary.IdentifiableCount} objects, {summary.UnresolvedReferenceCount} unresolved";
            AnalysisText = WorkspaceSummaryText(index);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load workspace: {ex.Message}";
        }
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value?.ObjectPath is { } path && _workspaceIndex is not null)
        {
            var trace = _workspaceIndex.Trace(path, TraceDirection.Both, maxDepth: 1);
            AnalysisText = ObjectDetailText(path, trace);
        }
    }

    [RelayCommand]
    private void TraceSelected()
    {
        if (_workspaceIndex is null || SelectedNode?.ObjectPath is not { } path)
        {
            AnalysisText = "Select an object in the workspace tree first.";
            return;
        }

        var trace = _workspaceIndex.Trace(path, TraceDirection.Both, maxDepth: 4);
        var sb = new StringBuilder($"trace from {path} (max 4 hops):\n");
        sb.AppendLine($"  {trace.Root}");
        foreach (var edge in trace.Edges)
            sb.AppendLine($"{new string(' ', (edge.Depth - 1) * 2 + 2)}→ [{edge.Kind}] {edge.TargetPath}");
        AnalysisText = sb.ToString();
    }

    [RelayCommand]
    private void ShowComm()
    {
        if (_workspaceIndex is null)
        {
            AnalysisText = "Open a workspace first.";
            return;
        }

        var model = CommunicationProjection.Build(_workspaceIndex);
        var sb = new StringBuilder("communication projection (structural — no direction claims):\n");
        foreach (var cluster in model.Clusters)
        {
            sb.AppendLine($"{cluster.ClusterPath}");
            sb.AppendLine($"  ecus: {(cluster.ConnectedEcus.Count > 0 ? string.Join(", ", cluster.ConnectedEcus.Select(p => p.Split('/').Last())) : "(none)")}");
            foreach (var framePath in cluster.FramePaths)
            {
                var frame = model.Frames.FirstOrDefault(f => f.FramePath == framePath);
                sb.AppendLine($"  frame {framePath.Split('/').Last()}");
                foreach (var pduPath in frame?.PduPaths ?? [])
                {
                    var pdu = model.Pdus.FirstOrDefault(p => p.PduPath == pduPath);
                    var signals = pdu is { SignalPaths.Count: > 0 }
                        ? string.Join(", ", pdu.SignalPaths.Select(s => s.Split('/').Last()))
                        : "(none)";
                    sb.AppendLine($"    pdu {pduPath.Split('/').Last()}  signals: {signals}");
                }
            }
        }
        sb.AppendLine($"orphans: {model.OrphanFrames.Count} frame(s), {model.OrphanPdus.Count} pdu(s), {model.OrphanSignals.Count} signal(s)");
        if (model.EcucCommModules.Count > 0)
            sb.AppendLine($"ecuc comm modules: {string.Join(", ", model.EcucCommModules)}");
        AnalysisText = sb.ToString();
    }

    [RelayCommand]
    private async Task CompareWithAsync()
    {
        if (_workspaceIndex is null)
        {
            AnalysisText = "Open a workspace first, then pick a second delivery to diff against.";
            return;
        }
        if (PickFolder is null)
            return;

        var other = await PickFolder();
        if (other is null)
            return;

        StatusText = "Computing semantic diff…";
        try
        {
            var diff = await Task.Run(() => SemanticDiff.Compare(WorkspacePath!, other, includePropertyDetail: true));
            var sb = new StringBuilder($"semantic diff: {WorkspacePath}\n            vs {other}\n\n");
            if (diff.IsEmpty)
            {
                sb.AppendLine("no semantic differences");
            }
            else
            {
                foreach (var change in diff.Objects)
                {
                    sb.AppendLine(change.ChangeType switch
                    {
                        ObjectChangeType.Added => $"+ {change.ElementType} {change.AbsolutePath}",
                        ObjectChangeType.Removed => $"- {change.ElementType} {change.AbsolutePath}",
                        _ => $"~ {change.ElementType} {change.AbsolutePath}",
                    });
                    if (change.Properties is { } properties)
                        foreach (var p in properties)
                            sb.AppendLine($"    {p.Path}: {p.Before ?? "(absent)"} → {p.After ?? "(absent)"}");
                }
                foreach (var r in diff.References)
                    sb.AppendLine($"{(r.ChangeType == ObjectChangeType.Added ? '+' : '-')} {r.Kind} {r.SourcePath} -> {r.TargetPath}");
            }
            AnalysisText = sb.ToString();
            StatusText = diff.IsEmpty
                ? "diff: identical"
                : $"diff: {diff.Objects.Count} object change(s), {diff.References.Count} reference change(s)";
        }
        catch (Exception ex)
        {
            StatusText = $"diff failed: {ex.Message}";
        }
    }

    private static string WorkspaceSummaryText(WorkspaceIndex index)
    {
        var summary = index.Summarize();
        var sb = new StringBuilder($"workspace summary\n  files: {summary.FileCount}\n  objects: {summary.IdentifiableCount}\n" +
            $"  references: {summary.ReferenceCount} ({summary.UnresolvedReferenceCount} unresolved)\n");
        if (summary.AutosarRelease is not null)
            sb.AppendLine($"  AUTOSAR release: {summary.AutosarRelease}{(summary.MixedSchema ? " (mixed schemas!)" : "")}");
        sb.AppendLine("\nselect an object to see its references, or use the Analysis menu.");
        return sb.ToString();
    }

    private string ObjectDetailText(string path, TraceResult trace)
    {
        var sb = new StringBuilder($"{path}\n\noutgoing references:\n");
        foreach (var r in _workspaceIndex!.OutgoingOf(path))
            sb.AppendLine($"  [{r.Kind}] {r.TargetPath}{(r.IsResolved ? "" : "  (unresolved)")}");
        sb.AppendLine("\nincoming references:");
        var incoming = _workspaceIndex.IncomingOf(path);
        if (incoming.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var r in incoming)
            sb.AppendLine($"  [{r.Kind}] from {r.SourcePath}");

        sb.AppendLine("\n1-hop trace:");
        foreach (var edge in trace.Edges)
            sb.AppendLine($"  → [{edge.Kind}] {edge.TargetPath}");
        return sb.ToString();
    }
}
