using SimpleXray.Core.Parsing;
using SimpleXray.Core.Storage;

namespace SimpleXray.Core.Tests.Storage;

public sealed class ProfileRepositoryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("simplexray-profiles-");
    private readonly AppPaths _paths;

    public ProfileRepositoryTests()
    {
        _paths = AppPaths.Resolve(_root.FullName, _root.FullName, _root.FullName, _ => false);
    }

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void AddOrUpdate_PersistsAndSelects()
    {
        var repo = new ProfileRepository(_paths);
        var added = repo.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls));

        var reloaded = new ProfileRepository(_paths);

        Assert.Single(reloaded.Profiles);
        Assert.Equal(added.Id, reloaded.Selected?.Id);
        Assert.Equal("Нидерланды", reloaded.Selected?.Name);
        Assert.Equal(["h2", "http/1.1"], reloaded.Selected?.Alpn);
    }

    [Fact]
    public void AddOrUpdate_SameServerAndUser_ReplacesInsteadOfDuplicating()
    {
        var repo = new ProfileRepository(_paths);
        var first = repo.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls));

        var second = repo.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls.Replace("mode=auto", "mode=packet-up", StringComparison.Ordinal)));

        Assert.Single(repo.Profiles);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("packet-up", repo.Selected?.XhttpMode);
    }

    [Fact]
    public void Remove_Selected_SelectsAnotherProfile()
    {
        var repo = new ProfileRepository(_paths);
        var a = repo.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls));
        var b = repo.AddOrUpdate(VlessLinkParser.Parse(TestLinks.RealityVision));

        repo.Remove(b.Id);

        Assert.Equal(a.Id, repo.Selected?.Id);
        repo.Remove(a.Id);
        Assert.Null(repo.Selected);
        Assert.Empty(new ProfileRepository(_paths).Profiles);
    }

    [Fact]
    public void Changed_IsRaisedOnSelect()
    {
        var repo = new ProfileRepository(_paths);
        var a = repo.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls));
        repo.AddOrUpdate(VlessLinkParser.Parse(TestLinks.RealityVision));
        var raised = 0;
        repo.Changed += (_, _) => raised++;

        repo.Select(a.Id);
        repo.Select(a.Id);

        Assert.Equal(1, raised);
        Assert.Equal(a.Id, new ProfileRepository(_paths).Selected?.Id);
    }

    [Fact]
    public void CorruptFile_IsSetAsideAndRepositoryStartsEmpty()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_paths.ProfilesFile, "{ not json");

        var repo = new ProfileRepository(_paths);

        Assert.Empty(repo.Profiles);
        Assert.True(File.Exists(_paths.ProfilesFile + ".corrupt"));
    }
}
