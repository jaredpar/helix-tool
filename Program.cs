
using System.Data.SqlTypes;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure.Core;
using Azure.Identity;
using Kusto.Cloud.Platform.Utils;
using Kusto.Data;
using Kusto.Data.Net.Client;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualBasic;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;
using Mono.Options;

const string AzdoOrganizationUrl = $"https://dev.azure.com/dnceng-public";
const string AzdoProjectName = "public";

try
{
    int prNumber = 0;
    bool detailed = false;
    string? phaseNameFilter = null;

    var options = new OptionSet
    {
        { "pr=", "The PR value (required)", (int v) => prNumber = v },
        { "phase=", "Filter to a phase", (string p) => phaseNameFilter = p },
        { "detailed", "Enable detailed mode", v => detailed = v != null }
    };

    var extra = options.Parse(args);
    if (extra.Count > 0)
    {
        throw new OptionException("Unrecognized option: " + extra[0], extra[0]);
    }

    if (prNumber == 0)
    {
        prNumber = 76828;
        Console.WriteLine($"No PR number specified, using {prNumber}");
    }

    var credential = new AzureCliCredential(
        new AzureCliCredentialOptions { TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47" });
    var allWorkItemResults = await GetAllHelixWorkItemResults(credential, prNumber);
    var azdoConnection = await GetAzdoConnection(credential);
    var buildClient = azdoConnection.GetClient<BuildHttpClient>();
    var httpClient = new HttpClient();
    foreach (var (azdoBuildId, workItemResults) in GetByBuild(allWorkItemResults))
    {
        try
        {
            var url = $"{AzdoOrganizationUrl}/{AzdoProjectName}/_build/results?buildId={azdoBuildId}";
            Console.WriteLine();
            Console.WriteLine(url);

            var buildArtifacts = await buildClient.GetArtifactsAsync(AzdoProjectName, azdoBuildId);
            var timeline = await buildClient.GetBuildTimelineAsync(AzdoProjectName, azdoBuildId);
            foreach (var g in workItemResults.GroupBy(x => x.AzdoPhaseName))
            {
                if (phaseNameFilter is not null && g.Key != phaseNameFilter)
                {
                    continue;
                }

                var attemptId = g.Max(x => x.AzdoAttempt);
                var phaseWorkItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
                await ComparePhase(
                    httpClient,
                    azdoBuildId,
                    g.Key,
                    attemptId,
                    g.ToList(),
                    buildArtifacts,
                    timeline,
                    detailed);
            }
        }
        catch (Exception ex)
        {
            Console.Write(ex.Message);
            Console.Write(ex.StackTrace);
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine(ex.Message);
    Console.WriteLine(ex.StackTrace);
}

IEnumerable<(int AzdoBuildId, List<WorkItemResult>)> GetByBuild(List<WorkItemResult> workItemResults)
{
    var buildIds = workItemResults
        .Select(x => x.AzdoBuildId)
        .Distinct()
        .OrderByDescending(x => x)
        .ToList();
    if (buildIds.Count == 1)
    {
        yield return (buildIds[0], workItemResults);
        yield break;
    }

    foreach (var buildId in buildIds)
    {
        yield return (buildId, workItemResults.Where(x => x.AzdoBuildId == buildId).ToList());
    }
}

// This will get all of the work item results for a given PR. If there are multiple builds for the PRs
// then this will return the work item results for all of them
async Task<List<WorkItemResult>> GetAllHelixWorkItemResults(TokenCredential credential, int prNumber)
{
    try
    {
        // Replace with your cluster URI and database name
        var clusterUrl = "https://engsrvprod.kusto.windows.net";
        var databaseName = "engineeringdata";

        var tokenRequestContext = new TokenRequestContext(["https://kusto.kusto.windows.net/.default"]);
        var token = await credential.GetTokenAsync(tokenRequestContext, default);

        // Create a Kusto connection string
        var kustoConnectionStringBuilder = new KustoConnectionStringBuilder(clusterUrl, databaseName)
            .WithAadTokenProviderAuthentication(() => token.Token);

        using var kustoQueryClient = KustoClientFactory.CreateCslQueryProvider(kustoConnectionStringBuilder);
        var query = $"""
            Jobs
            | where Repository == "dotnet/roslyn"
            | where Branch == "refs/pull/{prNumber}/merge"
            | project-away Started, Finished
            | join kind=inner WorkItems on JobId
            | extend  p = parse_json(Properties)
            | extend AzdoPhaseName = tostring(p["System.PhaseName"])
            | extend AzdoAttempt = tostring(p["System.JobAttempt"])
            | extend AzdoBuildId = toint(p["BuildId"])
            | extend ExecutionTime = (Finished - Started) / 1s
            | extend QueuedTime = (Started - Queued) / 1s
            | project FriendlyName, ExecutionTime, QueuedTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt
            """;

        var reader = kustoQueryClient.ExecuteQuery(query);
        var list = new List<WorkItemResult>();

        // Read and print results
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

            list.Add(new WorkItemResult(friendlyName, executionTime, queuedTime, azdoAttempt, azdoPhaseName, azdoBuildId));
        }

        return list;
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
        Console.WriteLine("Error reading Kusto, are you connected to the VPN?");
        throw;
    }
}

async Task ComparePhase(
    HttpClient httpClient,
    int buildId,
    string phaseName,
    int attemptId,
    List<WorkItemResult> helixResults,
    List<BuildArtifact> buildArtifacts,
    Timeline timeline,
    bool detailed)
{
    Debug.Assert(helixResults.All(x => x.AzdoPhaseName == phaseName));
    var azdoDataList = await GetAzdoWorkItemData(
        httpClient,
        buildId,
        phaseName,
        attemptId,
        buildArtifacts);

    Console.WriteLine($"Comparing {phaseName}");
    if (detailed)
    {
        foreach (var data in azdoDataList)
        {
            var helixResult = helixResults.SingleOrDefault(x => x.FriendlyName == data.Name);
            if (helixResult is null)
            {
                Console.WriteLine($"\t{data.Name} missing helix work item");
                continue;
            }

            var diff = helixResult.ExecutionTime - data.ExpectedExecutionTime;
            Console.WriteLine($"\t{data.Name} expected: {data.ExpectedExecutionTime:mm\\:ss} actual: {helixResult.ExecutionTime:mm\\:ss} diff: {diff:mm\\:ss} queued: {helixResult.QueuedTime:mm\\:ss}");
        }
    }
    Console.WriteLine($"\tTotal Estimated Time: {azdoDataList.Sum(x => x.ExpectedExecutionTime):mm\\:ss}");
    Console.WriteLine($"\tTotal Helix Queued Time: {helixResults.Sum(x => x.QueuedTime):mm\\:ss}");
    Console.WriteLine($"\tTotal Helix Execution Time: {helixResults.Sum(x => x.ExecutionTime):mm\\:ss}");
    var azdoExecutionTime = GetAzdoPhaseExecutionTime(timeline, phaseName);
    Console.WriteLine($"\tTotal Azdo Execution Time: {azdoExecutionTime:mm\\:ss}");
}

TimeSpan? GetAzdoPhaseExecutionTime(
    Timeline? timeline,
    string phaseName)
{
    if (timeline is null)
    {
        return null;
    }

    var record = timeline.Records.Where(x => x.Name ==  phaseName && x.RecordType == "Phase").FirstOrDefault();;
    if (record is null)
    {
        return null;
    }

    return record.FinishTime - record.StartTime;
}

async Task<VssConnection> GetAzdoConnection(TokenCredential credential)
{
    var accessToken = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]), cancellationToken: default);
    return new VssConnection(new Uri(AzdoOrganizationUrl), new VssOAuthAccessTokenCredential(accessToken.Token));
}

