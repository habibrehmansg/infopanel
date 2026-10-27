using System;
using System.Threading;

namespace InfoPanel.Documents;

internal static class Locks
{
    /// <summary>
    /// Takes <paramref name="gate"/>. A thread that finds it held by another tells <paramref name="waiting"/> (a test's
    /// hook, so a test knows a request is waiting for the lock) before it waits.
    /// </summary>
    public static Lock.Scope Enter(Lock gate, Action? waiting)
    {
        if (waiting is null)
            return gate.EnterScope();
        if (!gate.TryEnter())
        {
            waiting();
            return gate.EnterScope();
        }

        // Held once by TryEnter: the scope holds it again, and the hold left is the scope's.
        var scope = gate.EnterScope();
        gate.Exit();
        return scope;
    }
}
