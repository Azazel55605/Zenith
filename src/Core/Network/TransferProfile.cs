using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Zenith.Core.Network.Transport;

namespace Zenith.Core.Network;

/// <summary>Opt-in elapsed time for a single package/recovery download. BSP poll work overlaps the reader.</summary>
internal sealed class TransferProfile
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private int _frames, _accepted, _polls, _polledFrames, _receiveIrqs, _largestPoll, _waitTimeouts;
    private TcpRejectCounts _rejections;
    private static TransferProfile? s_pollObserver;
    private long _pollTicks;

    public long SetupTicks, ReceiveTicks, WaitTicks, TlsTicks, WriteTicks, CommitTicks;
    public int Reads, Waits;

    public static bool Requested(IReadOnlyDictionary<string, string> environment) =>
        environment.TryGetValue("ZENITH_TRANSFER_PROFILE", out string? value) && value == "1";

    public long Mark() => Stopwatch.GetTimestamp();
    public static long Since(long mark) => Stopwatch.GetTimestamp() - mark;

    public void ObservePolls()
    {
        _frames = EthernetIngress.Received;
        _accepted = EthernetIngress.Accepted;
        _polls = E1000ReceiveFilter.Polls;
        _polledFrames = E1000ReceiveFilter.PolledFrames;
        _receiveIrqs = E1000ReceiveFilter.ReceiveInterrupts;
        _rejections = TcpSegmentPolicy.Counters();
        Volatile.Write(ref s_pollObserver, this);
    }

    public void StopObserving()
    {
        if (Interlocked.CompareExchange(ref s_pollObserver, null, this) != this) { return; }
        _frames = unchecked(EthernetIngress.Received - _frames);
        _accepted = unchecked(EthernetIngress.Accepted - _accepted);
        _polls = unchecked(E1000ReceiveFilter.Polls - _polls);
        _polledFrames = unchecked(E1000ReceiveFilter.PolledFrames - _polledFrames);
        _receiveIrqs = unchecked(E1000ReceiveFilter.ReceiveInterrupts - _receiveIrqs);
        _rejections = TcpSegmentPolicy.Counters().Since(_rejections);
    }
    public static TransferProfile? PollObserver => Volatile.Read(ref s_pollObserver);
    public void AddPoll(long mark, int frames)
    {
        Interlocked.Add(ref _pollTicks, Since(mark));
        // Only the BSP writes this high-water mark.
        if (frames > _largestPoll) { Volatile.Write(ref _largestPoll, frames); }
    }

    public void AddReceive(long mark)
    {
        ReceiveTicks += Since(mark);
        Reads++;
    }

    public void AddWait(long mark, bool signalled)
    {
        WaitTicks += Since(mark);
        if (!signalled) { _waitTimeouts++; }
        Waits++;
    }

    private static long Milliseconds(long ticks) => ticks * 1000 / Stopwatch.Frequency;

    public string Summary(int bytes, int attempt) =>
        "fetch timing bytes=" + bytes + " attempt=" + attempt + " total-ms=" + Milliseconds(Since(_start)) +
        " setup-ms=" + Milliseconds(SetupTicks) + " receive-ms=" + Milliseconds(ReceiveTicks) +
        " wait-ms=" + Milliseconds(WaitTicks) + " tls-record-ms=" + Milliseconds(TlsTicks) +
        " write-ms=" + Milliseconds(WriteTicks) + " commit-ms=" + Milliseconds(CommitTicks) +
        " polls-ms=" + Milliseconds(Interlocked.Read(ref _pollTicks)) + " polls=" + _polls +
        " frames=" + _frames + " frames-accepted=" + _accepted + " polled-frames=" + _polledFrames +
        " peak-poll-frames=" + Volatile.Read(ref _largestPoll) + " rx-irqs=" + _receiveIrqs +
        " " + _rejections.Fields() + " reads=" + Reads + " wait-timeouts=" + _waitTimeouts + " waits=" + Waits;
}
