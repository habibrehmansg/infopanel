using System.Runtime.CompilerServices;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// The run folder every test engine's local channel lives in (<c>Engine:RunDir</c>, given as the <c>Engine__RunDir</c>
/// environment variable so in-process engines and the engines tests start as child processes both read it), so no test
/// touches the developer's own <c>{LocalApplicationData}/InfoPanel/run</c>. Short, so a socket path fits in 103 bytes.
/// Each data folder has its own channel in it.
/// </summary>
internal static class TestRunFolder
{
    public const string Variable = "Engine__RunDir";

    public static string Path { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ipr-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(Variable, Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }
}
