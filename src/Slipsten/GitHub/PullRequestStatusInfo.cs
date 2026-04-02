namespace Slipsten.GitHub;

public record PullRequestStatusInfo(
    string Repository,
    int Number,
    string Title,
    string Url,
    string AuthorLogin);
