using System.CommandLine;
using HelixCli;

var rootCommand = new RootCommand("Helix Tool - Navigate AzDO and Helix pipelines");

// failed-builds
var failedBuildsCommand = new Command("failed-builds", "List recently failed builds");
var definitionOption = new Option<string?>("--definition", "Filter by build definition name");
failedBuildsCommand.AddOption(definitionOption);
failedBuildsCommand.SetHandler(async (string? definition) =>
{
    var credential = HelixService.CreateCredential();
    var service = new HelixService(credential);
    var builds = await service.GetFailedBuilds(definition);

    Console.WriteLine($"{"Build ID",-12} {"Definition",-40} {"Status",-12} {"Finish Time",-25} {"URL"}");
    Console.WriteLine(new string('-', 120));
    foreach (var build in builds)
    {
        var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={build.Id}";
        Console.WriteLine($"{build.Id,-12} {build.Definition.Name,-40} {build.Result,-12} {build.FinishTime,-25} {url}");
    }
}, definitionOption);
rootCommand.AddCommand(failedBuildsCommand);

// helix-jobs
var helixJobsCommand = new Command("helix-jobs", "Get helix jobs for a pipeline build");
var prOption = new Option<int>("--pr", "The PR number");
var buildOption = new Option<int>("--build", "The AzDO build id");
helixJobsCommand.AddOption(prOption);
helixJobsCommand.AddOption(buildOption);
helixJobsCommand.SetHandler(async (int pr, int build) =>
{
    if (pr == 0 && build == 0)
    {
        Console.Error.WriteLine("Either --pr or --build must be specified");
        return;
    }

    var credential = HelixService.CreateCredential();
    var service = new HelixService(credential);
    var query = pr != 0
        ? service.GetHelixWorkItemQueryForPullRequest(pr)
        : service.GetHelixWorkItemQueryForBuild(build);

    var results = await service.GetAllHelixWorkItemResults(query);
    foreach (var (azdoBuildId, items) in service.GetByBuild(results))
    {
        var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={azdoBuildId}";
        Console.WriteLine($"Build {azdoBuildId}: {url}");
        Console.WriteLine($"  Phases: {items.Select(x => x.AzdoPhaseName).Distinct().Count()}");
        Console.WriteLine($"  Work Items: {items.Count}");
        Console.WriteLine();
    }
}, prOption, buildOption);
rootCommand.AddCommand(helixJobsCommand);

// helix-workitems
var helixWorkItemsCommand = new Command("helix-workitems", "Get helix work items per pipeline build");
var wiPrOption = new Option<int>("--pr", "The PR number");
var wiBuildOption = new Option<int>("--build", "The AzDO build id");
var phaseOption = new Option<string?>("--phase", "Filter to a phase name");
var detailedOption = new Option<bool>("--detailed", "Show detailed per-item breakdown");
helixWorkItemsCommand.AddOption(wiPrOption);
helixWorkItemsCommand.AddOption(wiBuildOption);
helixWorkItemsCommand.AddOption(phaseOption);
helixWorkItemsCommand.AddOption(detailedOption);
helixWorkItemsCommand.SetHandler(async (int pr, int build, string? phase, bool detailed) =>
{
    if (pr == 0 && build == 0)
    {
        Console.Error.WriteLine("Either --pr or --build must be specified");
        return;
    }

    var credential = HelixService.CreateCredential();
    var service = new HelixService(credential);
    var query = pr != 0
        ? service.GetHelixWorkItemQueryForPullRequest(pr)
        : service.GetHelixWorkItemQueryForBuild(build);

    var results = await service.GetAllHelixWorkItemResults(query);
    foreach (var (azdoBuildId, items) in service.GetByBuild(results))
    {
        var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={azdoBuildId}";
        Console.WriteLine(url);
        Console.WriteLine();

        var filteredItems = phase is not null
            ? items.Where(x => x.AzdoPhaseName.Contains(phase, StringComparison.OrdinalIgnoreCase)).ToList()
            : items;

        Console.WriteLine($"|{"Phase",-44}| {"Helix Que",-10}| {"Helix QueAvg",-13}| {"Helix Exec",-11}| {"Items",-6}| {"Machines",-9}|");
        Console.WriteLine($"|{new string('-', 44)}|{new string('-', 11)}|{new string('-', 14)}|{new string('-', 12)}|{new string('-', 7)}|{new string('-', 10)}|");

        foreach (var g in filteredItems.GroupBy(x => x.AzdoPhaseName).OrderBy(x => x.Key))
        {
            var attemptId = g.Max(x => x.AzdoAttempt);
            var phaseItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
            Console.Write($"| {g.Key,-43}|");
            Console.Write($" {phaseItems.Sum(x => x.QueuedTime):hh\\:mm\\:ss}  |");
            Console.Write($" {phaseItems.Average(x => x.QueuedTime):hh\\:mm\\:ss}     |");
            Console.Write($" {phaseItems.Sum(x => x.ExecutionTime):hh\\:mm\\:ss}  |");
            Console.Write($" {phaseItems.Count,-6}|");
            Console.Write($" {phaseItems.Select(x => x.MachineName).Distinct().Count(),-9}|");
            Console.WriteLine();
        }
        Console.WriteLine();

        if (detailed)
        {
            foreach (var g in filteredItems.GroupBy(x => x.AzdoPhaseName).OrderBy(x => x.Key))
            {
                Console.WriteLine(g.Key);
                Console.WriteLine();
                Console.WriteLine($"| {"Work Item",-16}| {"Queued",-9}| {"Execution",-10}| {"Machine",-11}|");
                Console.WriteLine($"|{new string('-', 17)}|{new string('-', 10)}|{new string('-', 11)}|{new string('-', 12)}|");
                foreach (var item in g.OrderBy(x => x.MachineName))
                {
                    Console.WriteLine($"| {item.FriendlyName,-16}| {item.QueuedTime:hh\\:mm\\:ss} | {item.ExecutionTime:hh\\:mm\\:ss}  | {item.MachineName,-11}|");
                }
                Console.WriteLine();
            }
        }
    }
}, wiPrOption, wiBuildOption, phaseOption, detailedOption);
rootCommand.AddCommand(helixWorkItemsCommand);

