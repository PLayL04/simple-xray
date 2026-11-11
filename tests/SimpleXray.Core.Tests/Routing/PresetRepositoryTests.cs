using Microsoft.Extensions.Logging.Abstractions;
using SimpleXray.Core.Models;
using SimpleXray.Core.Routing;
using SimpleXray.Core.Storage;

namespace SimpleXray.Core.Tests.Routing;

public sealed class PresetRepositoryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("simplexray-presets-");
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;

    public PresetRepositoryTests()
    {
        _paths = AppPaths.Resolve(_root.FullName, _root.FullName, _root.FullName, _ => false);
        _settings = new SettingsStore(_paths);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private PresetRepository Create() => new(_paths, new SettingsStore(_paths), NullLogger<PresetRepository>.Instance);

    [Fact]
    public void Selected_DefaultsToBypassRu_AndUnknownIdFallsBack()
    {
        Assert.Equal(BuiltInPresets.BypassRuId, Create().Selected.Id);

        _settings.Update(s => s with { SelectedPresetId = "user-gone" });

        Assert.Equal(BuiltInPresets.BypassRuId, Create().Selected.Id);
    }

    [Fact]
    public void Select_PersistsAndRaisesChanged()
    {
        var repo = Create();
        var raised = 0;
        repo.Changed += (_, _) => raised++;

        repo.Select(BuiltInPresets.GlobalId);
        repo.Select(BuiltInPresets.GlobalId);

        Assert.Equal(1, raised);
        Assert.Equal(BuiltInPresets.GlobalId, Create().Selected.Id);
        Assert.Equal(PresetError.NotFound, Assert.Throws<PresetException>(() => repo.Select("nope")).Error);
    }

    [Fact]
    public void Duplicate_CreatesEditableCopyThatSurvivesRestart()
    {
        var repo = Create();

        var copy = repo.Duplicate(BuiltInPresets.BypassRuId, "Моё");

        Assert.StartsWith("user-", copy.Id, StringComparison.Ordinal);
        Assert.False(copy.BuiltIn);
        Assert.Null(copy.Names);
        Assert.Equal(BuiltInPresets.Default.Rules.Count, copy.Rules.Count);
        Assert.True(File.Exists(Path.Combine(_paths.PresetsDirectory, copy.Id + ".json")));

        var reloaded = Create();
        Assert.Equal(4, reloaded.Presets.Count);
        Assert.Equal("Моё", reloaded.Find(copy.Id)?.Name);
    }

    [Fact]
    public void Save_UpdatesUserPreset_RejectsBuiltInAndInvalid()
    {
        var repo = Create();
        var copy = repo.Duplicate(BuiltInPresets.GlobalId, "Copy");

        repo.Save(copy with { Rules = [.. copy.Rules, new RoutingRule { Domain = ["domain:example.org"], Outbound = RouteOutbound.Block }] });

        Assert.Equal(2, Create().Find(copy.Id)?.Rules.Count);
        Assert.Equal(PresetError.BuiltInReadOnly, Assert.Throws<PresetException>(() => repo.Save(BuiltInPresets.Global)).Error);
        Assert.Equal(PresetError.EmptyRule, Assert.Throws<PresetException>(() => repo.Save(copy with { Rules = [new RoutingRule()] })).Error);
        Assert.Equal(2, Create().Find(copy.Id)?.Rules.Count);
    }

    [Fact]
    public void Delete_SelectedUserPreset_FallsBackToDefault()
    {
        var repo = Create();
        var copy = repo.Duplicate(BuiltInPresets.GlobalId, "Copy");
        repo.Select(copy.Id);

        repo.Delete(copy.Id);

        Assert.Equal(BuiltInPresets.DefaultId, repo.Selected.Id);
        Assert.False(File.Exists(Path.Combine(_paths.PresetsDirectory, copy.Id + ".json")));
        Assert.Equal(PresetError.BuiltInReadOnly, Assert.Throws<PresetException>(() => repo.Delete(BuiltInPresets.GlobalId)).Error);
    }

    [Fact]
    public void Import_ExportedBuiltIn_GetsNewIdAndPlainName()
    {
        var repo = Create();

        var imported = repo.Import(PresetRepository.Export(BuiltInPresets.Default), "https://example.org/p.json");

        Assert.NotEqual(BuiltInPresets.DefaultId, imported.Id);
        Assert.False(string.IsNullOrWhiteSpace(imported.Name));
        Assert.Equal("https://example.org/p.json", imported.SourceUrl);
    }

    [Fact]
    public void ReplaceFromSource_KeepsIdNameAndSource()
    {
        var repo = Create();
        var imported = repo.Import("""{ "name": "Remote", "rules": [ { "domain": ["domain:a.example"] } ] }""", "https://example.org/p.json");
        repo.Save(imported with { Name = "Renamed" });

        var updated = repo.ReplaceFromSource(imported.Id, """{ "name": "Other", "rules": [ { "domain": ["domain:b.example"] }, { "ip": ["geoip:ru"] } ] }""");

        Assert.Equal(imported.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("https://example.org/p.json", updated.SourceUrl);
        Assert.Equal(2, updated.Rules.Count);
    }

    [Fact]
    public void Load_SkipsBrokenFilesAndBuiltInIds()
    {
        Directory.CreateDirectory(_paths.PresetsDirectory);
        File.WriteAllText(Path.Combine(_paths.PresetsDirectory, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(_paths.PresetsDirectory, "global.json"), """{ "name": "Fake" }""");
        File.WriteAllText(Path.Combine(_paths.PresetsDirectory, "hand-made.json"), """{ "id": "ignored", "name": "Hand made" }""");

        var repo = Create();

        Assert.Equal(4, repo.Presets.Count);
        Assert.Equal("Hand made", repo.Find("hand-made")?.Name);
        Assert.Equal("Everything via proxy", repo.Find("global")?.Name);
    }

    [Fact]
    public void Settings_OldFileWithoutNewFields_GetsDefaults()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_paths.SettingsFile, """{ "schemaVersion": 1, "language": "en" }""");

        var settings = new SettingsStore(_paths).Settings;

        Assert.Equal("en", settings.Language);
        Assert.Equal(10888, settings.SocksPort);
        Assert.Equal(ConnectionMode.SystemProxy, settings.Mode);
        Assert.Equal(DnsSettings.DefaultRemoteServer, settings.Dns.RemoteServer);
        Assert.True(settings.Dns.PreferIPv4);
    }
}
