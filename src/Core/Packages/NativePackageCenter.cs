using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Zenith.Core.Security;
using Zenith.Core.Shell.Commands;
using Zenith.Core.Storage;
using Zenith.Core.Time;

namespace Zenith.Core.Packages;

/// <summary>An Orbit job: refreshing the sources, or applying a plan. One at a time (the package service).</summary>
internal sealed class PackageJob
{
    public long Id; public int Pid; public string Kind = "";
    public volatile int State;   // 1 running, 2 done, 3 failed, 4 cancelled
    public volatile bool CancelRequested, Committing;
    public string Step = "", Message = "";
    public long Done, Total; public bool Restart; public int Changes;
    public List<string> Planned = new();
}

/// <summary>Orbit's package service (kernel 0.0.70, call 668, docs/components/ORBIT.md): a front end to the same pkg
/// engine (signed indexes, planner, transactions, kernel staging) with no second database. Reads are open to every
/// user; applying needs root or the <c>org.zenith.packages.manage</c> grant. Results are line-based text.</summary>
internal static class PackageCenter
{
    public const string AboutCache = PackageManager.Root + "/about";
    private static readonly object s_gate = new();
    private static PackageJob? s_job;
    private static long s_next;
    private static long Now => new DateTimeOffset(SystemClock.UtcNow).ToUnixTimeSeconds();
    private static string Name(uint uid) => Accounts.Service?.Name(uid) ?? (uid == 0 ? "root" : uid.ToString(CultureInfo.InvariantCulture));

    /// <summary>Binds the history's clock and caller to the running system.</summary>
    public static void Bind()
    {
        PackageHistory.Now = () => Now;
        PackageHistory.Who = () => { uint uid = CredentialContext.Current.Uid; return (uid, Name(uid)); };
    }

    public static void RecordKernel(string from, string to)
    {
        lock (s_gate) { s_kernelRead = false; }
        Bind(); var who = PackageHistory.Who();
        PackageHistory.Append(new NativePackageStore(), new(Now, who.Uid, who.User, PackageHistory.Source, true,
            new[] { "kernel " + from + " " + to }, PackageHistory.Command, "takes effect at restart"));
    }

