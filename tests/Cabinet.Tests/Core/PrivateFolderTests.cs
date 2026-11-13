using System.Security.AccessControl;
using System.Security.Principal;
using Cabinet.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Core;

public sealed class PrivateFolderTests : IDisposable
{
    private readonly string _path = Directory.CreateTempSubdirectory("cabinet-private-").FullName;

    public void Dispose() => Directory.Delete(_path, recursive: true);

    [SkippableFact]
    public void AFolderEveryUserCanRead_IsMadePrivate_AndStaysUsableByTheServer()
    {
        // Under C:\ProgramData, a new folder inherits read for every user; the engine's folder holds
        // every event, the metadata database and the keys that sign dashboard sessions
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows access control lists");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var open = new DirectoryInfo(_path).GetAccessControl();
        open.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_path).SetAccessControl(open);
        PrivateFolder.IsOpen(_path).Should().BeTrue();

        PrivateFolder.Secure(_path).Should().BeTrue();

        PrivateFolder.IsOpen(_path).Should().BeFalse("only SYSTEM, Administrators and this account remain");
        File.WriteAllText(Path.Combine(_path, "still-writable.txt"), "yes");
        PrivateFolder.Secure(_path).Should().BeFalse("nothing left to change");
    }

    [Fact]
    public void AMissingFolder_IsLeftAlone()
    {
        PrivateFolder.Secure(Path.Combine(_path, "missing")).Should().BeFalse();
    }
}
