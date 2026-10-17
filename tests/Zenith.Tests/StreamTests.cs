using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Core.IO;
using Zenith.Core.Processes;
using Zenith.Core.Shell;

namespace Zenith.Tests;

public class StreamTests
{
    [Fact]
    public async Task PipeBoundsStorageAndPreservesBinaryAcrossWrapAndEof()
    {
        var pipe = new BytePipe(() => false);
        byte[] expected = Enumerable.Range(0, 160000).Select(i => (byte)i).ToArray();
        var writer = Task.Run(() => { pipe.Write(expected); pipe.CompleteWriter(); });
        Assert.True(SpinWait.SpinUntil(() => pipe.Buffered == BytePipe.Capacity, 3000));
        Assert.False(writer.IsCompleted);
        using var received = new MemoryStream(); byte[] chunk = new byte[997]; int count;
        while ((count = pipe.Read(chunk)) != 0)
        { Assert.InRange(pipe.Buffered, 0, BytePipe.Capacity); received.Write(chunk, 0, count); }
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, received.ToArray()); Assert.Equal(0, pipe.Read(chunk));
    }

    [Fact]
    public async Task ClosingReaderWakesBlockedWriter()
    {
        var pipe = new BytePipe(() => false);
        var writer = Task.Run(() => Assert.Equal(-32, pipe.Write(new byte[20000])));
        Assert.True(SpinWait.SpinUntil(() => pipe.Buffered == BytePipe.Capacity, 3000));
        pipe.CloseReader(); await writer.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CancellationWakesEmptyReaderAndFullWriter()
    {
        using var cancel = new CancellationTokenSource();
        var empty = new BytePipe(() => cancel.IsCancellationRequested);
        var full = new BytePipe(() => cancel.IsCancellationRequested);
        var reader = Task.Run(() => Assert.Equal(-125, empty.Read(new byte[1])));
        var writer = Task.Run(() => Assert.Equal(-125, full.Write(new byte[20000])));
        Assert.True(SpinWait.SpinUntil(() => full.Buffered == BytePipe.Capacity, 3000));
        cancel.Cancel(); await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CanonicalInputPreservesUtf8AcrossSmallReadsAndStickyEof()
    {
        int calls = 0; var input = new LineByteInput(() => ++calls == 1 ? "ü" : null, () => false);
        Assert.Equal(0, input.Read(Array.Empty<byte>())); Assert.Equal(0, calls);
        byte[] one = new byte[1]; using var received = new MemoryStream();
        while (input.Read(one) != 0) { received.WriteByte(one[0]); }
        Assert.Equal("ü\n", Encoding.UTF8.GetString(received.ToArray()));
        Assert.Equal(0, input.Read(one)); Assert.Equal(2, calls);
    }

    [Fact]
    public void CanonicalInputChecksCancellationAfterHostRead()
    {
        bool cancelled = false;
        var input = new LineByteInput(() => { cancelled = true; return null; }, () => cancelled);
        Assert.Equal(-125, input.Read(new byte[1]));
    }

    private sealed class Capture : IOutput
    {
        public string Text = "";
        public void Write(string value) => Text += value;
    }

    [Fact]
    public void TerminalDecoderKeepsSplitUtf8AndPipeKeepsAllBytes()
    {
        var capture = new Capture(); var terminal = new CommandByteOutput(capture);
        terminal.Write(new byte[] { 0xc3 }); Assert.Empty(capture.Text);
        terminal.Write(new byte[] { 0xbc }); terminal.Complete(); Assert.Equal("ü", capture.Text);
        var pipe = new BytePipe(() => false); var output = new CommandByteOutput(new PipeOutput(pipe));
        byte[] bytes = { 0, 255, 27, 91, 51, 49, 109 };
        output.Write(bytes); output.Complete(); pipe.CompleteWriter();
        byte[] read = new byte[bytes.Length]; Assert.Equal(bytes.Length, pipe.Read(read)); Assert.Equal(bytes, read);
    }

    [Fact]
    public void DescriptorsSeparateStderrAndRejectClosedOrOversizedOperations()
    {
        var stdout = new MemoryStream(); var stderr = new MemoryStream();
        var streams = new ProcessStreams(new ShellInput("abc"), (fd, data) => { (fd == 1 ? stdout : stderr).Write(data); return data.Length; }, false, false, true);
        byte[] read = new byte[4]; Assert.Equal(3, streams.Read(read)); Assert.Equal(0, streams.Read(read));
        Assert.Equal(1, streams.Write(1, new byte[] { 65 })); Assert.Equal(1, streams.Write(2, new byte[] { 66 }));
        Assert.Equal(new byte[] {65}, stdout.ToArray()); Assert.Equal(new byte[] {66}, stderr.ToArray());
        Assert.Equal(0, streams.IsTerminal(1)); Assert.Equal(1, streams.IsTerminal(2));
        Assert.Equal(-22, streams.Write(1, new byte[4097])); Assert.Equal(-22, streams.Read(new byte[4097]));
        Assert.Equal(0, streams.Close(2)); Assert.Equal(-9, streams.Write(2, new byte[] { 67 })); Assert.Equal(-9, streams.IsTerminal(2));
        streams.Close(); Assert.Equal(-9, streams.Read(read));
    }

    [Fact]
    public void ExactInputBudgetStillAllowsEofAndOutputBudgetIsShared()
    {
        var streams = new ProcessStreams(new ShellInput(new string('x', 1048576)), (_, bytes) => bytes.Length, false, false, false);
        byte[] chunk = new byte[4096];
        for (int i = 0; i < 256; i++) { Assert.Equal(4096, streams.Read(chunk)); Assert.Equal(4096, streams.Write(i % 2 + 1, chunk)); }
        Assert.Equal(0, streams.Read(chunk)); Assert.Equal(-28, streams.Write(2, new byte[1]));
    }

    [Fact]
    public void NativeStreamCancellationPropagatesAsErrnoWithoutConsumingBudgets()
    {
        bool cancelled = true;
        var pipe = new BytePipe(() => cancelled);
        var streams = new ProcessStreams(pipe, (_, bytes) => pipe.Write(bytes), false, false, false);
        Assert.Equal(-125, streams.Read(new byte[1])); Assert.Equal(-125, streams.Write(1, new byte[] { 7 }));
        cancelled = false;
        Assert.Equal(1, streams.Write(1, new byte[] { 7 }));
        byte[] read = new byte[1]; Assert.Equal(1, streams.Read(read)); Assert.Equal(7, read[0]);
    }

    [Fact]
    public void RedirectionPreservesNativeBytesAndBoundsBufferedOutput()
    {
        byte[] bytes = { 0, 255, 195, 188, 27, 91, 51, 49, 109 };
        Shell.Register(new Command("stream-test-binary", "", "", c => { new CommandByteOutput(c.Out).Write(bytes); return 0; }));
        using var sh = new ShellHarness(); sh.Run("stream-test-binary > binary"); sh.Run("stream-test-binary >> binary");
        Assert.Equal(bytes.Concat(bytes), File.ReadAllBytes(sh.PathOf("binary")));
        var output = new RedirectOutput(); Assert.Throws<IOException>(() => output.Write(new byte[1048577]));
    }

    [Fact]
    public async Task PipelineStreamsBeforeProducerFinishesAndKeepsLastStageState()
    {
        using var first = new ManualResetEventSlim(); using var consumed = new ManualResetEventSlim();
        Shell.Register(new Command("stream-test-produce", "", "", c => { c.Out.Write("first\n"); first.Set(); if (!consumed.Wait(3000)) { throw new IOException("pipeline buffered producer"); } c.Out.Write("last\n"); return 0; }));
        Shell.Register(new Command("stream-test-consume", "", "", c => { Assert.True(first.Wait(3000)); Assert.Equal("first", c.Input!.ReadLine()); consumed.Set(); c.Shell.Set("STREAMED", c.Input.ReadLine()!); return 0; }));
        using var sh = new ShellHarness();
        await Task.Run(() => sh.Run("stream-test-produce | stream-test-consume")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, sh.Shell.LastStatus); Assert.Equal("last\n", sh.Run("echo $STREAMED"));
    }

    [Fact]
    public async Task EarlyConsumerExitAndCancellationLeaveNextCommandUsable()
    {
        Shell.Register(new Command("stream-test-many", "", "", c => { for (int i = 0; i < 10000; i++) { c.WriteLine(new string('x', 4096)); } return 0; }));
        using var sh = new ShellHarness();
        await Task.Run(() => sh.Run("stream-test-many | true")).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, sh.Shell.LastStatus);
        var running = Task.Run(() => sh.Run("stream-test-many | sleep 20"));
        await Task.Delay(100); sh.Shell.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(130, sh.Shell.LastStatus); Assert.Equal("ready\n", sh.Run("echo ready"));
    }

    [Fact]
    public void PipelineProducerStateIsIsolatedAndOversizedPipelineIsRefused()
    {
        using var sh = new ShellHarness();
        Assert.Equal("old\n", sh.Run("X=old; { X=new; echo ignored; } | cat > /dev/null; echo $X"));
        Assert.Contains("8 stages", sh.Run("echo x | cat | cat | cat | cat | cat | cat | cat | cat"));
        Assert.Equal("ok\n", sh.Run("echo ok"));
        Assert.Equal("1\n", sh.Run("false; echo $? | cat"));
    }
}
