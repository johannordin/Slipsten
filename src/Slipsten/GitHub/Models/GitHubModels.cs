namespace Slipsten.GitHub.Models;

public record PullRequestInfo(long Id, string Title, string Url, string Repo);
public record WorkflowRunInfo(long Id, string WorkflowName, string Conclusion, string Url, string Repo);
