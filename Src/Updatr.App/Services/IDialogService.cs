using Updatr.Core.Operations;
using Updatr.Core.Packages;

namespace Updatr.App.Services;

/// <summary>Dialogs ViewModels can ask for without referencing Views. Implemented by the main window.</summary>
public interface IDialogService
{
    /// <summary>Asks a yes/no question. Must be called on the UI thread.</summary>
    Task<bool> ConfirmAsync(Confirmation confirmation);

    /// <summary>Shows what a batch run did. Must be called on the UI thread.</summary>
    Task ShowSummaryAsync(RunSummary summary);

    /// <summary>Asks for a line of text (a name). Null when canceled. Must be called on the UI thread.</summary>
    Task<string?> PromptAsync(string title, string label, string initial);
}
