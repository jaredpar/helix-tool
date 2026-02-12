using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure.Core;
using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Net.Client;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;
using Spectre.Console;

const string AzdoOrganizationUrl = "https://dev.azure.com/dnceng-public";
const string AzdoProjectName = "public";
const string EscHint = "[dim](Esc to go back)[/]";
const string EscHintQuit = "[dim](Esc to quit)[/]";

// Authenticate and initialize clients
AzureCliCredential credential;
BuildHttpClient buildClient;
HttpClient httpClient;

try
{
    credential = new AzureCliCredential(
        new AzureCliCredentialOptions { TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47" });

    var azdoConnection = await AnsiConsole.Status().StartAsync("Authenticating...", async ctx =>
    {
        return await GetAzdoConnection(credential);
    });

    buildClient = azdoConnection.GetClient<BuildHttpClient>();
    httpClient = new HttpClient();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine("[red bold]Authentication failed.[/]");
    AnsiConsole.MarkupLine("[yellow]Are you logged in? Run: az login[/]");
    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.Message)}[/]");
    return;
}

// TUI state
var currentScreen = Screen.BuildList;
List<BuildSummary>? builds = null;
BuildSummary? selectedBuild = null;
List<WorkItemResult>? buildWorkItems = null;
List<PhaseSummary>? phaseSummaries = null;
PhaseSummary? selectedPhase = null;
List<WorkItemResult>? phaseWorkItems = null;
WorkItemResult? selectedWorkItem = null;

