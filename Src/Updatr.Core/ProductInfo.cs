using System.Reflection;

namespace Updatr.Core;

/// <summary>Product name and running version, shared by the GUI, the CLI, and logs.</summary>
public static class ProductInfo
{
    public const string Name = "Updatr";

    /// <summary>
    /// The version of the running executable, from <c>Directory.Build.props</c>. Read from the
    /// entry assembly so the GUI and CLI each report their own build, not Core's.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(ProductInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "0.0.0" : informational;
    }
}
