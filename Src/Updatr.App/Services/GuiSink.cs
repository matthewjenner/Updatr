using Avalonia.Threading;
using Updatr.Core.Operations;

namespace Updatr.App.Services;

/// <summary>
/// The GUI's <see cref="IOperationSink"/>: hands every event to a ViewModel on the UI thread.
/// Operations run on background threads and never touch ViewModels directly.
/// </summary>
public sealed class GuiSink(Action<OperationEvent> onEvent, Func<Confirmation, Task<bool>>? confirm = null) : IOperationSink
{
    public void Report(OperationEvent operationEvent) => Dispatcher.UIThread.Post(() => onEvent(operationEvent));

    public async Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken)
    {
        if (confirm is null)
            return confirmation.DefaultAnswer;

        return await Dispatcher.UIThread.InvokeAsync(() => confirm(confirmation)).WaitAsync(cancellationToken);
    }
}
