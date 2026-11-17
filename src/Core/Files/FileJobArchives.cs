using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Zenith.Core.Files;

/// <summary>Archives as transfer jobs (Horizon HZ4): Extract unpacks .zip, .tar, .tar.gz/.tgz and .gz into a new
/// folder beside them; Compress writes .zip, .tar or .tar.gz. Same identity, gate, progress and Cancel as copies.
/// Entry names are checked: no absolute paths, no "..", no control characters; links and devices inside archives
/// are skipped. Zip64, encryption and methods other than stored/deflate are refused per entry.</summary>
internal sealed partial class FileJob
{
    public const long MaximumExtracted = 64L << 30;
    internal enum ArchiveKind { None, Zip, Tar, TarGz, Gz }

    public static ArchiveKind KindOf(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.EndsWith(".zip")) { return ArchiveKind.Zip; }
        if (n.EndsWith(".tar.gz") || n.EndsWith(".tgz")) { return ArchiveKind.TarGz; }
        if (n.EndsWith(".tar")) { return ArchiveKind.Tar; }
        if (n.EndsWith(".gz")) { return ArchiveKind.Gz; }
        return ArchiveKind.None;
    }
    /// <summary>"photos.tar.gz" → "photos".</summary>
    public static string StemOf(string name)
    {
        string n = name.ToLowerInvariant();
        foreach (string x in new[] { ".tar.gz", ".tgz", ".zip", ".tar", ".gz" }) { if (n.EndsWith(x) && name.Length > x.Length) { return name[..^x.Length]; } }
        return name + " (extracted)";
    }

    /// <summary>A name inside an archive as a safe relative path, or null.</summary>
    public static string? SafeEntry(string name)
    {
        var parts = new List<string>();
        foreach (string part in name.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") { continue; }
            if (part == ".." || part.Length > 255) { return null; }
            foreach (char c in part) { if (c < ' ' || c == '\u007f') { return null; } }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    /// <summary>Every read and write takes the job's gate for that one call (never across a whole file).</summary>
    private sealed class GatedStream : Stream
    {
        private readonly Stream _inner; private readonly object _gate;
        public GatedStream(Stream inner, object gate) { _inner = inner; _gate = gate; }
        private T Locked<T>(Func<T> step) { Monitor.Enter(_gate); T r; try { r = step(); } catch { Monitor.Exit(_gate); throw; } Monitor.Exit(_gate); return r; }
        public override int Read(byte[] buffer, int offset, int count) => Locked(() => _inner.Read(buffer, offset, count));
        public override void Write(byte[] buffer, int offset, int count) => Locked(() => { _inner.Write(buffer, offset, count); return 0; });
        public override long Seek(long offset, SeekOrigin origin) => Locked(() => _inner.Seek(offset, origin));
        public override void Flush() => Locked(() => { _inner.Flush(); return 0; });
        public override bool CanRead => _inner.CanRead; public override bool CanSeek => _inner.CanSeek; public override bool CanWrite => _inner.CanWrite;
        public override long Length => Locked(() => _inner.Length);
        public override long Position { get => Locked(() => _inner.Position); set => Locked(() => _inner.Position = value); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public void Close(bool _) { Locked(() => { _inner.Dispose(); return 0; }); }
    }
    /// <summary>At most <c>left</c> bytes of a stream (one zip entry's data).</summary>
    private sealed class Slice : Stream
    {
        private readonly Stream _inner; private long _left;
        public Slice(Stream inner, long length) { _inner = inner; _left = length; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) { return 0; }
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _left)); _left -= Math.Max(n, 0); return n;
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ---------- Extract.
    private string _extractRoot = "";
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);
    private long _extracted;

    private void Extract(string archive)
    {
        if (_cancelled) { return; }
        Set(() => _current = Leaf(archive));
        var kind = KindOf(Leaf(archive));
        if (kind == ArchiveKind.None) { Error(archive, new IOException("it is not an archive Zenith can open")); return; }
        GatedStream? input = null; long before = _bytesDoneSnapshot();
        try
        {
            input = new GatedStream(Locked(() => _store.OpenRead(archive)), _gate);
            if (kind == ArchiveKind.Gz) { ExtractGz(archive, input); }
            else
            {
                _extractRoot = Unused(Join(_destination, StemOf(Leaf(archive))));
                Locked(() => _store.CreateDirectory(_extractRoot)); _made.Clear(); _made.Add(_extractRoot);
                if (kind == ArchiveKind.Zip) { ExtractZip(input); }
                else
                {
                    var tar = new TarReader(this);
                    if (kind == ArchiveKind.Tar)
                    {
                        var chunk = new byte[Chunk]; int n;
                        while (!_cancelled && (n = input.Read(chunk, 0, chunk.Length)) > 0) { tar.Push(chunk, 0, n); Done(0, (ulong)n); }
                    }
                    else { Gunzip(input, (b, o, n) => tar.Push(b, o, n)); }
                    tar.End();
                }
            }
            Done(1);
        }
        catch (Exception e) { if (e is not OperationCanceledException) { Error(archive, e); } }
        input?.Close(true);
        // Progress counts the archive's own bytes, whatever the format consumed.
        ulong size = 0; try { Locked(() => _store.Kind(archive, out size)); } catch (Exception) { }
        long counted = _bytesDoneSnapshot() - before; if ((long)size > counted) { Done(0, (ulong)((long)size - counted)); }
    }
    private long _bytesDoneSnapshot() { lock (_lock) { return (long)_bytesDone; } }

    private string Target(string entry)
    {
        string? safe = SafeEntry(entry);
        if (safe is null) { throw new IOException("unsafe name in the archive: " + entry); }
        return Join(_extractRoot, safe);
    }
    private void MakeFolders(string folder)
    {
        if (_made.Contains(folder) || folder.Length <= _extractRoot.Length) { return; }
        MakeFolders(Parent(folder));
        if (Locked(() => _store.Kind(folder, out _)) != FileKind.Directory) { Locked(() => _store.CreateDirectory(folder)); }
        _made.Add(folder);
    }
    /// <summary>Writes one file of the archive; null when the entry was refused (counted as an error).</summary>
    private GatedStream? NewFile(string entry)
    {
        string path;
        try { path = Target(entry); MakeFolders(Parent(path)); }
        catch (Exception e) { Error(entry, e); return null; }
        if (Locked(() => _store.Kind(path, out _)) != FileKind.Missing) { Error(entry, new IOException("listed twice in the archive")); return null; }
        Set(() => _current = Leaf(path));
        _lastPath = path;
        return new GatedStream(Locked(() => _store.CreateNew(path)), _gate);
    }
    private string _lastPath = "";
    private void Abandon(GatedStream? output)
    {
        if (output is null) { return; }
        output.Close(true);
        try { Locked(() => _store.DeleteFile(_lastPath)); } catch (Exception) { }
    }
    private void Written(long bytes)
    {
        _extracted += bytes;
        if (_extracted > MaximumExtracted) { throw new IOException("the archive expands past 64 GiB"); }
    }

    private void ExtractZip(GatedStream input)
    {
        long length = input.Length;
        // End of central directory: the last "PK\x05\x06" in the final 64 KiB + 22 bytes.
        int tail = (int)Math.Min(length, 65557);
        var end = new byte[tail]; input.Position = length - tail; ReadExactly(input, end, tail);
        int at = -1;
        for (int i = tail - 22; i >= 0; i--) { if (end[i] == 0x50 && end[i + 1] == 0x4b && end[i + 2] == 5 && end[i + 3] == 6) { at = i; break; } }
        if (at < 0) { throw new IOException("not a zip archive (no central directory)"); }
        int entries = U16(end, at + 10); long size = U32(end, at + 12), offset = U32(end, at + 16);
        if (entries == 0xffff || offset == 0xffffffff) { throw new IOException("zip64 archives are not supported"); }
        if (offset + size > length) { throw new IOException("the zip directory lies outside the file"); }
        var directory = new byte[size]; input.Position = offset; ReadExactly(input, directory, (int)size);
        int p = 0;
        for (int i = 0; i < entries && !_cancelled; i++)
        {
            if (p + 46 > directory.Length || U32(directory, p) != 0x02014b50) { throw new IOException("damaged zip directory"); }
            int flags = U16(directory, p + 8), method = U16(directory, p + 10), nameLength = U16(directory, p + 28), extra = U16(directory, p + 30), comment = U16(directory, p + 32);
            uint crc = U32(directory, p + 16); long packed = U32(directory, p + 20), unpacked = U32(directory, p + 24), local = U32(directory, p + 42);
            string name = ((flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.Latin1).GetString(directory, p + 46, nameLength);
            p += 46 + nameLength + extra + comment;
            if (name.EndsWith('/')) { try { MakeFolders(Target(name)); } catch (Exception e) { Error(name, e); } continue; }
            if ((flags & 1) != 0) { Error(name, new IOException("encrypted entries are not supported")); continue; }
            if (method is not (0 or 8)) { Error(name, new IOException("compression method " + method + " is not supported")); continue; }
            if (packed == 0xffffffff || unpacked == 0xffffffff) { Error(name, new IOException("zip64 entries are not supported")); continue; }
            var header = new byte[30]; input.Position = local; ReadExactly(input, header, 30);
            if (U32(header, 0) != 0x04034b50) { Error(name, new IOException("damaged entry")); continue; }
            input.Position = local + 30 + U16(header, 26) + U16(header, 28);
            GatedStream? output = NewFile(name); if (output is null) { continue; }
            uint actual = 0; long wrote = 0; bool ok = false;
            try
            {
                var data = new Slice(input, packed);
                Action<byte[], int, int> sink = (b, o, n) => { output.Write(b, o, n); actual = Crc32.Update(actual, b.AsSpan(o, n)); wrote += n; Written(n); if (_cancelled) { throw new OperationCanceledException(); } };
                if (method == 0) { var chunk = new byte[Chunk]; int n; while ((n = data.Read(chunk, 0, chunk.Length)) > 0) { sink(chunk, 0, n); } }
                else { new Inflater(data, sink, unpacked).Run(); }
                if (wrote != unpacked || actual != crc) { throw new IOException("the data does not match its checksum"); }
                output.Flush(); ok = true;
            }
            catch (Exception e) { if (e is not OperationCanceledException) { Error(name, e); } }
            if (ok) { output.Close(true); Done(0, (ulong)packed); } else { Abandon(output); }
        }
    }

    private void ExtractGz(string archive, GatedStream input)
    {
        string target = Unused(Join(_destination, StemOf(Leaf(archive)).EndsWith(" (extracted)") ? Leaf(archive)[..^3] : StemOf(Leaf(archive))));
        GatedStream output = new(Locked(() => _store.CreateNew(target)), _gate);
        _lastPath = target; bool ok = false;
        try { Gunzip(input, (b, o, n) => { output.Write(b, o, n); Written(n); if (_cancelled) { throw new OperationCanceledException(); } }); output.Flush(); ok = true; }
        catch (Exception e) { if (e is not OperationCanceledException) { Error(archive, e); } }
        if (ok) { output.Close(true); } else { Abandon(output); }
    }

    /// <summary>One gzip member: header, DEFLATE data, CRC and size checked.</summary>
    private void Gunzip(Stream input, Action<byte[], int, int> sink)
    {
        var head = new byte[10]; ReadExactly(input, head, 10);
        if (head[0] != 0x1f || head[1] != 0x8b || head[2] != 8) { throw new IOException("not a gzip file"); }
        int flags = head[3];
        if ((flags & 4) != 0) { var x = new byte[2]; ReadExactly(input, x, 2); Skip(input, x[0] | x[1] << 8); }
        if ((flags & 8) != 0) { while (Read1(input) != 0) { } }
        if ((flags & 16) != 0) { while (Read1(input) != 0) { } }
        if ((flags & 2) != 0) { Skip(input, 2); }
        uint crc = 0; long size = 0, reported = 0; Inflater? inflater = null;
        inflater = new Inflater(input, (b, o, n) =>
        {
            crc = Crc32.Update(crc, b.AsSpan(o, n)); size += n; sink(b, o, n);
            long consumed = inflater!.Consumed; Done(0, (ulong)Math.Max(0, consumed - reported)); reported = consumed;   // progress in archive bytes
        }, MaximumExtracted);
        inflater.Run();
        Done(0, (ulong)Math.Max(0, inflater.Consumed - reported));
        uint storedCrc = 0, storedSize = 0;
        for (int i = 0; i < 4; i++) { storedCrc |= (uint)inflater.AlignedByte() << (8 * i); }
        for (int i = 0; i < 4; i++) { storedSize |= (uint)inflater.AlignedByte() << (8 * i); }
        if (storedCrc != crc || storedSize != (uint)size) { throw new IOException("the data does not match its checksum"); }
    }

    /// <summary>ustar/GNU tar as a push parser, so it reads plain and gzip-compressed archives alike.</summary>
    private sealed class TarReader
    {
        private readonly FileJob _job;
        private readonly byte[] _header = new byte[512];
        private int _headerFill;
        private long _left, _padding;
        private GatedStream? _output;
        private string _name = "", _longName = "";
        private bool _ended;
        public TarReader(FileJob job) { _job = job; }

        public void Push(byte[] data, int offset, int count)
        {
            while (count > 0 && !_ended)
            {
                if (_left > 0)
                {
                    int n = (int)Math.Min(count, _left);
                    if (_output is not null) { _output.Write(data, offset, n); _job.Written(n); }
                    else if (_collecting is not null) { _collecting.Append(Encoding.UTF8.GetString(data, offset, n)); }
                    _left -= n; offset += n; count -= n;
                    if (_left == 0) { FinishEntry(); }
                    continue;
                }
                if (_padding > 0) { int n = (int)Math.Min(count, _padding); _padding -= n; offset += n; count -= n; continue; }
                int take = Math.Min(count, 512 - _headerFill);
                Array.Copy(data, offset, _header, _headerFill, take); _headerFill += take; offset += take; count -= take;
                if (_headerFill == 512) { _headerFill = 0; Header(); }
                if (_job._cancelled) { throw new OperationCanceledException(); }
            }
        }
        public void End()
        {
            if (_output is not null) { _job.Abandon(_output); _output = null; throw new IOException("the archive ends inside a file"); }
        }
        private StringBuilder? _collecting; private char _collectKind;
        private void Header()
        {
            bool zero = true; foreach (byte b in _header) { if (b != 0) { zero = false; break; } }
            if (zero) { _ended = true; return; }
            long size = Octal(124, 12);
            uint sum = 0; for (int i = 0; i < 512; i++) { sum += i is >= 148 and < 156 ? 32u : _header[i]; }
            if (sum != (uint)Octal(148, 8)) { throw new IOException("damaged tar header"); }
            char type = (char)_header[156];
            string name = Text(0, 100), prefix = Encoding.ASCII.GetString(_header, 257, 6).StartsWith("ustar") ? Text(345, 155) : "";
            if (prefix.Length > 0) { name = prefix + "/" + name; }
            if (_longName.Length > 0) { name = _longName; _longName = ""; }
            _name = name; _left = size; _padding = (512 - size % 512) % 512; _output = null; _collecting = null;
            switch (type)
            {
                case 'L': case 'x':
                    _collecting = new StringBuilder(); _collectKind = type; break;
                case 'g': break;   // global pax header: ignored
                case '5':
                    try { _job.MakeFolders(_job.Target(name)); } catch (Exception e) { _job.Error(name, e); }
                    break;
                case '0': case '\0': case '7':
                    _output = _job.NewFile(name); break;
                default:
                    _job.Error(name, new IOException(type == '2' || type == '1' ? "links inside archives are not extracted" : "special files are not extracted")); break;
            }
            if (_left == 0) { FinishEntry(); }
        }
        private void FinishEntry()
        {
            if (_output is not null) { _output.Flush(); _output.Close(true); _output = null; }
            if (_collecting is not null)
            {
                string text = _collecting.ToString(); _collecting = null;
                if (_collectKind == 'L') { _longName = text.TrimEnd('\0'); }
                else
                {
                    // pax records: "LEN key=value\n"; only path matters here.
                    foreach (string record in text.Split('\n'))
                    {
                        int space = record.IndexOf(' '), eq = record.IndexOf('=');
                        if (space > 0 && eq > space && record[(space + 1)..eq] == "path") { _longName = record[(eq + 1)..]; }
                    }
                }
            }
        }
        private string Text(int at, int length) { int n = 0; while (n < length && _header[at + n] != 0) { n++; } return Encoding.UTF8.GetString(_header, at, n); }
        private long Octal(int at, int length)
        {
            if ((_header[at] & 0x80) != 0) { long v = 0; for (int i = 1; i < length; i++) { v = v << 8 | _header[at + i]; } return v; }   // GNU base-256
            long value = 0;
            for (int i = 0; i < length; i++) { byte b = _header[at + i]; if (b is (byte)' ' or 0) { if (value > 0) { break; } continue; } if (b is < (byte)'0' or > (byte)'7') { break; } value = value * 8 + (b - '0'); }
            return value;
        }
    }

    // ---------- Compress.
    private void Compress()
    {
        var kind = KindOf(Leaf(_destination));
        if (kind is ArchiveKind.None or ArchiveKind.Gz) { Finish(FileJobState.Failed, "Name the archive .zip, .tar or .tar.gz."); return; }
        string target = Unused(_destination);
        GatedStream output = new(Locked(() => _store.CreateNew(target)), _gate);
        _lastPath = target; bool ok = false;
        try
        {
            if (kind == ArchiveKind.Zip) { WriteZip(output); }
            else if (kind == ArchiveKind.Tar) { WriteTar((b, o, n) => output.Write(b, o, n)); }
            else
            {
                output.Write(new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 3 }, 0, 10);
                uint crc = 0; long size = 0;
                var deflater = new Deflater((b, o, n) => output.Write(b, o, n));
                WriteTar((b, o, n) => { crc = Crc32.Update(crc, b.AsSpan(o, n)); size += n; deflater.Write(b.AsSpan(o, n)); });
                deflater.Finish();
                var trailer = new byte[8];
                for (int i = 0; i < 4; i++) { trailer[i] = (byte)(crc >> (8 * i)); trailer[4 + i] = (byte)((uint)size >> (8 * i)); }
                output.Write(trailer, 0, 8);
            }
            output.Flush(); ok = !_cancelled;
        }
        catch (Exception e) { if (e is not OperationCanceledException) { Error(target, e); } }
        if (ok) { output.Close(true); Set(() => _message = Leaf(target)); } else { Abandon(output); }
    }

    /// <summary>Every item below the sources, as (path, name in the archive, kind, size), folders before contents.</summary>
    private List<(string Path, string Name, FileKind Kind, ulong Size)> Members()
    {
        var list = new List<(string, string, FileKind, ulong)>();
        void Add(string path, string name, int depth)
        {
            if (depth > MaximumDepth || list.Count > MaximumItems) { throw new IOException("too many items for one archive"); }
            ulong size = 0; FileKind kind = Locked(() => _store.Kind(path, out size));
            if (kind is FileKind.Link or FileKind.Special) { Error(path, new IOException(kind == FileKind.Link ? "links are not archived" : "special files are not archived")); return; }
            list.Add((path, name, kind, size));
            if (kind == FileKind.Directory) { foreach (string child in Locked(() => _store.Names(path))) { Add(Join(path, child), name + "/" + child, depth + 1); } }
        }
        foreach (string source in _sources) { Add(source, Leaf(source), 0); }
        return list;
    }

    private void WriteZip(GatedStream output)
    {
        var central = new MemoryStream(); int count = 0;
        var now = DateTime.Now; ushort time = (ushort)(now.Hour << 11 | now.Minute << 5 | now.Second / 2), date = (ushort)((Math.Max(1980, now.Year) - 1980) << 9 | now.Month << 5 | now.Day);
        foreach (var m in Members())
        {
            if (_cancelled) { throw new OperationCanceledException(); }
            if (++count > 65534) { throw new IOException("more than 65,534 items: zip64 is not supported"); }
            string name = m.Kind == FileKind.Directory ? m.Name + "/" : m.Name;
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            long offset = output.Position; bool folder = m.Kind == FileKind.Directory;
            if (offset > 0xfffffff0L || m.Size > 0xfffffff0UL) { throw new IOException("over 4 GiB: zip64 is not supported"); }
            var header = new byte[30 + nameBytes.Length];
            W32(header, 0, 0x04034b50); W16(header, 4, 20); W16(header, 6, 0x800); W16(header, 8, folder ? 0 : 8); W16(header, 10, time); W16(header, 12, date);
            W16(header, 26, nameBytes.Length); nameBytes.CopyTo(header, 30);
            output.Write(header, 0, header.Length);
            uint crc = 0; long packed = 0, unpacked = 0;
            if (!folder)
            {
                Set(() => _current = Leaf(m.Path));
                Stream input = new GatedStream(Locked(() => _store.OpenRead(m.Path)), _gate);
                var deflater = new Deflater((b, o, n) => { output.Write(b, o, n); packed += n; });
                var chunk = new byte[Chunk]; int n;
                while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (_cancelled) { ((GatedStream)input).Close(true); throw new OperationCanceledException(); }
                    crc = Crc32.Update(crc, chunk.AsSpan(0, n)); unpacked += n; deflater.Write(chunk.AsSpan(0, n)); Done(0, (ulong)n);
                }
                deflater.Finish(); ((GatedStream)input).Close(true);
                long end = output.Position;
                var sizes = new byte[12]; W32(sizes, 0, crc); W32(sizes, 4, (uint)packed); W32(sizes, 8, (uint)unpacked);
                output.Position = offset + 14; output.Write(sizes, 0, 12); output.Position = end;
            }
            Done(1);
            var entry = new byte[46 + nameBytes.Length];
            W32(entry, 0, 0x02014b50); W16(entry, 4, 0x031e); W16(entry, 6, 20); W16(entry, 8, 0x800); W16(entry, 10, folder ? 0 : 8);
            W16(entry, 12, time); W16(entry, 14, date); W32(entry, 16, crc); W32(entry, 20, (uint)packed); W32(entry, 24, (uint)unpacked);
            W16(entry, 28, nameBytes.Length); W32(entry, 38, folder ? 0x41ed0010u : 0x81a40000u); W32(entry, 42, (uint)offset); nameBytes.CopyTo(entry, 46);
            central.Write(entry, 0, entry.Length);
        }
        long start = output.Position; byte[] directory = central.ToArray(); output.Write(directory, 0, directory.Length);
        var tail = new byte[22]; W32(tail, 0, 0x06054b50); W16(tail, 8, count); W16(tail, 10, count); W32(tail, 12, (uint)directory.Length); W32(tail, 16, (uint)start);
        output.Write(tail, 0, 22);
    }

    private void WriteTar(Action<byte[], int, int> write)
    {
        long mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var m in Members())
        {
            if (_cancelled) { throw new OperationCanceledException(); }
            bool folder = m.Kind == FileKind.Directory;
            string name = folder ? m.Name + "/" : m.Name;
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 100)
            {
                // GNU long name: an 'L' entry holding the name.
                write(TarHeader("././@LongLink", nameBytes.Length + 1, 'L', mtime), 0, 512);
                var block = new byte[(nameBytes.Length + 1 + 511) / 512 * 512]; nameBytes.CopyTo(block, 0); write(block, 0, block.Length);
            }
            write(TarHeader(name, folder ? 0 : (long)m.Size, folder ? '5' : '0', mtime), 0, 512);
            if (folder) { Done(1); continue; }
            Set(() => _current = Leaf(m.Path));
            var input = new GatedStream(Locked(() => _store.OpenRead(m.Path)), _gate);
            var chunk = new byte[Chunk]; int n; long total = 0;
            while ((n = input.Read(chunk, 0, chunk.Length)) > 0 && total < (long)m.Size)
            {
                if (_cancelled) { input.Close(true); throw new OperationCanceledException(); }
                n = (int)Math.Min(n, (long)m.Size - total); write(chunk, 0, n); total += n; Done(0, (ulong)n);
            }
            input.Close(true);
            if (total < (long)m.Size) { throw new IOException(Leaf(m.Path) + " shrank while it was archived"); }
            int pad = (int)((512 - total % 512) % 512); if (pad > 0) { write(new byte[pad], 0, pad); }
            Done(1);
        }
        write(new byte[1024], 0, 1024);
    }
    private static byte[] TarHeader(string name, long size, char type, long mtime)
    {
        var h = new byte[512];
        byte[] n = Encoding.UTF8.GetBytes(name); Array.Copy(n, h, Math.Min(n.Length, 100));
        void Octal(int at, int length, long value) { string s = Convert.ToString(value, 8).PadLeft(length - 1, '0'); Encoding.ASCII.GetBytes(s).CopyTo(h, at); }
        Octal(100, 8, type == '5' ? 0x1ed : 0x1a4); Octal(108, 8, 0); Octal(116, 8, 0); Octal(124, 12, size); Octal(136, 12, mtime);
        h[156] = (byte)type; Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(h, 257);
        for (int i = 148; i < 156; i++) { h[i] = (byte)' '; }
        uint sum = 0; foreach (byte b in h) { sum += b; }
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);
        return h;
    }

    // ---------- Little helpers.
    /// <summary>The name itself when it is free, else "name (2)" and so on.</summary>
    private string Unused(string path) => Locked(() => _store.Kind(path, out _)) == FileKind.Missing ? path : Free(path);
    private static int U16(byte[] b, int at) => b[at] | b[at + 1] << 8;
    private static uint U32(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
    private static void W16(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    private static void W32(byte[] b, int at, uint v) { for (int i = 0; i < 4; i++) { b[at + i] = (byte)(v >> (8 * i)); } }
    private static void ReadExactly(Stream s, byte[] buffer, int count)
    {
        int done = 0; while (done < count) { int n = s.Read(buffer, done, count - done); if (n <= 0) { throw new IOException("the archive ends early"); } done += n; }
    }
    private static int Read1(Stream s) { int b = s.ReadByte(); if (b < 0) { throw new IOException("the archive ends early"); } return b; }
    private static void Skip(Stream s, int count) { for (int i = 0; i < count; i++) { Read1(s); } }
}