while (currentScreen != Screen.Exit)
{
    switch (currentScreen)
    {
        case Screen.BuildList:
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold blue]Helix Build Explorer - dotnet/roslyn[/]").LeftJustified());
            AnsiConsole.WriteLine();

            if (builds is null)
            {
                try
                {
                    builds = await AnsiConsole.Status().StartAsync("Fetching recent builds...", async ctx =>
                    {
                        return await GetRecentBuilds(buildClient);
                    });
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]Failed to fetch builds.[/]");
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.Message)}[/]");
                    AnsiConsole.MarkupLine("Press any key to retry...");
                    Console.ReadKey(true);
                    continue;
                }
            }

            if (builds.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No recent builds found.[/]");
                AnsiConsole.MarkupLine("Press any key to exit...");
                Console.ReadKey(true);
                currentScreen = Screen.Exit;
                break;
            }

            var buildChoices = builds.Select(b => b.ToDisplayString()).ToList();
            var selectedIndex = RunSelection($"{EscHintQuit} Select a build:", buildChoices);

            if (selectedIndex < 0)
            {
                currentScreen = Screen.Exit;
            }
            else
            {
                selectedBuild = builds[selectedIndex];
                buildWorkItems = null;
                phaseSummaries = null;
                currentScreen = Screen.PhaseList;
            }
            break;
        }

        case Screen.PhaseList:
        {
            Debug.Assert(selectedBuild is not null);
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold blue]Build {selectedBuild.BuildId} - {Markup.Escape(selectedBuild.BranchDisplay)}[/]").LeftJustified());
            AnsiConsole.WriteLine();

            // Fetch work items if not already loaded
            if (buildWorkItems is null)
            {
                try
                {
                    var query = GetHelixWorkItemQueryForBuild(selectedBuild.BuildId);
                    buildWorkItems = await AnsiConsole.Status().StartAsync("Querying Helix data...", async ctx =>
                    {
                        return await GetAllHelixWorkItemResults(credential, query);
                    });
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]Failed to fetch Helix data.[/]");
                    AnsiConsole.MarkupLine("[yellow]Are you connected to the VPN?[/]");
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.Message)}[/]");
                    AnsiConsole.MarkupLine("Press any key to go back...");
                    Console.ReadKey(true);
                    currentScreen = Screen.BuildList;
                    break;
                }
            }

            if (buildWorkItems.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No Helix work items found for this build.[/]");
                AnsiConsole.MarkupLine("Press any key to go back...");
                Console.ReadKey(true);
                currentScreen = Screen.BuildList;
                break;
            }

            // Build phase summaries if not cached
            if (phaseSummaries is null)
            {
                try
                {
                    phaseSummaries = await AnsiConsole.Status().StartAsync("Loading phase details...", async ctx =>
                    {
                        return await BuildPhaseSummaries(buildClient, httpClient, selectedBuild.BuildId, buildWorkItems);
                    });
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]Failed to load phase details.[/]");
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.Message)}[/]");
                    AnsiConsole.MarkupLine("Press any key to go back...");
                    Console.ReadKey(true);
                    currentScreen = Screen.BuildList;
                    break;
                }
            }

            // Display summary table
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("Phase").Width(44))
                .AddColumn(new TableColumn("AzDo").RightAligned())
                .AddColumn(new TableColumn("AzDo Est").RightAligned())
                .AddColumn(new TableColumn("Helix Que").RightAligned())
                .AddColumn(new TableColumn("Que Avg").RightAligned())
                .AddColumn(new TableColumn("Helix Exec").RightAligned())
                .AddColumn(new TableColumn("Items").RightAligned())
                .AddColumn(new TableColumn("Machines").RightAligned());

            foreach (var ps in phaseSummaries)
            {
                table.AddRow(
                    Markup.Escape(ps.PhaseName),
                    FormatTimeSpan(ps.AzdoExecutionTime),
                    FormatTimeSpan(ps.EstimatedTime),
                    ps.TotalQueuedTime.ToString(@"hh\:mm\:ss"),
                    ps.AverageQueuedTime.ToString(@"hh\:mm\:ss"),
                    ps.TotalExecutionTime.ToString(@"hh\:mm\:ss"),
                    ps.WorkItemCount.ToString(),
                    ps.MachineCount.ToString());
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Phase selection prompt
            var phaseChoices = phaseSummaries.Select(p => p.PhaseName).ToList();
            var selectedIndex = RunSelection($"{EscHint} Select a phase:", phaseChoices);

            if (selectedIndex < 0)
            {
                currentScreen = Screen.BuildList;
            }
            else
            {
                selectedPhase = phaseSummaries[selectedIndex];
                phaseWorkItems = buildWorkItems
                    .Where(x => x.AzdoPhaseName == selectedPhase.PhaseName && x.AzdoAttempt == selectedPhase.AttemptId)
                    .OrderBy(x => x.MachineName)
                    .ThenBy(x => x.FriendlyName)
                    .ToList();
                currentScreen = Screen.WorkItemList;
            }
            break;
        }

        case Screen.WorkItemList:
        {
            Debug.Assert(selectedBuild is not null);
            Debug.Assert(selectedPhase is not null);
            Debug.Assert(phaseWorkItems is not null);

            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold blue]{Markup.Escape(selectedPhase.PhaseName)}[/] [dim](Build {selectedBuild.BuildId}, Attempt {selectedPhase.AttemptId})[/]").LeftJustified());
            AnsiConsole.WriteLine();

            // Display work items table
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("Work Item").Width(20))
                .AddColumn(new TableColumn("Queued").RightAligned())
                .AddColumn(new TableColumn("Execution").RightAligned())
                .AddColumn(new TableColumn("Machine"));

            foreach (var wi in phaseWorkItems)
            {
                table.AddRow(
                    Markup.Escape(wi.FriendlyName),
                    wi.QueuedTime.ToString(@"hh\:mm\:ss"),
                    wi.ExecutionTime.ToString(@"hh\:mm\:ss"),
                    Markup.Escape(wi.MachineName));
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Work item selection prompt
            var wiChoices = phaseWorkItems.Select(wi =>
                $"{wi.FriendlyName} ({wi.ExecutionTime:hh\\:mm\\:ss} on {wi.MachineName})").ToList();

            var selectedIndex = RunSelection($"{EscHint} Select a work item:", wiChoices);

            if (selectedIndex < 0)
            {
                currentScreen = Screen.PhaseList;
            }
            else
            {
                selectedWorkItem = phaseWorkItems[selectedIndex];
                currentScreen = Screen.WorkItemDetail;
            }
            break;
        }

        case Screen.WorkItemDetail:
        {
            Debug.Assert(selectedBuild is not null);
            Debug.Assert(selectedPhase is not null);
            Debug.Assert(selectedWorkItem is not null);

            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold blue]{Markup.Escape(selectedWorkItem.FriendlyName)}[/]").LeftJustified());
            AnsiConsole.WriteLine();

            var buildUrl = $"{AzdoOrganizationUrl}/{AzdoProjectName}/_build/results?buildId={selectedBuild.BuildId}";

            var table = new Table()
                .Border(TableBorder.Rounded)
                .HideHeaders()
                .AddColumn(new TableColumn("Field").Width(20))
                .AddColumn(new TableColumn("Value"));

            table.AddRow("[bold]Friendly Name[/]", Markup.Escape(selectedWorkItem.FriendlyName));
            table.AddRow("[bold]Build ID[/]", selectedBuild.BuildId.ToString());
            table.AddRow("[bold]Phase[/]", Markup.Escape(selectedWorkItem.AzdoPhaseName));
            table.AddRow("[bold]Attempt[/]", selectedWorkItem.AzdoAttempt.ToString());
            table.AddRow("[bold]Machine[/]", Markup.Escape(selectedWorkItem.MachineName));
            table.AddRow("[bold]Queued Time[/]", selectedWorkItem.QueuedTime.ToString(@"hh\:mm\:ss"));
            table.AddRow("[bold]Execution Time[/]", selectedWorkItem.ExecutionTime.ToString(@"hh\:mm\:ss"));
            table.AddRow("[bold]AzDO Build[/]", Markup.Escape(buildUrl));

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Press Esc or any key to go back...[/]");
            Console.ReadKey(true);
            currentScreen = Screen.WorkItemList;
            break;
        }
    }
}

