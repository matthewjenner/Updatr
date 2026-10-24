namespace Updatr.Core.Operations;

/// <summary>
/// One user-facing action (list packages, upgrade, apply defluff, ...). The GUI and the CLI are
/// both thin adapters over these, which is how they stay at parity: neither has logic the other
/// lacks.
/// </summary>
/// <typeparam name="TRequest">What the user asked for.</typeparam>
/// <typeparam name="TResult">What came of it.</typeparam>
/// <remarks>
/// Implementations report progress through <see cref="IOperationSink"/> and never know which front
/// end is listening. They honor cancellation and let <see cref="OperationCanceledException"/>
/// propagate. Every implementation carries an <see cref="OperationAttribute"/>.
/// </remarks>
public interface IOperation<in TRequest, TResult>
{
    Task<TResult> RunAsync(TRequest request, IOperationSink sink, CancellationToken cancellationToken);
}

/// <summary>
/// Names an operation. The name is the CLI command path (space-separated, e.g. <c>"cart merge"</c>),
/// and the parity test checks every named operation has that command.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OperationAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
