using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Zenith.Core.Security;

namespace Zenith.Core.Files;

internal enum FileJobOperation { Copy = 1, Move = 2, Delete = 3, Extract = 4, Compress = 5 }
internal enum FileJobState { Counting = 1, Working = 2, Asking = 3, Done = 4, Failed = 5, Cancelled = 6 }
internal enum FileJobAnswer { Skip = 1, Replace = 2, KeepBoth = 3, Merge = 4 }
internal enum FileKind { Missing, File, Directory, Link, Special }

/// <summary>What a transfer job may do to the filesystem. The kernel implements it over the VFS, as the
/// caller's own identity; host tests over a temporary directory.</summary>
internal interface IFileJobStore
{
    /// <summary>Kind and size without following a final symbolic link.</summary>
    FileKind Kind(string path, out ulong size);
    List<string> Names(string directory);
    void CreateDirectory(string path);
    Stream OpenRead(string path);
    /// <summary>A new file; fails when the name exists.</summary>
    Stream CreateNew(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    /// <summary>Renames within one filesystem; false (nothing changed) when it cannot.</summary>
    bool TryRename(string from, string to);
    string ReadLink(string path);
    void Symlink(string path, string target);
    ulong Available(string directory);
    /// <summary>Sets the permission bits (0..0777) where the caller may; best effort, failures are ignored.</summary>
    void SetMode(string path, uint mode);
    /// <summary>The permission bits (0..0777), or 0 when unknown.</summary>
    uint Mode(string path);
    bool SameFilesystem(string a, string b);
}

internal readonly record struct FileJobStatus(FileJobState State, FileJobOperation Operation, int Errors, int ItemsDone, int ItemsTotal,
    ulong BytesDone, ulong BytesTotal, string Current, string Message, string Question, int QuestionKind);

/// <summary>One copy, move or delete of up to 64 items, run on its own thread as the person who asked. Every
/// filesystem step takes <c>gate</c>, so it never overlaps a native file call; a copy holds it for one
/// 64 KiB chunk at a time. Questions (a name already exists) wait for an answer; cancel stops between
/// chunks and removes the file being written.</summary>
internal sealed partial class FileJob
{
    public const int MaximumSources = 64, MaximumItems = 200000, MaximumDepth = 64, Chunk = 65536;
    public const uint ReplaceAll = 1, SkipAll = 2;
    private readonly IFileJobStore _store;
    private readonly object _gate;
    private readonly string _destination;
    private readonly string[] _sources;
    private readonly object _lock = new();
    private FileJobState _state = FileJobState.Counting;
    private int _errors, _itemsDone, _itemsTotal, _questionKind;
    private ulong _bytesDone, _bytesTotal;
    private string _current = "", _message = "", _question = "";
    private FileJobAnswer? _answer, _always;
    private volatile bool _cancelled;
    private readonly byte[] _buffer = new byte[Chunk];

    public FileJob(long id, int pid, FileJobOperation operation, uint flags, string destination, string[] sources, IFileJobStore store, object gate)
    {
        Id = id; Pid = pid; Operation = operation; _destination = destination; _sources = sources; _store = store; _gate = gate;
        if ((flags & ReplaceAll) != 0) { _always = FileJobAnswer.Replace; }
        else if ((flags & SkipAll) != 0) { _always = FileJobAnswer.Skip; }
    }

    public long Id { get; }
    public int Pid { get; }
    public FileJobOperation Operation { get; }
    public bool Finished { get { lock (_lock) { return _state >= FileJobState.Done; } } }

    public FileJobStatus Status
    {
        get { lock (_lock) { return new(_state, Operation, _errors, _itemsDone, _itemsTotal, _bytesDone, _bytesTotal, _current, _message, _question, _questionKind); } }
    }

    public void Cancel() => _cancelled = true;

    /// <summary>Answers the pending question; false when there is none or the answer does not fit it.</summary>
    public bool Answer(FileJobAnswer answer, bool all)
    {
        lock (_lock)
        {
            if (_state != FileJobState.Asking || answer is < FileJobAnswer.Skip or > FileJobAnswer.Merge) { return false; }
            if (answer == FileJobAnswer.Merge && _questionKind != 2) { return false; }
            _answer = answer; if (all) { _always = answer; }
            return true;
        }
    }

    // Explicit release: Cosmos NativeAOT skips finally blocks (and so lock/using) while unwinding an exception,
    // and a gate left held here would stop every native file call.
    private T Locked<T>(Func<T> step)
    {
        Monitor.Enter(_gate); T result;
        try { result = step(); }
        catch { Monitor.Exit(_gate); throw; }
        Monitor.Exit(_gate); return result;
    }
    private void Locked(Action step) => Locked(() => { step(); return 0; });
    private void Set(Action change) { lock (_lock) { change(); } }

