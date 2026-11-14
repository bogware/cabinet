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
using AgentSignature = Cabinet.Agent.Updating.ReleaseSignature;
using ServerSignature = Cabinet.API.Services.Updates.ReleaseSignature;

namespace Cabinet.Tests.Services;

public sealed class UpdateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-update-").FullName;

    /// <summary>A throwaway publisher key, standing in for the real one, whose private half is only in CI.</summary>
    private readonly ECDsa _publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _publisher.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>The publisher's public key, as a program would embed it.</summary>
    private ECDsa PublicKey() => ServerSignature.LoadPublicKey(_publisher.ExportSubjectPublicKeyInfoPem());

    /// <summary>A signature as the release workflow writes it: base64 of the DER signature.</summary>
    private static string Sign(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>Releases held in memory: archives, their checksums and signatures by name.</summary>
    private sealed class FakeReleases(ECDsa? signer = null) : IReleaseSource
    {
        private readonly Dictionary<string, byte[]> _files = new();
        private readonly List<ReleaseAsset> _assets = [];

        public string Tag { get; set; } = "v9.9.9";

        public int Downloads { get; private set; }

        /// <summary>An archive with its checksum, and signed by the publisher unless told otherwise.</summary>
        public void Add(string name, byte[] contents, string? checksum = null, string? signature = null, bool signed = true)
        {
            Put(name, contents);
            Put(name + ".sha256", Encoding.ASCII.GetBytes($"{checksum ?? Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant()}  {name}\n"));
            if (signature != null || (signed && signer != null))
            {
                Put(name + ".sig", Encoding.ASCII.GetBytes((signature ?? Sign(signer!, contents)) + "\n"));
            }
        }

        public void Put(string name, byte[] contents)
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

    private FakeReleases Releases() => new(_publisher);

    private UpdateService Service(IReleaseSource source, bool allowUnsigned = false) => new(
        source,
        new UpdateApplier(new UpdateOptions(), Mock.Of<IServer>(server => server.Features == new FeatureCollection()), NullLogger<UpdateApplier>.Instance),
        new UpdateOptions { WorkDirectory = Path.Combine(_directory, "work"), AllowUnsigned = allowUnsigned },
        NullLogger<UpdateService>.Instance,
        PublicKey());

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

    [Theory]
    [InlineData(new string[0], "anything", true)]
    [InlineData(new[] { "game-dev" }, "game-dev", true)]
    [InlineData(new[] { "game-dev" }, "GAME-DEV", true)]
    [InlineData(new[] { "game-dev" }, "game-prod", false)]
    [InlineData(new[] { "web-*", "db-1" }, "web-07", true)]
    [InlineData(new[] { "web-*" }, "xweb-07", false)]
    [InlineData(new[] { "a.b" }, "axb", false)]
    public void AgentRollout_OffersTheBuildToListedHosts(string[] hosts, string host, bool offered)
    {
        new UpdateOptions { AgentHosts = [.. hosts] }.OffersAgentTo(host).Should().Be(offered);
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
        var status = await Service(Releases()).CheckAsync();

        status.Latest.Should().Be("9.9.9");
        status.Available.Should().BeTrue();
        status.CheckError.Should().BeNull();
    }

    [Fact]
    public async Task CorruptDownload_IsRefused_AndLeavesNothingBehind()
    {
        var releases = Releases();
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
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "new program")));
        var service = Service(releases);
        var release = await releases.GetLatestAsync();

        var staged = await service.StageAsync(release, "win-x64");
        await service.StageAsync(release, "win-x64");

        File.ReadAllText(Path.Combine(staged, "api", "Cabinet.API.exe")).Should().Be("new program");
        releases.Downloads.Should().Be(3, "the archive, its checksum and its signature, once");
    }

    [Fact]
    public async Task ADownload_SignedByAnotherKey_IsRefused_BeforeUnpacking()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var archive = Zip(("api/Cabinet.API.exe", "someone else's program"));
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", archive, signature: Sign(stranger, archive));

        var release = await releases.GetLatestAsync();

        var act = () => Service(releases).StageAsync(release, "win-x64");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*isn't signed by the Cabinet release key*");
        Directory.GetFiles(Path.Combine(_directory, "work"), "*", SearchOption.AllDirectories).Should().BeEmpty("nothing is unpacked");
    }

    [Fact]
    public async Task AModifiedArchive_WithAMatchingChecksum_IsRefused()
    {
        // Whoever can replace the archive can replace its checksum too; only the signature holds
        var original = Zip(("api/Cabinet.API.exe", "the real program"));
        var modified = Zip(("api/Cabinet.API.exe", "a modified program"));
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", modified, signature: Sign(_publisher, original));

        var release = await releases.GetLatestAsync();

        var act = () => Service(releases).StageAsync(release, "win-x64");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*isn't signed by the Cabinet release key*");
        Directory.GetFiles(Path.Combine(_directory, "work"), "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public async Task AMalformedSignature_IsRefused(string signature)
    {
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "x")), signature: signature);

        var release = await releases.GetLatestAsync();

        var act = () => Service(releases, allowUnsigned: true).StageAsync(release, "win-x64");

        await act.Should().ThrowAsync<InvalidOperationException>("a signature that's there but wrong is refused even when unsigned releases are allowed")
            .WithMessage("*isn't signed by the Cabinet release key*");
    }

    [Fact]
    public async Task AnUnsignedRelease_IsRefused_UnlessAllowedForDevelopment()
    {
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "unsigned program")), signed: false);
        var release = await releases.GetLatestAsync();

        var act = () => Service(releases).StageAsync(release, "win-x64");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has no signature for cabinet-win-x64.zip*");
        releases.Downloads.Should().Be(0, "it's refused before downloading anything");

        var staged = await Service(releases, allowUnsigned: true).StageAsync(release, "win-x64");
        File.ReadAllText(Path.Combine(staged, "api", "Cabinet.API.exe")).Should().Be("unsigned program");

        var again = () => Service(releases).StageAsync(release, "win-x64");
        await again.Should().ThrowAsync<InvalidOperationException>("an unsigned download isn't reused once unsigned releases aren't allowed");
    }

    [Fact]
    public async Task AReleaseStagedBeforeSignatures_IsVerifiedAgain()
    {
        // Servers before 3.1.0 unpacked without checking a signature and marked it ".complete"
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "verified program")));
        var release = await releases.GetLatestAsync();
        var old = Path.Combine(_directory, "work", release.Tag, "win-x64");
        Directory.CreateDirectory(Path.Combine(old, "api"));
        File.WriteAllText(Path.Combine(old, "api", "Cabinet.API.exe"), "unverified program");
        File.WriteAllText(Path.Combine(old, ".complete"), "x");

        var staged = await Service(releases).StageAsync(release, "win-x64");

        File.ReadAllText(Path.Combine(staged, "api", "Cabinet.API.exe")).Should().Be("verified program");
    }

    [Fact]
    public void OneKey_CantRewriteAnotherKeysAgents_OrFillTheList()
    {
        var service = Service(new FakeReleases());

        service.RecordAgent("web-1", "linux-x64", "3.0.0", sender: "key-a");
        service.RecordAgent("web-1", "linux-x64", "0.0.1", sender: "key-b");
        for (var i = 0; i < UpdateService.MaxAgentsPerSender + 50; i++)
        {
            service.RecordAgent($"fake-{i}", "linux-x64", "1.0.0", sender: "key-b");
        }

        service.RecordAgent("web-2", "linux-x64", "3.0.0", sender: "key-a");
        var agents = service.GetStatus().Agents;
        agents.Should().Contain(a => a.Host == "web-1" && a.Version == "3.0.0", "key-a's report stands");
        agents.Should().Contain(a => a.Host == "web-2", "another key's flood doesn't crowd out key-a");
        agents.Count(a => a.Host.StartsWith("fake-", StringComparison.Ordinal)).Should().BeLessThan(UpdateService.MaxAgentsPerSender);
    }

    [Fact]
    public void AServerOnACandidate_IsOfferedTheNewestRelease_CandidatesIncluded()
    {
        // GitHub's "latest" skips pre-releases, so rc.1 would never hear of rc.2
        var asset = new List<ReleaseAsset> { new(1, "cabinet-win-x64.zip", 1) };
        ReleaseInfo Release(string tag, bool withAssets = true) =>
            new(tag, ReleaseVersions.Parse(tag)!.Value.Core, null, null, withAssets ? asset : []);

        GitHubReleaseSource.Newest([Release("v2.5.4"), Release("v3.0.0-rc.2"), Release("v3.0.0-rc.10"), Release("v3.0.0-rc.11", withAssets: false)])!
            .Tag.Should().Be("v3.0.0-rc.10", "numbered labels order by number, and a release without files is skipped");
        GitHubReleaseSource.Newest([Release("v3.0.0-rc.3"), Release("v3.0.0")])!.Tag.Should().Be("v3.0.0");
    }

    [Fact]
    public async Task AReleaseCandidate_AndItsRelease_AreStagedApart()
    {
        // Both are version 3.0.0; reusing the candidate's files would reinstall it on every update
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "candidate")));
        var service = Service(releases);
        var assets = (await releases.GetLatestAsync()).Assets;
        var candidate = new ReleaseInfo("v3.0.0-rc.1", new Version(3, 0, 0), "notes", DateTimeOffset.UtcNow, assets);
        var final = new ReleaseInfo("v3.0.0", new Version(3, 0, 0), "notes", DateTimeOffset.UtcNow, assets);

        var first = await service.StageAsync(candidate, "win-x64");
        var second = await service.StageAsync(final, "win-x64");

        second.Should().NotBe(first);
        releases.Downloads.Should().Be(6, "each release is downloaded with its checksum and signature");
    }

    [Fact]
    public async Task OlderDownloads_AreRemoved_WhenANewReleaseIsStaged()
    {
        // Each is a few hundred megabytes; a server that updated nine times kept 3.5 GB of them
        var releases = Releases();
        releases.Add("cabinet-win-x64.zip", Zip(("api/Cabinet.API.exe", "x")));
        var service = Service(releases);
        var assets = (await releases.GetLatestAsync()).Assets;
        var work = Path.Combine(_directory, "work");
        var running = Path.Combine(work, ReleaseVersions.CurrentTag, "win-x64");
        Directory.CreateDirectory(running);

        await service.StageAsync(new ReleaseInfo("v8.0.0", new Version(8, 0, 0), null, null, assets), "win-x64");
        await service.StageAsync(new ReleaseInfo("v9.0.0", new Version(9, 0, 0), null, null, assets), "win-x64");

        Directory.GetDirectories(work).Select(Path.GetFileName).Should().BeEquivalentTo([ReleaseVersions.CurrentTag, "v9.0.0"],
            "the running version's download stays for agents, and the newest for the update");
    }

    [Fact]
    public async Task Agents_GetTheBuildMatchingThisServer_WithItsSignature()
    {
        var releases = Releases();
        releases.Add("cabinet-linux-x64.tar.gz", TarGz(("agent/cabinet-agent", "agent program"), ("api/Cabinet.API", "server")));
        var agentSignature = Sign(_publisher, Encoding.UTF8.GetBytes("agent program"));
        releases.Put("cabinet-agent-linux-x64.sig", Encoding.ASCII.GetBytes(agentSignature + "\n"));
        var service = Service(releases);

        var package = await service.GetAgentPackageAsync("linux-x64");

        package.Should().NotBeNull();
        package!.Version.Should().Be(ReleaseVersions.Current.ToString());
        File.ReadAllText(package.Path).Should().Be("agent program");
        package.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("agent program"))));
        package.Signature.Should().Be(agentSignature, "agents check the publisher's signature themselves");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task Agents_AreNotOfferedABuild_WithoutAGoodSignature(string? signature)
    {
        var releases = Releases();
        releases.Add("cabinet-linux-x64.tar.gz", TarGz(("agent/cabinet-agent", "agent program")));
        if (signature == "wrong")
        {
            releases.Put("cabinet-agent-linux-x64.sig", Encoding.ASCII.GetBytes(Sign(_publisher, Encoding.UTF8.GetBytes("another program"))));
        }

        (await Service(releases).GetAgentPackageAsync("linux-x64")).Should().BeNull();
    }

    [Fact]
    public async Task Agents_AreNotOfferedABuild_FromAnUnsignedArchive()
    {
        var releases = Releases();
        releases.Add("cabinet-linux-x64.tar.gz", TarGz(("agent/cabinet-agent", "agent program")), signed: false);
        releases.Put("cabinet-agent-linux-x64.sig", Encoding.ASCII.GetBytes(Sign(_publisher, Encoding.UTF8.GetBytes("agent program"))));

        (await Service(releases).GetAgentPackageAsync("linux-x64")).Should().BeNull();
    }

    [Fact]
    public void TheEmbeddedReleaseKey_IsTheCommittedOne_OnServerAndAgent()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "build", "release-signing.pub.pem")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("build/release-signing.pub.pem is in the repository");
        var committed = File.ReadAllText(Path.Combine(directory!.FullName, "build", "release-signing.pub.pem"));

        ServerSignature.EmbeddedPublicKeyPem.Should().Be(committed);
        AgentSignature.EmbeddedPublicKeyPem.Should().Be(committed);
        using var server = ServerSignature.LoadEmbeddedKey();
        using var agent = AgentSignature.LoadEmbeddedKey();
        server.KeySize.Should().Be(256);
        agent.KeySize.Should().Be(256);
    }

    [Fact]
    public void AKeyOnAnotherCurve_IsRefused()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        var server = () => ServerSignature.LoadPublicKey(p384.ExportSubjectPublicKeyInfoPem());
        var agent = () => AgentSignature.LoadPublicKey(p384.ExportSubjectPublicKeyInfoPem());

        server.Should().Throw<CryptographicException>();
        agent.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void TheUpdater_IsToldTheFullRunningVersion_AndTheReleaseTag()
    {
        // It logged "Updating from 3.0.0 to 3.0.0" when the server ran 3.0.0-rc.4
        var staged = Path.Combine(_directory, "work", "v3.0.1", "linux-x64");
        Directory.CreateDirectory(staged);
        var applier = new UpdateApplier(new UpdateOptions(), Mock.Of<IServer>(server => server.Features == new FeatureCollection()), NullLogger<UpdateApplier>.Instance);

        var start = applier.PrepareStart(staged, "3.0.0-rc.4", "v3.0.1");

        start.ArgumentList.Should().Contain("3.0.0-rc.4").And.Contain("v3.0.1");
        var script = Directory.GetFiles(staged, "apply-update.*").Single();
        File.ReadAllText(script).Should().Contain("Updating from");
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("freebsd-x64")]
    public async Task UnknownPlatforms_AreRefused(string platform)
    {
        (await Service(Releases()).GetAgentPackageAsync(platform)).Should().BeNull();
    }

    [Fact]
    public async Task OutsideAService_ApplyingExplainsWhy()
    {
        var service = Service(Releases());

        service.GetStatus().CanApply.Should().BeFalse();
        var act = () => service.ApplyAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*service*");
    }

    /// <summary>A server answering an agent's update check.</summary>
    private sealed class FakeServer(string version, byte[] binary, string sha256, string? signature = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.GetValues("X-API-Key").Should().Equal("agent-key");
            var response = request.RequestUri!.AbsolutePath.EndsWith("/agent", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { version, sha256, size = binary.Length, signature }), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(binary) };
            return Task.FromResult(response);
        }
    }

    /// <summary>A server offering a build signed by the publisher.</summary>
    private FakeServer SignedServer(string version, byte[] binary) =>
        new(version, binary, Convert.ToHexString(SHA256.HashData(binary)), Sign(_publisher, binary));

    private (AgentUpdater Updater, string Program) Agent(HttpMessageHandler server, OutputConfiguration? output = null, bool allowUnsigned = false)
    {
        var program = Path.Combine(_directory, "cabinet-agent");
        File.WriteAllText(program, "old agent");
        var config = new AgentConfiguration
        {
            Output = output ?? new OutputConfiguration { ServerUrl = "https://cabinet.example:8443", ApiKey = "agent-key" },
            Update = new UpdateConfiguration { AllowUnsigned = allowUnsigned },
        };
        var key = AgentSignature.LoadPublicKey(_publisher.ExportSubjectPublicKeyInfoPem());
        return (new AgentUpdater(new HttpClient(server), config, Mock.Of<IHostApplicationLifetime>(), NullLogger<AgentUpdater>.Instance, program, key), program);
    }

    [Fact]
    public async Task Agent_InstallsAVerifiedNewerBuild_KeepingTheOldOne()
    {
        var (updater, program) = Agent(SignedServer("9.9.9", Encoding.UTF8.GetBytes("new agent")));

        (await updater.CheckOnceAsync()).Should().BeTrue();

        File.ReadAllText(program).Should().Be("new agent");
        File.ReadAllText(program + ".old").Should().Be("old agent");
    }

    [Fact]
    public async Task Agent_RefusesABuildSignedByAnotherKey()
    {
        // A server that has been taken over can offer any program with a matching checksum
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var binary = Encoding.UTF8.GetBytes("someone else's agent");
        var (updater, program) = Agent(new FakeServer("9.9.9", binary, Convert.ToHexString(SHA256.HashData(binary)), Sign(stranger, binary)));

        (await updater.CheckOnceAsync()).Should().BeFalse();

        File.ReadAllText(program).Should().Be("old agent");
        File.Exists(program + ".new").Should().BeFalse();
    }

    [Fact]
    public async Task Agent_RefusesAModifiedBuild_WithAMatchingChecksum()
    {
        var modified = Encoding.UTF8.GetBytes("modified agent");
        var (updater, program) = Agent(new FakeServer("9.9.9", modified, Convert.ToHexString(SHA256.HashData(modified)), Sign(_publisher, Encoding.UTF8.GetBytes("new agent"))));

        (await updater.CheckOnceAsync()).Should().BeFalse();

        File.ReadAllText(program).Should().Be("old agent");
        File.Exists(program + ".new").Should().BeFalse();
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    public async Task Agent_RefusesAMalformedSignature(string signature)
    {
        var binary = Encoding.UTF8.GetBytes("new agent");
        var (updater, program) = Agent(new FakeServer("9.9.9", binary, Convert.ToHexString(SHA256.HashData(binary)), signature), allowUnsigned: true);

        (await updater.CheckOnceAsync()).Should().BeFalse("a signature that's there but wrong is refused even when unsigned builds are allowed");

        File.ReadAllText(program).Should().Be("old agent");
    }

    [Fact]
    public async Task Agent_RefusesAnUnsignedBuild_UnlessAllowedForDevelopment()
    {
        // Servers before 3.1.0 send no signature
        var binary = Encoding.UTF8.GetBytes("new agent");
        var unsigned = new FakeServer("9.9.9", binary, Convert.ToHexString(SHA256.HashData(binary)));

        var (updater, program) = Agent(unsigned);
        (await updater.CheckOnceAsync()).Should().BeFalse();
        File.ReadAllText(program).Should().Be("old agent");

        var (allowing, allowed) = Agent(unsigned, allowUnsigned: true);
        (await allowing.CheckOnceAsync()).Should().BeTrue();
        File.ReadAllText(allowed).Should().Be("new agent");
    }

    [Theory]
    [InlineData("http://cabinet.example:8080", true, null)]
    [InlineData("https://cabinet.example:8443", false, null)]
    public async Task Agent_WontUpdate_OverAConnectionThatDoesntProveItsTheServer(string url, bool verify, string? pin)
    {
        // Whoever sits between the agent and an unauthenticated server could offer any program
        var binary = Encoding.UTF8.GetBytes("new agent");
        var (updater, program) = Agent(
            new FakeServer("9.9.9", binary, Convert.ToHexString(SHA256.HashData(binary))),
            new OutputConfiguration { ServerUrl = url, VerifySsl = verify, CertificateSha256 = pin, ApiKey = "agent-key" });

        (await updater.CheckOnceAsync()).Should().BeFalse();

        File.ReadAllText(program).Should().Be("old agent");
    }

    [Fact]
    public void Agent_UpdatesOverHttps_VerifiedOrPinned()
    {
        AgentUpdater.AuthenticatesServer(new OutputConfiguration { ServerUrl = "https://a:8443", VerifySsl = true }).Should().BeTrue();
        AgentUpdater.AuthenticatesServer(new OutputConfiguration { ServerUrl = "https://a:8443", VerifySsl = false, CertificateSha256 = new string('A', 64) }).Should().BeTrue();
    }

    [Fact]
    public async Task Agent_RefusesABuildThatDoesntMatchItsChecksum()
    {
        var tampered = Encoding.UTF8.GetBytes("tampered");
        var (updater, program) = Agent(new FakeServer("9.9.9", tampered, new string('A', 64), Sign(_publisher, tampered)));

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
