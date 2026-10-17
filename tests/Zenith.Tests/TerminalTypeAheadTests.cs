using System.Collections.Generic;
using Zenith.Core.IO;

namespace Zenith.Tests;

public class TerminalTypeAheadTests
{
    [Fact]
    public void LineAndEofTypedBeforeReadArrivesAreDeliveredInOrder()
    {
        var input = new TerminalTypeAhead();
        input.Enqueue("typed ahead"); input.EndInput();
        Assert.False(input.TryAnswer(_ => false));
        var replies = new List<string?>();
        Assert.True(input.TryAnswer(value => { replies.Add(value); return true; }));
        Assert.True(input.TryAnswer(value => { replies.Add(value); return true; }));
        Assert.Equal(new string?[] { "typed ahead", null }, replies);
    }

    [Fact]
    public void EofWaitsUntilTheWorkerConsumesItsPreviousLine()
    {
        var input = new TerminalTypeAhead();
        input.Enqueue("first");
        string? reply = null; bool pending = true, eof = false;
        bool Answer(string? value)
        {
            if (!pending || reply is not null || eof) { return false; }
            reply = value; eof = value is null; return true;
        }
        Assert.True(input.TryAnswer(Answer));
        input.EndInput();
        Assert.False(input.TryAnswer(Answer));
        Assert.Equal("first", reply); Assert.False(eof);
        pending = false; reply = null;
        Assert.False(input.TryAnswer(Answer));
        pending = true;
        Assert.True(input.TryAnswer(Answer)); Assert.True(eof);
    }

    [Fact]
    public void CompletedJobDropsItsEofButRetainsTheNextCommand()
    {
        var input = new TerminalTypeAhead();
        input.EndInput(); input.Enqueue("echo next");
        Assert.True(input.TryTakeCommand(out string command));
        Assert.Equal("echo next", command);
        Assert.False(input.TryTakeCommand(out _));
    }

    [Fact]
    public void CancelOrSecretPreparationClearsQueuedLinesAndEof()
    {
        var input = new TerminalTypeAhead();
        input.Enqueue("public type-ahead"); input.EndInput(); input.Clear();
        bool called = false;
        Assert.False(input.TryAnswer(_ => { called = true; return true; }));
        Assert.False(called); Assert.False(input.TryTakeCommand(out _));
        input.Enqueue("new input");
        Assert.True(input.TryAnswer(value => value == "new input"));
    }
}
