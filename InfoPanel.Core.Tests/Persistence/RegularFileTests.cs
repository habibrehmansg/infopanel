using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Persistence;

/// <summary>
/// The data folder's layer itself (<see cref="RegularFile"/>): what is at a path, only regular files opened, and listings
/// that are bounded and never read "can't tell" as empty.
/// </summary>
public sealed class RegularFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void StateOf_AndList_TellAbsentFromEverythingElse_AndAreBounded()
    {
        Assert.Equal(PathState.Absent, RegularFile.StateOf(_temp.Combine("nothing")));
        Assert.Empty(RegularFile.List(_temp.Combine("nothing"), new ListingBudget(10, "test")));
        Directory.CreateDirectory(_temp.Combine("folder"));
        for (var i = 0; i < 5; i++)
            File.WriteAllText(_temp.Combine("folder", $"{i}"), "");
        File.WriteAllText(_temp.Combine("file"), "");

        Assert.Equal(PathState.Folder, RegularFile.StateOf(_temp.Combine("folder")));
        Assert.Equal(PathState.Other, RegularFile.StateOf(_temp.Combine("file")));
        Assert.Equal(5, RegularFile.List(_temp.Combine("folder"), new ListingBudget(5, "test")).Count());
        var budget = new ListingBudget(7, "two listings");
        Assert.Equal(5, RegularFile.List(_temp.Combine("folder"), budget).Count());
        Assert.Throws<ListingBudgetException>(() => RegularFile.List(_temp.Combine("folder"), budget).Count());
        Assert.ThrowsAny<IOException>(() => RegularFile.List(_temp.Combine("file"), new ListingBudget(10, "test")).Count());
        Assert.Throws<FileNotFoundException>(() => RegularFile.OpenRead(_temp.Combine("nothing")));
    }

    [Fact]
    public void LinksToNothing_AndLinksThatLoop_AreUnresolved_NeverAbsent()
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need privileges there
        Directory.CreateDirectory(_temp.Combine("folder"));
        Directory.CreateSymbolicLink(_temp.Combine("to nothing"), _temp.Combine("unmounted"));
        File.CreateSymbolicLink(_temp.Combine("loop a"), _temp.Combine("loop b"));
        File.CreateSymbolicLink(_temp.Combine("loop b"), _temp.Combine("loop a"));
        Directory.CreateSymbolicLink(_temp.Combine("to folder"), _temp.Combine("folder"));

        foreach (var unresolved in new[] { "to nothing", "loop a" })
        {
            var path = _temp.Combine(unresolved);
            Assert.Equal(PathState.Unresolved, RegularFile.StateOf(path));
            Assert.Throws<UnresolvedLinkException>(() => RegularFile.List(path, new ListingBudget(10, "test")).Count());
            Assert.Throws<UnresolvedLinkException>(() => RegularFile.OpenRead(path));
            Assert.Throws<UnresolvedLinkException>(() => RegularFile.OpenAppend(path));
            Assert.Throws<UnresolvedLinkException>(() => RegularFile.OpenLock(path));
        }
        Assert.Equal(PathState.Folder, RegularFile.StateOf(_temp.Combine("to folder")));
        Assert.False(Directory.Exists(_temp.Combine("unmounted")), "nothing was created through the link");
    }

    /// <summary>Appending and locking open only regular files, without waiting on a FIFO whatever its other end does.</summary>
    [Fact]
    public void OpenAppend_AndOpenLock_RefuseAFifo_WithoutWaiting()
    {
        if (!Fifo.Supported)
            return;
        var fifo = _temp.Combine("fifo");
        Fifo.Create(fifo);

        Fifo.Within(10, () => Assert.Throws<NotARegularFileException>(() => RegularFile.OpenAppend(fifo)), fifo);
        Fifo.Within(10, () => Assert.Throws<NotARegularFileException>(() => RegularFile.OpenLock(fifo)), fifo);
        Fifo.Within(10, () => Assert.Throws<NotARegularFileException>(() => RegularFile.OpenRead(fifo)), fifo);
    }

    [Fact]
    public void OpenAppend_CreatesOrAppends_AndOpenLock_IsExclusive()
    {
        var log = _temp.Combine("log");
        using (var first = RegularFile.OpenAppend(log))
            first.Write("one\n"u8);
        using (var second = RegularFile.OpenAppend(log))
            second.Write("two\n"u8);
        Assert.Equal("one\ntwo\n", File.ReadAllText(log));

        var lockFile = _temp.Combine("lock");
        using (RegularFile.OpenLock(lockFile))
            Assert.Throws<LockHeldException>(() => RegularFile.OpenLock(lockFile));
        using (RegularFile.OpenLock(lockFile))
        {
            // The existing file, opened again once released.
        }
    }
}
