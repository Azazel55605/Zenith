using System;
using System.IO;
using Zenith.Core.Files;

namespace Zenith.Tests;

public sealed class FileBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-files-" + Guid.NewGuid());
    private readonly FileBrowser _browser = new();
    public FileBrowserTests() { Directory.CreateDirectory(_root); _browser.Navigate(_root); }
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void CreateRenameCopyMoveAndDeletePreserveDataAndRejectOverwrites()
    {
        string source = _browser.Create("source", false); File.WriteAllText(source, "content");
        string renamed = _browser.Transfer(source, "renamed", true);
        Assert.False(File.Exists(source));
        string copied = _browser.Transfer(renamed, "copied", false);
        Assert.Equal("content", File.ReadAllText(copied));
        Assert.Throws<IOException>(() => _browser.Transfer(renamed, "copied", false));
        string folder = _browser.Create("folder", true); _browser.Navigate(folder);
        string moved = _browser.Transfer(copied, "moved", true);
        Assert.False(File.Exists(copied)); Assert.Equal("content", File.ReadAllText(moved));
        _browser.Delete(moved); _browser.Up(); _browser.Delete(folder);
        Assert.Single(_browser.Entries); Assert.Equal("renamed", _browser.Entries[0].Name);
    }

    [Theory]
    [InlineData("")] [InlineData("..")] [InlineData(".")]
    [InlineData("../escape")] [InlineData("x/y")] [InlineData("x\\y")] [InlineData("x\n")]
    public void InvalidNamesCannotEscapeDirectory(string name)
    {
        Assert.Throws<IOException>(() => _browser.Create(name, false));
        Assert.Empty(_browser.Entries);
    }

    [Fact]
    public void FailedNavigationKeepsCurrentListingAndDirectoriesSortFirst()
    {
        _browser.Create("z-folder", true); _browser.Create("a-file", false);
        Assert.True(_browser.Entries[0].Directory);
        Assert.Throws<DirectoryNotFoundException>(() => _browser.Navigate("missing"));
        Assert.Equal(_root, _browser.CurrentPath); Assert.Equal(2, _browser.Entries.Count);
        Assert.Equal("/a/c", FileBrowser.Normalize("../c", "/a/b"));
        Assert.Equal("/", FileBrowser.Normalize("../../../", "/a"));
    }

    [Fact]
    public void NonemptyFolderDeletionAndRecursiveFolderMoveAreRejected()
    {
        string folder = _browser.Create("folder", true); File.WriteAllText(folder + "/inside", "keep");
        Assert.Throws<IOException>(() => _browser.Delete(folder));
        _browser.Navigate(folder);
        Assert.Throws<IOException>(() => _browser.Transfer(folder, "child", true));
        Assert.Equal("keep", File.ReadAllText(folder + "/inside"));
    }

    [Fact]
    public void FoldersCanBeRenamedButNotCopiedOrMovedAcrossParents()
    {
        string folder = _browser.Create("folder", true);
        File.WriteAllText(folder + "/inside", "keep");
        Assert.Throws<IOException>(() => _browser.Transfer(folder, "copy", false));
        string renamed = _browser.Transfer(folder, "renamed", true);
        string destination = _browser.Create("destination", true);
        _browser.Navigate(destination);
        Assert.Throws<IOException>(() => _browser.Transfer(renamed, "moved", true));
        Assert.Equal("keep", File.ReadAllText(renamed + "/inside"));
        Assert.Empty(_browser.Entries);
    }

    [Fact]
    public void LargeAndLinkedFilesAreNotOpenedOrCopied()
    {
        string large = _browser.Create("large", false);
        using (var file = File.OpenWrite(large)) file.SetLength(8 * 1024 * 1024 + 1);
        Assert.Throws<IOException>(() => FileBrowser.RequireEditable(large));
        Assert.Throws<IOException>(() => _browser.Transfer(large, "copy", false));
        Assert.False(File.Exists(_root + "/copy"));
        string link = _root + "/link"; File.CreateSymbolicLink(link, large);
        Assert.Throws<IOException>(() => FileBrowser.RequireEditable(link));
        Assert.Throws<IOException>(() => _browser.Transfer(link, "copy", false));
    }
}