    public void Run()
    {
        try
        {
            Count();
            if (_cancelled) { Finish(FileJobState.Cancelled, "Cancelled before anything changed."); return; }
            if (Operation is not (FileJobOperation.Delete or FileJobOperation.Extract))
            {
                ulong needed; lock (_lock) { needed = _bytesTotal; }
                ulong available = Locked(() => _store.Available(Operation == FileJobOperation.Compress ? Parent(_destination) : _destination));
                if (needed > available)
                { Finish(FileJobState.Failed, "Not enough space: " + Size(needed) + " needed, " + Size(available) + " available."); return; }
            }
            Set(() => _state = FileJobState.Working);
            if (Operation == FileJobOperation.Compress) { Compress(); }
            else foreach (string source in _sources)
            {
                if (_cancelled) { break; }
                if (Operation == FileJobOperation.Extract) { Extract(source); continue; }
                if (Operation == FileJobOperation.Delete) { Delete(source); }
                else
                {
                    string target = Join(_destination, Leaf(source));
                    if (Operation == FileJobOperation.Move) { Move(source, target, 0); }
                    else { Copy(source, target, 0, sameFolder: target == source); }
                }
            }
            if (_cancelled) { Finish(FileJobState.Cancelled, "Cancelled. Items already finished stay."); return; }
            int errors; lock (_lock) { errors = _errors; }
            Finish(FileJobState.Done, errors == 0 ? "Done." : errors == 1 ? "Done, with 1 error: " + _message : "Done, with " + errors + " errors. Last: " + _message);
        }
        catch (Exception e) { Finish(FileJobState.Failed, e.Message); }
    }

    private void Finish(FileJobState state, string message) => Set(() => { _state = state; _message = message; _current = ""; _question = ""; _questionKind = 0; });

    // ---------- Counting: items and bytes, bounded; moves within a filesystem count one item each.
    private void Count()
    {
        int items = 0; ulong bytes = 0;
        foreach (string source in _sources)
        {
            if (Operation == FileJobOperation.Extract) { ulong size = 0; Locked(() => _store.Kind(source, out size)); items++; bytes += size; continue; }
            if (Operation == FileJobOperation.Move && Locked(() => _store.SameFilesystem(source, _destination))) { items++; continue; }
            Walk(source, 0, ref items, ref bytes);
            if (_cancelled) { return; }
        }
        Set(() => { _itemsTotal = items; _bytesTotal = bytes; });
    }
    private void Walk(string path, int depth, ref int items, ref ulong bytes)
    {
        if (_cancelled) { return; }
        if (depth > MaximumDepth) { throw new IOException("Folders nest deeper than " + MaximumDepth + " levels."); }
        if (++items > MaximumItems) { throw new IOException("More than " + MaximumItems + " items."); }
        ulong size = 0; FileKind kind = Locked(() => _store.Kind(path, out size));
        if (kind == FileKind.File) { bytes += size; }
        if (kind == FileKind.Directory)
        {
            foreach (string name in Locked(() => _store.Names(path))) { Walk(Join(path, name), depth + 1, ref items, ref bytes); }
        }
        if (depth == 0) { int i = items; ulong b = bytes; Set(() => { _itemsTotal = i; _bytesTotal = b; _current = Leaf(path); }); }
    }

    // ---------- Questions.
    private FileJobAnswer Ask(string target, int kind)
    {
        lock (_lock)
        {
            if (_always is FileJobAnswer always && (always != FileJobAnswer.Merge || kind == 2)) { return always; }
            _state = FileJobState.Asking; _question = Leaf(target); _questionKind = kind; _answer = null;
        }
        while (true)
        {
            if (_cancelled) { Set(() => { _state = FileJobState.Working; _question = ""; _questionKind = 0; }); return FileJobAnswer.Skip; }
            lock (_lock)
            {
                if (_answer is FileJobAnswer answer) { _state = FileJobState.Working; _question = ""; _questionKind = 0; return answer; }
            }
            Thread.Sleep(20);
        }
    }

    /// <summary>"name (2).ext", "name (3).ext", ... the first that does not exist.</summary>
    private string Free(string target)
    {
        string folder = Parent(target), leaf = Leaf(target);
        int dot = leaf.LastIndexOf('.');
        string stem = dot > 0 ? leaf[..dot] : leaf, extension = dot > 0 ? leaf[dot..] : "";
        for (int n = 2; n < 10000; n++)
        {
            string candidate = Join(folder, stem + " (" + n + ")" + extension);
            if (Locked(() => _store.Kind(candidate, out _)) == FileKind.Missing) { return candidate; }
        }
        throw new IOException("No free name for " + leaf + ".");
    }

