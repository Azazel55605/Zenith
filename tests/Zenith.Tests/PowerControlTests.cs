using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zenith.Tests
{
    public class PowerControlTests
    {
        [Fact]
        public void ActiveNativeProgramPreventsPowerAction()
        {
            Cosmos.Kernel.System.Power.Calls = 0;
            Zenith.Core.Processes.NativeProcess.IsRunning = true;
            try { Assert.Throws<InvalidOperationException>(() => Zenith.Core.PowerControl.PowerOff()); Assert.Equal(0, Cosmos.Kernel.System.Power.Calls); }
            finally { Zenith.Core.Processes.NativeProcess.IsRunning = false; }
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FlushFailurePreventsHardwareActionAndReleasesMaintenanceGate(bool reboot)
        {
            Cosmos.Kernel.System.Power.Calls = 0;
            var failure = new IOException("device flush failed");
            Zenith.Core.Storage.SystemMounts.Failure = failure;
            try
            {
                var thrown = Assert.Throws<IOException>(() => { if (reboot) { Zenith.Core.PowerControl.Reboot(); } else { Zenith.Core.PowerControl.PowerOff(); } });
                Assert.Same(failure, thrown); Assert.Equal(0, Cosmos.Kernel.System.Power.Calls);
                Assert.True(Task.Run(() =>
                {
                    bool entered = Monitor.TryEnter(Zenith.Core.Installer.MaintenanceGate, 200);
                    if (entered) { Monitor.Exit(Zenith.Core.Installer.MaintenanceGate); }
                    return entered;
                }).GetAwaiter().GetResult());
            }
            finally { Zenith.Core.Storage.SystemMounts.Failure = null; }
        }
    }
}

// Host seams only: production PowerControl is linked unchanged above.
namespace Zenith.Core
{
    internal static class Installer { public static readonly object MaintenanceGate = new(); public static bool IsRunning => false; }
    internal static class Log { public static void Write(string tag, string message) { } }
}
namespace Zenith.Core.Storage
{
    internal static class SystemMounts { public static Exception? Failure; public static void Shutdown() { if (Failure is not null) { throw Failure; } } }
}
namespace Zenith.Core.Boot { internal static class NativeKernelUpdates { public static bool IsRunning => false; } }
namespace Zenith.Core.Processes { internal static class NativeProcess { public static bool IsRunning; } }
namespace Zenith.Core.Shell.Commands { internal static class PackageCommands { public static bool IsRunning => false; } }
namespace Cosmos.Kernel.System
{
    internal static class Power { public static int Calls; public static void Reboot() => Calls++; public static void Shutdown() => Calls++; }
}
