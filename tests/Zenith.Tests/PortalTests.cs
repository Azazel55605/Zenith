using System;
using System.Buffers.Binary;
using System.Text;
using Zenith.Core.Portal;
using Zenith.Core.Processes;

namespace Zenith.Tests;

[Collection("portal")]
public class PortalTests : IDisposable
{
    private readonly AuthorizationPortal _auth;
    private bool _rootUnlocked = true;
    public PortalTests()
    {
        PortalBroker.Reset(); ProcessPrivileges.Reset();
        _auth = new AuthorizationPortal(uid => uid == 0 || uid == 1000, () => _rootUnlocked, uid => uid == 0 ? "root" : uid == 1000 ? "user" : "guest");
        PortalBroker.Register(_auth);
    }
    public void Dispose() { PortalBroker.Reset(); ProcessPrivileges.Reset(); }

    private static byte[] Action(string id) { var b = new byte[64]; Encoding.ASCII.GetBytes(id).CopyTo(b, 0); return b; }
    private static PortalCaller User(int pid = 40, uint uid = 1000) => new(pid, uid, uid, "equinox");
    private long Ask(PortalCaller caller, string method = "Authorize", string action = "org.zenith.disks.manage")
        => PortalBroker.Open(caller, AuthorizationPortal.InterfaceName, method, Action(action));

    [Fact]
    public void WithoutADesktopNobodyCanAnswer()
    {
        long handle = Ask(User());
        Assert.True(handle > 0);
        var r = PortalBroker.Find(handle, 40)!;
        Assert.Equal(PortalState.Unavailable, r.State);
        Assert.Contains("sudo", r.Message);
        Assert.False(ProcessPrivileges.Holds(40, Privilege.Disks));
    }

