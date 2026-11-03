using System.IO.Pipes;
using InfoPanel.SensorHelper;
using Xunit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// How the task's helper reads its client (<see cref="HelperPipeClient.Read"/>), on a fake of Windows' calls that models
/// what the PC showed (W5.2 PC check 1): the engine connects at identification level, at which
/// <c>ImpersonateNamedPipeClient</c> and <c>OpenThreadToken(TOKEN_QUERY, OpenAsSelf)</c> succeed but nothing else done
/// while impersonating does (it runs as the client, who allowed identification only: .NET's <c>RunAsClient</c>, which the
/// helper used before, and even a library or type loaded then fail with <c>ERROR_BAD_IMPERSONATION_LEVEL</c>, 0x80070542).
/// The real calls, read by SYSTEM with a client in the user's desktop session, run in
/// <c>HelperPipeSessionWindowsTests</c> (opt-in).
/// </summary>
public sealed class HelperPipeClientTests
{
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";

    private const int BadImpersonationLevel = unchecked((int)0x80070542);

    /// <summary>Windows' calls for one client connected at identification level.</summary>
    private sealed class FakeToken : IPipeClientToken
    {
        public bool Refuse { get; init; }

        public bool NoToken { get; init; }

        /// <summary>The open throws, as anything not ready before the thread took the client's token does (W5.2 fixer 4 on the PC).</summary>
        public bool OpenThrows { get; init; }

        public Func<HelperClient?>? Reading { get; init; }

        /// <summary>Whether this thread holds the client's token now.</summary>
        public bool Impersonating { get; private set; }

        public List<string> Calls { get; } = [];

        public List<IntPtr> Closed { get; } = [];

        public bool Impersonate(NamedPipeServerStream pipe)
        {
            Calls.Add("impersonate");
            if (Refuse)
                return false;
            Impersonating = true;
            return true;
        }

        public IntPtr OpenThreadToken()
        {
            Calls.Add("open");
            if (OpenThrows)
                throw new IOException("Unknown error (0x542)", BadImpersonationLevel);
            return Impersonating && !NoToken ? 42 : IntPtr.Zero; // opened as self: identification is enough
        }

        public void RevertToSelf()
        {
            Calls.Add("revert");
            Impersonating = false;
        }

        public HelperClient? Read(IntPtr token)
        {
            Calls.Add("read");
            try
            {
                if (Impersonating)
                    throw new IOException("Unknown error (0x542)", BadImpersonationLevel); // reading as the client
                return Reading is { } reading ? reading() : new HelperClient(Sid, HelperTask.MediumIntegrity);
            }
            finally
            {
                Closed.Add(token);
            }
        }
    }

    private static NamedPipeServerStream Pipe() =>
        new(HelperTestKit.PipeName(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    [Fact]
    public void AnIdentificationLevelClient_IsRead_FromTheThreadsToken_OnceTheThreadIsItselfAgain()
    {
        using var pipe = Pipe();
        var token = new FakeToken();

        var client = HelperPipeClient.Read(pipe, token);

        Assert.Equal(new HelperClient(Sid, HelperTask.MediumIntegrity), client);
        Assert.False(token.Impersonating);
        Assert.Equal(["impersonate", "open", "revert", "read"], token.Calls);
        Assert.Equal([(IntPtr)42], token.Closed);
    }

    public static TheoryData<string> Failures => ["refused", "noToken", "openThrows", "unreadable", "io", "other"];

    [Theory]
    [MemberData(nameof(Failures))]
    public void TheThread_RevertsToSelf_OnEveryPath(string failure)
    {
        using var pipe = Pipe();
        var token = new FakeToken
        {
            Refuse = failure == "refused",
            NoToken = failure == "noToken",
            OpenThrows = failure == "openThrows",
            Reading = failure switch
            {
                "unreadable" => () => null,
                "io" => () => throw new IOException("the token couldn't be read"),
                "other" => () => throw new OutOfMemoryException(),
                _ => null,
            },
        };

        HelperClient? client = null;
        var thrown = Record.Exception(() => client = HelperPipeClient.Read(pipe, token));

        Assert.Null(client);
        Assert.Equal(failure == "other" ? typeof(OutOfMemoryException) : null, thrown?.GetType());
        Assert.False(token.Impersonating);
        Assert.Equal(1, token.Calls.Count(c => c == "revert"));
        Assert.DoesNotContain("read", token.Calls.TakeWhile(c => c != "revert")); // nothing read as the client
        Assert.Equal(failure is "refused" or "noToken" or "openThrows" ? 0 : 1, token.Closed.Count); // a token opened is closed
    }
}
