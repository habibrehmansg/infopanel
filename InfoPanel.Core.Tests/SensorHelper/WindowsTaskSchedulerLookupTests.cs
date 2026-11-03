using System.Runtime.InteropServices;
using InfoPanel.SensorHelper;
using Xunit;

// The lookup's handling of what the Task Scheduler throws, through the adapter on a stand-in for its COM object (late-bound,
// as the real one is): no Windows call, so it runs on every OS.
#pragma warning disable CA1416

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// <see cref="WindowsTaskScheduler.ActionOf(string)"/>'s lookup: only a task or folder that isn't there is no task (null);
/// any other failure (the Task Scheduler busy or unavailable, access refused) propagates, so the helper's start says the
/// task couldn't be read (<c>taskUnreadable</c>) instead of taking it for none.
/// </summary>
public sealed class WindowsTaskSchedulerLookupTests
{
    private const string Path = @"\InfoPanel\SensorHelper-S-1-5-21-1-2-3-1001";
    private const int SchedulerBusy = unchecked((int)0x80041323); // SCHED_E_SERVICE_TOO_BUSY
    private const int SchedulerUnavailable = unchecked((int)0x80041322); // SCHED_E_SERVICE_NOT_AVAILABLE
    private const int AccessDenied = unchecked((int)0x80070005); // E_ACCESSDENIED
    private const int FileNotFound = unchecked((int)0x80070002); // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
    private const int PathNotFound = unchecked((int)0x80070003); // HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)

    // Public, as the late binder (called from the engine's assembly) sees only public members of public types.
    public sealed class Service(Func<string, Folder> folder)
    {
        public List<string> Asked { get; } = [];

        public Folder GetFolder(string path)
        {
            Asked.Add("folder " + path);
            return folder(path);
        }
    }

    public sealed class Folder(Func<string, ScheduledTask> task)
    {
        public ScheduledTask GetTask(string name) => task(name);
    }

    public sealed class ScheduledTask(string? userId, params ExecAction[] actions)
    {
        public ScheduledTask(string path, string arguments)
            : this("SYSTEM", new ExecAction(path, arguments))
        {
        }

        public Definition Definition { get; } = new(new Principal(userId), new Actions(actions));
    }

    public sealed class Definition(Principal principal, Actions actions)
    {
        public Principal Principal => principal;

        public Actions Actions => actions;
    }

    public sealed class Principal(string? userId)
    {
        public string? UserId => userId;
    }

    public sealed class Actions(ExecAction[] actions)
    {
        public int Count => actions.Length;

        public ExecAction Item(int index) => index >= 1 && index <= actions.Length ? actions[index - 1] : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public sealed class ExecAction(string? path, string? arguments, int type = 0)
    {
        public int Type => type; // 0: TASK_ACTION_EXEC
        public string? Path => path;
        public string? Arguments => arguments;
    }

    private static Service TaskThrows(Exception ex) => new(_ => new Folder(_ => throw ex));

    private static Service FolderThrows(Exception ex) => new(_ => throw ex);

    [Fact]
    public void ATaskThatIsThere_IsItsAction()
    {
        var service = new Service(_ => new Folder(name => new ScheduledTask(@"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", "--serve --user " + name)));

        var action = WindowsTaskScheduler.ActionOf(service, Path);

        Assert.NotNull(action);
        Assert.Equal((@"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe", "--serve --user SensorHelper-S-1-5-21-1-2-3-1001"), action.Value);
        Assert.Equal(new[] { @"folder \InfoPanel" }, service.Asked);
    }

    [Fact]
    public void ADefinition_IsItsPrincipalsSid_AndEachActionInOrder()
    {
        // Codex W5.2 r4 1: --register reads its task back. The principal as the Task Scheduler names it, told as a SID.
        const string helper = @"C:\Program Files\InfoPanel\sensor-helper\InfoPanel.SensorHelper.exe";
        var service = new Service(_ => new Folder(_ => new ScheduledTask("SYSTEM",
            new ExecAction(helper, "--serve --user S-1-5-21-1-2-3-1001"), new ExecAction(null, null), new ExecAction("x", "y", type: 5))));
        var told = new List<string>();

        var definition = WindowsTaskScheduler.DefinitionOf(service, Path, name =>
        {
            told.Add(name);
            return name == "SYSTEM" ? "S-1-5-18" : null;
        });

        Assert.NotNull(definition);
        Assert.Equal("S-1-5-18", definition.UserSid);
        Assert.Equal(["SYSTEM"], told);
        Assert.Equal([new HelperTaskAction(helper, "--serve --user S-1-5-21-1-2-3-1001"), new HelperTaskAction("", ""), new HelperTaskAction(null, "")], definition.Actions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone")]
    public void APrincipalWithNoUser_OrOneThatCantBeTold_HasNoSid(string? userId)
    {
        var service = new Service(_ => new Folder(_ => new ScheduledTask(userId, new ExecAction("x", "y"))));

        Assert.Null(WindowsTaskScheduler.DefinitionOf(service, Path, _ => null)!.UserSid);
    }

    [Theory]
    [MemberData(nameof(Absences))]
    public void ADefinition_OfATaskThatIsntThere_IsNone(string kind) =>
        Assert.Null(WindowsTaskScheduler.DefinitionOf(TaskThrows(Absence(kind)), Path, _ => "S-1-5-18"));

    [Theory]
    [MemberData(nameof(Failures))]
    public void ADefinition_ATaskSchedulerFailure_Propagates(int hresult) =>
        Assert.Equal(hresult, Assert.Throws<COMException>(() => WindowsTaskScheduler.DefinitionOf(TaskThrows(new COMException("failed", hresult)), Path, _ => "S-1-5-18")).HResult);

    public static TheoryData<string> Absences() => ["file", "directory", "com file", "com path"];

    private static Exception Absence(string kind) => kind switch
    {
        "file" => new FileNotFoundException("The system cannot find the file specified."),
        "directory" => new DirectoryNotFoundException("The system cannot find the path specified."),
        "com file" => new COMException("The system cannot find the file specified.", FileNotFound),
        _ => new COMException("The system cannot find the path specified.", PathNotFound),
    };

    [Theory]
    [MemberData(nameof(Absences))]
    public void ATaskOrFolderThatIsntThere_IsNoTask(string kind)
    {
        Assert.Null(WindowsTaskScheduler.ActionOf(TaskThrows(Absence(kind)), Path));
        Assert.Null(WindowsTaskScheduler.ActionOf(FolderThrows(Absence(kind)), Path));
    }

    public static TheoryData<int> Failures() => [SchedulerBusy, SchedulerUnavailable, AccessDenied];

    [Theory]
    [MemberData(nameof(Failures))]
    public void ATaskSchedulerFailure_IsntNoTask_ItPropagates(int hresult)
    {
        // Codex W5.2 r3: "Scheduler busy" taken for no task bypassed taskUnreadable; the child then served unelevated
        // while a later read said on, with no fallback.
        var fromTask = Assert.Throws<COMException>(() => WindowsTaskScheduler.ActionOf(TaskThrows(new COMException("failed", hresult)), Path));
        Assert.Equal(hresult, fromTask.HResult);
        var fromFolder = Assert.Throws<COMException>(() => WindowsTaskScheduler.ActionOf(FolderThrows(new COMException("failed", hresult)), Path));
        Assert.Equal(hresult, fromFolder.HResult);
    }
}