// --- Data fetching functions ---

async Task<List<BuildSummary>> GetRecentBuilds(BuildHttpClient client, int count = 20)
{
    var builds = await client.GetBuildsAsync(
        project: AzdoProjectName,
        repositoryId: "dotnet/roslyn",
        repositoryType: "GitHub",
        queryOrder: BuildQueryOrder.StartTimeDescending,
        top: count);

    return builds.Select(b => new BuildSummary(
        BuildId: b.Id,
        BuildNumber: b.BuildNumber ?? "",
        SourceBranch: b.SourceBranch ?? "",
        RequestedBy: b.RequestedBy?.DisplayName ?? "Unknown",
        Result: b.Result,
        Status: b.Status,
        StartTime: b.StartTime,
        FinishTime: b.FinishTime
    )).ToList();
}

async Task<List<PhaseSummary>> BuildPhaseSummaries(
    BuildHttpClient client,
    HttpClient http,
    int buildId,
    List<WorkItemResult> workItems)
{
    var timeline = await client.GetBuildTimelineAsync(AzdoProjectName, buildId);
    var buildArtifacts = await client.GetArtifactsAsync(AzdoProjectName, buildId);
    var summaries = new List<PhaseSummary>();

    foreach (var g in workItems.GroupBy(x => x.AzdoPhaseName).OrderBy(x => x.Key))
    {
        var attemptId = g.Max(x => x.AzdoAttempt);
        var phaseItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
        var azdoExecTime = GetAzdoPhaseExecutionTime(timeline, g.Key);

        TimeSpan? estimatedTime = null;
        try
        {
            var azdoData = await GetAzdoWorkItemData(http, buildId, g.Key, attemptId, buildArtifacts);
            estimatedTime = azdoData.Sum(x => x.ExpectedExecutionTime);
        }
        catch
        {
            // Artifact may not be available
        }

        summaries.Add(new PhaseSummary(
            PhaseName: g.Key,
            AttemptId: attemptId,
            WorkItemCount: phaseItems.Count,
            MachineCount: phaseItems.Select(x => x.MachineName).Distinct().Count(),
            TotalExecutionTime: phaseItems.Sum(x => x.ExecutionTime),
            TotalQueuedTime: phaseItems.Sum(x => x.QueuedTime),
            AverageQueuedTime: phaseItems.Average(x => x.QueuedTime),
            AzdoExecutionTime: azdoExecTime,
            EstimatedTime: estimatedTime));
    }
    return summaries;
}

string FormatTimeSpan(TimeSpan? ts) =>
    ts.HasValue ? ts.Value.ToString(@"hh\:mm\:ss") : "N/A";

