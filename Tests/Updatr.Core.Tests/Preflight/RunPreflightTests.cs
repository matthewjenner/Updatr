using Updatr.Core.Operations;
using Updatr.Core.Preflight;

namespace Updatr.Core.Tests.Preflight;

public sealed class RunPreflightTests
{
    [Fact]
    public async Task ResultsComeBackInRegistrationOrder()
    {
        RunPreflight preflight = new([new FakeCheck("b", delayMs: 50), new FakeCheck("a"), new FakeCheck("c", delayMs: 10)]);

        PreflightReport report = await preflight.RunAsync(new PreflightRequest(), new RecordingSink(), CancellationToken.None);

        report.Results.Select(r => r.CheckId).Should().Equal("b", "a", "c");
    }

    [Fact]
    public async Task ADependentCheckSeesWhatItsDependencyLearned()
    {
        FakeCheck network = new("network", run: ctx => { ctx.IsOnline = false; return PreflightResult.Fail("offline"); });
        FakeCheck catalog = new("catalog", ["network"], ctx => PreflightResult.Info(ctx.IsOnline == false ? "used cache" : "used remote"));

        PreflightReport report = await new RunPreflight([catalog, network]).RunAsync(new PreflightRequest(), new RecordingSink(), CancellationToken.None);

        report.Results.Single(r => r.CheckId == "catalog").Result.Summary.Should().Be("used cache");
    }

    [Fact]
    public async Task IndependentChecksRunAtTheSameTime()
    {
        // Each waits for the other to start; run one after the other, this would time out.
        TaskCompletionSource aStarted = new(), bStarted = new();
        FakeCheck a = new("a", runAsync: async _ => { aStarted.SetResult(); await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); return PreflightResult.Pass("a"); });
        FakeCheck b = new("b", runAsync: async _ => { bStarted.SetResult(); await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); return PreflightResult.Pass("b"); });

        PreflightReport report = await new RunPreflight([a, b]).RunAsync(new PreflightRequest(), new RecordingSink(), CancellationToken.None);

        report.Worst.Should().Be(PreflightStatus.Pass);
    }

    [Fact]
    public async Task ACheckThatThrowsBecomesAFailAndTheRestStillRun()
    {
        FakeCheck broken = new("broken", run: _ => throw new InvalidOperationException("registry exploded"));
        FakeCheck fine = new("fine");

        PreflightReport report = await new RunPreflight([broken, fine]).RunAsync(new PreflightRequest(), new RecordingSink(), CancellationToken.None);

        report.Results[0].Result.Status.Should().Be(PreflightStatus.Fail);
        report.Results[0].Result.Summary.Should().Contain("registry exploded");
        report.Results[1].Result.Status.Should().Be(PreflightStatus.Pass);
        report.HasFailures.Should().BeTrue();
    }

    [Fact]
    public async Task OnlyRunsTheChosenChecksAndTheirDependencies()
    {
        RunPreflight preflight = new([new FakeCheck("os"), new FakeCheck("network"), new FakeCheck("catalog", ["network"]), new FakeCheck("disk")]);

        PreflightReport report = await preflight.RunAsync(new PreflightRequest(["catalog"]), new RecordingSink(), CancellationToken.None);

        report.Results.Select(r => r.CheckId).Should().Equal("network", "catalog");
    }

    [Fact]
    public async Task AnUnknownCheckIdIsAUsageError()
    {
        Func<Task> run = () => new RunPreflight([new FakeCheck("os")]).RunAsync(new PreflightRequest(["nope"]), new RecordingSink(), CancellationToken.None);

        (await run.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("nope").And.Contain("os");
    }

    [Fact]
    public async Task EveryCheckReportsStartedThenFinished()
    {
        RecordingSink sink = new();

        await new RunPreflight([new FakeCheck("a"), new FakeCheck("b", ["a"])]).RunAsync(new PreflightRequest(), sink, CancellationToken.None);

        sink.Events.OfType<CheckStarted>().Select(e => e.CheckId).Should().BeEquivalentTo("a", "b");
        sink.Events.OfType<CheckFinished>().Select(e => e.CheckId).Should().Equal("a", "b");
        sink.Events.IndexOf(sink.Events.OfType<CheckFinished>().First(e => e.CheckId == "a"))
            .Should().BeLessThan(sink.Events.IndexOf(sink.Events.OfType<CheckStarted>().First(e => e.CheckId == "b")));
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        using CancellationTokenSource cts = new();
        FakeCheck slow = new("slow", runAsync: async ct => { await cts.CancelAsync(); await Task.Delay(Timeout.Infinite, ct); return PreflightResult.Pass("never"); });

        Func<Task> run = () => new RunPreflight([slow]).RunAsync(new PreflightRequest(), new RecordingSink(), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ADependencyCycleIsReported()
    {
        RunPreflight preflight = new([new FakeCheck("a", ["b"]), new FakeCheck("b", ["a"])]);

        Func<Task> run = () => preflight.RunAsync(new PreflightRequest(), new RecordingSink(), CancellationToken.None);

        run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cycle*");
    }

    private sealed class FakeCheck(
        string id,
        IReadOnlyList<string>? dependsOn = null,
        Func<PreflightContext, PreflightResult>? run = null,
        Func<CancellationToken, Task<PreflightResult>>? runAsync = null,
        int delayMs = 0) : IPreflightCheck
    {
        public string Id => id;

        public string Title => id.ToUpperInvariant();

        public IReadOnlyList<string> DependsOn => dependsOn ?? [];

        public async Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);

            if (runAsync is not null)
                return await runAsync(cancellationToken);

            return run?.Invoke(context) ?? PreflightResult.Pass(id);
        }
    }
}

/// <summary>An <see cref="IOperationSink"/> that keeps every event, in order, and answers confirmations.</summary>
/// <param name="answer">The answer to every confirmation (default yes, like <c>--yes</c>).</param>
internal sealed class RecordingSink(bool answer = true) : IOperationSink
{
    private readonly Lock _gate = new();
    private readonly List<OperationEvent> _events = [];

    public List<OperationEvent> Events
    {
        get
        {
            lock (_gate)
                return [.. _events];
        }
    }

    public void Report(OperationEvent operationEvent)
    {
        lock (_gate)
            _events.Add(operationEvent);
    }

    public Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken) => Task.FromResult(answer);
}