    private static string Field(string? text) => text is null || text.Length == 0 ? "-" : PackageHistory.Clean(text, 240).Replace('\t', ' ');
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Line(StringBuilder s, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++) { if (i > 0) { s.Append('\t'); } s.Append(i == 0 ? fields[i] : Field(fields[i])); }
        s.Append('\n');
    }

    // ---------- What is known (every user).
    private sealed class View
    {
        public IReadOnlyList<RepositoryEntry> Catalog = Array.Empty<RepositoryEntry>();
        public IReadOnlyList<InstalledPackage> Installed = Array.Empty<InstalledPackage>();
        public (string Running, string HighWater, bool Pending)? Kernel;
        public string Error = "";
    }
    private static View Load()
    {
        var view = new View();
        try { view.Installed = new PackageManager(new NativePackageStore()).List(); }
        catch (Exception e) { view.Error = e.Message; }
        try { view.Catalog = NativeRepositories.Catalog(); }
        catch (Exception e) { if (view.Error.Length == 0) { view.Error = e.Message; } }
        view.Kernel = KernelVersions();
        return view;
    }
    // The boot state only changes when a kernel is staged (or at the next boot): read it once, not on every call,
    // since reading it checks the partition table.
    private static (string Running, string HighWater, bool Pending)? s_kernel;
    private static bool s_kernelRead;
    private static (string Running, string HighWater, bool Pending)? KernelVersions()
    {
        lock (s_gate) { if (s_kernelRead) { return s_kernel; } }
        var versions = Zenith.Core.Boot.NativeKernelUpdates.Versions();
        lock (s_gate) { s_kernel = versions; s_kernelRead = versions is not null; }
        return versions;
    }
    private static List<PackageRow> Rows(View view, Action cancel)
    {
        string floor = view.Kernel?.HighWater ?? "0.0.0";
        return PackageCatalogView.Rows(view.Catalog, SafeRepositories(), view.Installed, view.Kernel?.Running, floor, cancel);
    }
    private static IReadOnlyList<PackageRepository> SafeRepositories()
    {
        try { return NativeRepositories.Repositories; }
        catch (Exception) { return PackageRepositories.Parse(PackageRepositories.Default); }
    }

    public static bool Busy => PackageCommands.IsRunning || Installer.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning
        || (s_job is { } job && job.State == 1);

    // ---------- The background update check (U14 notifications): two minutes after the desktop starts and then once a
    // day, only online and when nothing else changes packages. A notification tells when updates are ready, and again
    // only when their number changed. A check that could not run tries again in half an hour.
    public const long FirstCheckMicros = 120_000_000, CheckEveryMicros = 86_400_000_000, RetryMicros = 1_800_000_000;
    private static long s_nextCheck;
    private static int s_lastOffered = -1;
    public static void BackgroundCheck(long nowMicros)
    {
        if (s_nextCheck == 0) { s_nextCheck = nowMicros + FirstCheckMicros; return; }
        if (nowMicros < s_nextCheck) { return; }
        if (SystemMounts.Mode != BootMode.Installed || !Zenith.Core.Network.NetworkService.Online || Busy) { s_nextCheck = nowMicros + RetryMicros; return; }
        s_nextCheck = nowMicros + CheckEveryMicros;
        try
        {
            using var root = CredentialContext.Enter(Credentials.Root);
            long id = Start("refresh", "", 0, 0);
            Log.Write("pkg", id > 0 ? "background update check started" : "background update check deferred: " + id);
            if (id <= 0) { s_nextCheck = nowMicros + RetryMicros; }
        }
        catch (Exception e) { Log.Write("pkg", "background update check skipped: " + e.Message); s_nextCheck = nowMicros + RetryMicros; }
    }
    private static long NowMicros() => System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
    private static int UpdateCount()
    {
        int updates = 0;
        foreach (var row in Rows(Load(), () => { })) { if (row.State == PackageRowState.Update) { updates++; } }
        return updates;
    }

    public static byte[] Overview(bool mayApply, Action cancel)
    {
        var view = Load(); var rows = Rows(view, cancel);
        int installed = 0, updates = 0, available = 0, notOffered = 0;
        foreach (var row in rows)
        {
            if (row.Kind == "kernel") { if (row.State == PackageRowState.Update) { updates++; } continue; }
            if (row.Installed is not null) { installed++; }
            if (row.State == PackageRowState.Update) { updates++; }
            else if (row.State == PackageRowState.Available) { available++; }
            else if (row.State == PackageRowState.NotOffered) { notOffered++; }
        }
        var s = new StringBuilder("ZENITH-ORBIT-OVERVIEW\t1\n");
        Line(s, "kernel", view.Kernel?.Running, view.Kernel?.HighWater, view.Kernel?.Pending == true ? "1" : "0");
        Line(s, "counts", N(installed), N(updates), N(available), N(notOffered), N(Components()));
        AppendSources(s);
        ulong free = 0; try { free = new NativePackageStore().FreeBytes; } catch (Exception) { }
        bool mutable = SystemMounts.Mode == BootMode.Installed;
        var utc = SystemClock.UtcNow;
        Line(s, "system", N((long)free), mutable ? "1" : "0", Busy ? "1" : "0", mayApply ? "1" : "0",
            N((long)(SystemClock.ToLocal(utc) - utc).TotalSeconds), N(new DateTimeOffset(utc).ToUnixTimeSeconds()));
        if (view.Error.Length > 0) { Line(s, "error", Words(view.Error)); }
        return Encoding.UTF8.GetBytes(s.ToString());
    }

    /// <summary>pkg's messages in plain words where they are about setup.</summary>
    public static string Words(string error) => error switch
    {
        "run pkg refresh first" => "The package lists have not been downloaded yet. Check for updates to get them.",
        "set pkg mirror HTTPS_BASE_URL first" => "No package source is set. An administrator sets one with pkg mirror.",
        _ when error.StartsWith("package trust missing", StringComparison.Ordinal) => "No package signing key is trusted yet. An administrator pins one with pkg trust.",
        _ when error.StartsWith("repository metadata expired", StringComparison.Ordinal) => "The package lists are out of date. Check for updates.",
        _ => error,
    };

    private static void AppendSources(StringBuilder s)
    {
        byte[]? key = null;
        try { var store = new NativePackageStore(); if (store.State(PackageCommands.KeyPath) != PackagePathState.Missing) { key = store.Read(PackageCommands.KeyPath, 4096); } }
        catch (Exception) { }
        Line(s, "key", key is null ? "-" : PackagePlanner.Digest(key));
        foreach (var repository in SafeRepositories())
        {
            string cache = NativePackageCache.Path + "/" + repository.Name + "-v3.zri";
            var store = new NativePackageStore();
            if (store.State(cache) == PackagePathState.Missing) { cache = NativePackageCache.Path + "/" + repository.Name + ".zri"; }
            string generation = "-", issued = "-", expires = "-", entries = "-", checkedAt = "-";
            try
            {
                if (key is not null && store.State(cache) != PackagePathState.Missing)
                {
                    var index = RepositoryIndex.Read(store.Read(cache, RepositoryIndex.MaximumBytes), key, new NativePackageVerifier(), repository.Name);
                    generation = N((long)index.Generation); issued = N(index.Issued); expires = N(index.Expires); entries = N(index.Entries.Count);
                    if (new VfsFileMetadata().TryRead(cache, out var stat)) { checkedAt = N(stat.Mtime); }
                }
            }
            catch (Exception) { }
            Line(s, "source", repository.Name, N(repository.Priority), repository.Mirror, generation, issued, expires, entries, checkedAt);
        }
        Line(s, "cache", NativePackageCache.Automatic ? "1" : "0");
    }

    public static byte[] List(Action cancel)
    {
        var view = Load(); var rows = Rows(view, cancel);
        var s = new StringBuilder("ZENITH-ORBIT-LIST\t1\n");
        foreach (var row in rows)
        {
            cancel();
            var about = About(row.Name, row.Installed, view);
            Line(s, "pkg", row.Name, row.Kind, row.Repository, N((int)row.State), row.Installed, row.Candidate, row.Latest, N(row.Bytes),
                N(row.PayloadBytes), N(row.Files), row.Programs ? "1" : "0", about?.Title, about?.Summary, about?.Category, about?.Icon, row.Reason);
        }
        if (view.Error.Length > 0) { Line(s, "error", Words(view.Error)); }
        return Encoding.UTF8.GetBytes(s.ToString());
    }

    /// <summary>The package's about file: the installed copy, else the one kept from a downloaded archive.</summary>
    private static PackageAbout? About(string name, string? installedVersion, View view)
    {
        if (name == "zenith-kernel") { return null; }
        var store = new NativePackageStore();
        try
        {
            foreach (var item in view.Installed)
            {
                if (item.Manifest.Name != name) { continue; }
                foreach (var file in item.Manifest.Files)
                {
                    if (file.Path == PackageAbout.PathOf(name) && store.State(file.Path) != PackagePathState.Missing)
                    { return PackageAbout.Parse(Encoding.UTF8.GetString(store.Read(file.Path, PackageAbout.MaximumBytes))); }
                }
            }
            string cached = AboutCache + "/" + name + ".about";
            if (store.State(cached) != PackagePathState.Missing) { return PackageAbout.Parse(Encoding.UTF8.GetString(store.Read(cached, PackageAbout.MaximumBytes))); }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Everything about one package. With <paramref name="fetch"/>, a package that is not installed is
    /// downloaded (verified against the signed index) to read its own description; -16 while the service is busy.</summary>
    public static byte[] Details(string name, bool fetch, Action cancel)
    {
        var view = Load();
        RepositoryEntry? latest = null;
        foreach (var entry in view.Catalog)
        { if (entry.Kind == "data" && entry.Name == name && (latest is null || PackageVersions.Compare(entry.Version, latest.Version) > 0)) { latest = entry; } }
        InstalledPackage? installed = null;
        foreach (var item in view.Installed) { if (item.Manifest.Name == name) { installed = item; } }
        if (latest is null && installed is null && name != "zenith-kernel") { throw new ArgumentException("unknown package"); }
        PackageRow? row = null;
        foreach (var r in Rows(view, cancel)) { if (r.Name == name) { row = r; } }
        var about = About(name, installed?.Manifest.Version, view);
        bool fetched = false;
        if (about is null && fetch && latest is not null && SystemMounts.Mode == BootMode.Installed && HasAbout(latest.Manifest))
        {
            string? failure = null;
            bool ran = PackageCommands.TryExclusive(() =>
            {
                var scope = CredentialContext.Enter(Credentials.Root);
                try { KeepAbout(latest, cancel); fetched = true; }
                catch (Exception e) { if (e is OperationCanceledException) { scope.Dispose(); throw; } failure = e.Message; }
                scope.Dispose();
            });
            if (!ran) { failure = "busy"; }
            if (fetched) { about = About(name, null, view); }
            else if (failure == "busy") { throw new InvalidOperationException("busy"); }
            else { Log.Write("pkg", "orbit details of " + name + ": " + failure); }
        }
        var manifest = installed is not null && (row?.Candidate is null) ? installed.Manifest : (row?.Candidate is not null ? Find(view.Catalog, name, row.Candidate)?.Manifest : null) ?? latest?.Manifest ?? installed?.Manifest;
        var s = new StringBuilder("ZENITH-ORBIT-DETAILS\t1\n");
        Line(s, "name", name);
        if (row is not null)
        {
            Line(s, "state", N((int)row.State), row.Installed, row.Candidate, row.Latest, row.Repository, N(row.Bytes), N(row.PayloadBytes), row.Reason);
        }
        if (about is not null)
        {
            Line(s, "title", about.Title); Line(s, "summary", about.Summary); Line(s, "description", about.Description);
            Line(s, "category", about.Category); Line(s, "icon", about.Icon);
            if (about.Replaces.Length > 0) { Line(s, "replaces", about.Replaces); }
            foreach (var change in about.Changes) { Line(s, "change", change.Version, change.Text); }
            foreach (string planned in about.Planned) { Line(s, "planned", planned); }
        }
        else { Line(s, "noabout", latest is not null && HasAbout(latest.Manifest) ? "download" : "none"); }
        if (manifest is not null)
        {
            foreach (var capability in PackageAbout.Capabilities(manifest)) { Line(s, "can", capability.Title, capability.Detail, capability.Allowed ? "1" : "0"); }
            foreach (var need in manifest.Dependencies) { Line(s, "needs", PackageAbout.Need(need)); }
            foreach (string conflict in manifest.Conflicts) { Line(s, "conflicts", conflict); }
            int files = 0;
            foreach (var file in manifest.Files) { if (files++ < 64) { Line(s, "file", file.Path, N(file.Length)); } }
        }
        string? digest = row?.Candidate is not null ? Find(view.Catalog, name, row.Candidate)?.Digest : installed?.ArchiveDigest ?? latest?.Digest;
        Line(s, "digest", digest);
        Line(s, "signed", installed?.KeyId ?? (latest is not null ? "index" : "-"));
        foreach (var t in PackageHistory.Read(new NativePackageStore()))
        {
            foreach (string change in t.Changes)
            {
                string[] f = change.Split(' ');
                if (f.Length >= 2 && (f[1] == name || (name == "zenith-kernel" && f[0] == "kernel")))
                { Line(s, "mine", N(t.Time), PackageHistory.Words(change), t.User, t.Source); }
            }
        }
        if (fetched) { Line(s, "fetched", "1"); }
        return Encoding.UTF8.GetBytes(s.ToString());
    }
    private static int Components() { int n = 0; foreach (var _ in BootstrapComponents.All) { n++; } return n; }
    private static bool HasAbout(PackageManifest manifest)
    {
        foreach (var file in manifest.Files) { if (file.Path == PackageAbout.PathOf(manifest.Name)) { return true; } }
        return false;
    }
    private static RepositoryEntry? Find(IReadOnlyList<RepositoryEntry> catalog, string name, string version)
    {
        foreach (var entry in catalog) { if (entry.Kind == "data" && entry.Name == name && entry.Version == version) { return entry; } }
        return null;
    }
    /// <summary>Downloads (or reuses) the archive, verifies it, and keeps its about file for Orbit.</summary>
    private static void KeepAbout(RepositoryEntry entry, Action cancel)
    {
        var package = NativeRepositories.Data(entry, new PackageReporter { Cancel = cancel });
        PackageFile? about = null;
        foreach (var file in package.Manifest.Files) { if (file.Path == PackageAbout.PathOf(entry.Name)) { about = file; } }
        if (about is null || about.Length > PackageAbout.MaximumBytes) { return; }
        var store = new NativePackageStore();
        if (store.State(AboutCache) == PackagePathState.Missing) { store.Directory(AboutCache); }
        store.Write(AboutCache + "/" + entry.Name + ".about", package.Read(about), 0x1a4); store.Sync();
    }

    // ---------- Plans.
    public static (List<string> Install, List<string> Remove, bool Kernel) ParseChanges(string text)
    {
        var install = new List<string>(); var remove = new List<string>(); bool kernel = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim(); if (line.Length == 0) { continue; }
            string[] f = line.Split(' ');
            if (f.Length == 1 && f[0] == "kernel") { kernel = true; }
            else if (f.Length == 2 && f[0] is "install" or "upgrade" && PackageManifest.ValidName(f[1]) && f[1] != "zenith-kernel") { install.Add(f[1]); }
            else if (f.Length == 2 && f[0] == "remove" && PackageManifest.ValidName(f[1])) { remove.Add(f[1]); }
            else { throw new ArgumentException("invalid plan line"); }
        }
        if (install.Count + remove.Count == 0 && !kernel) { throw new ArgumentException("empty plan"); }
        return (install, remove, kernel);
    }
    private static ChangePlan MakePlan(View view, string text, Action cancel)
    {
        var (install, remove, kernel) = ParseChanges(text);
        var store = new NativePackageStore();
        int upgrades = 0;
        foreach (var row in Rows(view, cancel)) { if (row.State == PackageRowState.Update && row.Kind != "kernel") { upgrades++; } }
        return PackageCatalogView.Plan(view.Catalog, SafeRepositories(), view.Installed, install, remove, kernel, view.Kernel?.Running,
            view.Kernel?.HighWater ?? "0.0.0", digest => store.State(NativePackageCache.Path + "/" + digest + ".zpk") != PackagePathState.Missing, upgrades, cancel);
    }
    public static byte[] Plan(string text, Action cancel)
    {
        var view = Load();
        if (view.Catalog.Count == 0 && view.Error.Length > 0) { throw new IOException(Words(view.Error)); }
        var plan = MakePlan(view, text, cancel);
        var s = new StringBuilder("ZENITH-ORBIT-PLAN\t1\n");
        foreach (var a in plan.Actions)
        { Line(s, "action", a.Verb.ToString().ToLowerInvariant(), a.Name, a.From, a.To, a.Repository, N(a.Bytes), a.AlsoNeeded ? "1" : "0"); }
        foreach (string blocker in plan.Blockers) { Line(s, "blocked", blocker); }
        ulong free = 0; try { free = new NativePackageStore().FreeBytes; } catch (Exception) { }
        Line(s, "sizes", N(plan.DownloadBytes), N(plan.DiskChange), N((long)free));
        Line(s, "restart", plan.Restart ? "1" : "0");
        foreach (string command in plan.Command.Split('\n')) { if (command.Length > 0) { Line(s, "command", command); } }
        return Encoding.UTF8.GetBytes(s.ToString());
    }

    // ---------- Jobs (root or the grant for apply; refresh for everyone: signed and rollback-protected).
    public static long Start(string kind, string text, int pid, uint realUid)
    {
        if (kind == "apply")
        {
            if (!CredentialContext.Current.IsRoot) { throw new UnauthorizedAccessException("changing packages needs permission"); }
            _ = ParseChanges(text);
        }
        if (SystemMounts.Mode != BootMode.Installed) { throw new IOException("Packages can only be changed on an installed system, not on the live system."); }
        // The background update check steps aside for a person's request: it is asked to stop (it only downloads
        // package lists) and given up to ten seconds.
        if (pid != 0 && s_job is { Pid: 0, State: 1 } background)
        {
            background.CancelRequested = true;
            for (int waited = 0; background.State == 1 && waited < 10_000; waited += 50) { Thread.Sleep(50); }
            Log.Write("pkg", "background update check " + (background.State == 1 ? "still running" : "stopped") + " for pid " + pid);
        }
        PackageJob job;
        lock (s_gate)
        {
            if (Busy) { return -16; }
            job = new PackageJob { Id = ++s_next, Pid = pid, Kind = kind, State = 1, Step = kind == "apply" ? "Preparing" : "Checking for updates" };
            s_job = job;
        }
        string user = Name(realUid);
        Log.Write("pkg", "orbit job " + job.Id + " " + kind + " for pid " + pid + " (" + user + ")");
        void Body()
        {
            var scope = CredentialContext.Enter(Credentials.Root);
            string? failure = null; bool cancelled = false;
            bool ran = false;
            try
            {
                ran = PackageCommands.TryExclusive(() =>
                {
                    PackageHistory.Source = "Orbit"; PackageHistory.Command = "";
                    Bind(); PackageHistory.Who = () => (realUid, user);
                    if (kind == "apply") { Apply(job, text); } else { Refresh(job); }
                });
            }
            catch (Exception e) { cancelled = e is OperationCanceledException; failure = cancelled ? "Cancelled; nothing changed" : e.Message; ran = true; }
            if (!ran)
            {
                string blocking = Zenith.Core.Processes.NativeProcess.Processes.BlockingNames();
                failure = Zenith.Core.Processes.NativeProcess.IsRunning && blocking.Length > 0
                    ? "Close these programs first, they keep packages from changing: " + blocking
                    : "Another package change or system update is running; try again when it has finished.";
            }
            if (failure is not null && kind == "apply" && !cancelled)
            {
                PackageHistory.Append(new NativePackageStore(), new(Now, realUid, user, "Orbit", false, job.Planned,
                    PackageHistory.Command, Words(failure) + (job.Committing ? "" : "; nothing changed")));
            }
            PackageHistory.Source = "shell"; PackageHistory.Command = "";
            scope.Dispose();
            lock (s_gate)
            {
                if (failure is null) { job.State = 2; }
                else { job.Message = Words(failure); job.State = cancelled ? 4 : 3; }
                job.Step = failure is null ? "Done" : cancelled ? "Cancelled" : "Failed";
            }
            Log.Write("pkg", "orbit job " + job.Id + " " + (failure is null ? "done: " + job.Message : (cancelled ? "cancelled" : "failed: " + failure)));
            if (cancelled && kind == "refresh" && job.Pid == 0) { s_nextCheck = Math.Min(s_nextCheck, NowMicros() + RetryMicros); }
            // The background check (pid 0) tells about updates; Orbit shows its own checks itself.
            if (failure is null && kind == "refresh" && job.Pid == 0)
            {
                int updates = 0;
                try { updates = UpdateCount(); } catch (Exception) { updates = 0; }
                if (updates > 0 && updates != s_lastOffered)
                {
                    Zenith.Core.Notifications.NotificationCenter.FromSystem("Orbit", updates == 1 ? "1 update is ready" : updates + " updates are ready",
                        "Open Orbit to see what changes and apply them.", "package",
                        new Zenith.Core.Notifications.NotificationCenter.NoteAction("Show updates", "Orbit", new[] { "updates" }));
                }
                s_lastOffered = updates;
            }
            // Outside the lock: the sound and the notification never hold up a status call.
            if (failure is null && kind == "apply") { Zenith.Core.Audio.SystemSounds.Play("done"); }
            // Results of a package change are notifications too (U14): Orbit may be closed or behind another window.
            if (kind == "apply" && !cancelled)
            {
                string text = failure is null ? job.Message : Words(failure);
                if (failure is null && job.Restart)
                {
                    // A staged kernel takes effect at restart: the notification offers it.
                    Zenith.Core.Notifications.NotificationCenter.FromSystem("Orbit", "Restart to finish the update",
                        text.Length > 160 ? text[..157] + "..." : text, "rotate-ccw",
                        new Zenith.Core.Notifications.NotificationCenter.NoteAction("Restart now", "", Array.Empty<string>(), true),
                        new Zenith.Core.Notifications.NotificationCenter.NoteAction("Later", "", Array.Empty<string>()));
                }
                else
                Zenith.Core.Notifications.NotificationCenter.FromSystem("Orbit", failure is null ? "Changes applied" : "Changes not applied",
                    text.Length > 160 ? text[..157] + "..." : text, failure is null ? "package" : "triangle-alert");
            }
        }
        new Thread(Body).Start();
        return job.Id;
    }

    private static PackageReporter Reporter(PackageJob job) => new()
    {
        Cancel = () => { if (job.CancelRequested && !job.Committing) { throw new OperationCanceledException(); } },
        Say = text => { lock (s_gate) { job.Step = text; } },
        Progress = text =>
        {
            const string fetch = "fetch progress ";
            if (text.StartsWith(fetch, StringComparison.Ordinal))
            {
                string[] v = text.Substring(fetch.Length).Split('/');
                if (v.Length == 2 && long.TryParse(v[0], out long done) && long.TryParse(v[1], out long total)) { lock (s_gate) { job.Done = done; job.Total = total; } }
                return;
            }
            lock (s_gate) { job.Step = text; }
        },
    };

    private static void Refresh(PackageJob job)
    {
        NativeRepositories.Refresh(Reporter(job));
        job.Message = "The package lists are up to date.";
    }

    private static void Apply(PackageJob job, string text)
    {
        PackageCommands.Mutable();
        var reporter = Reporter(job);
        var view = Load();
        if (view.Error.Length > 0 && view.Catalog.Count == 0) { throw new IOException(view.Error); }
        var (_, _, kernel) = ParseChanges(text);
        if (kernel) { var ready = Zenith.Core.Boot.NativeKernelUpdates.ReadyForUpdate(); view.Kernel = (view.Kernel?.Running ?? ready.HighWater, ready.HighWater, ready.Pending); }
        var plan = MakePlan(view, text, reporter.Cancel);
        PackageHistory.Command = plan.Command.Replace("\n", "; ");
        if (plan.Blockers.Count > 0) { throw new IOException(plan.Blockers[0]); }
        if (plan.Empty) { throw new IOException("Nothing to change"); }
        job.Changes = plan.Actions.Count;
        foreach (var a in plan.Actions)
        {
            job.Planned.Add(a.Verb switch
            {
                PlanVerb.Install => "install " + a.Name + " " + a.To,
                PlanVerb.Upgrade => "upgrade " + a.Name + " " + a.From + " " + a.To,
                PlanVerb.Remove => "remove " + a.Name + " " + a.From,
                _ => "kernel " + (a.From ?? "0.0.0") + " " + a.To,
            });
        }
        var downloads = new List<RepositoryEntry>(plan.Downloads);
        if (plan.Kernel is not null) { downloads.Add(plan.Kernel); }
        NativePackageCache.EnsureSpace(downloads, reporter.Say);
        var packages = new List<SignedPackage>();
        foreach (var entry in plan.Downloads) { reporter.Cancel(); packages.Add(NativeRepositories.Data(entry, reporter)); }
        string? kernelPath = null;
        if (plan.Kernel is not null)
        {
            kernelPath = NativeRepositories.Download(plan.Kernel, reporter);
            reporter.Say("Checking the new kernel");
            Zenith.Core.Boot.NativeKernelUpdates.CheckCandidate(kernelPath);
        }
        _ = NativeRepositories.Catalog();
        reporter.Cancel();
        if (packages.Count + plan.Removals.Count > 0)
        {
            job.Committing = true; reporter.Say("Applying the changes");
            new PackageManager(new NativePackageStore()).Change(packages, plan.Removals, () => { }, reporter.Progress);
        }
        if (kernelPath is not null)
        {
            job.Committing = true; reporter.Say("Staging the new kernel");
            string staged = Zenith.Core.Boot.NativeKernelUpdates.Stage(kernelPath, () => { }, reporter.Progress);
            RecordKernel(view.Kernel?.Running ?? "0.0.0", staged);
            job.Restart = true;
        }
        NativePackageCache.AfterTransaction(reporter.Say);
        job.Message = plan.Actions.Count + (plan.Actions.Count == 1 ? " change applied" : " changes applied") + (job.Restart ? "; the new kernel takes effect at restart" : "");
    }

    public static byte[] Job(int pid)
    {
        var s = new StringBuilder("ZENITH-ORBIT-JOB\t1\n");
        lock (s_gate)
        {
            var job = s_job;
            if (job is null) { Line(s, "job", "0", "-", "0"); }
            else
            {
                Line(s, "job", N(job.Id), job.Kind, N(job.State), job.Pid == pid ? "1" : "0", N(job.Done), N(job.Total), job.Restart ? "1" : "0", N(job.Changes));
                Line(s, "step", job.Step); Line(s, "message", job.Message);
            }
        }
        return Encoding.UTF8.GetBytes(s.ToString());
    }

    /// <summary>Waits up to <paramref name="milliseconds"/> (at most 5 s) for the running job to finish, so a program
    /// without a window does not spin; 1 when no job is running any more, 0 when it still runs.</summary>
    public static int Wait(uint milliseconds, Action cancel)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew(); if (milliseconds > 5000) { milliseconds = 5000; }
        while (true)
        {
            var job = s_job;
            if (job is null || job.State != 1) { return 1; }
            if (clock.ElapsedMilliseconds >= milliseconds) { return 0; }
            cancel(); Thread.Sleep(20);
        }
    }
    public static int Cancel(int pid, bool root)
    {
        lock (s_gate)
        {
            var job = s_job;
            if (job is null || job.State != 1) { return -3; }
            if (job.Pid != pid && !root) { return -1; }
            job.CancelRequested = true; return job.Committing ? -16 : 0;
        }
    }

    public static byte[] History()
    {
        var s = new StringBuilder("ZENITH-ORBIT-HISTORY\t1\n");
        var all = PackageHistory.Read(new NativePackageStore());
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var t = all[i];
            Line(s, "transaction", N(t.Time), N(t.Uid), t.User, t.Source, t.Succeeded ? "1" : "0", t.Command, t.Message);
            foreach (string change in t.Changes) { Line(s, "change", PackageHistory.Words(change)); }
        }
        return Encoding.UTF8.GetBytes(s.ToString());
    }

    public static byte[] Sources()
    {
        var s = new StringBuilder("ZENITH-ORBIT-SOURCES\t1\n");
        AppendSources(s);
        return Encoding.UTF8.GetBytes(s.ToString());
    }
}
