using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cosmos.Kernel.System.Graphics;
using Zenith.Core.Configuration;
using Zenith.Core.Security;
using MemoryInfo = Cosmos.Kernel.System.Diagnostics.MemoryInfo;
namespace Zenith.Gui.Shell;

/// <summary>BSP session-owner adapter for the pinned 3.0.89 canvases. Virtio and GOP
/// ignore Mode requests; their published static catalogues are not runtime capability.</summary>
internal static class DisplayController
{
    [DllImport("*", EntryPoint="zenith_svga_register")]
    [SuppressGCTransition] private static extern uint Register(uint index);
    private static Canvas s_physical=null!;
    private static DisplayLayout[] s_modes=Array.Empty<DisplayLayout>();
    private static DisplayLayout s_layout;
    private static string s_driver="";
    private static Desktop? s_desktop;
    private static Desktop.DisplayFrames? s_previous;
    private static Canvas? s_previousCanvas;
    private static DisplayLayout s_previousLayout;
    private static bool s_preferencesLoaded;
    public static Canvas Initialize()
    {
        s_physical=Canvas.GetFullScreen(); s_driver=s_physical.Name;
        s_layout=new(s_physical.Width,s_physical.Height,1);
        var modes=new List<DisplayLayout> {s_layout};
        // ApplyMode in the pinned VMware canvases changes hardware. Bound its
        // advertised list by the actual register limits and framebuffer memory.
        if(s_driver.StartsWith("VMWare",StringComparison.OrdinalIgnoreCase))
        {
            uint maxW=Register(4),maxH=Register(5),vram=Register(15),offset=Register(14);
            foreach(var mode in s_physical.AvailableModes)
            {
                var m=new DisplayLayout(mode.Width,mode.Height,1);
                if(mode.ColorDepth!=ColorDepth.ColorDepth32 || !DisplayLayout.FitsVmware(m,maxW,maxH,vram,offset) || modes.Contains(m)) { continue; }
                if(modes.Count<24) { modes.Add(m); }
            }
        }
        s_modes=modes.ToArray(); return s_physical;
    }
    public static void LoadPreferences()
    {
        // Boot splash acquires the framebuffer before shared settings/VFS exist.
        if(s_preferencesLoaded || SystemConfiguration.Display is null) { return; }
        s_preferencesLoaded=true;
        DisplayLayout? saved=SystemConfiguration.Display.Saved();
        if(SafeBoot()) { saved=null; }
        // Boot has no old application state to preserve. Unsupported saved modes
        // fall back to firmware/current mode, retaining the file for diagnosis.
        if(saved is DisplayLayout wanted && wanted.Usable && HasMode(wanted))
        {
            ulong pages=(ulong)wanted.LogicalWidth*(ulong)wanted.LogicalHeight*16/4096+(ulong)wanted.Width*(ulong)wanted.Height*4/4096+8192;
            if(MemoryInfo.FreePages>=pages)
            {
                try
                {
                    Canvas screen=wanted.Scale==1?s_physical:new DisplayCanvas(s_physical,wanted);
                    SetHardware(wanted); s_layout=wanted;
                    SessionDisplay.ReplaceScreen(screen);
                }
                catch { SetHardware(s_layout); Zenith.Core.Log.Write("display","saved mode refused; using boot mode"); }
            }
            else { Zenith.Core.Log.Write("display","insufficient memory for saved mode; using boot mode"); }
        }
        SessionDisplay.Scale=s_layout.Scale;
        SystemConfiguration.Display.Attach(s_driver,s_layout,s_modes,null,null);
        if(s_layout.Scale==1) { SessionDisplay.ReplaceScreen(s_physical); }
    }
    private static unsafe bool SafeBoot()
    {
        string line=Marshal.PtrToStringUTF8((IntPtr)Cosmos.Kernel.Boot.Limine.Limine.Cmdline)??"";
        foreach(string word in line.Split(' ')) { if(word=="display=safe") { return true; } }
        return false;
    }
    private static bool HasMode(DisplayLayout layout)
    { foreach(var mode in s_modes) { if(mode.Width==layout.Width && mode.Height==layout.Height) { return true; } } return false; }
    private static void SetHardware(DisplayLayout layout)
    {
        if(s_physical.Width!=layout.Width || s_physical.Height!=layout.Height)
        { Canvas.GetFullScreen(new Mode(layout.Width,layout.Height,ColorDepth.ColorDepth32)); }
        if(s_physical.Width!=layout.Width || s_physical.Height!=layout.Height) { throw new InvalidOperationException("driver did not apply requested mode"); }
        if(s_driver.StartsWith("VMWare",StringComparison.OrdinalIgnoreCase)
            && (Register(2)!=(uint)layout.Width || Register(3)!=(uint)layout.Height || Register(7)!=32
                || Register(12)!=(uint)layout.Width*4 || !DisplayLayout.FitsVmware(layout,Register(4),Register(5),Register(15),Register(14))))
        { throw new InvalidOperationException("SVGA hardware mode readback differs from request"); }
    }
    public static void Attach(Desktop desktop)
    {
        s_desktop=desktop; SystemConfiguration.Display.Attach(s_driver,s_layout,s_modes,Apply,Finish);
    }
    private static bool Apply(DisplayLayout layout)
    {
        if(s_desktop is null) { return false; }
        if(s_previous is not null)
        {
            if(layout!=s_previousLayout) { return false; }
            SetHardware(layout); SessionDisplay.ReplaceScreen(s_previousCanvas!); SessionDisplay.Scale=layout.Scale;
            s_desktop.UseDisplay(s_previousCanvas!,s_previous); s_layout=layout; SessionDisplay.ConfigurePointer();
            SessionDisplay.LiveFrame=s_desktop.FramePixels; return true;
        }
        // Retain every old full-screen buffer until Keep/Revert. Allocate first,
        // including scaled output, and refuse before touching the device if short.
        ulong pages=(ulong)layout.LogicalWidth*(ulong)layout.LogicalHeight*4*4/4096
            +(layout.Scale==1?0:(ulong)layout.Width*(ulong)layout.Height*4/4096)+8192;
        if(MemoryInfo.FreePages<pages) { return false; }
        Desktop.DisplayFrames frames,old;
        Canvas screen,oldCanvas;
        var oldLayout=s_layout;
        try
        {
            frames=s_desktop.PrepareDisplay(layout.LogicalWidth,layout.LogicalHeight);
            screen=layout.Scale==1?s_physical:new DisplayCanvas(s_physical,layout);
            old=s_desktop.CaptureDisplay(); oldCanvas=SessionDisplay.Screen;
        }
        catch { return false; } // No device or desktop mutation has occurred.
        try
        {
            SetHardware(layout);
            SessionDisplay.ReplaceScreen(screen); SessionDisplay.Scale=layout.Scale;
            s_desktop.UseDisplay(screen,frames); SessionDisplay.ConfigurePointer();
        }
        catch
        {
            SetHardware(oldLayout); SessionDisplay.ReplaceScreen(oldCanvas); SessionDisplay.Scale=oldLayout.Scale;
            s_desktop.UseDisplay(oldCanvas,old); SessionDisplay.ConfigurePointer(); frames.Bar.Dispose();
            SessionDisplay.LiveFrame=s_desktop.FramePixels; return false;
        }
        s_previous=old; s_previousCanvas=oldCanvas; s_previousLayout=oldLayout; s_layout=layout;
        SessionDisplay.LiveFrame=s_desktop.FramePixels;
        Zenith.Core.Log.Write("display","preview "+layout.Width+"x"+layout.Height+" scale="+layout.Scale); return true;
    }
    private static void Finish(bool kept)
    { if(kept) { s_previous?.Bar.Dispose(); } s_previous=null; s_previousCanvas=null; Zenith.Core.Log.Write("display",kept?"kept":"restored"); }
    public static void Tick() => SystemConfiguration.Display.Tick(Zenith.Core.Configuration.DisplayService.Now);
    public static void Detach()
    { SystemConfiguration.Display.Detach(Zenith.Core.Configuration.DisplayService.Now); s_desktop=null; }
}