    [Fact]
    public void AnAdministratorApprovesWithTheirOwnPassword()
    {
        PortalBroker.Attach(name => name == AuthorizationPortal.InterfaceName);
        long handle = Ask(User());
        var shown = PortalBroker.TakeNext()!;
        Assert.Equal(handle, shown.Handle); Assert.Equal(PortalState.Shown, shown.State);
        var prompt = Assert.IsType<AuthorizationPrompt>(shown.Prompt);
        Assert.Equal("user", prompt.Approver); Assert.True(prompt.OwnPassword);
        Assert.Null(PortalBroker.TakeNext());
        Assert.True(_auth.Approve(shown, 1000, "user"));
        Assert.Equal(PortalState.Approved, shown.State);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(shown.Response));
        Assert.True(ProcessPrivileges.Holds(40, Privilege.Disks));
        Assert.False(ProcessPrivileges.Holds(40, Privilege.SystemSettings));
        Assert.False(ProcessPrivileges.Holds(41, Privilege.Disks));
        // Asking again is answered at once.
        long again = Ask(User());
        Assert.Equal(PortalState.Approved, PortalBroker.Find(again, 40)!.State);
        PortalBroker.ProcessExited(40); ProcessPrivileges.Drop(40);
        Assert.Null(PortalBroker.Find(handle, 40));
        Assert.False(ProcessPrivileges.Holds(40, Privilege.Disks));
    }

    [Fact]
    public void OthersNeedRootAndAWrongApproverIsRefused()
    {
        PortalBroker.Attach(_ => true);
        Ask(User(50, 2000));
        var shown = PortalBroker.TakeNext()!;
        var prompt = (AuthorizationPrompt)shown.Prompt!;
        Assert.Equal("root", prompt.Approver); Assert.False(prompt.OwnPassword);
        Assert.False(_auth.Approve(shown, 1000, "user"));   // an administrator, but not the one asked
        Assert.Equal(PortalState.Denied, shown.State);
        Assert.False(ProcessPrivileges.Holds(50, Privilege.Disks));
        _rootUnlocked = false;
        long none = Ask(User(51, 2000));
        Assert.Equal(PortalState.Denied, PortalBroker.Find(none, 51)!.State);
    }

    [Fact]
    public void RootProgramsCheckAndReleaseNeedNoDialog()
    {
        PortalBroker.Attach(_ => true);
        long root = Ask(User(60, 0));
        Assert.Equal(PortalState.Approved, PortalBroker.Find(root, 60)!.State);
        long check = Ask(User(61), "Check");
        Assert.Equal(PortalState.Denied, PortalBroker.Find(check, 61)!.State);
        ProcessPrivileges.Grant(61, Privilege.Disks);
        Assert.Equal(PortalState.Approved, PortalBroker.Find(Ask(User(61), "Check"), 61)!.State);
        Ask(User(61), "Release");
        Assert.False(ProcessPrivileges.Holds(61, Privilege.Disks));
        Assert.Null(PortalBroker.TakeNext());
    }

    [Fact]
    public void RequestsAreBoundedPrivateAndWithdrawable()
    {
        PortalBroker.Attach(_ => true);
        Assert.Equal(-38, PortalBroker.Open(User(), "org.example.Missing", "Authorize", Action("x.y")));
        Assert.Equal(-2, Ask(User(), action: "org.zenith.nothing"));
        Assert.Equal(-38, Ask(User(), "Explode"));
        Assert.Equal(-22, PortalBroker.Open(User(), AuthorizationPortal.InterfaceName, "Authorize", new byte[3]));
        long first = Ask(User());
        Assert.Null(PortalBroker.Find(first, 41));   // another process cannot read it
        Assert.Equal(-9, PortalBroker.Close(first, 41));
        long last = 0;
        for (int i = 1; i < PortalBroker.MaximumPerProcess; i++) { last = Ask(User()); Assert.True(last > 0); }
        Assert.Equal(-24, Ask(User()));
        var shown = PortalBroker.TakeNext()!;
        Assert.Equal(0, PortalBroker.Close(first, 40));
        Assert.Equal(PortalState.Cancelled, shown.State);
        Assert.False(_auth.Approve(shown, 1000, "user"));   // too late: withdrawn
        Assert.False(ProcessPrivileges.Holds(40, Privilege.Disks));
        PortalBroker.Detach();   // the session ended: whatever still waits is cancelled
        Assert.Equal(PortalState.Cancelled, PortalBroker.Find(last, 40)!.State);
        Assert.Null(PortalBroker.TakeNext());
    }

    [Fact]
    public void GrantsCoverOnlyTheirCalls()
    {
        Assert.Equal(Privilege.Disks, ProcessPrivileges.For(648, 0));
        Assert.Equal(Privilege.SystemSettings, ProcessPrivileges.For(621, 0));
        Assert.Equal(Privilege.SystemSettings, ProcessPrivileges.For(622, 0));
        Assert.Equal(Privilege.None, ProcessPrivileges.For(622, 1));   // personal appearance stays personal
        Assert.Equal(Privilege.None, ProcessPrivileges.For(2, 0));     // files never
        Assert.Equal(Privilege.None, ProcessPrivileges.For(650, 0));
        ProcessPrivileges.Grant(70, Privilege.Disks);
        Assert.True(ProcessPrivileges.Elevates(70, 647, 0));
        Assert.False(ProcessPrivileges.Elevates(70, 621, 0));
        Assert.False(ProcessPrivileges.Elevates(71, 647, 0));
    }

    [Fact]
    public void NativeAbiRoundTrip()
    {
        PortalBroker.Attach(_ => true);
        byte[] request = new byte[ProcessPortal.RequestBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1);
        Encoding.ASCII.GetBytes(AuthorizationPortal.InterfaceName).CopyTo(request, 8);
        Encoding.ASCII.GetBytes("Authorize").CopyTo(request, 72);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(104), 64);
        Encoding.ASCII.GetBytes("org.zenith.disks.manage").CopyTo(request, 112);
        long handle = ProcessPortal.Open(request, User());
        Assert.True(handle > 0);
        byte[] poll = ProcessPortal.Poll(PortalBroker.Find(handle, 40)!);
        Assert.Equal((uint)PortalState.Pending, BinaryPrimitives.ReadUInt32LittleEndian(poll.AsSpan(4)));
        Assert.Equal(handle, BinaryPrimitives.ReadInt64LittleEndian(poll.AsSpan(8)));
        request[112 + 70] = 1;   // bytes past payload_bytes must be zero
        Assert.Equal(-22, ProcessPortal.Open(request, User()));
        byte[] info = ProcessPortal.Info();
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8)));
        Assert.Equal(AuthorizationPortal.InterfaceName, Encoding.ASCII.GetString(info, 16, AuthorizationPortal.InterfaceName.Length));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(16 + 64)));
    }
}
