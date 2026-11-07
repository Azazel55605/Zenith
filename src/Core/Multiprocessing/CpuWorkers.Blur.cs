using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Zenith.Core.Shell;

namespace Zenith.Core.Multiprocessing;

internal static partial class CpuWorkers
{
    [DllImport("*", EntryPoint = "zenith_cpu_blur_submit"), SuppressGCTransition]
    private static extern unsafe ulong SubmitBlur(int* source, ulong width, ulong height, ulong x, ulong y, ulong w, ulong h);
    [DllImport("*", EntryPoint = "zenith_cpu_blur_copy"), SuppressGCTransition]
    private static extern unsafe ulong CopyBlur(ulong token, int* target, ulong length);

    // One BSP-owned production consumer at a time. Buffers/tokens are reused;
    // no AP receives a scene, Surface, managed reference, credentials or delegate.
    private static readonly ulong[] s_blurTokens = new ulong[8];
    private static readonly int[] s_blurX = new int[8], s_blurY = new int[8], s_blurW = new int[8], s_blurH = new int[8];
    private static int[]? s_blurScratch, s_blurResult;
    private static volatile bool s_parallelBlur = true;
    private static int s_blurRevision;
    internal static bool ParallelBlur
    {
        get => s_parallelBlur;
        set { s_parallelBlur = value; Interlocked.Increment(ref s_blurRevision); }
    }
    internal static int BlurRevision => s_blurRevision;
    internal static long BlurTiles => s_blurTiles;
    private static bool s_blurFailed;
    private static long s_blurFrames, s_blurTiles, s_blurFallbacks;
    internal static string BlurStatus => "parallel blur=" + ParallelBlur + " frames=" + s_blurFrames
        + " ap-tiles=" + s_blurTiles + " fallbacks=" + s_blurFallbacks + " quarantined=" + s_blurFailed + "; " + ComposeStatus;

