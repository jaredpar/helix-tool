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

namespace HelixCli;

public class HelixService
{
    public const string AzdoOrganizationUrl = "https://dev.azure.com/dnceng-public";
    public const string AzdoProjectName = "public";

    private readonly TokenCredential _credential;

    public HelixService(TokenCredential credential)
    {
        _credential = credential;
    }

    public static TokenCredential CreateCredential() =>
        new AzureCliCredential(
            new AzureCliCredentialOptions { TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47" });

    public string GetHelixWorkItemQueryForPullRequest(int prNumber) => $"""
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

    public string GetHelixWorkItemQueryForBuild(int buildNumber) => $"""
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

    public async Task<List<WorkItemResult>> GetAllHelixWorkItemResults(string query)
    {
        var clusterUrl = "https://engsrvprod.kusto.windows.net";
        var databaseName = "engineeringdata";

        var tokenRequestContext = new TokenRequestContext(["https://kusto.kusto.windows.net/.default"]);
        var token = await _credential.GetTokenAsync(tokenRequestContext, default);

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

    public async Task<VssConnection> GetAzdoConnection()
    {
        var accessToken = await _credential.GetTokenAsync(
            new TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]),
            cancellationToken: default);
        return new VssConnection(new Uri(AzdoOrganizationUrl), new VssOAuthAccessTokenCredential(accessToken.Token));
    }

    public async Task<List<Build>> GetFailedBuilds(string? definition = null)
    {
        var connection = await GetAzdoConnection();
        var buildClient = connection.GetClient<BuildHttpClient>();
        var builds = await buildClient.GetBuildsAsync(
            AzdoProjectName,
            statusFilter: BuildStatus.Completed,
            resultFilter: BuildResult.Failed,
            queryOrder: BuildQueryOrder.FinishTimeDescending,
            top: 20);

        if (definition is not null)
        {
            builds = builds.Where(b => b.Definition.Name.Contains(definition, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return builds;
    }

    public IEnumerable<(int AzdoBuildId, List<WorkItemResult> Items)> GetByBuild(List<WorkItemResult> workItemResults)
    {
        var buildIds = workItemResults
            .Select(x => x.AzdoBuildId)
            .Distinct()
            .OrderByDescending(x => x)
            .ToList();

        foreach (var buildId in buildIds)
        {
            yield return (buildId, workItemResults.Where(x => x.AzdoBuildId == buildId).ToList());
        }
    }

    public TimeSpan? GetAzdoPhaseExecutionTime(Timeline? timeline, string phaseName)
    {
        if (timeline is null)
            return null;

        var record = timeline.Records
            .Where(x => x.Name == phaseName && x.RecordType == "Phase")
            .FirstOrDefault();

        return record is null ? null : record.FinishTime - record.StartTime;
    }

    public async Task<List<WorkItemData>> GetAzdoWorkItemData(
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

    /// <summary>
    /// Gets a summary of helix work items grouped by phase for a given build.
    /// </summary>
    public async Task<List<PhaseSummary>> GetPhaseSummaries(int azdoBuildId, List<WorkItemResult> workItemResults)
    {
        var connection = await GetAzdoConnection();
        var buildClient = connection.GetClient<BuildHttpClient>();
        var httpClient = new HttpClient();

        var buildArtifacts = await buildClient.GetArtifactsAsync(AzdoProjectName, azdoBuildId);
        var timeline = await buildClient.GetBuildTimelineAsync(AzdoProjectName, azdoBuildId);

        var summaries = new List<PhaseSummary>();
        var groupedResults = workItemResults
            .GroupBy(x => x.AzdoPhaseName)
            .OrderBy(x => x.Key);

        foreach (var g in groupedResults)
        {
            var attemptId = g.Max(x => x.AzdoAttempt);
            var phaseItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
            var azdoExecutionTime = GetAzdoPhaseExecutionTime(timeline, g.Key);

            TimeSpan? estimatedTime = null;
            try
            {
                var azdoDataList = await GetAzdoWorkItemData(httpClient, azdoBuildId, g.Key, attemptId, buildArtifacts);
                estimatedTime = azdoDataList.Sum(x => x.ExpectedExecutionTime);
            }
            catch
            {
                // Artifact may not be available
            }

            summaries.Add(new PhaseSummary(
                g.Key,
                azdoExecutionTime,
                estimatedTime,
                phaseItems.Sum(x => x.QueuedTime),
                phaseItems.Average(x => x.QueuedTime),
                phaseItems.Sum(x => x.ExecutionTime),
                phaseItems.Count,
                phaseItems.Select(x => x.MachineName).Distinct().Count(),
                phaseItems));
        }

        return summaries;
    }
}

public sealed record PhaseSummary(
    string PhaseName,
    TimeSpan? AzdoExecutionTime,
    TimeSpan? EstimatedTime,
    TimeSpan TotalQueuedTime,
    TimeSpan AverageQueuedTime,
    TimeSpan TotalExecutionTime,
    int WorkItemCount,
    int MachineCount,
    List<WorkItemResult> WorkItems);