// wait-times
var waitTimesCommand = new Command("wait-times", "Show helix queue wait times for a build");
var wtPrOption = new Option<int>("--pr", "The PR number");
var wtBuildOption = new Option<int>("--build", "The AzDO build id");
waitTimesCommand.AddOption(wtPrOption);
waitTimesCommand.AddOption(wtBuildOption);
waitTimesCommand.SetHandler(async (int pr, int build) =>
{
    if (pr == 0 && build == 0)
    {
        Console.Error.WriteLine("Either --pr or --build must be specified");
        return;
    }

    var credential = HelixService.CreateCredential();
    var service = new HelixService(credential);
    var query = pr != 0
        ? service.GetHelixWorkItemQueryForPullRequest(pr)
        : service.GetHelixWorkItemQueryForBuild(build);

    var results = await service.GetAllHelixWorkItemResults(query);
    foreach (var (azdoBuildId, items) in service.GetByBuild(results))
    {
        var url = $"{HelixService.AzdoOrganizationUrl}/{HelixService.AzdoProjectName}/_build/results?buildId={azdoBuildId}";
        Console.WriteLine($"Build {azdoBuildId}: {url}");
        Console.WriteLine();

        Console.WriteLine($"| {"Phase",-44}| {"Total Wait",-11}| {"Avg Wait",-9}| {"Max Wait",-9}| {"Min Wait",-9}| {"Items",-6}|");
        Console.WriteLine($"|{new string('-', 44)}|{new string('-', 12)}|{new string('-', 10)}|{new string('-', 10)}|{new string('-', 10)}|{new string('-', 7)}|");

        foreach (var g in items.GroupBy(x => x.AzdoPhaseName).OrderBy(x => x.Key))
        {
            var attemptId = g.Max(x => x.AzdoAttempt);
            var phaseItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
            Console.Write($"| {g.Key,-43}|");
            Console.Write($" {phaseItems.Sum(x => x.QueuedTime):hh\\:mm\\:ss}   |");
            Console.Write($" {phaseItems.Average(x => x.QueuedTime):hh\\:mm\\:ss} |");
            Console.Write($" {phaseItems.Max(x => x.QueuedTime):hh\\:mm\\:ss} |");
            Console.Write($" {phaseItems.Min(x => x.QueuedTime):hh\\:mm\\:ss} |");
            Console.Write($" {phaseItems.Count,-6}|");
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine($"Overall: Total Wait={items.Sum(x => x.QueuedTime):hh\\:mm\\:ss}, Avg={items.Average(x => x.QueuedTime):hh\\:mm\\:ss}");
        Console.WriteLine();
    }
}, wtPrOption, wtBuildOption);
rootCommand.AddCommand(waitTimesCommand);

return await rootCommand.InvokeAsync(args);
