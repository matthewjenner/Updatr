using System.CommandLine;
using System.Text;
using Updatr.Platform.Elevation;

namespace Updatr.Cli;

internal static class Program
{
    /// <summary>
    /// How long Ctrl+C waits for the run to stop on its own. Operations stop between items, and a
    /// winget query in flight is allowed to finish: killing a process mid-query is one of the loads
    /// that preceded this machine's 0x153 bugchecks (see WinGetComSource).
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> Main(string[] args)
    {
        // Launched elevated as the broker for another Updatr process: serve it and exit.
        if (BrokerHost.TryRun(args) is int brokerExit)
            return brokerExit;

        Console.OutputEncoding = Encoding.UTF8;

        // A reader that goes away (`updatr-cli list | head`) must not crash the run mid-work.
        Console.SetOut(new ForgivingWriter(Console.Out));
        Console.SetError(new ForgivingWriter(Console.Error));

        InvocationConfiguration invocation = new() { ProcessTerminationTimeout = StopTimeout };
        return await CliApp.BuildRootCommand().Parse(args).InvokeAsync(invocation).ConfigureAwait(false);
    }

    /// <summary>Writes through until the output breaks (the reader closed the pipe), then drops output quietly.</summary>
    private sealed class ForgivingWriter(TextWriter inner) : TextWriter
    {
        private bool _broken;

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => Try(() => inner.Write(value));

        public override void Write(string? value) => Try(() => inner.Write(value));

        public override void WriteLine(string? value) => Try(() => inner.WriteLine(value));

        public override void WriteLine() => Try(inner.WriteLine);

        public override void Flush() => Try(inner.Flush);

        private void Try(Action write)
        {
            if (_broken)
                return;

            try
            {
                write();
            }
            catch (IOException)
            {
                _broken = true;
            }
        }
    }
}
