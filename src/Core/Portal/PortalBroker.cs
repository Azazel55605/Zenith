using System;
using System.Collections.Generic;

namespace Zenith.Core.Portal;

/// <summary>Where a portal request stands. Numbers are zenith-native-portal-abi 1.0 (zenith_portal.h).</summary>
internal enum PortalState : uint
{
    Pending = 1,      // waiting for the desktop to show it
    Shown = 2,        // a dialog is on screen
    Approved = 3,
    Denied = 4,       // the person said no (or authentication failed three times)
    Cancelled = 5,    // the program withdrew it, or the session ended
    Failed = 6,       // the interface could not carry it out
    Unavailable = 7,  // nobody can answer: no graphical session, or no frontend for the interface
}

/// <summary>The program asking: its process, account and name as the process table knows it.</summary>
internal sealed record PortalCaller(int Pid, uint Uid, uint Gid, string Program);

/// <summary>One request from one program to one portal interface. The interface fills in what the
/// frontend shows; the frontend answers through <see cref="PortalBroker"/>, never by setting state.</summary>
internal sealed class PortalRequest
{
    public PortalRequest(long handle, PortalCaller caller, IPortalInterface target, string method, byte[] payload)
    { Handle = handle; Caller = caller; Interface = target; Method = method; Payload = payload; }
    public long Handle { get; }
    public PortalCaller Caller { get; }
    public IPortalInterface Interface { get; }
    public string Method { get; }
    public byte[] Payload { get; }
    public PortalState State { get; internal set; } = PortalState.Pending;
    /// <summary>A short line for the program (and logs): why it ended as it did.</summary>
    public string Message { get; internal set; } = "";
    /// <summary>Interface-defined result bytes (at most <see cref="PortalBroker.MaximumResponse"/>).</summary>
    public byte[] Response { get; internal set; } = Array.Empty<byte>();
    /// <summary>Interface-defined data for its frontend dialog.</summary>
    public object? Prompt { get; set; }
    public bool Finished => State >= PortalState.Approved;
}

/// <summary>A portal interface: a named, versioned set of methods that programs call and that may
/// need the person's answer. New interfaces (file chooser, screenshot, open with...) register here
/// with a frontend dialog in the desktop; nothing else in the broker or the ABI changes.</summary>
internal interface IPortalInterface
{
    /// <summary>Reverse-DNS name, ASCII, at most 63 characters: org.zenith.portal.Authorization.</summary>
    string Name { get; }
    uint Version { get; }
    /// <summary>Checks a new request. Returns 0 and leaves it Pending for the frontend, or finishes it
    /// at once through <see cref="PortalBroker.Finish"/> (nothing to ask), or returns a negative errno
    /// to refuse it before it gets a handle.</summary>
    int Open(PortalRequest request);
    /// <summary>The request ended without an answer (cancelled, process exit, session end).</summary>
    void Abandoned(PortalRequest request) { }
}

/// <summary>Desktop Portal: the one place where programs ask for things only the person may decide.
/// Programs open requests (native calls 650 to 653); the graphical session is the frontend, which
/// shows a dialog drawn by the desktop itself (a program cannot draw over it or type into it) and
/// answers. Without a frontend a request that needs one ends Unavailable.</summary>
internal static class PortalBroker
{
    public const int MaximumPayload = 144, MaximumResponse = 136, MaximumPerProcess = 4, MaximumRequests = 32;
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, IPortalInterface> s_interfaces = new(StringComparer.Ordinal);
    private static readonly List<PortalRequest> s_requests = new();
    private static long s_next = 1;
    private static Func<string, bool>? s_frontend;

    public static void Register(IPortalInterface target)
    {
        if (!ValidName(target.Name)) { throw new ArgumentException("invalid portal interface name"); }
        lock (s_gate) { s_interfaces[target.Name] = target; }
    }
    public static IReadOnlyList<(string Name, uint Version, bool Answered)> Interfaces()
    {
        lock (s_gate)
        {
            var list = new List<(string, uint, bool)>();
            foreach (var pair in s_interfaces) { list.Add((pair.Key, pair.Value.Version, s_frontend?.Invoke(pair.Key) == true)); }
            list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            return list;
        }
    }
    public static bool HasFrontend { get { lock (s_gate) { return s_frontend is not null; } } }

    /// <summary>The graphical session attaches as the frontend; <paramref name="supports"/> names the
    /// interfaces it has dialogs for. Detaching cancels everything still waiting for an answer.</summary>
    public static void Attach(Func<string, bool> supports) { lock (s_gate) { s_frontend = supports; } }
    public static void Detach()
    {
        List<PortalRequest> open;
        lock (s_gate)
        {
            s_frontend = null;
            open = s_requests.FindAll(r => !r.Finished);
            foreach (var r in open) { r.State = PortalState.Cancelled; r.Message = "the graphical session ended"; }
        }
        foreach (var r in open) { r.Interface.Abandoned(r); }
    }