/// Returns selected index, or -1 if Escape was pressed.
int RunSelection(string title, List<string> choices, int pageSize = 20)
{
    AnsiConsole.MarkupLine(title);

    int selected = 0;
    int scrollOffset = 0;
    int visible = Math.Min(pageSize, choices.Count);
    int startRow = Console.CursorTop;

    int width;
    try { width = Console.WindowWidth; } catch { width = 120; }
    if (width <= 0) width = 120;

    Console.CursorVisible = false;
    try
    {
        Render();
        while (true)
        {
            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (selected > 0)
                    {
                        selected--;
                        if (selected < scrollOffset)
                            scrollOffset = selected;
                        Render();
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (selected < choices.Count - 1)
                    {
                        selected++;
                        if (selected >= scrollOffset + visible)
                            scrollOffset = selected - visible + 1;
                        Render();
                    }
                    break;
                case ConsoleKey.Enter:
                    return selected;
                case ConsoleKey.Escape:
                    return -1;
            }
        }
    }
    finally
    {
        Console.CursorVisible = true;
    }

    void Render()
    {
        for (int i = 0; i < visible; i++)
        {
            Console.SetCursorPosition(0, startRow + i);
            int idx = scrollOffset + i;
            if (idx >= choices.Count)
            {
                Console.Write(new string(' ', width - 1));
                continue;
            }

            string prefix = idx == selected ? "> " : "  ";
            string text = $"{prefix}{choices[idx]}";
            if (text.Length < width - 1)
                text = text.PadRight(width - 1);
            else
                text = text[..(width - 1)];

            if (idx == selected)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(text);
                Console.ResetColor();
            }
            else
            {
                Console.Write(text);
            }
        }

        if (choices.Count > visible)
        {
            Console.SetCursorPosition(0, startRow + visible);
            string info = $"  [{scrollOffset + 1}-{Math.Min(scrollOffset + visible, choices.Count)} of {choices.Count}]";
            if (info.Length < width - 1)
                info = info.PadRight(width - 1);
            else
                info = info[..(width - 1)];
            Console.Write(info);
        }
    }
}

// --- Kusto query builders ---

string GetHelixWorkItemQueryForPullRequest(int prNumber) => $"""
    Jobs
    | where Repository == "dotnet/roslyn"
    | where Branch == "refs/pull/{prNumber}/merge"
    | project-away Started, Finished
    | join kind=inner WorkItems on JobId
    | extend p = parse_json(Properties)
    | extend AzdoPhaseName = tostring(p["System.PhaseName"])
    | extend AzdoAttempt = tostring(p["System.JobAttempt"])
    | extend AzdoBuildId = toint(p["BuildId"])
    | extend ExecutionTime = (Finished - Started) / 1s
    | extend QueuedTime = (Started - Queued) / 1s
    | project FriendlyName, ExecutionTime, QueuedTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt, MachineName
    """;

string GetHelixWorkItemQueryForBuild(int buildNumber) => $"""
    Jobs
    | where Repository == "dotnet/roslyn"
    | project-away Started, Finished
    | join kind=inner WorkItems on JobId
    | extend p = parse_json(Properties)
    | extend AzdoBuildId = toint(p["BuildId"])
    | where AzdoBuildId == {buildNumber}
    | extend AzdoPhaseName = tostring(p["System.PhaseName"])
    | extend AzdoAttempt = tostring(p["System.JobAttempt"])
    | extend ExecutionTime = (Finished - Started) / 1s
    | extend QueuedTime = (Started - Queued) / 1s
    | project FriendlyName, ExecutionTime, QueuedTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt, MachineName
    """;

// --- Kusto data fetching ---

async Task<List<WorkItemResult>> GetAllHelixWorkItemResults(TokenCredential cred, string query)
{
    try
    {
        var clusterUrl = "https://engsrvprod.kusto.windows.net";
        var databaseName = "engineeringdata";

        var tokenRequestContext = new TokenRequestContext(["https://kusto.kusto.windows.net/.default"]);
        var token = await cred.GetTokenAsync(tokenRequestContext, default);

        var kustoConnectionStringBuilder = new KustoConnectionStringBuilder(clusterUrl, databaseName)
            .WithAadTokenProviderAuthentication(() => token.Token);

        using var kustoQueryClient = KustoClientFactory.CreateCslQueryProvider(kustoConnectionStringBuilder);
        var reader = kustoQueryClient.ExecuteQuery(query);
        var list = new List<WorkItemResult>();

        while (reader.Read())
        {
            var friendlyName = reader.GetString(0);
            if (!Regex.IsMatch(friendlyName, @"workitem_\d+"))
            {
                continue;
            }

            var executionTime = TimeSpan.FromSeconds(reader.GetDouble(1));
            var queuedTime = TimeSpan.FromSeconds(reader.GetDouble(2));
            var azdoBuildId = reader.GetInt32(3);
            var azdoPhaseName = reader.GetString(4);
            var azdoAttempt = int.Parse(reader.GetString(5));
            var machineName = reader.GetString(6);

            list.Add(new WorkItemResult(friendlyName, executionTime, queuedTime, azdoAttempt, azdoPhaseName, azdoBuildId, machineName));
        }

        return list;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(ex.Message)}[/]");
        AnsiConsole.MarkupLine("[yellow]Error reading Kusto, are you connected to the VPN?[/]");
        throw;
    }
}

