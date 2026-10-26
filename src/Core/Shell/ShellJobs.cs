using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Zenith.Core.Security;

namespace Zenith.Core.Shell;

internal readonly record struct ShellJobInfo(int Id, bool Running, int? Status);

internal sealed partial class Shell
{
    private const int MaximumBackgroundJobs = 4;
    private const int MaximumJobHistory = 20;

    private sealed class BackgroundJob(int id, Shell shell)
    {
        public readonly int Id = id;
        public readonly Shell Shell = shell;
        public int Done, Status;
    }

    private sealed class SessionJobs
    {
        public readonly object Gate = new();
        public readonly Dictionary<int, BackgroundJob> Entries = new();
        public int Next;
    }

    private readonly SessionJobs _jobs = new();
    private readonly bool _background;
    private int _lastJob;
    public bool IsBackground => _background;

    public bool HasBackgroundJobs
    {
        get
        {
            lock (_jobs.Gate)
            {
                foreach (var job in _jobs.Entries.Values)
                {
                    if (Volatile.Read(ref job.Done) == 0) { return true; }
                }
                return false;
            }
        }
    }

    public IReadOnlyList<ShellJobInfo> Jobs()
    {
        lock (_jobs.Gate)
        {
            var result = new List<ShellJobInfo>();
            foreach (var job in _jobs.Entries.Values)
            {
                bool running = Volatile.Read(ref job.Done) == 0;
                result.Add(new(job.Id, running, running ? null : job.Status));
            }
            result.Sort((a, b) => a.Id.CompareTo(b.Id));
            return result;
        }
    }

    public bool StopJob(int id)
    {
        BackgroundJob? job;
        lock (_jobs.Gate)
        {
            if (!_jobs.Entries.TryGetValue(id, out job) || Volatile.Read(ref job.Done) != 0) { return false; }
        }
        job.Shell.Cancel();
        return true;
    }

    public void CancelSession()
    {
        Cancel();
        lock (_jobs.Gate)
        {
            foreach (var job in _jobs.Entries.Values)
            {
                if (Volatile.Read(ref job.Done) == 0) { job.Shell.Cancel(); }
            }
        }
    }

    private void ClearCompletedJobs()
    {
        lock (_jobs.Gate)
        {
            var completed = new List<int>();
            foreach (var item in _jobs.Entries)
            {
                if (Volatile.Read(ref item.Value.Done) != 0) { completed.Add(item.Key); }
            }
            foreach (int id in completed) { _jobs.Entries.Remove(id); }
        }
        _lastJob = 0;
    }

    public int WaitJobs(int? id = null)
    {
        var selected = new List<BackgroundJob>();
        lock (_jobs.Gate)
        {
            if (id is int wanted)
            {
                if (!_jobs.Entries.TryGetValue(wanted, out var job)) { return 127; }
                selected.Add(job);
            }
            else
            {
                selected.AddRange(_jobs.Entries.Values);
                selected.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
        }
        int status = 0;
        foreach (var job in selected)
        {
            while (Volatile.Read(ref job.Done) == 0)
            {
                if (IsCancelled) { return 130; }
                Thread.Sleep(1);
            }
            status = job.Status;
        }
        return status;
    }

    private int StartBackground(Node command)
    {
        if (_background) { throw new IOException("nested background jobs are not supported"); }
        BackgroundJob job;
        lock (_jobs.Gate)
        {
            int running = 0;
            foreach (var existing in _jobs.Entries.Values)
            {
                if (Volatile.Read(ref existing.Done) == 0) { running++; }
            }
            if (running >= MaximumBackgroundJobs || _jobs.Next == int.MaxValue)
            { throw new IOException("maximum 4 background jobs per session"); }

            var child = new Shell(this, false);
            child.ReadInputLine = null;
            child.ReadSecret = null;
            child.NativeTerminal = null;
            job = new BackgroundJob(++_jobs.Next, child);
            _jobs.Entries.Add(job.Id, job);
            // Allocate the job before reserving a worker: failed setup cannot leak
            // reservations. Other pipelines may change the count before this lock.
            lock (_pipelineWork)
            {
                if (_pipelineWork.Workers >= MaximumPipelineStages - 1)
                {
                    _jobs.Entries.Remove(job.Id);
                    throw new IOException("background/pipeline worker limit reached");
                }
                _pipelineWork.Workers++;
            }
            while (_jobs.Entries.Count > MaximumJobHistory)
            {
                int oldest = int.MaxValue;
                foreach (var existing in _jobs.Entries.Values)
                {
                    if (Volatile.Read(ref existing.Done) != 0 && existing.Id < oldest) { oldest = existing.Id; }
                }
                if (oldest == int.MaxValue) { break; }
                _jobs.Entries.Remove(oldest);
            }
        }
        try
        {
            new Thread(() => RunBackground(job, command)).Start();
        }
        catch
        {
            lock (_pipelineWork) { _pipelineWork.Workers--; }
            job.Status = 1;
            Volatile.Write(ref job.Done, 1);
            throw;
        }
        _lastJob = job.Id;
        _terminal.Write("[" + job.Id + "] started (stdin=/dev/null)\n");
        return 0;
    }

    private void RunBackground(BackgroundJob job, Node command)
    {
        var credentials = CredentialContext.Enter(job.Shell.Identity);
        int status;
        try
        {
            status = job.Shell.Exec(command, new ShellInput(""), _terminal);
            if (job.Shell._control == Control.Exit) { status = job.Shell._controlStatus; }
            if (job.Shell.IsCancelled) { status = 130; }
        }
        catch (Exception error)
        {
            status = job.Shell.IsCancelled || error is OperationCanceledException ? 130 : 1;
        }
        // Explicit cleanup is required by the pinned Cosmos exception runtime.
        credentials.Dispose();
        job.Status = status;
        lock (_pipelineWork) { _pipelineWork.Workers--; }
        Volatile.Write(ref job.Done, 1);
    }
}
