using System;
using System.IO;
using Zenith.Core.Shell;

namespace Zenith.Tests;

/// <summary>A shell rooted in a fresh temp directory, with its output captured as plain text.</summary>
public sealed class ShellHarness : IDisposable
{
    private readonly BufferCapture _output = new();

    public ShellHarness() : this(null) { }

    internal ShellHarness(IFileMetadata? metadata)
    {
        Root = Path.Combine(Path.GetTempPath(), "zenith-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Shell = new Shell(_output, login: false, metadata: metadata);
        Assert.True(Shell.ChangeDirectory(Root));
    }

    public string Root { get; }

    internal Shell Shell { get; }

    /// <summary>Runs one command line and returns what it printed, ANSI colors removed.</summary>
    public string Run(string line)
    {
        _output.Clear();
        Shell.Execute(line);
        return Ansi.Strip(_output.Text);
    }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private sealed class BufferCapture : IOutput
    {
        public string Text { get; private set; } = string.Empty;

        public void Write(string text) => Text += text;

        public void Clear() => Text = string.Empty;
    }
}
