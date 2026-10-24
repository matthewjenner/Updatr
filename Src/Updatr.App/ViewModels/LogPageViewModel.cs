using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Updatr.Core.Diagnostics;

namespace Updatr.App.ViewModels;

/// <summary>Live view of this run's diagnostic log (the same lines written to Updatr.data\logs\).</summary>
public sealed partial class LogPageViewModel : PageViewModel
{
    public LogPageViewModel()
    {
        foreach (LogEntry entry in DiagLog.Snapshot())
            Entries.Add(entry.ToString());

        DiagLog.EntryAdded += entry => Dispatcher.UIThread.Post(() => Add(entry));
    }

    public override string Title => "Log";

    public ObservableCollection<string> Entries { get; } = [];

    [RelayCommand]
    private void Clear() => Entries.Clear();

    private void Add(LogEntry entry)
    {
        Entries.Add(entry.ToString());

        while (Entries.Count > DiagLog.BufferLimit)
            Entries.RemoveAt(0);
    }
}
