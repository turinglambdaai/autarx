using Autarx.Core.Diff;
using Autarx.Core.Index;
using Autarx.Core.Parsing;
using Autarx.Core.Patch;
using Xunit;

namespace Autarx.Tests;

public class PatchEngineTests : IDisposable
{
    private readonly string _workspace;

    public PatchEngineTests()
    {
        // apply/undo mutate the workspace, so every test runs on a copy
        _workspace = Path.Combine(Path.GetTempPath(), "autarx-patch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        foreach (var source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DiffBefore"), "*.arxml"))
            File.Copy(source, Path.Combine(_workspace, Path.GetFileName(source)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    private static IReadOnlyList<PatchOp> Ops(params PatchOp[] ops) => ops;

    [Fact]
    public void Plan_set_parameter_produces_valid_plan_with_property_diff()
    {
        var plan = PatchEngine.Plan(_workspace, Ops(
            new SetParameterOp("/Config/Diag", "/AURIX2G/Diag/DiagGeneral/DiagParamA", "5")));

        Assert.True(plan.Valid);
        Assert.Equal(["config.arxml"], plan.AffectedFiles);
        Assert.NotNull(plan.Diff);

        var diag = plan.Diff.Objects.Single(o => o.AbsolutePath == "/Config/Diag");
        var change = Assert.Single(diag.Properties!, p => p.Path.EndsWith("/DiagParamA/VALUE"));
        Assert.Equal("1", change.Before);
        Assert.Equal("5", change.After);
    }

    [Fact]
    public void Plan_rename_rewrites_incoming_references()
    {
        var plan = PatchEngine.Plan(_workspace, Ops(
            new RenameOp("/Vehicle/Ecus/Legacy", "LegacyNew")));

        Assert.True(plan.Valid);

        // the rename itself is remove+add under path identity, and the
        // VALUE-REF pointing at Legacy must follow
        Assert.Contains(plan.Diff!.References, r =>
            r.ChangeType == ObjectChangeType.Added
            && r.SourcePath == "/Config/Diag"
            && r.TargetPath == "/Vehicle/Ecus/LegacyNew");
        Assert.Contains(plan.Diff.References, r =>
            r.ChangeType == ObjectChangeType.Removed
            && r.SourcePath == "/Config/Diag"
            && r.TargetPath == "/Vehicle/Ecus/Legacy");
    }

    [Fact]
    public void Plan_with_unknown_target_is_invalid_and_diffless()
    {
        var plan = PatchEngine.Plan(_workspace, Ops(
            new SetParameterOp("/Config/DoesNotExist", "/AURIX2G/Diag/DiagGeneral/DiagParamA", "5")));

        Assert.False(plan.Valid);
        Assert.Null(plan.Diff);
        Assert.False(plan.Operations[0].Ok);
        Assert.Contains("not found", plan.Operations[0].Error);
    }

    [Fact]
    public void Apply_writes_files_and_undo_restores_them()
    {
        var plan = PatchEngine.Plan(_workspace, Ops(
            new SetParameterOp("/Config/Diag", "/AURIX2G/Diag/DiagGeneral/DiagParamA", "7"),
            new SetReferenceOp("/Config/Diag", "/AURIX2G/Diag/DiagGeneral/DiagTesterRef", "/Vehicle/Clusters/VehicleCan")));
        Assert.True(plan.Valid);

        PatchEngine.Apply(_workspace, plan, plan.TempWorkspace!);

        var configPath = Path.Combine(_workspace, "config.arxml");
        var backupPath = Path.Combine(_workspace, ".autarx", "config.arxml.autarx-bak");
        Assert.True(File.Exists(backupPath));

        // the written file must re-parse and carry the new values
        var applied = ArxmlParser.ParseFile(configPath);
        Assert.Contains(applied.Descendants(), e => e.Name == "VALUE" && e.Text == "7");
        Assert.Contains(applied.Descendants(), e => e.Name == "VALUE-REF" && e.Text == "/Vehicle/Clusters/VehicleCan");

        var manifest = PatchEngine.Undo(_workspace);
        Assert.NotNull(manifest);
        Assert.False(File.Exists(backupPath));

        var restored = ArxmlParser.ParseFile(configPath);
        Assert.Contains(restored.Descendants(), e => e.Name == "VALUE" && e.Text == "1");
        Assert.Contains(restored.Descendants(), e => e.Name == "VALUE-REF" && e.Text == "/Vehicle/Ecus/Legacy");

        // second undo finds nothing
        Assert.Null(PatchEngine.Undo(_workspace));
    }

    [Fact]
    public void Canonical_roundtrip_preserves_semantics()
    {
        var source = Path.Combine(_workspace, "system.arxml");
        var rewritten = Path.Combine(_workspace, "rewritten.arxml");

        ArxmlWriter.WriteFile(ArxmlParser.ParseFile(source), rewritten);

        var before = WorkspaceIndex.Build(source);
        var after = WorkspaceIndex.Build(rewritten);

        Assert.Equal(before.Objects.Count, after.Objects.Count);
        Assert.Equal(
            before.Objects.Select(o => (o.AbsolutePath, o.ContentHash)),
            after.Objects.Select(o => (o.AbsolutePath, o.ContentHash)));
        Assert.Equal(before.References.Count, after.References.Count);
    }

    [Fact]
    public void Op_set_parse_rejects_incomplete_input()
    {
        var (ops, errors) = PatchOpSet.Parse("""{ "operations": [ { "op": "rename" } ] }""");
        Assert.Empty(ops);
        Assert.Single(errors);

        (ops, errors) = PatchOpSet.Parse("not json");
        Assert.Empty(ops);
        Assert.Single(errors);

        (ops, errors) = PatchOpSet.Parse(
            $$"""{ "operations": [ { "op": "rename", "target": "/A/B", "newName": "C" }, { "op": "set-parameter", "target": "/A/B", "definition": "/D/E", "value": "1" } ] }""");
        Assert.Empty(errors);
        Assert.Equal(2, ops.Count);
    }
}
