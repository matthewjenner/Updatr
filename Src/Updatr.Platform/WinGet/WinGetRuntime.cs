using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using Updatr.Core.Diagnostics;
using Updatr.Platform.Data;

namespace Updatr.Platform.WinGet;

/// <summary>
/// Makes the WinGet COM API usable from a portable single-file exe. Call <see cref="EnsureReady"/>
/// (or <see cref="CreatePackageManager"/>) before touching any <c>Microsoft.Management.Deployment</c> type.
/// </summary>
/// <remarks>
/// Two things, both from the Phase 0 spikes (Docs/TECHARCH.md, "WinGet COM activation"):
/// <list type="number">
/// <item>The <c>.winmd</c> must sit beside the exe, or cross-process marshaling fails with
/// <c>0x80073D54</c>. If it is missing, a copy embedded in this assembly is written back.</item>
/// <item>Activation goes through an explicit handler that loads the native factory with
/// <see cref="NativeLibrary"/> - which finds it in the single-file extraction folder - and calls
/// its <c>DllGetActivationFactory</c>. CsWinRT's own probing cannot see bundled native DLLs. This
/// path was measured working both unelevated and elevated.</item>
/// </list>
/// </remarks>
public static partial class WinGetRuntime
{
    public const string MetadataFileName = "Microsoft.Management.Deployment.winmd";

    private const string Namespace = "Microsoft.Management.Deployment.";
    private const string FactoryDll = "Microsoft.Management.Deployment.dll";
    private const string EmbeddedMetadata = "Updatr.WinGet." + MetadataFileName;

    private static readonly Lock Gate = new();
    private static string? _readyProblem;
    private static bool _initialized;
    private static IntPtr _getActivationFactory;

    /// <summary>
    /// Installs the activation handler and makes sure the metadata is beside the exe. Idempotent.
    /// Returns null when ready, otherwise why WinGet cannot work (the preflight shows it).
    /// </summary>
    public static string? EnsureReady()
    {
        lock (Gate)
        {
            if (_initialized)
                return _readyProblem;

            _initialized = true;
            WinRT.ActivationFactory.ActivationHandler = Activate;
            _readyProblem = EnsureMetadataBesideExe();
            return _readyProblem;
        }
    }

    /// <summary>Creates the WinGet <see cref="PackageManager"/>. Throws if WinGet is unavailable.</summary>
    public static PackageManager CreatePackageManager()
    {
        if (EnsureReady() is { } problem)
            throw new InvalidOperationException(problem);

        return new PackageManager();
    }

    /// <summary>
    /// Whether an exception from the WinGet COM API means "winget is not usable here" rather than a
    /// bug. <see cref="InvalidCastException"/> is an App Installer older than the API this build
    /// targets: the server is there but lacks a newer interface (e.g. <c>PackageManager.Version</c>),
    /// which is common on a fresh image before the Store has updated it.
    /// </summary>
    public static bool IsUnavailable(Exception ex)
        => ex is COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException;

    /// <summary>A short reason for the user, naming the installed App Installer when it is too old.</summary>
    public static string Describe(Exception ex)
    {
        string? installed = AppInstallerVersion();
        DiagLog.WinGetApiUnavailable(installed, ex);

        return ex is InvalidCastException
            ? $"winget on this PC is too old (App Installer package {installed ?? "version unknown"}); Updatr is built for winget {TargetVersion}"
            : ex.Message;
    }

    /// <summary>The winget API version this build is compiled against (the interop package).</summary>
    public const string TargetVersion = "1.29";

    /// <summary>The installed App Installer package version for this user, or null if none or unknown.</summary>
    public static string? AppInstallerVersion()
    {
        try
        {
            foreach (global::Windows.ApplicationModel.Package package in new global::Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty, RepairWinGet.FamilyName))
            {
                global::Windows.ApplicationModel.PackageVersion v = package.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Diagnostic only - it runs inside other catch blocks, so it must never throw.
        }

        return null;
    }

    private static string? EnsureMetadataBesideExe()
    {
        string path = Path.Combine(AppLocations.ExeDirectory, MetadataFileName);

        if (File.Exists(path))
            return null;

        try
        {
            using Stream source = typeof(WinGetRuntime).Assembly.GetManifestResourceStream(EmbeddedMetadata)
                ?? throw new InvalidOperationException("the embedded copy is missing from this build");
            using FileStream target = File.Create(path);
            source.CopyTo(target);
            DiagLog.WinGetMetadataRestored(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagLog.WinGetMetadataMissing(ex.Message);
            return $"{MetadataFileName} is missing beside the exe and could not be restored ({ex.Message}). "
                + "Copy it next to Updatr.exe from the release zip.";
        }
    }

    private static unsafe IntPtr Activate(string typeName, Guid iid)
    {
        if (!typeName.StartsWith(Namespace, StringComparison.Ordinal))
            return IntPtr.Zero; // Not ours: let CsWinRT do its normal activation.

        if (_getActivationFactory == IntPtr.Zero)
        {
            IntPtr module = NativeLibrary.Load(FactoryDll, typeof(WinGetRuntime).Assembly, DllImportSearchPath.AssemblyDirectory);
            _getActivationFactory = NativeLibrary.GetExport(module, "DllGetActivationFactory");
        }

        Marshal.ThrowExceptionForHR(WindowsCreateString(typeName, (uint)typeName.Length, out IntPtr hstring));

        try
        {
            IntPtr factory;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)_getActivationFactory)(hstring, &factory));

            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory, in iid, out IntPtr result));
                return result;
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(IntPtr hstring);
}
