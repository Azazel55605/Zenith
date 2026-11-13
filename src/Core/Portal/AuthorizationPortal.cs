using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Zenith.Core.Portal;

/// <summary>An administrative action a program may ask for, and what granting it gives.</summary>
internal sealed record PortalAction(string Id, string Title, string Description, Privilege Privilege);

/// <summary>What the authorization dialog shows and whom it authenticates.</summary>
internal sealed record AuthorizationPrompt(PortalAction Action, string Approver, bool OwnPassword);

/// <summary>org.zenith.portal.Authorization 1: a program asks for an administrative action
/// (polkit-like). Root programs, and programs already holding it, get it at once. Anyone else is
/// asked in a desktop dialog: an administrator (wheel) types their own password, another account
/// types root's. Granted privileges last until the program exits or releases them.
/// <para>Methods (payload: action id, ASCII, NUL-padded to 64 bytes):
/// <c>Authorize</c> asks; <c>Check</c> answers at once without asking (Approved when held, else
/// Denied); <c>Release</c> gives the privilege back. Response: u32 granted (1/0), u32 privileges held.</para></summary>
internal sealed class AuthorizationPortal : IPortalInterface
{
    public const string InterfaceName = "org.zenith.portal.Authorization";
    public const int Attempts = 3;
    public string Name => InterfaceName;
    public uint Version => 1;

    private readonly Dictionary<string, PortalAction> _actions = new(StringComparer.Ordinal);
    private readonly Func<uint, bool> _isAdministrator;
    private readonly Func<bool> _rootUnlocked;
    private readonly Func<uint, string> _name;

    public AuthorizationPortal(Func<uint, bool> isAdministrator, Func<bool> rootUnlocked, Func<uint, string> name)
    {
        _isAdministrator = isAdministrator; _rootUnlocked = rootUnlocked; _name = name;
        Add(new("org.zenith.disks.manage", "manage disks and drives", "Partition, format, mount and unmount drives, and choose what mounts at startup.", Privilege.Disks));
        Add(new("org.zenith.settings.system", "change settings for all users", "Keyboard layout, time zone, default session, display and the system appearance.", Privilege.SystemSettings));
    }

    /// <summary>A new action (a later app's administrative area). Ids are reverse-DNS like interfaces.</summary>
    public void Add(PortalAction action)
    {
        if (!PortalBroker.ValidName(action.Id) || action.Privilege == Privilege.None) { throw new ArgumentException("invalid portal action"); }
        _actions[action.Id] = action;
    }
    public PortalAction? Action(string id) => _actions.TryGetValue(id, out var a) ? a : null;

    public int Open(PortalRequest request)
    {
        if (request.Payload.Length != 64) { return -22; }
        string? id = Field(request.Payload);
        if (id is null) { return -22; }
        var action = Action(id);
        if (action is null) { return -2; }   // ENOENT: no such action
        var caller = request.Caller;
        switch (request.Method)
        {
            case "Check":
                Answer(request, ProcessPrivileges.Holds(caller.Pid, action.Privilege) || caller.Uid == 0, "checked");
                return 0;
            case "Release":
                ProcessPrivileges.Revoke(caller.Pid, action.Privilege);
                PortalBroker.Finish(request, PortalState.Approved, "released", Result(false, caller.Pid));
                return 0;
            case "Authorize":
                break;
            default:
                return -38;
        }
        if (caller.Uid == 0 || ProcessPrivileges.Holds(caller.Pid, action.Privilege))
        {
            ProcessPrivileges.Grant(caller.Pid, action.Privilege);
            Answer(request, true, caller.Uid == 0 ? "the program runs as root" : "already granted");
            return 0;
        }
        if (_isAdministrator(caller.Uid)) { request.Prompt = new AuthorizationPrompt(action, _name(caller.Uid), true); return 0; }
        if (_rootUnlocked()) { request.Prompt = new AuthorizationPrompt(action, "root", false); return 0; }
        PortalBroker.Finish(request, PortalState.Denied, "no administrator account can approve this", Result(false, caller.Pid));
        return 0;
    }

    /// <summary>The frontend authenticated <paramref name="proven"/> (uid of the account whose password
    /// was typed). Only the prompt's approver counts, and only while that account is an administrator.</summary>
    public bool Approve(PortalRequest request, uint proven, string provenName)
    {
        if (request.Prompt is not AuthorizationPrompt prompt || provenName != prompt.Approver || !_isAdministrator(proven))
        {
            PortalBroker.Finish(request, PortalState.Denied, "authentication did not match the approver", Result(false, request.Caller.Pid));
            return false;
        }
        // Granted before Finish publishes Approved: a program that sees Approved can use it at once.
        ProcessPrivileges.Grant(request.Caller.Pid, prompt.Action.Privilege);
        if (!PortalBroker.Finish(request, PortalState.Approved, "authenticated as " + provenName, Result(true, request.Caller.Pid)))
        { ProcessPrivileges.Revoke(request.Caller.Pid, prompt.Action.Privilege); return false; }
        return true;
    }
    public void Deny(PortalRequest request, string why) => PortalBroker.Finish(request, PortalState.Denied, why, Result(false, request.Caller.Pid));

    private static void Answer(PortalRequest request, bool granted, string message)
        => PortalBroker.Finish(request, granted ? PortalState.Approved : PortalState.Denied, message, Result(granted, request.Caller.Pid));
    private static byte[] Result(bool granted, int pid)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, granted ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)ProcessPrivileges.Held(pid));
        return b;
    }
    private static string? Field(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0); if (end < 0) { return null; }
        foreach (byte b in field[end..]) { if (b != 0) { return null; } }
        foreach (byte b in field[..end]) { if (b < 33 || b > 126) { return null; } }
        return Encoding.ASCII.GetString(field[..end]);
    }
}
