using System.Collections.Concurrent;
using System.Reflection;

using StackExchange.Redis;

namespace ServiceTests;

/// <summary>
/// Every Redis command a repository sends, with its key and where it went: <c>db</c> (straight to the
/// database), <c>batch:n</c> (pipelined, not atomic) or <c>transaction:n</c> (inside the n-th MULTI/EXEC).
/// </summary>
public sealed class CommandRecorder
{
    private static readonly string[] WritePrefixes =
    [
        "StringSet", "StringIncrement", "StringDecrement", "StringAppend", "StringGetSet", "StringGetDelete",
        "KeyDelete", "KeyRename", "KeyExpire", "KeyPersist", "KeyMove", "KeyCopy",
        "SetAdd", "SetRemove", "SetMove", "SetPop",
        "SortedSetAdd", "SortedSetRemove", "SortedSetIncrement", "SortedSetDecrement", "SortedSetPop", "SortedSetCombineAndStore",
        "HashSet", "HashDelete", "HashIncrement", "HashDecrement",
        "ListLeftPush", "ListRightPush", "ListSet", "ListRemove", "ListTrim", "ListLeftPop", "ListRightPop", "ListMove",
        "ScriptEvaluate", "Execute",
    ];

    private int scopes;

    public ConcurrentQueue<(string Scope, string Command, string? Key)> Commands { get; } = new();

    /// <summary>
    /// Called inside ITransaction.ExecuteAsync of a transaction that queued a write, before the real EXEC (reads
    /// are transactions too, and are left alone); a throw stands for a failure before EXEC.
    /// </summary>
    public Func<Task>? BeforeExecute { get; set; }

    public static bool IsWrite(string command) =>
        Array.Exists(WritePrefixes, p => command.StartsWith(p, StringComparison.Ordinal));

    public IEnumerable<(string Scope, string Command, string? Key)> Writes => Commands.Where(c => IsWrite(c.Command));

    internal string NextScope(string kind) => $"{kind}:{Interlocked.Increment(ref scopes)}";

    public IDatabase Wrap(IDatabase database) => RecordingProxy<IDatabase>.Create(database, this, "db");
}

/// <summary>A <see cref="DispatchProxy"/> over IDatabase, IBatch and ITransaction that records each call.</summary>
public class RecordingProxy<T> : DispatchProxy where T : class
{
    private T inner = null!;
    private CommandRecorder recorder = null!;
    private string scope = "";

    public static T Create(T inner, CommandRecorder recorder, string scope)
    {
        var proxy = Create<T, RecordingProxy<T>>();
        var self = (RecordingProxy<T>)(object)proxy;
        self.inner = inner;
        self.recorder = recorder;
        self.scope = scope;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name is not ("CreateTransaction" or "CreateBatch" or "ExecuteAsync" or "Execute" or "AddCondition"))
            recorder.Commands.Enqueue((scope, targetMethod.Name, args is { Length: > 0 } && args[0] is RedisKey key ? (string?)key : null));

        if (scope.StartsWith("transaction", StringComparison.Ordinal) && targetMethod.Name == "ExecuteAsync")
            return ExecuteAsync(targetMethod, args);

        var result = Call(targetMethod, args);
        return targetMethod.Name switch
        {
            "CreateTransaction" => RecordingProxy<ITransaction>.Create((ITransaction)result!, recorder, recorder.NextScope("transaction")),
            "CreateBatch" => RecordingProxy<IBatch>.Create((IBatch)result!, recorder, recorder.NextScope("batch")),
            _ => result,
        };
    }

    private async Task<bool> ExecuteAsync(MethodInfo method, object?[]? args)
    {
        if (recorder.BeforeExecute is { } before && recorder.Writes.Any(w => w.Scope == scope))
            await before();
        return await (Task<bool>)Call(method, args)!;
    }

    private object? Call(MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