async Task<List<WorkItemData>> GetAzdoWorkItemData(
    HttpClient httpClient,
    int buildId,
    string phaseName,
    int attemptId,
    List<BuildArtifact> buildArtifacts)
{
    var name = $"{phaseName} Attempt {attemptId} Logs";
    var artifact = buildArtifacts.FirstOrDefault(x => x.Name == name)!;
    using var stream = await httpClient.GetStreamAsync(artifact.Resource.DownloadUrl);
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
    int azdoBuildId)
{
    public string FriendlyName { get; } = friendlyName;
    public TimeSpan ExecutionTime { get; } = executionTime;
    public TimeSpan QueuedTime { get; } = queuedTime;
    public int AzdoAttempt { get; } = azdoAttempt;
    public string AzdoPhaseName { get; } = azdoPhaseName;
    public int AzdoBuildId { get; } = azdoBuildId;

    public override string ToString() => $"{FriendlyName} ({AzdoBuildId})";
}

internal static class Extensions
{
    public static TimeSpan Sum(this IEnumerable<TimeSpan> @this)
    {
        var d = @this.Sum(x => x.TotalSeconds);
        return TimeSpan.FromSeconds(d);
    }

    public static TimeSpan Sum<T>(this IEnumerable<T> @this, Func<T, TimeSpan> func) =>
        @this.Select(func).Sum();
}