    private void Error(string path, Exception e) => Set(() => { _errors++; _message = Leaf(path) + ": " + e.Message; });
    private void Done(int items, ulong bytes = 0) => Set(() => { _itemsDone += items; _bytesDone += bytes; });

    /// <summary>Resolves a name clash before writing: the target to write, or null to skip. merge says the
    /// existing folder is kept and filled.</summary>
    private string? Resolve(string source, string target, FileKind sourceKind, out bool merge)
    {
        merge = false;
        FileKind existing = Locked(() => _store.Kind(target, out _));
        if (existing == FileKind.Missing) { return target; }
        bool folders = sourceKind == FileKind.Directory && existing == FileKind.Directory;
        FileJobAnswer answer = Ask(target, folders ? 2 : 1);
        if (answer == FileJobAnswer.Skip) { return null; }
        if (answer == FileJobAnswer.KeepBoth) { return Free(target); }
        if (answer == FileJobAnswer.Merge && folders) { merge = true; return target; }
        if (source == target) { return null; }
        RemoveTree(target);
        return target;
    }

    // ---------- Copy.
    /// <returns>True when the item and everything in it were copied.</returns>
    private bool Copy(string source, string target, int depth, bool sameFolder = false)
    {
        if (_cancelled) { return false; }
        Set(() => _current = Leaf(source));
        ulong size = 0;
        FileKind kind;
        try { kind = Locked(() => _store.Kind(source, out size)); }
        catch (Exception e) { Error(source, e); return false; }
        if (kind == FileKind.Missing) { Error(source, new IOException("it no longer exists")); return false; }
        if (kind == FileKind.Special) { Error(source, new IOException("device files, sockets and FIFOs are not copied")); Done(1); return false; }
        string? destination; bool merge = false;
        try { destination = sameFolder ? Free(target) : Resolve(source, target, kind, out merge); }
        catch (Exception e) { Error(source, e); return false; }
        if (destination is null) { SkipTree(source); return false; }
        try
        {
            if (kind == FileKind.Link) { string link = Locked(() => _store.ReadLink(source)); Locked(() => _store.Symlink(destination, link)); Done(1); return true; }
            if (kind == FileKind.File) { return CopyFile(source, destination, size); }
            if (!merge) { Locked(() => _store.CreateDirectory(destination)); }
            Done(1);
        }
        catch (Exception e) { Error(source, e); return false; }
        bool all = true;
        List<string> names;
        try { names = Locked(() => _store.Names(source)); }
        catch (Exception e) { Error(source, e); return false; }
        foreach (string name in names)
        {
            if (_cancelled) { return false; }
            all &= Copy(Join(source, name), Join(destination, name), depth + 1);
        }
        // A new folder takes the source folder's permissions after its contents, so a read-only one still received them.
        if (!merge) { KeepMode(source, destination); }
        return all;
    }

    private bool CopyFile(string source, string destination, ulong size)
    {
        Stream? input = null, output = null; bool complete = false; ulong copied = 0;
        try
        {
            Locked(() => { input = _store.OpenRead(source); output = _store.CreateNew(destination); });
            while (!_cancelled)
            {
                int read = Locked(() => { int n = input!.Read(_buffer, 0, _buffer.Length); if (n > 0) { output!.Write(_buffer, 0, n); } return n; });
                if (read == 0) { break; }
                copied += (ulong)read; Done(0, (ulong)read);
            }
            if (!_cancelled) { Locked(() => output!.Flush()); complete = true; }
        }
        catch (Exception e) { Error(source, e); }
        finally
        {
            try { Locked(() => { input?.Dispose(); output?.Dispose(); }); } catch (Exception) { complete = false; }
            if (!complete && output is not null) { try { Locked(() => _store.DeleteFile(destination)); } catch (Exception) { } }
        }
        // The total was counted earlier; a file that changed size since then must not overshoot it.
        if (copied < size) { Done(0, size - copied); }
        if (complete) { KeepMode(source, destination); Done(1); }
        return complete;
    }

    /// <summary>The copy gets the source's permission bits (never setuid, setgid or sticky); best effort, as the
    /// copy itself already succeeded.</summary>
    private void KeepMode(string source, string destination)
    {
        try { uint mode = Locked(() => _store.Mode(source)); if (mode != 0) { Locked(() => _store.SetMode(destination, mode)); } }
        catch (Exception) { }
    }