// --- AzDO helper functions ---

async Task<VssConnection> GetAzdoConnection(TokenCredential cred)
{
    var accessToken = await cred.GetTokenAsync(new TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]), cancellationToken: default);
    return new VssConnection(new Uri(AzdoOrganizationUrl), new VssOAuthAccessTokenCredential(accessToken.Token));
}

TimeSpan? GetAzdoPhaseExecutionTime(
    Timeline? timeline,
    string phaseName)
{
    if (timeline is null)
    {
        return null;
    }

    var record = timeline.Records.Where(x => x.Name == phaseName && x.RecordType == "Phase").FirstOrDefault();
    if (record is null)
    {
        return null;
    }

    return record.FinishTime - record.StartTime;
}

async Task<List<WorkItemData>> GetAzdoWorkItemData(
    HttpClient http,
    int buildId,
    string phaseName,
    int attemptId,
    List<BuildArtifact> buildArtifacts)
{
    var name = $"{phaseName} Attempt {attemptId} Logs";
    var artifact = buildArtifacts.FirstOrDefault(x => x.Name == name)!;
    using var stream = await http.GetStreamAsync(artifact.Resource.DownloadUrl);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
    var helixEntry = zip.Entries.Single(x => x.Name == "helix.proj");
    using var reader = new StreamReader(helixEntry.Open());
    var xmlContent = await reader.ReadToEndAsync();

    var document = XDocument.Parse(xmlContent);
    return document
        .Descendants("HelixWorkItem")
        .Select(e =>
        {
            var expectedStr = e.Element("ExpectedExecutionTime")?.Value;
            if (string.IsNullOrEmpty(expectedStr))
            {
                throw new Exception($"ExpectedExecutionTime missing for {e.Attribute("Include")!.Value}");
            }

            return new WorkItemData(e.Attribute("Include")!.Value, TimeSpan.Parse(expectedStr));
        })
        .ToList();
}

// --- Data models ---

enum Screen { BuildList, PhaseList, WorkItemList, WorkItemDetail, Exit }

internal sealed record BuildSummary(
    int BuildId,
    string BuildNumber,
    string SourceBranch,
    string RequestedBy,
    BuildResult? Result,
    BuildStatus? Status,
    DateTime? StartTime,
    DateTime? FinishTime)
{
    public string BranchDisplay =>
        SourceBranch
            .Replace("refs/heads/", "")
            .Replace("refs/pull/", "PR ")
            .Replace("/merge", "");

    public string ToDisplayString()
    {
        var status = Result?.ToString() ?? Status?.ToString() ?? "Unknown";
        var time = StartTime?.ToString("yyyy-MM-dd HH:mm") ?? "?";
        return $"{BuildId} | {BranchDisplay,-30} | {status,-12} | {time} | {RequestedBy}";
    }
}

internal sealed record PhaseSummary(
    string PhaseName,
    int AttemptId,
    int WorkItemCount,
    int MachineCount,
    TimeSpan TotalExecutionTime,
    TimeSpan TotalQueuedTime,
    TimeSpan AverageQueuedTime,
    TimeSpan? AzdoExecutionTime,
    TimeSpan? EstimatedTime);

internal sealed class WorkItemData(string name, TimeSpan expectedExecutionTime)
{
    public string Name { get; } = name;
    public TimeSpan ExpectedExecutionTime { get; } = expectedExecutionTime;

    public override string ToString() => $"{Name} ({ExpectedExecutionTime})";
}

internal sealed class WorkItemResult(
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

// --- Extension methods ---

internal static class Extensions
{
    public static TimeSpan Sum(this IEnumerable<TimeSpan> @this)
    {
        var d = @this.Sum(x => x.TotalSeconds);
        return TimeSpan.FromSeconds(d);
    }

    public static TimeSpan Sum<T>(this IEnumerable<T> @this, Func<T, TimeSpan> func) =>
        @this.Select(func).Sum();

    public static TimeSpan Average(this IEnumerable<TimeSpan> @this)
    {
        var d = @this.Average(x => x.TotalSeconds);
        return TimeSpan.FromSeconds(d);
    }

    public static TimeSpan Average<T>(this IEnumerable<T> @this, Func<T, TimeSpan> func) =>
        @this.Select(func).Average();
}