    /// <summary>A program's new request: a handle (positive) or a negative errno.</summary>
    public static long Open(PortalCaller caller, string name, string method, byte[] payload)
    {
        if (!ValidName(name) || method.Length is 0 or > 31 || payload.Length > MaximumPayload) { return -22; }
        IPortalInterface? target; PortalRequest request;
        lock (s_gate)
        {
            if (!s_interfaces.TryGetValue(name, out target)) { return -38; }   // ENOSYS: no such interface
            int mine = 0;
            foreach (var r in s_requests) { if (r.Caller.Pid == caller.Pid) { mine++; } }
            if (mine >= MaximumPerProcess) { return -24; }
            if (s_requests.Count >= MaximumRequests) { return -16; }
            request = new PortalRequest(s_next++, caller, target, method, payload);
        }
        int opened;
        try { opened = target.Open(request); }
        catch (Exception) { opened = -5; }
        if (opened < 0) { return opened; }
        lock (s_gate)
        {
            if (!request.Finished && s_frontend?.Invoke(name) != true)
            {
                request.State = PortalState.Unavailable;
                request.Message = s_frontend is null ? "no graphical session can answer; in a terminal use sudo" : "the desktop cannot show this request";
            }
            s_requests.Add(request);
        }
        Log.Write("portal", caller.Program + " (pid " + caller.Pid + ") " + name + "." + method + ": " + Describe(request));
        return request.Handle;
    }

    /// <summary>The caller's own request only: another process's handle reads as unknown.</summary>
    public static PortalRequest? Find(long handle, int pid)
    {
        lock (s_gate) { foreach (var r in s_requests) { if (r.Handle == handle && r.Caller.Pid == pid) { return r; } } }
        return null;
    }

    /// <summary>Withdraws (if still open) and forgets the request.</summary>
    public static int Close(long handle, int pid)
    {
        PortalRequest? request;
        lock (s_gate)
        {
            request = null;
            foreach (var r in s_requests) { if (r.Handle == handle && r.Caller.Pid == pid) { request = r; } }
            if (request is null) { return -9; }
            s_requests.Remove(request);
            if (request.Finished) { return 0; }
            request.State = PortalState.Cancelled; request.Message = "withdrawn by the program";
        }
        request.Interface.Abandoned(request);
        return 0;
    }

    /// <summary>A process ended: its requests go, open ones are cancelled.</summary>
    public static void ProcessExited(int pid)
    {
        var open = new List<PortalRequest>();
        lock (s_gate)
        {
            for (int i = s_requests.Count - 1; i >= 0; i--)
            {
                var r = s_requests[i];
                if (r.Caller.Pid != pid) { continue; }
                s_requests.RemoveAt(i);
                if (!r.Finished) { r.State = PortalState.Cancelled; r.Message = "the program ended"; open.Add(r); }
            }
        }
        foreach (var r in open) { r.Interface.Abandoned(r); }
    }

    /// <summary>For the frontend: the oldest request that is waiting to be shown; it becomes Shown.</summary>
    public static PortalRequest? TakeNext()
    {
        lock (s_gate)
        {
            foreach (var r in s_requests)
            {
                if (r.State != PortalState.Pending) { continue; }
                r.State = PortalState.Shown; return r;
            }
        }
        return null;
    }

    /// <summary>Ends a request with an answer. A request already ended (withdrawn meanwhile) stays as it was.</summary>
    public static bool Finish(PortalRequest request, PortalState state, string message, byte[]? response = null)
    {
        if (state < PortalState.Approved) { throw new ArgumentOutOfRangeException(nameof(state)); }
        if (response is not null && response.Length > MaximumResponse) { throw new ArgumentException("portal response too long"); }
        lock (s_gate)
        {
            if (request.Finished) { return false; }
            request.State = state; request.Message = message.Length > 95 ? message[..95] : message;
            request.Response = response ?? Array.Empty<byte>();
        }
        Log.Write("portal", request.Caller.Program + " (pid " + request.Caller.Pid + ") " + request.Interface.Name + "." + request.Method + ": " + Describe(request));
        return true;
    }

    private static string Describe(PortalRequest r) => r.State switch
    {
        PortalState.Pending => "waiting for the desktop",
        PortalState.Shown => "shown",
        PortalState.Approved => "approved" + (r.Message.Length > 0 ? " (" + r.Message + ")" : ""),
        PortalState.Denied => "denied" + (r.Message.Length > 0 ? " (" + r.Message + ")" : ""),
        PortalState.Cancelled => "cancelled (" + r.Message + ")",
        PortalState.Failed => "failed (" + r.Message + ")",
        _ => "unavailable (" + r.Message + ")",
    };

    public static bool ValidName(string name)
    {
        if (name.Length is < 3 or > 63) { return false; }
        foreach (char c in name) { if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_')) { return false; } }
        return name[0] != '.' && name[^1] != '.';
    }

    /// <summary>Host tests only: forget every interface and request.</summary>
    internal static void Reset() { lock (s_gate) { s_interfaces.Clear(); s_requests.Clear(); s_frontend = null; } }
}
