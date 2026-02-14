using System.ComponentModel;
using System.Text;
using HelixCli;
using ModelContextProtocol.Server;

namespace HelixMcp;

[McpServerToolType]
public static class HelixTools
{
    [McpServerTool, Description("List recently failed AzDO builds, optionally filtered by definition name")]
    public static async Task<string> ListFailedBuilds(
        [Description("Optional build definition name filter")] string? definition = null)
    {
        var credential = HelixService.CreateCredential();
        var service = new HelixService(credential);
        var builds = await service.GetFailedBuilds(definition);

        if (builds.Count == 0)
            return "No failed builds found.";

        var sb = new StringBuilder();
        sb.AppendLine($"{"Build ID",-12} {"Definition",-40} {"Result",-12} {"Finish Time",-25}");
        sb.AppendLine(new string('-', 100));
        foreach (var build in builds)
        {
            var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={build.Id}";
            sb.AppendLine($"{build.Id,-12} {build.Definition.Name,-40} {build.Result,-12} {build.FinishTime,-25}");
            sb.AppendLine($"  URL: {url}");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Get helix jobs for a pipeline identified by PR number or AzDO build id")]
    public static async Task<string> GetHelixJobs(
        [Description("The PR number (provide either pr or build)")] int pr = 0,
        [Description("The AzDO build id (provide either pr or build)")] int build = 0)
    {
        if (pr == 0 && build == 0)
            return "Error: Either pr or build must be specified.";

        var credential = HelixService.CreateCredential();
        var service = new HelixService(credential);
        var query = pr != 0
            ? service.GetHelixWorkItemQueryForPullRequest(pr)
            : service.GetHelixWorkItemQueryForBuild(build);

        var results = await service.GetAllHelixWorkItemResults(query);
        var sb = new StringBuilder();

        foreach (var (azdoBuildId, items) in service.GetByBuild(results))
        {
            var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={azdoBuildId}";
            sb.AppendLine($"Build {azdoBuildId}: {url}");
            sb.AppendLine($"  Phases: {items.Select(x => x.AzdoPhaseName).Distinct().Count()}");
            sb.AppendLine($"  Work Items: {items.Count}");
            sb.AppendLine();
        }

        return sb.Length > 0 ? sb.ToString() : "No helix jobs found.";
    }

    [McpServerTool, Description("Get helix work items per pipeline build with execution details")]
    public static async Task<string> GetHelixWorkItems(
        [Description("The PR number (provide either pr or build)")] int pr = 0,
        [Description("The AzDO build id (provide either pr or build)")] int build = 0,
        [Description("Optional phase name filter")] string? phase = null)
    {
        if (pr == 0 && build == 0)
            return "Error: Either pr or build must be specified.";

        var credential = HelixService.CreateCredential();
        var service = new HelixService(credential);
        var query = pr != 0
            ? service.GetHelixWorkItemQueryForPullRequest(pr)
            : service.GetHelixWorkItemQueryForBuild(build);

        var results = await service.GetAllHelixWorkItemResults(query);
        var sb = new StringBuilder();

        foreach (var (azdoBuildId, items) in service.GetByBuild(results))
        {
            var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={azdoBuildId}";
            sb.AppendLine(url);
            sb.AppendLine();

            var filteredItems = phase is not null
                ? items.Where(x => x.AzdoPhaseName.Contains(phase, StringComparison.OrdinalIgnoreCase)).ToList()
                : items;

            sb.AppendLine($"|{"Phase",-44}| {"Helix Que",-10}| {"Helix QueAvg",-13}| {"Helix Exec",-11}| {"Items",-6}| {"Machines",-9}|");
            sb.AppendLine($"|{new string('-', 44)}|{new string('-', 11)}|{new string('-', 14)}|{new string('-', 12)}|{new string('-', 7)}|{new string('-', 10)}|");

            foreach (var g in filteredItems.GroupBy(x => x.AzdoPhaseName).OrderBy(x => x.Key))
            {
                var attemptId = g.Max(x => x.AzdoAttempt);
                var phaseItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
                sb.Append($"| {g.Key,-43}|");
                sb.Append($" {phaseItems.Sum(x => x.QueuedTime):hh\\:mm\\:ss}  |");
                sb.Append($" {phaseItems.Average(x => x.QueuedTime):hh\\:mm\\:ss}     |");
                sb.Append($" {phaseItems.Sum(x => x.ExecutionTime):hh\\:mm\\:ss}  |");
                sb.Append($" {phaseItems.Count,-6}|");
                sb.Append($" {phaseItems.Select(x => x.MachineName).Distinct().Count(),-9}|");
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        return sb.Length > 0 ? sb.ToString() : "No work items found.";
    }

    [McpServerTool, Description("Show helix queue wait times for a build identified by PR number or AzDO build id")]
    public static async Task<string> GetWaitTimes(
        [Description("The PR number (provide either pr or build)")] int pr = 0,
        [Description("The AzDO build id (provide either pr or build)")] int build = 0)
    {
        if (pr == 0 && build == 0)
            return "Error: Either pr or build must be specified.";

        var credential = HelixService.CreateCredential();
        var service = new HelixService(credential);
        var query = pr != 0
            ? service.GetHelixWorkItemQueryForPullRequest(pr)
            : service.GetHelixWorkItemQueryForBuild(build);

        var results = await service.GetAllHelixWorkItemResults(query);
        var sb = new StringBuilder();

        foreach (var (azdoBuildId, items) in service.GetByBuild(results))
        {
            var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={azdoBuildId}";
            sb.AppendLine($"Build {azdoBuildId}: {url}");
            sb.AppendLine();

            sb.AppendLine($"| {"Phase",-44}| {"Total Wait",-11}| {"Avg Wait",-9}| {"Max Wait",-9}| {"Min Wait",-9}| {"Items",-6}|");
            sb.AppendLine($"|{new string('-', 44)}|{new string('-', 12)}|{new string('-', 10)}|{new string('-', 10)}|{new string('-', 10)}|{new string('-', 7)}|");

            foreach (var g in items.GroupBy(x => x.AzdoPhaseName).OrderBy(x => x.Key))
            {
                var attemptId = g.Max(x => x.AzdoAttempt);
                var phaseItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
                sb.Append($"| {g.Key,-43}|");
                sb.Append($" {phaseItems.Sum(x => x.QueuedTime):hh\\:mm\\:ss}   |");
                sb.Append($" {phaseItems.Average(x => x.QueuedTime):hh\\:mm\\:ss} |");
                sb.Append($" {phaseItems.Max(x => x.QueuedTime):hh\\:mm\\:ss} |");
                sb.Append($" {phaseItems.Min(x => x.QueuedTime):hh\\:mm\\:ss} |");
                sb.Append($" {phaseItems.Count,-6}|");
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine($"Overall: Total Wait={items.Sum(x => x.QueuedTime):hh\\:mm\\:ss}, Avg={items.Average(x => x.QueuedTime):hh\\:mm\\:ss}");
            sb.AppendLine();
        }

        return sb.Length > 0 ? sb.ToString() : "No wait time data found.";
    }
}
