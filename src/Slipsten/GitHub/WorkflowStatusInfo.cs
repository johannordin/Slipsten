namespace Slipsten.GitHub;

public enum CiStatus { Unknown, Green, Red }

public record WorkflowStatusInfo(
    string Repository,
    string WorkflowName,
    string WorkflowFileName,
    CiStatus Status,
    string RunUrl,
    DateTime LastChecked);
