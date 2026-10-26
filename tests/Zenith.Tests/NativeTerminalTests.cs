using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Zenith.Core.IO;
using Zenith.Core.Shell;
using Zenith.Core.Processes;

namespace Zenith.Tests;
public class NativeTerminalTests
{
    [Fact]
    public void UartRawInputPreservesHighBytesAndEchoDecodesSplitUtf8()
    {
        string echoed="";var terminal=new TerminalInputChannel(()=>null,s=>echoed+=s,()=>{});
        using var lease=terminal.Acquire(()=>false);lease.SetMode(2);
        terminal.ReceiveBytes(new byte[]{0xc3});Assert.Equal("",echoed);
        terminal.ReceiveBytes(new byte[]{0xbc});Assert.Equal("ü",echoed);
        var data=new byte[2];Assert.Equal(2,lease.Read(data));Assert.Equal(new byte[]{0xc3,0xbc},data);
        lease.SetMode(0);terminal.ReceiveBytes(new byte[]{0xff,0});Assert.Equal(2,lease.Read(data));
        Assert.Equal(new byte[]{0xff,0},data);Assert.Equal("ü",echoed);
    }
    [Fact]
    public void RawBytesAreImmediateUnechoedAndRestoredOnClose()
    {
        string output="";var terminal=new TerminalInputChannel(()=>"cooked",s=>output+=s,()=>{});
        using var lease=terminal.Acquire(()=>false);
        Assert.Equal(7,lease.GetMode());Assert.False(terminal.Receive('x'));Assert.Equal(0,lease.SetMode(0));
        Assert.True(terminal.Receive("\x1b[Aü\x03\x04"));var buffer=new byte[16];int n=lease.Read(buffer);
        Assert.Equal("\x1b[Aü\x03\x04",Encoding.UTF8.GetString(buffer,0,n));Assert.Equal("",output);
        lease.Dispose();Assert.False(terminal.RawActive);Assert.Equal(-9,lease.GetMode());Assert.False(terminal.Receive('x'));
        using var next=terminal.Acquire(()=>false);Assert.Equal(7,next.GetMode());
    }
    [Fact]
    public void SignalAndEchoFlagsAreExplicitAndCancellationUnblocksRead()
    {
        bool cancelled=false;string output="";
        var terminal=new TerminalInputChannel(()=>null,s=>output+=s,()=>cancelled=true);
        using var lease=terminal.Acquire(()=>cancelled);
        Assert.Equal(0,lease.SetMode(6));terminal.Receive('q');Assert.Equal("q",output);
        Assert.Equal(1,lease.Read(new byte[1]));terminal.Receive((char)3);
        Assert.True(cancelled);Assert.Contains("^C",output);Assert.Equal(-125,lease.Read(new byte[1]));
    }
    [Fact]
    public void OwnershipAndUnsupportedModesCannotChangeAnotherReader()
    {
        var terminal=new TerminalInputChannel(()=>null,_=>{},()=>{});using var lease=terminal.Acquire(()=>false);
        Assert.Throws<IOException>(()=>terminal.Acquire(()=>false));
        foreach(int mode in new[]{-1,1,3,5,8,int.MaxValue}) { Assert.Equal(-22,lease.SetMode(mode));Assert.Equal(7,lease.GetMode()); }
        lease.Dispose();using var next=terminal.Acquire(()=>false);
        Assert.Equal(-9,lease.SetMode(0));Assert.Equal(7,next.GetMode());
    }
    [Fact]
    public void ChangingModesFlushesCanonicalAndRawTypeAhead()
    {
        int reads=0;var terminal=new TerminalInputChannel(()=>++reads==1?"first":"second",_=>{},()=>{});
        using var lease=terminal.Acquire(()=>false);Assert.Equal(1,lease.Read(new byte[1]));
        lease.SetMode(0);terminal.Receive("stale");lease.SetMode(7);
        var buffer=new byte[20];int n=lease.Read(buffer);
        Assert.Equal("second\n",Encoding.UTF8.GetString(buffer,0,n));
    }
    [Fact]
    public void OverflowIsReportedAndDoesNotBecomeTheNextCommand()
    {
        var terminal=new TerminalInputChannel(()=>null,_=>{},()=>{});using var lease=terminal.Acquire(()=>false);
        lease.SetMode(0);terminal.Receive(new string('x',TerminalInputChannel.MaximumBytes+1));
        Assert.Equal(-75,lease.Read(new byte[1]));lease.Dispose();
        using var next=terminal.Acquire(()=>false);Assert.Equal(0,next.Read(new byte[1]));
    }
    [Fact]
    public async Task ClosingRawInputUnblocksItsWaitingReader()
    {
        var terminal=new TerminalInputChannel(()=>null,_=>{},()=>{});using var lease=terminal.Acquire(()=>false);
        lease.SetMode(0);var read=Task.Run(()=>lease.Read(new byte[1]));await Task.Delay(25);lease.Dispose();
        Assert.Equal(-9,await read.WaitAsync(TimeSpan.FromSeconds(2)));
    }
    [Fact]
    public void StreamCloseRestoresModeAndPipesRejectTerminalControl()
    {
        var terminal=new TerminalInputChannel(()=>null,_=>{},()=>{});var lease=terminal.Acquire(()=>false);
        var streams=new ProcessStreams(lease,(_,b)=>b.Length,true,true,true);
        Assert.Equal(-25,streams.TerminalMode(1));Assert.Equal(0,streams.TerminalMode(0,0));
        Assert.Equal(0,streams.Close(0));Assert.False(terminal.RawActive);Assert.Equal(-9,streams.TerminalMode(0));
        streams.Close();
        var pipe=new ProcessStreams(new ShellInput(""),(_,b)=>b.Length,false,false,true);
        Assert.Equal(-25,pipe.TerminalMode(0,0));Assert.Equal(-9,pipe.TerminalMode(9));
    }
    [Theory]
    [InlineData("UpArrow",'\0',false,"\x1b[A")]
    [InlineData("C",'c',true,"\x03")]
    [InlineData("Enter",'\0',false,"\r")]
    [InlineData("D",'d',true,"\x04")]
    [InlineData("Unknown",'ü',false,"ü")]
    public void RawKeyEncodingIsSharedByGraphicalAndLocalHosts(string name,char value,bool control,string expected)
    { Assert.Equal(expected,TerminalKeyEncoding.Encode(name,value,control)); }
}