    /// <summary>Pure radius-four computation over immutable snapshots. The BSP
    /// publishes no filtered region until this method returns success; on a
    /// refused/cancelled batch the caller overwrites it with the scalar filter.
    /// Busy producers and small/unsupported regions never wait for admission.</summary>
    internal static unsafe bool TryBlur(int[] source, int[] target, int offset, int width, int height)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !ParallelBlur || s_count < 2 || s_stopping || s_blurFailed
            || width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height < 16384
            || offset < 0 || (long)offset + (long)width * height > source.Length
            || (long)offset + (long)width * height > target.Length || ReferenceEquals(source, target)) { return false; }
        if (!Monitor.TryEnter(s_gate)) { s_blurFallbacks++; return false; }
        try
        {
            s_blurScratch ??= new int[BoxBlur.TileWidth * (BoxBlur.TileHeight + 8)];
            s_blurResult ??= new int[BoxBlur.TileWidth * BoxBlur.TileHeight];
            int columns = (width + BoxBlur.TileWidth - 1) / BoxBlur.TileWidth;
            int tiles = columns * ((height + BoxBlur.TileHeight - 1) / BoxBlur.TileHeight);
            if (tiles < 2) { Monitor.Exit(s_gate); return false; }
            int tile = 0;
            while (tile < tiles)
            {
                int admitted = 0;
                // Leave one tile for the BSP while the selected idle APs work.
                for (; admitted < s_count - 1 && tile + 1 < tiles; admitted++, tile++)
                {
                    BlurBounds(tile, admitted, columns, width, height);
                    ulong token;
                    fixed (int* p = &source[offset])
                        token = SubmitBlur(p, (ulong)width, (ulong)height, (ulong)s_blurX[admitted], (ulong)s_blurY[admitted], (ulong)s_blurW[admitted], (ulong)s_blurH[admitted]);
                    if (token == 0) { break; }
                    s_blurTokens[admitted] = token;
                }
                if (admitted == 0 && tile == 0)
                { s_blurFallbacks++; Monitor.Exit(s_gate); return false; }
                BlurBounds(tile++, 7, columns, width, height);
                BoxBlur.Tile(source, offset, width, height, s_blurX[7], s_blurY[7], s_blurW[7], s_blurH[7], s_blurScratch, s_blurResult);
                CommitBlur(target, offset, width, 7);
                for (int i = 0; i < admitted; i++)
                {
                    ulong token = s_blurTokens[i];
                    long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    while (Job(token, 0) is 1 or 2 && Stopwatch.GetTimestamp() < deadline)
                    {
                        ManagedCpuJobs.PumpRequests();
                        if (WaitInterrupt(token) == 0) { Thread.Sleep(1); }
                    }
                    bool copied;
                    fixed (int* p = s_blurResult) copied = CopyBlur(token, p, (ulong)(s_blurW[i] * s_blurH[i])) == 1;
                    if (!copied) { s_blurFailed = true; s_blurFallbacks++; CancelOutstanding(); Monitor.Exit(s_gate); return false; }
                    CommitBlur(target, offset, width, i); s_blurTiles++;
                    Require(Control(token, 2) == 1, "complete blur tile ownership lost"); s_blurTokens[i] = 0;
                }
            }
            s_blurFrames++;
            Monitor.Exit(s_gate); return true;

        }
        catch
        {
            s_blurFailed = true; s_blurFallbacks++; CancelOutstanding(); Monitor.Exit(s_gate); return false;
        }
    }

    private static void BlurBounds(int index, int slot, int columns, int width, int height)
    {
        int x = index % columns * BoxBlur.TileWidth, y = index / columns * BoxBlur.TileHeight;
        s_blurX[slot] = x; s_blurY[slot] = y;
        s_blurW[slot] = Math.Min(BoxBlur.TileWidth, width - x); s_blurH[slot] = Math.Min(BoxBlur.TileHeight, height - y);
    }

    private static void CommitBlur(int[] target, int offset, int width, int slot)
    {
        int w = s_blurW[slot], h = s_blurH[slot];
        for (int row = 0; row < h; row++)
            Array.Copy(s_blurResult!, row * w, target, offset + (s_blurY[slot] + row) * width + s_blurX[slot], w);
    }

    private static void CancelOutstanding()
    {
        // A nonterminal/quarantined slot stays reserved. Never free/reassign its
        // snapshot while an AP might still be writing it, and never replay work.
        for (int i = 0; i < s_blurTokens.Length; i++)
        {
            ulong token = s_blurTokens[i]; if (token == 0) { continue; }
            Control(token, 0);
            if (Job(token, 0) >= 3) { Control(token, 2); }
            s_blurTokens[i] = 0;
        }
    }

    private static void BlurTest(CommandContext c)
    {
        const int width = 1021, height = 509, offset = 7, area = width * height;
        int[] source = new int[area + 14], scratch = new int[source.Length], scalar = new int[source.Length], parallel = new int[source.Length];
        uint value = 17;
        for (int i = 0; i < source.Length; i++) { value = value * 1664525 + 1013904223; source[i] = (int)value; }
        Array.Fill(parallel, 0x12345678);
        // Equal full-sized warm-up, outside measured dispatch/filter/copy time.
        BoxBlur.Pass(source, scratch, offset, width, height, true); BoxBlur.Pass(scratch, scalar, offset, width, height, false);
        bool supported = s_count > 1 && ParallelBlur;
        if (supported) { Require(TryBlur(source, parallel, offset, width, height), "blur warm-up refused"); }
        long[] scalarTimes = new long[5], parallelTimes = new long[5];
        long tiles = s_blurTiles;
        bool ran = false;
        for (int sample = 0; sample < scalarTimes.Length; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            BoxBlur.Pass(source, scratch, offset, width, height, true); BoxBlur.Pass(scratch, scalar, offset, width, height, false);
            scalarTimes[sample] = (Stopwatch.GetTimestamp() - start) * 1000000 / Stopwatch.Frequency;
            start = Stopwatch.GetTimestamp();
            ran = TryBlur(source, parallel, offset, width, height);
            parallelTimes[sample] = (Stopwatch.GetTimestamp() - start) * 1000000 / Stopwatch.Frequency;
            Require(ran == supported, "blur sample admission boundary");
        }
        Array.Sort(scalarTimes); Array.Sort(parallelTimes);
        long scalarTime = scalarTimes[2], parallelTime = parallelTimes[2];
        Require(ran == supported, "blur admission boundary");
        if (!ran) { Array.Copy(scalar, offset, parallel, offset, area); }
        ulong hash = 0xcbf29ce484222325UL;
        for (int i = 0; i < area; i++)
        {
            Require(scalar[offset + i] == parallel[offset + i], "blur pixel/seam mismatch");
            hash = (hash ^ (uint)parallel[offset + i]) * 0x100000001b3UL;
        }
        for (int i = 0; i < offset; i++) { Require(parallel[i] == 0x12345678 && parallel[offset + area + i] == 0x12345678, "blur region escape"); }
        c.WriteLine("material blur verified; cpus=" + s_count + " scalar-us=" + scalarTime + " parallel-us=" + parallelTime
            + " ap-tiles=" + (s_blurTiles - tiles) + " hash=" + hash.ToString("x"));
        c.WriteLine(BlurStatus);
    }
}
