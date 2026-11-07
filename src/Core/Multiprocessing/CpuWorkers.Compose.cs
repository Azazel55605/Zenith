using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zenith.Core.Multiprocessing;

internal static partial class CpuWorkers
{
    [DllImport("*", EntryPoint = "zenith_cpu_compose_submit"), SuppressGCTransition]
    private static extern unsafe ulong SubmitCompose(int* foreground, ulong foregroundStride,
        int* background, ulong backgroundStride, ulong width, ulong height);
    private static int[]? s_composeOutput;
    private static long s_composeFrames, s_composeTiles, s_composeFallbacks;
    private static bool s_composeFailed;
    internal static string ComposeStatus => "parallel compose frames=" + s_composeFrames
        + " ap-tiles=" + s_composeTiles + " fallbacks=" + s_composeFallbacks + " quarantined=" + s_composeFailed;

    // One retained output for the active production consumer. No target pixels
    // are published until every owned tile is complete, so fallback never blends
    // the foreground twice. The queue gate is shared with the blur consumer.
    internal static unsafe bool TryCompose(int[] foreground, int foregroundOffset, int foregroundStride,
        int[] background, int backgroundOffset, int backgroundStride, int width, int height)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !ParallelBlur || s_count < 2 || s_stopping || s_composeFailed
            || width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height < 131072
            || (long)width * height > 32 * 1024 * 1024 || foregroundStride < width || backgroundStride < width
            || foregroundStride > 16384 || backgroundStride > 16384 || foregroundOffset < 0 || backgroundOffset < 0
            || (long)foregroundOffset + (long)(height - 1) * foregroundStride + width > foreground.Length
            || (long)backgroundOffset + (long)(height - 1) * backgroundStride + width > background.Length
            || ReferenceEquals(foreground, background)) { return false; }
        if (!Monitor.TryEnter(s_gate)) { s_composeFallbacks++; return false; }
        try
        {
            int area = width * height;
            if (s_composeOutput is null || s_composeOutput.Length < area)
            {
                if (Cosmos.Kernel.System.Diagnostics.MemoryInfo.FreePages < (ulong)area * 4 / 4096 + 8192)
                { s_composeFallbacks++; Monitor.Exit(s_gate); return false; }
                s_composeOutput = new int[area];
            }
            s_blurResult ??= new int[BoxBlur.TileWidth * BoxBlur.TileHeight];
            int columns = (width + BoxBlur.TileWidth - 1) / BoxBlur.TileWidth;
            int tiles = columns * ((height + BoxBlur.TileHeight - 1) / BoxBlur.TileHeight), tile = 0;
            while (tile < tiles)
            {
                int admitted = 0;
                for (; admitted < s_count - 1 && tile + 1 < tiles; admitted++, tile++)
                {
                    BlurBounds(tile, admitted, columns, width, height);
                    int x = s_blurX[admitted], y = s_blurY[admitted]; ulong token;
                    fixed (int* fg = &foreground[foregroundOffset + y * foregroundStride + x])
                    fixed (int* bg = &background[backgroundOffset + y * backgroundStride + x])
                        token = SubmitCompose(fg, (ulong)foregroundStride, bg, (ulong)backgroundStride,
                            (ulong)s_blurW[admitted], (ulong)s_blurH[admitted]);
                    if (token == 0) { break; }
                    s_blurTokens[admitted] = token;
                }
                if (admitted == 0 && tile == 0) { s_composeFallbacks++; Monitor.Exit(s_gate); return false; }
                BlurBounds(tile++, 7, columns, width, height);
                for (int y = s_blurY[7]; y < s_blurY[7] + s_blurH[7]; y++)
                for (int x = s_blurX[7]; x < s_blurX[7] + s_blurW[7]; x++)
                {
                    int f = foreground[foregroundOffset + y * foregroundStride + x], b = background[backgroundOffset + y * backgroundStride + x];
                    int alpha = (int)((uint)f >> 24); uint color = 0xff000000;
                    if (alpha == 255) { s_composeOutput[y * width + x] = f; continue; }
                    if (alpha == 0) { s_composeOutput[y * width + x] = b; continue; }
                    for (int shift = 0; shift < 24; shift += 8)
                        color |= (uint)(((f >> shift) & 255) + (((b >> shift) & 255) * (255 - alpha) + 127) / 255) << shift;
                    s_composeOutput[y * width + x] = (int)color;
                }
                for (int i = 0; i < admitted; i++)
                {
                    ulong token = s_blurTokens[i]; long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    while (Job(token, 0) is 1 or 2 && Stopwatch.GetTimestamp() < deadline)
                    { ManagedCpuJobs.PumpRequests(); if (WaitInterrupt(token) == 0) { Thread.Sleep(1); } }
                    bool copied;
                    fixed (int* output = s_blurResult) copied = CopyBlur(token, output, (ulong)(s_blurW[i] * s_blurH[i])) == 1;
                    if (!copied) { s_composeFailed = true; s_composeFallbacks++; CancelOutstanding(); Monitor.Exit(s_gate); return false; }
                    CommitBlur(s_composeOutput, 0, width, i); s_composeTiles++;
                    Require(Control(token, 2) == 1, "complete compose tile ownership lost"); s_blurTokens[i] = 0;
                }
            }
            for (int row = 0; row < height; row++)
                Array.Copy(s_composeOutput, row * width, background, backgroundOffset + row * backgroundStride, width);
            s_composeFrames++; Monitor.Exit(s_gate); return true;
        }
        catch { s_composeFailed = true; s_composeFallbacks++; CancelOutstanding(); Monitor.Exit(s_gate); return false; }
    }
}
