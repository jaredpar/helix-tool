
using System.Data.SqlTypes;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

const string AzdoOrganizationUrl = $"https://dev.azure.com/dnceng-public";
const string AzdoProjectName = "public";

try
{
    int prNumber;
    if (args.Length == 0)
    {
        prNumber = 76828;
        Console.WriteLine($"No PR number specified, using {prNumber}");
    }
    else
    {
        prNumber = int.Parse(args[0]);
    }

    var workItemResults = GetHelixWorkItemResults(prNumber);
    var azdoConnection = await GetAzdoConnection();
    var buildClient = azdoConnection.GetClient<BuildHttpClient>();
    var httpClient = new HttpClient();
    var azdoBuildId = workItemResults.First().AzdoBuildId;
    var buildArtifacts = await buildClient.GetArtifactsAsync(AzdoProjectName, azdoBuildId);

    foreach (var g in workItemResults.GroupBy(x => x.AzdoPhaseName))
    {
        var attemptId = g.Max(x => x.AzdoAttempt);
        var phaseWorkItems = g.Where(x => x.AzdoAttempt == attemptId).ToList();
        try
        {
            await ComparePhase(
                httpClient,
                azdoBuildId,
                g.Key,
                attemptId,
                g.ToList(),
                buildArtifacts);
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

List<WorkItemResult> GetHelixWorkItemResults(int prNumber)
{
    // Replace with your cluster URI and database name
    var clusterUrl = "https://engsrvprod.kusto.windows.net";
    var databaseName = "engineeringdata";

    // Create a Kusto connection string
    var kustoConnectionStringBuilder = new KustoConnectionStringBuilder(clusterUrl, databaseName)
        .WithAadAzCliAuthentication(interactive: true);

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
        | project FriendlyName, ExecutionTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt
        """;

    var reader = kustoQueryClient.ExecuteQuery(query);
    var list = new List<WorkItemResult>();

    // Read and print results
    while (reader.Read())
    {
        var friendlyName = reader.GetString(0);
        if (!Regex.IsMatch(friendlyName, @"workitem_\d+"))
        {
            Console.WriteLine($"Skipping helix work item {friendlyName}");
            continue;
        }

        var executionTime = reader.IsDBNull(1) ? (TimeSpan?)null : TimeSpan.FromSeconds(reader.GetDouble(1));
        var azdoBuildId = reader.GetInt32(2);
        var azdoPhaseName = reader.GetString(3);
        var azdoAttempt = int.Parse(reader.GetString(4));

        list.Add(new WorkItemResult(friendlyName, executionTime, azdoAttempt, azdoPhaseName, azdoBuildId));
    }

    // When there is a retry work items from multiple builds will be in the data 
    // here. Need to reduce down to the latest build.
    var buildId = list.Max(x => x.AzdoBuildId);
    list.RemoveAll(x => x.AzdoBuildId != buildId); 
    return list;
}

async Task ComparePhase(
    HttpClient httpClient,
    int buildId,
    string phaseName,
    int attemptId,
    List<WorkItemResult> helixResults,
    List<BuildArtifact> buildArtifacts)
{
    Debug.Assert(helixResults.All(x => x.AzdoPhaseName == phaseName));
    var azdoDataList = await GetAzdoWorkItemData(
        httpClient,
        buildId,
        phaseName,
        attemptId,
        buildArtifacts);

    Console.WriteLine($"Comparing {phaseName}");
    foreach (var data in azdoDataList)
    {
        var helixResult = helixResults.SingleOrDefault(x => x.FriendlyName == data.Name);
        if (helixResult is null)
        {
            Console.WriteLine($"\t{data.Name} missing helix work item");
            continue;
        }

        if (helixResult.ExecutionTime is null)
        {
            Console.WriteLine($"\t{data.Name} missing helix execution time");
        }

        if (data.ExpectedExecutionTime is null)
        {
            Console.WriteLine($"\t{data.Name} missing azdo execution time");
        }

        var diff = helixResult.ExecutionTime - data.ExpectedExecutionTime;
        Console.WriteLine($"\t{data.Name} expected: {data.ExpectedExecutionTime:mm\\:ss} actual: {helixResult.ExecutionTime:mm\\:ss} diff: {diff:mm\\:ss}");
    }
}

async Task<VssConnection> GetAzdoConnection()
{
    var credential = new AzureCliCredential(
        new AzureCliCredentialOptions { TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47" });
    var accessToken = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]));
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
            var t = string.IsNullOrEmpty(expectedStr) ? (TimeSpan?)null : TimeSpan.Parse(expectedStr);
            return new WorkItemData(e.Attribute("Include")!.Value, t);
        })
        .ToList();
}

internal sealed class WorkItemData(string name, TimeSpan? expectedExecutionTime)
{
    public string Name { get; } = name;
    public TimeSpan? ExpectedExecutionTime { get; } = expectedExecutionTime;

    public override string ToString() => $"{Name} ({ExpectedExecutionTime})";
}

internal sealed class WorkItemResult(
    string friendlyName,
    TimeSpan? executionTime,
    int azdoAttempt,
    string azdoPhaseName,
    int azdoBuildId)
{
    public string FriendlyName { get; } = friendlyName;
    public TimeSpan? ExecutionTime { get; } = executionTime;
    public int AzdoAttempt { get; } = azdoAttempt;
    public string AzdoPhaseName { get; } = azdoPhaseName;
    public int AzdoBuildId { get; } = azdoBuildId;

    public override string ToString() => $"{FriendlyName} ({AzdoBuildId})";
}
