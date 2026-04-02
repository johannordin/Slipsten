namespace Slipsten.AzureDevOps;

public sealed record AzureDevOpsWorkItemInfo(
    int Id,
    string Title,
    string State,
    string WorkItemType,
    string Project,
    string? Url);
