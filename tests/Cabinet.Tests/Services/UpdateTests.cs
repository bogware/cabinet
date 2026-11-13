using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Updating;
using Cabinet.API.Services.Updates;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class UpdateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-update-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Releases held in memory: archives and their checksums by name.</summary>
    private sealed class FakeReleases : IReleaseSource
    {
        private readonly Dictionary<string, byte[]> _files = new();
        private readonly List<ReleaseAsset> _assets = [];

        public string Tag { get; set; } = "v9.9.9";

        public int Downloads { get; private set; }

        public void Add(string name, byte[] contents, string? checksum = null)
        {
            Put(name, contents);
            Put(name + ".sha256", Encoding.ASCII.GetBytes($"{checksum ?? Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant()}  {name}\n"));
        }

        private void Put(string name, byte[] contents)
        {
            _files[name] = contents;
            _assets.Add(new ReleaseAsset(_assets.Count + 1, name, contents.Length));
        }

        private ReleaseInfo Release(string tag) => new(tag, ReleaseVersions.FromTag(tag)!, "notes", DateTimeOffset.UtcNow, _assets);

        public Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult(Release(Tag));

        public Task<ReleaseInfo?> GetByTagAsync(string tag, CancellationToken cancellationToken = default) => Task.FromResult<ReleaseInfo?>(Release(tag));

        public Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken cancellationToken = default)
        {
            Downloads++;
            return Task.FromResult<Stream>(new MemoryStream(_files[asset.Name]));
        }
    }

    private UpdateService Service(IReleaseSource source) => new(
        source,
        new UpdateApplier(new UpdateOptions(), Mock.Of<IServer>(server => server.Features == new FeatureCollection()), NullLogger<UpdateApplier>.Instance),
        new UpdateOptions { WorkDirectory = Path.Combine(_directory, "work") },
        NullLogger<UpdateService>.Instance);

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }

        return stream.ToArray();
    }

    private byte[] TarGz(params (string Path, string Text)[] files)
    {
        var root = Directory.CreateTempSubdirectory("cabinet-tar-").FullName;
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllText(Path.Combine(root, path), text);
        }

        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        {
            TarFile.CreateFromDirectory(root, gzip, includeBaseDirectory: false);
        }

        Directory.Delete(root, recursive: true);
        return stream.ToArray();
    }

    [Theory]
    [InlineData("v2.0.1", "2.0.1")]
    [InlineData("2.10.0", "2.10.0")]
    [InlineData("v2.0.1-beta", null)]
    [InlineData("latest", null)]
    public void Tags_AreReadAsVersions(string tag, string? expected)
    {
        ReleaseVersions.FromTag(tag)?.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("3.0.0-rc.1", "3.0.0", -1)]
    [InlineData("3.0.0-rc.2", "3.0.0-rc.1", 1)]
    [InlineData("3.0.0-rc.10", "3.0.0-rc.2", 1)]
    [InlineData("2.5.4", "3.0.0-rc.1", -1)]
    [InlineData("v3.0.0", "3.0.0", 0)]
    [InlineData("3.0.1-beta", "3.0.0", 1)]
    public void PreReleases_OrderBelowTheirRelease_OnServerAndAgent(string a, string b, int expected)
    {
        Math.Sign(ReleaseVersions.Compare(ReleaseVersions.Parse(a)!.Value, ReleaseVersions.Parse(b)!.Value)).Should().Be(expected);
        Math.Sign(Cabinet.Agent.Updating.AgentUpdater.Compare(Cabinet.Agent.Updating.AgentUpdater.Parse(a.TrimStart('v'))!.Value, Cabinet.Agent.Updating.AgentUpdater.Parse(b.TrimStart('v'))!.Value))
            .Should().Be(expected, "the agent orders versions as the server does");
    }

    [Theory]
    [InlineData("3.0.0-rc.1+0123abcd", "3.0.0", "3.0.0-rc.1")]
    [InlineData("3.0.0+0123abcd", "3.0.0", "3.0.0")]
    [InlineData("1.0.0", "3.0.0", "3.0.0")]
    [InlineData(null, "3.0.0", "3.0.0")]
    public void TheFullVersion_ComesFromTheBuild_WhenItMatches(string? informational, string core, string expected)
    {
        ReleaseVersions.FullVersion(informational, Version.Parse(core)).Should().Be(expected);
    }

    [Fact]
    public void FourPartVersions_CompareAsThree()
    {
        ReleaseVersions.Normalize(new Version(2, 0, 1, 0)).Should().Be(new Version(2, 0, 1));
        (ReleaseVersions.FromTag("v2.0.10") > ReleaseVersions.FromTag("v2.0.9")).Should().BeTrue();
    }

    [Fact]
    public async Task Check_ReportsANewerRelease()
    {
        var status = await Service(new FakeReleases()).CheckAsync();

        status.Latest.Should().Be("9.9.9");
        status.Available.Should().BeTrue();
        status.CheckError.Should().BeNull();
    }

    [Fact]
    public async Task CorruptDownload_IsRefused_AndLeavesNothingBehind()
    {
        var releases = new FakeReleases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "new")), checksum: new string('0', 64));
        var service = Service(releases);
        var release = await releases.GetLatestAsync();

        var act = () => service.StageAsync(release, "win-x64");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*doesn't match its checksum*");
        Directory.GetFiles(Path.Combine(_directory, "work"), "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task VerifiedDownload_IsUnpacked_AndReused()
    {
        var releases = new FakeReleases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "new program")));
        var service = Service(releases);
        var release = await releases.GetLatestAsync();

        var staged = await service.StageAsync(release, "win-x64");
        await service.StageAsync(release, "win-x64");

        File.ReadAllText(Path.Combine(staged, "api", "Cabinet.API.exe")).Should().Be("new program");
        releases.Downloads.Should().Be(2, "the archive and its checksum, once");
    }

    [Fact]
    public async Task Agents_GetTheBuildMatchingThisServer()
    {
        var releases = new FakeReleases();
        releases.Add("cabinet-linux-x64.tar.gz", TarGz(("agent/cabinet-agent", "agent program"), ("api/Cabinet.API", "server")));
        var service = Service(releases);

        var package = await service.GetAgentPackageAsync("linux-x64");

        package.Should().NotBeNull();
        package!.Version.Should().Be(ReleaseVersions.Current.ToString());
        File.ReadAllText(package.Path).Should().Be("agent program");
        package.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("agent program"))));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("freebsd-x64")]
    public async Task UnknownPlatforms_AreRefused(string platform)
    {
        (await Service(new FakeReleases()).GetAgentPackageAsync(platform)).Should().BeNull();
    }

    [Fact]
    public async Task OutsideAService_ApplyingExplainsWhy()
    {
        var service = Service(new FakeReleases());

        service.GetStatus().CanApply.Should().BeFalse();
        var act = () => service.ApplyAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*service*");
    }

    /// <summary>A server answering an agent's update check.</summary>
    private sealed class FakeServer(string version, byte[] binary, string sha256) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.GetValues("X-API-Key").Should().Equal("agent-key");
            var response = request.RequestUri!.AbsolutePath.EndsWith("/agent", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { version, sha256, size = binary.Length }), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(binary) };
            return Task.FromResult(response);
        }
    }

    private (AgentUpdater Updater, string Program) Agent(HttpMessageHandler server)
    {
        var program = Path.Combine(_directory, "cabinet-agent");
        File.WriteAllText(program, "old agent");
        var config = new AgentConfiguration { Output = new OutputConfiguration { ServerUrl = "https://cabinet.example:8443", ApiKey = "agent-key" } };
        return (new AgentUpdater(new HttpClient(server), config, Mock.Of<IHostApplicationLifetime>(), NullLogger<AgentUpdater>.Instance, program), program);
    }

    [Fact]
    public async Task Agent_InstallsAVerifiedNewerBuild_KeepingTheOldOne()
    {
        var binary = Encoding.UTF8.GetBytes("new agent");
        var (updater, program) = Agent(new FakeServer("9.9.9", binary, Convert.ToHexString(SHA256.HashData(binary))));

        (await updater.CheckOnceAsync()).Should().BeTrue();

        File.ReadAllText(program).Should().Be("new agent");
        File.ReadAllText(program + ".old").Should().Be("old agent");
    }

    [Fact]
    public async Task Agent_RefusesABuildThatDoesntMatchItsChecksum()
    {
        var (updater, program) = Agent(new FakeServer("9.9.9", Encoding.UTF8.GetBytes("tampered"), new string('A', 64)));

        (await updater.CheckOnceAsync()).Should().BeFalse();

        File.ReadAllText(program).Should().Be("old agent");
        File.Exists(program + ".new").Should().BeFalse();
    }

    [Fact]
    public async Task Agent_IgnoresAnOfferThatIsntNewer()
    {
        var (updater, program) = Agent(new FakeServer("0.0.1", Encoding.UTF8.GetBytes("older"), "x"));

        (await updater.CheckOnceAsync()).Should().BeFalse();

        File.ReadAllText(program).Should().Be("old agent");
    }
}
