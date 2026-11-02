using System.Runtime.InteropServices;
using EasyTerminal.Core.Ssh;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using WinRT;

namespace EasyTerminal;

public static class Program
{
    private const int AppModelErrorNoPackage = 15700;

    [STAThread]
    public static int Main(string[] args)
    {
        // ssh.exe re-invokes this executable for prompts and proxying; those modes never touch XAML.
        if (args.Length > 0 && SshHelperEntry.TryRun(args, out var exitCode))
            return exitCode;

#if SELF_CONTAINED_RUNTIME
        // The installer build carries the runtime next to the exe. What the SDK's skipped initializer
        // would do: name that folder, then load the DLL that activates its WinRT types.
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WindowsAppRuntime_EnsureIsLoaded();
#else
        // MSIX installs resolve the runtime through their package graph. Running the bare exe, as the
        // Unpackaged launch profile does, has to locate the framework package itself.
        if (!HasPackageIdentity())
        {
            var version = new PackageVersion(global::Microsoft.WindowsAppSDK.Runtime.Version.UInt64);
            if (!Bootstrap.TryInitialize(global::Microsoft.WindowsAppSDK.Release.MajorMinor, global::Microsoft.WindowsAppSDK.Release.VersionTag, version, Bootstrap.InitializeOptions.None, out int hr))
                return hr;
        }
#endif

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool HasPackageIdentity()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    [DllImport("Microsoft.WindowsAppRuntime.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();
}