    /// <summary>Counts a skipped subtree as done, so the progress still reaches its end.</summary>
    private void SkipTree(string path)
    {
        int items = 0; ulong bytes = 0;
        try { Walk(path, 0, ref items, ref bytes); } catch (Exception) { items = Math.Max(items, 1); }
        Done(items, bytes);
    }

    // ---------- Move: a rename within one filesystem, otherwise a copy and then removal of what was copied.
    private void Move(string source, string target, int depth)
    {
        if (_cancelled) { return; }
        Set(() => _current = Leaf(source));
        if (Parent(source) == Parent(target) && depth == 0) { Done(1); return; }   // already here
        ulong size = 0; FileKind kind;
        try { kind = Locked(() => _store.Kind(source, out size)); }
        catch (Exception e) { Error(source, e); return; }
        if (kind == FileKind.Missing) { Error(source, new IOException("it no longer exists")); return; }
        bool same = Locked(() => _store.SameFilesystem(source, Parent(target)));
        if (same)
        {
            string? destination; bool merge;
            try { destination = Resolve(source, target, kind, out merge); }
            catch (Exception e) { Error(source, e); return; }
            if (destination is null) { Done(depth == 0 ? 1 : 0); return; }
            if (merge)
            {
                // Fill the existing folder entry by entry, then remove the emptied source.
                try
                {
                    foreach (string name in Locked(() => _store.Names(source))) { Move(Join(source, name), Join(destination, name), depth + 1); }
                    if (!_cancelled && Locked(() => _store.Names(source)).Count == 0) { Locked(() => _store.DeleteDirectory(source)); }
                }
                catch (Exception e) { Error(source, e); }
                if (depth == 0) { Done(1); }
                return;
            }
            try
            {
                if (!Locked(() => _store.TryRename(source, destination))) { throw new IOException("it could not be renamed"); }
                if (depth == 0) { Done(1); }
            }
            catch (Exception e) { Error(source, e); }
            return;
        }
        if (Copy(source, target, depth) && !_cancelled) { RemoveTree(source); }
    }

    // ---------- Delete.
    private void Delete(string path)
    {
        if (_cancelled) { return; }
        Set(() => _current = Leaf(path));
        try
        {
            FileKind kind = Locked(() => _store.Kind(path, out _));
            if (kind == FileKind.Missing) { Done(1); return; }
            if (kind == FileKind.Directory)
            {
                foreach (string name in Locked(() => _store.Names(path))) { if (_cancelled) { return; } Delete(Join(path, name)); }
                if (_cancelled) { return; }
                Locked(() => _store.DeleteDirectory(path));
            }
            else { Locked(() => _store.DeleteFile(path)); }
            Done(1);
        }
        catch (Exception e) { Error(path, e); }
    }

    private void RemoveTree(string path)
    {
        FileKind kind = Locked(() => _store.Kind(path, out _));
        if (kind == FileKind.Directory)
        {
            foreach (string name in Locked(() => _store.Names(path))) { RemoveTree(Join(path, name)); }
            Locked(() => _store.DeleteDirectory(path));
        }
        else if (kind != FileKind.Missing) { Locked(() => _store.DeleteFile(path)); }
    }

    // ---------- Paths: absolute, no trailing slash except "/".
    public static string Join(string folder, string name) => folder == "/" ? "/" + name : folder + "/" + name;
    public static string Leaf(string path) { int slash = path.LastIndexOf('/'); return slash < 0 ? path : path[(slash + 1)..]; }
    public static string Parent(string path) { int slash = path.LastIndexOf('/'); return slash <= 0 ? "/" : path[..slash]; }
    public static string Size(ulong bytes)
    {
        if (bytes < 1024) { return bytes + " B"; }
        string[] units = { "KiB", "MiB", "GiB", "TiB" };
        double value = bytes; int unit = -1;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        ulong tenths = (ulong)(value * 10 + 0.5);
        return (tenths / 10) + "." + (tenths % 10) + " " + units[unit];
    }
}

/// <summary>Running and finished transfer jobs: two running per program, eight in all; finished ones are
/// kept (at most 16) until the program forgets them or exits. A program's jobs are cancelled when it exits.</summary>
internal static class FileJobs
{
    public const int PerProcess = 2, Running = 8, Kept = 16;
    private static readonly object s_lock = new();
    private static readonly List<FileJob> s_jobs = new();
    private static long s_next;

