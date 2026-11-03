using InfoPanel.SensorHelper;
using Xunit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The PawnIO driver's state as the sensor helper reads it at its start and the engine every 10 s
/// (<see cref="PawnIoDriver.Read"/>), from a fake registry: the uninstall entry's <c>DisplayVersion</c> missing or
/// unparseable is <c>missing</c> (1.4.x: not installed), below 2.0.0.0 <c>outdated</c> (1.4.x's minimum), else
/// <c>installed</c>, with the version as Windows lists it; the helper says <c>installed</c> only with administrator
/// rights, <c>notLoaded</c> without them (<see cref="PawnIoDriver.OfHelper"/>); and the state reaches the engine in
/// <c>hello</c>.
/// </summary>
public sealed class PawnIoDriverTests
{
    [Theory]
    [InlineData(null, "missing", null)]
    [InlineData("1.0.3.0", "outdated", "1.0.3.0")]
    [InlineData("1.9", "outdated", "1.9")]
    [InlineData("2.0.0.0", "installed", "2.0.0.0")]
    [InlineData("2.0.0", "installed", "2.0.0")]
    [InlineData("2.2.0", "installed", "2.2.0")]
    [InlineData(" 2.2.0.0 ", "installed", "2.2.0.0")]
    [InlineData("3.1.4.1", "installed", "3.1.4.1")]
    [InlineData("", "missing", null)]
    [InlineData("2.2.0-beta", "missing", null)]
    [InlineData("v2.2", "missing", null)]
    [InlineData("2", "missing", null)]
    public void TheRegistrysVersion_GivesTheState_AndTheVersion(string? displayVersion, string state, string? version)
    {
        var read = PawnIoDriver.Read(new FakePawnIoRegistry { Value = displayVersion });

        Assert.Equal(new PawnIoReading(state, version), read);
    }

    [Fact]
    public void ARegistryItCantRead_IsMissing_NotAThrow()
    {
        var registry = new FakePawnIoRegistry { Failure = new UnauthorizedAccessException("denied") };

        Assert.Equal(PawnIoReading.None, PawnIoDriver.Read(registry));
    }

    [Theory]
    [InlineData("missing", false, "missing")]
    [InlineData("missing", true, "missing")]
    [InlineData("outdated", false, "outdated")]
    [InlineData("outdated", true, "outdated")]
    [InlineData("installed", true, "installed")]
    [InlineData("installed", false, "notLoaded")] // LibreHardwareMonitorLib can't open PawnIO without administrator rights
    [InlineData("unknown", false, "unknown")]
    public void TheHelper_SaysInstalled_OnlyWithAdministratorRights_NotLoadedWithout(string read, bool elevated, string said) =>
        Assert.Equal(said, PawnIoDriver.OfHelper(new PawnIoReading(read, read == "missing" ? null : "2.2.0.0"), elevated));

    [Theory]
    [InlineData(null, false, "missing")]
    [InlineData("1.0.3.0", false, "outdated")]
    [InlineData("2.2.0", true, "installed")]
    [InlineData("2.2.0", false, "notLoaded")]
    public async Task TheHelpersHello_SaysTheState_ItReadAtItsStart(string? displayVersion, bool elevated, string state)
    {
        var registry = new FakePawnIoRegistry { Value = displayVersion };
        var tree = new RegistryTree(registry, elevated);
        registry.Value = "9.9.9.9"; // read at the start only
        var (pipe, run) = HelperTestKit.StartHelper(tree);
        await using (var client = await HelperTestKit.Connect(pipe))
        {
            var hello = await client.HelloAsync(TestContext.Current.CancellationToken);
            Assert.Equal(state, hello.Driver);
        }

        await run.WaitAsync(HelperTestKit.Patience, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A tree that reads the driver as the helper's own does (<c>LibreTree</c>): once, as it is made, said as a helper with
    /// or without administrator rights says it (the elevation a seam here).
    /// </summary>
    private sealed class RegistryTree(IPawnIoRegistry registry, bool elevated) : ISensorTree
    {
        private readonly FakeTree _inner = FakeTree.Pc();

        public bool Elevated => elevated;

        public string Driver { get; } = PawnIoDriver.OfHelper(PawnIoDriver.Read(registry), elevated);

        public void Configure(bool storage, int storageInterval) => _inner.Configure(storage, storageInterval);

        public TreeSnapshot Update() => _inner.Update();

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>A registry whose PawnIO <c>DisplayVersion</c> the test sets, or that fails to be read; counts its reads.</summary>
internal sealed class FakePawnIoRegistry : IPawnIoRegistry
{
    private string? _value;
    private int _reads;

    public string? Value
    {
        get => Volatile.Read(ref _value);
        set => Volatile.Write(ref _value, value);
    }

    public Exception? Failure { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public string? DisplayVersion()
    {
        Interlocked.Increment(ref _reads);
        if (Failure is { } failure)
            throw failure;
        return Value;
    }
}
