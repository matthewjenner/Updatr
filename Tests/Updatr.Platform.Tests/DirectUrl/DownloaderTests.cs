using System.Net;
using System.Text;
using Updatr.Core.Catalog;
using Updatr.Platform.DirectUrl;
using Updatr.Platform.Net;

namespace Updatr.Platform.Tests.DirectUrl;

public sealed class DownloaderTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Downloader Fast(FakeHandler handler) => new(new HttpClient(handler))
    {
        StallTimeout = TimeSpan.FromMilliseconds(300),
        AttemptsPerUrl = 2,
        Backoff = [TimeSpan.FromMilliseconds(10)],
    };

    [Fact]
    public async Task DownloadsToTheTargetWithNoPartFileLeft()
    {
        FakeHandler handler = new(_ => FakeHandler.Ok("hello"));
        string target = Path.Combine(_temp.Path, "a.bin");

        await Fast(handler).DownloadAsync(["https://example.test/a.bin"], target, null, null, Ct);

        File.ReadAllText(target).Should().Be("hello");
        Directory.GetFiles(_temp.Path).Should().ContainSingle();
    }

    [Fact]
    public async Task AStalledServerIsRetriedThenTheMirrorIsUsed()
    {
        FakeHandler handler = new(request => request.RequestUri!.Host == "primary.test" ? FakeHandler.Stalled() : FakeHandler.Ok("from mirror"));
        string target = Path.Combine(_temp.Path, "b.bin");

        await Fast(handler).DownloadAsync(["https://primary.test/b.bin", "https://mirror.test/b.bin"], target, null, null, Ct);

        File.ReadAllText(target).Should().Be("from mirror");
        handler.Requests.Count(u => u.Contains("primary", StringComparison.Ordinal)).Should().Be(2, "two attempts on the stalled primary");
    }

    [Fact]
    public async Task ANotFoundIsNotRetried()
    {
        FakeHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Func<Task> download = () => Fast(handler).DownloadAsync(["https://primary.test/c.bin"], Path.Combine(_temp.Path, "c.bin"), null, null, Ct);

        (await download.Should().ThrowAsync<DownloadFailedException>()).Which.Failures.Should().ContainSingle();
    }

    [Fact]
    public async Task ATruncatedDownloadIsRetriedAndNeverLeftBehind()
    {
        FakeHandler handler = new(_ => FakeHandler.Truncated());
        string target = Path.Combine(_temp.Path, "d.bin");

        Func<Task> download = () => Fast(handler).DownloadAsync(["https://primary.test/d.bin"], target, null, null, Ct);

        (await download.Should().ThrowAsync<DownloadFailedException>()).Which.Message.Should().Contain("ended early");
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task GitHubReleaseAssetsAreMatchedByPattern()
    {
        const string release = """
            { "tag_name": "v2.0", "assets": [
                { "name": "tool-2.0-arm64.msi", "browser_download_url": "https://github.test/tool-2.0-arm64.msi" },
                { "name": "tool-2.0-x64.msi", "browser_download_url": "https://github.test/tool-2.0-x64.msi" } ] }
            """;
        FakeHandler handler = new(_ => FakeHandler.Ok(release));
        DirectUrlInstaller installer = new(new HttpClient(handler), new Updatr.Platform.Processes.ProcessRunner(), null);
        DirectUrlSourceRef source = new()
        {
            GithubRelease = new GitHubReleaseRef { Repo = "owner/tool", AssetPattern = "tool-*-x64.msi" },
            InstallerType = InstallerType.Msi,
            Signer = "Owner",
            Mirrors = ["https://mirror.test/tool.msi"],
        };

        IReadOnlyList<string> urls = await installer.ResolveAsync(source, Ct);

        urls.Should().Equal("https://github.test/tool-2.0-x64.msi", "https://mirror.test/tool.msi");
        handler.Requests.Single().Should().Be("https://api.github.com/repos/owner/tool/releases/latest");
    }

    [Fact]
    public void AHashMismatchIsRefusedBeforeTheSignatureIsEvenChecked()
    {
        string file = Path.Combine(_temp.Path, "e.exe");
        File.WriteAllText(file, "not what was expected");
        DirectUrlSourceRef source = new() { Url = "https://x.test/e.exe", InstallerType = InstallerType.Exe, Signer = "X", Sha256 = new string('A', 64) };

        DirectUrlInstaller.Verify(file, source).Should().Contain("SHA-256");
    }

    [Theory]
    [InlineData(InstallerType.Msi, null, "/i|FILE|/qn|/norestart")]
    [InlineData(InstallerType.Inno, null, "/VERYSILENT|/SUPPRESSMSGBOXES|/NORESTART|/SP-")]
    [InlineData(InstallerType.Nullsoft, null, "/S")]
    [InlineData(InstallerType.Burn, null, "/quiet|/norestart")]
    [InlineData(InstallerType.Inno, "/VERYSILENT /DIR=\"C:\\Program Files\\X\"", "/VERYSILENT|/DIR=C:\\Program Files\\X")]
    public void InstallerTypesGetTheirSilentSwitches(InstallerType type, string? silentArgs, string expected)
    {
        (string program, IReadOnlyList<string> args) = DirectUrlInstaller.Command("FILE", type, silentArgs);

        string.Join("|", args).Should().Be(expected);
        program.Should().EndWith(type is InstallerType.Msi ? "msiexec.exe" : "FILE");
    }

    /// <summary>An HttpMessageHandler that answers from a function and records request URLs.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

        /// <summary>Headers arrive, then the body never does.</summary>
        public static HttpResponseMessage Stalled() => new(HttpStatusCode.OK) { Content = new StreamContent(new StallStream()) };

        /// <summary>Claims 100 bytes, sends 5.</summary>
        public static HttpResponseMessage Truncated()
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream("short"u8.ToArray())) };
            response.Content.Headers.ContentLength = 100;
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.ToString());

            return Task.FromResult(respond(request));
        }
    }

    private sealed class StallStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
