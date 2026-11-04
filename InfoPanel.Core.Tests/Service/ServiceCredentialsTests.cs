using System.Security.Cryptography;
using System.Text;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Service;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Service;

/// <summary>
/// The credential a client provides for the configured service (spec/docs/api.md, "The configured service"): its
/// address rules, the private-file store everywhere (the protected store on Windows only), a fingerprint of the stored
/// credential, a change of address, and a rejection that lasts across a restart until a client provides another.
/// </summary>
public sealed class ServiceCredentialsTests : IDisposable
{
    private const string Address = "https://service.example";
    private const string Credential = "test-credential.A1b2~+/==";

    private readonly TempDirectory _temp = new();
    private readonly List<LogEvent> _log = [];
    private readonly ILogger _logger;
    private string? _address = Address;

    public ServiceCredentialsTests() => _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_log)).CreateLogger();

    public void Dispose() => _temp.Dispose();

    private string FilePath => Path.Combine(_temp.Path, FileServiceCredentialStore.PrivateFileName);

    private FileServiceCredentialStore Store() => FileServiceCredentialStore.ForPrivateFile(_temp.Path, _logger);

    private ServiceCredentials Open(IServiceCredentialStore? store = null) => new(store ?? Store(), () => _address, _logger);

    private static string Sha16(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..16];

    // ---- addresses --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://service.example", "https://service.example")]
    [InlineData("https://Service.Example/", "https://service.example")]
    [InlineData("https://service.example:443", "https://service.example")]
    [InlineData("https://service.example:8443", "https://service.example:8443")]
    [InlineData("http://localhost:8787", "http://localhost:8787")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787")]
    [InlineData("http://127.5.6.7", "http://127.5.6.7")]
    [InlineData("http://[::1]:8787", "http://[::1]:8787")]
    public void AnAddress_IsAnHttpsOrigin_OrHttpOnALoopbackHost_InItsNormalForm(string given, string normal)
    {
        Assert.True(ServiceAddress.TryNormalize(given, out var address));
        Assert.Equal(normal, address);
        Assert.True(ServiceAddress.IsNormal(normal));
    }

    [Theory]
    [InlineData("http://service.example")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://192.168.1.2:8787")]
    [InlineData("http://localhost.example")]
    [InlineData("https://service.example/api")]
    [InlineData("https://service.example/./")]
    [InlineData("https://service.example?x=1")]
    [InlineData("https://service.example#top")]
    [InlineData("https://user@service.example")]
    [InlineData("https://service%2Eexample")]
    [InlineData("https://*.example")]
    [InlineData("ftp://service.example")]
    [InlineData("service.example")]
    [InlineData("https://service.example\n")]
    [InlineData(" https://service.example")]
    [InlineData("http://[fe80::1%25en0]")]
    [InlineData("")]
    public void AnythingElse_IsNoAddress(string given) => Assert.False(ServiceAddress.TryNormalize(given, out _));

    [Theory]
    [InlineData("abc")]
    [InlineData("A-b.c_d~e+f/g")]
    [InlineData("dGVzdA==")]
    public void ACredential_IsAnRfc6750Token(string credential) => Assert.True(ServiceCredentials.IsWellFormed(credential));

    [Theory]
    [InlineData("")]
    [InlineData("==")]
    [InlineData("ab=c")]
    [InlineData("a b")]
    [InlineData("abc\n")]
    [InlineData("abc\r")]
    [InlineData("é")]
    [InlineData("a:b")]
    public void AnythingElse_IsNoCredential(string credential) => Assert.False(ServiceCredentials.IsWellFormed(credential));

    [Fact]
    public void ACredential_IsAtMost4096Characters() =>
        Assert.Equal((true, false), (ServiceCredentials.IsWellFormed(new string('a', 4096)), ServiceCredentials.IsWellFormed(new string('a', 4097))));

    // ---- the fingerprint --------------------------------------------------------------------------------------------

    [Fact]
    public void TheFingerprint_IsNullWhenAbsent_TheHashPrefixWhenPresent_NeverTheCredential_AndChangesWhenReplaced()
    {
        var service = Open();
        Assert.Equal(new ServiceStatus(Address, ServiceCredentialState.Absent, null, null), service.Status);

        service.Put(Credential);
        var first = service.Status;
        Assert.Equal(ServiceCredentialState.Present, first.Credential);
        Assert.Equal("private", first.Store);
        Assert.Equal(Sha16(Credential), first.Fingerprint);
        Assert.Matches("^[0-9a-f]{16}$", first.Fingerprint!);
        Assert.DoesNotContain(Credential, first.ToString());

        service.Put("another-credential");
        Assert.Equal(Sha16("another-credential"), service.Status.Fingerprint);
        Assert.NotEqual(first.Fingerprint, service.Status.Fingerprint);

        service.Delete();
        Assert.Equal(new ServiceStatus(Address, ServiceCredentialState.Absent, null, null), service.Status);
        Assert.False(File.Exists(FilePath));
    }

    // ---- the private store ------------------------------------------------------------------------------------------

    [Fact]
    public void ThePrivateStore_KeepsTheCredentialInOneFile_OnlyItsOwnerMayRead_AcrossARestart()
    {
        Open().Put(Credential);

        Assert.True(File.Exists(FilePath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
        // The only file the store wrote, beside nothing else of its own.
        Assert.Equal([FilePath], Directory.GetFiles(_temp.Path));

        var again = Open();
        Assert.Equal(ServiceCredentialState.Present, again.Status.Credential);
        Assert.Equal(Sha16(Credential), again.Status.Fingerprint);
        Assert.True(again.TryTake(out var address, out var credential, out _));
        Assert.Equal((Address, Credential), (address, credential));
    }

    [Fact]
    public void ThePrivateStore_RemovesTheTempFilesAnInterruptedWriteLeft()
    {
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, Credential);

        Store();

        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void AStoreThatRefuses_KeepsTheCredentialItHad_AndAFileItCantRead_IsNoCredential()
    {
        var store = new RefusingStore(Store());
        var service = Open(store);
        service.Put(Credential);

        store.Refuse = true;
        Assert.Throws<ServiceStoreException>(() => service.Put("another-credential"));
        Assert.Throws<ServiceStoreException>(() => service.Delete());
        Assert.Equal(Sha16(Credential), service.Status.Fingerprint);

        File.WriteAllText(FilePath, "{ not a record");
        var unreadable = Open();
        Assert.Equal(ServiceCredentialState.Absent, unreadable.Status.Credential);
        Assert.DoesNotContain(_log, e => e.RenderMessage().Contains(Credential));
    }

    [Fact]
    public void AFolderFlushThatFailsAfterTheRename_LeavesTheNewCredentialInEffect_AsOnDisk_AndARetryFlushes()
    {
        var faults = new MatchFaults();
        var service = Open(FileServiceCredentialStore.ForPrivateFile(_temp.Path, _logger, faults));
        service.Put(Credential);

        faults.Fail = (step, _) => step == StorageStep.SyncFolder;
        Assert.Throws<ServiceStoreException>(() => service.Put("another-credential"));

        // The file holds the new one, which is what a restart reads: so it is the one in effect now.
        Assert.Equal(Sha16("another-credential"), Open().Status.Fingerprint);
        Assert.Equal(Sha16("another-credential"), service.Status.Fingerprint);
        Assert.True(service.TryTake(out _, out var credential, out _));
        Assert.Equal("another-credential", credential);

        // Provided again: written and its folder flushed.
        faults.Fail = null;
        faults.Steps.Clear();
        service.Put("another-credential");
        Assert.Contains(faults.Steps, s => s.Step == StorageStep.SyncFolder);
        Assert.Equal(Sha16("another-credential"), service.Status.Fingerprint);
    }

    [Fact]
    public void AFolderFlushThatFailsAfterTheUnlink_LeavesNoCredentialInEffect_AndARetryFlushes()
    {
        var faults = new MatchFaults();
        var service = Open(FileServiceCredentialStore.ForPrivateFile(_temp.Path, _logger, faults));
        service.Put(Credential);

        faults.Fail = (step, _) => step == StorageStep.SyncFolder;
        Assert.Throws<ServiceStoreException>(() => service.Delete());

        // The file is gone, as a restart finds it: none in effect now either.
        Assert.False(File.Exists(FilePath));
        Assert.Equal(ServiceCredentialState.Absent, service.Status.Credential);
        Assert.False(service.TryTake(out _, out _, out _));

        // Removed again: the file is already gone, and the folder is flushed all the same.
        faults.Fail = null;
        faults.Steps.Clear();
        service.Delete();
        Assert.Contains(faults.Steps, s => s.Step == StorageStep.SyncFolder);

        // Once flushed, a removal with nothing to do touches nothing.
        faults.Steps.Clear();
        service.Delete();
        Assert.Empty(faults.Steps);
    }

    [Fact]
    public void AStoreFolderThatCantBeRead_IsNoCredential_LoggedOnce_AndStartUpGoesOn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A Unix mode that keeps the owner out of the folder.");
        var folder = Path.Combine(_temp.Path, "locked");
        Directory.CreateDirectory(folder);
        FileServiceCredentialStore.ForPrivateFile(folder, _logger).Write(new StoredServiceCredential(Address, Credential, false));
        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            if (CanList(folder))
                Assert.Skip("Running with privileges that ignore the folder's mode.");

            var service = Open(FileServiceCredentialStore.ForPrivateFile(folder, _logger));

            Assert.Equal(ServiceCredentialState.Absent, service.Status.Credential);
            Assert.False(service.TryTake(out _, out _, out _));
            Assert.Single(_log, e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.StartsWith("The stored service credential couldn't be read", StringComparison.Ordinal));
            Assert.DoesNotContain(_log, e => e.RenderMessage().Contains(Credential));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        static bool CanList(string folder)
        {
            try
            {
                Directory.GetFileSystemEntries(folder);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [Fact]
    public void WithoutAnAddress_NoCredentialIsKept()
    {
        _address = null;
        var service = Open();

        Assert.Throws<NoServiceException>(() => service.Put(Credential));
        Assert.False(File.Exists(FilePath));
        Assert.Equal(new ServiceStatus(null, ServiceCredentialState.Absent, null, null), service.Status);
    }

    [Fact]
    public void AMalformedCredential_IsRefused_AndNothingIsStored()
    {
        var service = Open();

        Assert.Throws<ArgumentException>(() => service.Put("abc\n"));
        Assert.False(File.Exists(FilePath));
    }

    // ---- the address ------------------------------------------------------------------------------------------------

    [Fact]
    public void AChangeOfAddress_RemovesTheCredential_FileAndAll()
    {
        var service = Open();
        service.Put(Credential);

        _address = "https://other.example";
        service.AddressChanged();

        Assert.Equal(new ServiceStatus("https://other.example", ServiceCredentialState.Absent, null, null), service.Status);
        Assert.False(File.Exists(FilePath));
        Assert.False(service.TryTake(out _, out _, out _));
    }

    [Fact]
    public void AChangeOfAddress_WhileTheEngineWasStopped_RemovesTheCredentialAtStart()
    {
        Open().Put(Credential);

        _address = "http://127.0.0.1:8787";
        var restarted = Open();

        Assert.Equal(ServiceCredentialState.Absent, restarted.Status.Credential);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void NoAddressAnyMore_RemovesTheCredential()
    {
        var service = Open();
        service.Put(Credential);

        _address = null;
        service.AddressChanged();

        Assert.False(File.Exists(FilePath));
        Assert.Equal(ServiceCredentialState.Absent, service.Status.Credential);
    }

    [Fact]
    public void ACredentialForAnotherAddressThatCantBeRemovedAtStart_RefusesAChangeOfAddress_UntilItIsRemoved()
    {
        Open().Put(Credential);
        var store = new RefusingStore(Store()) { Refuse = true };
        _address = "https://other.example";

        var service = Open(store);
        Assert.Equal(ServiceCredentialState.Absent, service.Status.Credential);
        Assert.True(File.Exists(FilePath));

        // Back to the address it was provided for: refused while the store can't remove it, so it can't come back.
        Assert.Throws<ServiceStoreException>(() => service.AddressChanging(Address));
        // Another setting changes, the address doesn't: nothing to do.
        service.AddressChanging("https://other.example").Dispose();

        store.Refuse = false;
        using (service.AddressChanging(Address))
            _address = Address;
        service.AddressChanged();

        Assert.False(File.Exists(FilePath));
        Assert.Equal(ServiceCredentialState.Absent, service.Status.Credential);
        Assert.Equal(ServiceCredentialState.Absent, Open().Status.Credential);
    }

    // ---- rejected ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ARejection_LastsAcrossARestart_AndANewCredentialClearsIt()
    {
        var service = Open();
        service.Put(Credential);
        Assert.True(service.TryTake(out _, out _, out _, out var sent));
        service.Reject(sent);
        Assert.Equal(ServiceCredentialState.Rejected, service.Status.Credential);
        Assert.Equal(Sha16(Credential), service.Status.Fingerprint);

        var restarted = Open();
        Assert.Equal(ServiceCredentialState.Rejected, restarted.Status.Credential);
        Assert.False(restarted.TryTake(out _, out var none, out var state));
        Assert.Null(none);
        Assert.Equal(ServiceCredentialState.Rejected, state);

        restarted.Put("another-credential");
        Assert.Equal(ServiceCredentialState.Present, restarted.Status.Credential);
        Assert.Equal(ServiceCredentialState.Present, Open().Status.Credential);
    }

    [Fact]
    public void ARejectionOfACredentialAlreadyReplaced_ChangesNothing()
    {
        var service = Open();
        service.Put(Credential);
        Assert.True(service.TryTake(out _, out _, out _, out var sent));
        service.Put("another-credential");

        service.Reject(sent);

        Assert.Equal(ServiceCredentialState.Present, service.Status.Credential);
    }

    // ---- the protected store (Windows) ------------------------------------------------------------------------------

    [Fact]
    public void TheProtectedStore_KeepsTheCredentialEncryptedForThisUser_AcrossARestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: DPAPI.");
        var service = new ServiceCredentials(FileServiceCredentialStore.ForProtectedFile(_temp.Path, createTemp: null, _logger), () => _address, _logger);
        service.Put(Credential);

        var file = Path.Combine(_temp.Path, FileServiceCredentialStore.ProtectedFileName);
        Assert.Equal([file], Directory.GetFiles(_temp.Path));
        var bytes = File.ReadAllBytes(file);
        // Its part no JSON writer escapes (the record's writer turns '+' into \u002B), in either encoding.
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Credential[..20])));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Credential[..20])));
        Assert.Equal("protected", service.Status.Store);

        var again = new ServiceCredentials(FileServiceCredentialStore.ForProtectedFile(_temp.Path, createTemp: null, _logger), () => _address, _logger);
        Assert.True(again.TryTake(out _, out var credential, out _, out var sent));
        Assert.Equal(Credential, credential);
        again.Reject(sent);
        Assert.Equal(ServiceCredentialState.Rejected,
            new ServiceCredentials(FileServiceCredentialStore.ForProtectedFile(_temp.Path, createTemp: null, _logger), () => _address, _logger).Status.Credential);
    }

    [Fact]
    public void TheProtectedStore_RefusesAFileItCantDecrypt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: DPAPI.");
        var file = Path.Combine(_temp.Path, FileServiceCredentialStore.ProtectedFileName);
        File.WriteAllBytes(file, new StoredServiceCredential(Address, Credential, false).ToBytes());

        var store = FileServiceCredentialStore.ForProtectedFile(_temp.Path, createTemp: null, _logger);

        Assert.Throws<ServiceStoreException>(() => store.Read());
        Assert.Equal(ServiceCredentialState.Absent, new ServiceCredentials(store, () => _address, _logger).Status.Credential);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A store that refuses every write and delete while <see cref="Refuse"/> is set.</summary>
    internal sealed class RefusingStore(IServiceCredentialStore inner) : IServiceCredentialStore
    {
        public bool Refuse { get; set; }

        public string Name => inner.Name;

        public StoredServiceCredential? Read() => inner.Read();

        public void Write(StoredServiceCredential record)
        {
            if (Refuse) throw new ServiceStoreException("Refused.");
            inner.Write(record);
        }

        public void Delete()
        {
            if (Refuse) throw new ServiceStoreException("Refused.");
            inner.Delete();
        }
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events) events.Add(logEvent);
        }
    }
}
