namespace HelixCli;

public sealed class WorkItemResult(
    string friendlyName,
    TimeSpan executionTime,
    TimeSpan queuedTime,
    int azdoAttempt,
    string azdoPhaseName,
    int azdoBuildId,
    string machineName)
{
    public string FriendlyName { get; } = friendlyName;
    public TimeSpan ExecutionTime { get; } = executionTime;
    public TimeSpan QueuedTime { get; } = queuedTime;
    public int AzdoAttempt { get; } = azdoAttempt;
    public string AzdoPhaseName { get; } = azdoPhaseName;
    public int AzdoBuildId { get; } = azdoBuildId;
    public string MachineName { get; } = machineName;

    public override string ToString() => $"{FriendlyName} ({AzdoBuildId})";
}

public sealed class WorkItemData(string name, TimeSpan expectedExecutionTime)
{
    public string Name { get; } = name;
    public TimeSpan ExpectedExecutionTime { get; } = expectedExecutionTime;

    public override string ToString() => $"{Name} ({ExpectedExecutionTime})";
}

public static class TimeSpanExtensions
{
    public static TimeSpan Sum(this IEnumerable<TimeSpan> source)
    {
        var d = source.Sum(x => x.TotalSeconds);
        return TimeSpan.FromSeconds(d);
    }

    public static TimeSpan Sum<T>(this IEnumerable<T> source, Func<T, TimeSpan> func) =>
        source.Select(func).Sum();

    public static TimeSpan Average(this IEnumerable<TimeSpan> source)
    {
        var d = source.Average(x => x.TotalSeconds);
        return TimeSpan.FromSeconds(d);
    }

    public static TimeSpan Average<T>(this IEnumerable<T> source, Func<T, TimeSpan> func) =>
        source.Select(func).Average();
}
