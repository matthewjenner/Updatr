using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Updatr.Core.Diagnostics;
using Updatr.Core.Operations;

namespace Updatr.App.ViewModels;

/// <summary>One log line as columns: time, level (centered badge), category, message.</summary>
public sealed record LogRowViewModel(LogEntry Entry)
{
    public string Time => Entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Level => Entry.Level.ToString();

    public string Category => Entry.Category.ToString();

    public string Message => Entry.Message;

    /// <summary>Reuses the check colors: errors red, warnings amber, info blue, detail grey.</summary>
    public CheckRowState Tone => Entry.Level switch
    {
        LogLevel.Error => CheckRowState.Fail,
        LogLevel.Warning => CheckRowState.Warn,
        LogLevel.Info => CheckRowState.Running,
        _ => CheckRowState.Pending,
    };

    /// <summary>The whole line, for copying.</summary>
    public string Text => Entry.ToString();
}

/// <summary>Live view of this run's diagnostic log (the same lines written to Updatr.data\logs\).</summary>
public sealed partial class LogPageViewModel : PageViewModel
{
    public LogPageViewModel()
    {
        foreach (LogEntry entry in DiagLog.Snapshot())
            Entries.Add(new LogRowViewModel(entry));

        DiagLog.EntryAdded += entry => Dispatcher.UIThread.Post(() => Add(entry));
    }

    public override string Title => "Log";

    public ObservableCollection<LogRowViewModel> Entries { get; } = [];

    [RelayCommand]
    private void Clear() => Entries.Clear();

    private void Add(LogEntry entry)
    {
        Entries.Add(new LogRowViewModel(entry));

        while (Entries.Count > DiagLog.BufferLimit)
            Entries.RemoveAt(0);
    }
}
