using Updatr.Core.Sources;

namespace Updatr.Core.Operations;

/// <summary>
/// Where an operation reports what it is doing. The GUI's sink marshals to the UI thread and
/// updates ViewModels; the CLI's sink writes to the console or emits JSON lines.
/// </summary>
/// <remarks>
/// <see cref="Report"/> may be called from any thread and must return quickly - the operation does
/// not wait for the UI.
/// </remarks>
public interface IOperationSink
{
    void Report(OperationEvent operationEvent);

    /// <summary>
    /// Asks the user a yes/no question mid-operation. The CLI answers from <c>--yes</c> or a
    /// prompt; the GUI shows a dialog. Returns false when the user declines or nobody can answer.
    /// </summary>
    Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken);
}

/// <summary>Something an operation wants the front end to know.</summary>
public abstract record OperationEvent;

/// <summary>A named phase began ("Checking winget", "Downloading 3 installers").</summary>
public sealed record StepStarted(string Step) : OperationEvent;

/// <summary>Progress within the current step or item. Any field may be unknown.</summary>
/// <param name="ItemId">The item this is about, or null for the step as a whole.</param>
/// <param name="Fraction">0..1 when known.</param>
/// <param name="Bytes">Bytes done, for downloads.</param>
/// <param name="TotalBytes">Total bytes, when the server said.</param>
/// <param name="Message">Short status text.</param>
public sealed record ProgressReported(
    string? ItemId,
    double? Fraction = null,
    long? Bytes = null,
    long? TotalBytes = null,
    string? Message = null) : OperationEvent;

/// <summary>Severity of a <see cref="LogLine"/>.</summary>
public enum LogLevel
{
    Detail,
    Info,
    Warning,
    Error,
}

/// <summary>A line for the run log. Item-specific lines carry the item id.</summary>
public sealed record LogLine(LogLevel Level, string Text, string? ItemId = null) : OperationEvent;

/// <summary>An item finished (or was skipped). Exactly one per item an operation was given.</summary>
public sealed record ItemFinished(string ItemId, string DisplayName, PackageOutcome Outcome) : OperationEvent;

/// <summary>A yes/no question for the user.</summary>
/// <param name="Title">Short question.</param>
/// <param name="Detail">What happens on yes, and on no. With <see cref="Items"/>, the note under them.</param>
/// <param name="DefaultAnswer">What a non-interactive run (<c>--yes</c> not given, no console) assumes.</param>
public sealed record Confirmation(string Title, string Detail, bool DefaultAnswer = false)
{
    /// <summary>What the question is about, one row each - the GUI shows a grid, the CLI a table.</summary>
    public IReadOnlyList<ConfirmationItem> Items { get; init; } = [];

    /// <summary>Column titles for <see cref="Items"/>. A column no item fills is left out.</summary>
    public ConfirmationColumns Columns { get; init; } = new();
}

/// <summary>One row of a <see cref="Confirmation"/>. Every field but the name is optional.</summary>
/// <param name="Current">The version (or state) now.</param>
/// <param name="Next">The version (or state) after.</param>
public sealed record ConfirmationItem(string Name, string? Id = null, string? Current = null, string? Next = null, string? Note = null);

/// <summary>Titles for the <see cref="ConfirmationItem"/> columns.</summary>
public sealed record ConfirmationColumns(
    string Name = "Name",
    string Id = "Id",
    string Current = "Installed",
    string Next = "After",
    string Note = "Note")
{
    /// <summary>The rows as aligned plain text, for a console or a log.</summary>
    public static string Table(Confirmation confirmation)
    {
        ConfirmationColumns c = confirmation.Columns;
        List<(string Title, Func<ConfirmationItem, string?> Get)> columns =
        [
            (c.Name, i => i.Name), (c.Id, i => i.Id), (c.Current, i => i.Current), (c.Next, i => i.Next), (c.Note, i => i.Note),
        ];
        columns.RemoveAll(col => col.Title != c.Name && confirmation.Items.All(i => string.IsNullOrEmpty(col.Get(i))));

        int[] widths = [.. columns.Select(col => Math.Max(col.Title.Length, confirmation.Items.Max(i => col.Get(i)?.Length ?? 0)))];
        string Row(Func<int, string> cell) => "  " + string.Join("  ", columns.Select((_, n) => n == columns.Count - 1 ? cell(n) : cell(n).PadRight(widths[n]))).TrimEnd();

        return string.Join("\n", [Row(n => columns[n].Title), Row(n => new string('-', widths[n])), .. confirmation.Items.Select(i => Row(n => columns[n].Get(i) ?? ""))]);
    }
}
