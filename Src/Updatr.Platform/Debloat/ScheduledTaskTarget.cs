using System.Runtime.InteropServices;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;

namespace Updatr.Platform.Debloat;

/// <summary>
/// Scheduled tasks through the Task Scheduler COM API (<c>Schedule.Service</c>), late-bound - its
/// interfaces are dispatch-only, and there is no projection to reference. Reading works
/// unelevated for most Microsoft tasks; disabling needs administrator access.
/// </summary>
/// <remarks>
/// Late binding turns a failing HRESULT into its .NET exception (0x80070002 is
/// <see cref="FileNotFoundException"/>, 0x80070005 <see cref="UnauthorizedAccessException"/>), not
/// <see cref="COMException"/> - match on <see cref="Exception.HResult"/>.
/// </remarks>
public sealed class ScheduledTaskTarget(ElevationService elevation) : IDebloatTarget
{
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);

    public bool Handles(DebloatAction action) => action is ScheduledTaskAction;

    public bool NeedsAdmin(DebloatAction action) => true;

    public Task<ActionStatus> DetectAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        try
        {
            bool? enabled = ReadEnabled(((ScheduledTaskAction)action).Path);

            return Task.FromResult(enabled switch
            {
                null => new ActionStatus(ActionState.NotApplicable, "Not on this version of Windows"),
                false => new ActionStatus(ActionState.Applied),
                true => new ActionStatus(ActionState.NotApplied, "Enabled"),
            });
        }
        catch (Exception ex) when (ex.HResult == AccessDenied)
        {
            return Task.FromResult(new ActionStatus(ActionState.Unknown, "Only an administrator can read this task"));
        }
    }

    public Task<ActionBackup> CaptureAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        bool? enabled;

        try
        {
            enabled = ReadEnabled(((ScheduledTaskAction)action).Path);
        }
        catch (Exception ex) when (ex.HResult == AccessDenied)
        {
            // Unreadable unelevated; Windows ships these enabled, which is what a revert restores.
            enabled = true;
        }

        return Task.FromResult(new ActionBackup { Action = action, Existed = enabled is not null, Value = enabled == false ? "disabled" : "enabled" });
    }

    public Task<PackageOutcome> ApplyAsync(DebloatAction action, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        => DebloatElevation.ApplyAsync(elevation, action, () => Task.FromResult(SetEnabledLocal((ScheduledTaskAction)action, enabled: false)));

    public Task<PackageOutcome> RevertAsync(ActionBackup backup, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        => backup.Existed
            ? DebloatElevation.RevertAsync(elevation, backup, () => Task.FromResult(SetEnabledLocal((ScheduledTaskAction)backup.Action, backup.Value != "disabled")))
            : Task.FromResult(new PackageOutcome(OutcomeKind.AlreadyCurrent, "The task did not exist"));

    internal static PackageOutcome SetEnabledLocal(ScheduledTaskAction action, bool enabled)
    {
        try
        {
            dynamic? task = OpenTask(action.Path);

            if (task is null)
                return new PackageOutcome(OutcomeKind.AlreadyCurrent, "The task does not exist on this version of Windows");

            task.Enabled = enabled;
            return PackageOutcome.Ok();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            return PackageOutcome.Failed($"Could not {(enabled ? "enable" : "disable")} {action.Path}: {ex.Message.Trim()}");
        }
    }

    /// <summary>Null when the task (or its folder) does not exist.</summary>
    private static bool? ReadEnabled(string path)
    {
        dynamic? task = OpenTask(path);
        return task is null ? null : (bool)task.Enabled;
    }

    private static dynamic? OpenTask(string path)
    {
        int split = path.LastIndexOf('\\');
        string folderPath = split <= 0 ? "\\" : path[..split];
        string name = path[(split + 1)..];

        Type type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();

        try
        {
            return service.GetFolder(folderPath).GetTask(name);
        }
        catch (Exception ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            return null;
        }
    }
}