    /// <summary>Validates and starts a job; its id, or a negative error.</summary>
    public static long Start(int pid, Credentials identity, FileJobOperation operation, uint flags, string destination, string[] sources,
        IFileJobStore store, object gate, bool thread = true)
    {
        if (sources.Length is < 1 or > FileJob.MaximumSources || flags > 2) { return -22; }
        if (operation == FileJobOperation.Delete ? destination.Length != 0 : !Absolute(destination)) { return -22; }
        foreach (string source in sources)
        {
            if (!Absolute(source) || source == "/") { return -22; }
            if (operation is FileJobOperation.Copy or FileJobOperation.Move && (destination == source || destination.StartsWith(source + "/", StringComparison.Ordinal))) { return -22; }
            if (operation == FileJobOperation.Compress && destination.StartsWith(source + "/", StringComparison.Ordinal)) { return -22; }
        }
        // Checked as the caller under the gate, released explicitly (see FileJob.Locked).
        int refused = 0;
        var scope = CredentialContext.Enter(identity);
        Monitor.Enter(gate);
        try
        {
            string folder = operation == FileJobOperation.Compress ? FileJob.Parent(destination) : destination;
            if (operation != FileJobOperation.Delete && store.Kind(folder, out _) != FileKind.Directory) { refused = -20; }
            else if (operation == FileJobOperation.Compress && FileJob.KindOf(FileJob.Leaf(destination)) is FileJob.ArchiveKind.None or FileJob.ArchiveKind.Gz) { refused = -22; }
            else if (operation == FileJobOperation.Extract && Array.Exists(sources, s => FileJob.KindOf(FileJob.Leaf(s)) == FileJob.ArchiveKind.None)) { refused = -22; }
            else { foreach (string source in sources) { if (store.Kind(source, out _) == FileKind.Missing) { refused = -2; break; } } }
        }
        catch (Exception e) { refused = e is UnauthorizedAccessException ? -13 : -5; }
        Monitor.Exit(gate); scope.Dispose();
        if (refused != 0) { return refused; }
        FileJob job;
        lock (s_lock)
        {
            int mine = 0, running = 0;
            foreach (var j in s_jobs) { if (!j.Finished) { running++; if (j.Pid == pid) { mine++; } } }
            if (mine >= PerProcess) { return -24; }
            if (running >= Running) { return -16; }
            job = new FileJob(++s_next, pid, operation, flags, destination, sources, store, gate);
            s_jobs.Add(job);
            // Forget the oldest finished jobs beyond the limit.
            for (int i = 0; s_jobs.Count > Kept && i < s_jobs.Count;) { if (s_jobs[i].Finished) { s_jobs.RemoveAt(i); } else { i++; } }
        }
        Log.Write("files", "job " + job.Id + " " + operation.ToString().ToLowerInvariant() + " of " + sources.Length + (sources.Length == 1 ? " item" : " items")
            + (operation == FileJobOperation.Delete ? "" : " to " + destination) + " for pid " + pid);
        void Body()
        {
            var scope = CredentialContext.Enter(identity);
            try { job.Run(); } catch (Exception) { }
            scope.Dispose();
            var s = job.Status;
            Log.Write("files", "job " + job.Id + " " + s.State.ToString().ToLowerInvariant() + ": " + s.ItemsDone + "/" + s.ItemsTotal + " items, " + s.Errors + " errors. " + s.Message);
        }
        if (thread) { new Thread(Body).Start(); } else { Body(); }
        return job.Id;
    }

    public static FileJob? Find(long id, int pid)
    {
        lock (s_lock) { foreach (var j in s_jobs) { if (j.Id == id && j.Pid == pid) { return j; } } }
        return null;
    }

    public static int Forget(long id, int pid)
    {
        lock (s_lock)
        {
            for (int i = 0; i < s_jobs.Count; i++)
            {
                if (s_jobs[i].Id != id || s_jobs[i].Pid != pid) { continue; }
                if (!s_jobs[i].Finished) { return -16; }
                s_jobs.RemoveAt(i); return 0;
            }
        }
        return -9;
    }

    /// <summary>The program ended: cancel what it still runs and forget the rest.</summary>
    public static void ProcessExited(int pid)
    {
        lock (s_lock)
        {
            for (int i = s_jobs.Count - 1; i >= 0; i--)
            {
                if (s_jobs[i].Pid != pid) { continue; }
                s_jobs[i].Cancel();
                if (s_jobs[i].Finished) { s_jobs.RemoveAt(i); }
            }
        }
    }

    private static bool Absolute(string path) => path.Length is > 0 and <= 1024 && path[0] == '/' && (path == "/" || path[^1] != '/')
        && path.IndexOf('\0') < 0 && path.IndexOf("//", StringComparison.Ordinal) < 0 && !path.Contains("/../") && !path.EndsWith("/..") && !path.Contains("/./") && !path.EndsWith("/.");
}
