using Octokit;
using Slipsten.Settings;

namespace Slipsten.GitHub;

public sealed record GitHubRepositoryCatalogItem(long Id, string FullName, bool IsConfigured);
public sealed record GitHubWorkflowCatalogItem(string Name, string FileName);
public sealed record GitHubTeamCatalogItem(long Id, string Organization, string Slug, string Name);

public sealed class GitHubDiscoveryService
{
    public GitHubClient? CreateClient(string personalAccessToken)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
            return null;

        return new GitHubClient(new ProductHeaderValue("Slipsten"))
        {
            Credentials = new Credentials(personalAccessToken.Trim())
        };
    }

    public async Task<IReadOnlyList<GitHubRepositoryCatalogItem>> GetAccessiblePrivateRepositoriesAsync(AppSettings settings)
    {
        var client = CreateClient(settings.GitHub.PersonalAccessToken);
        if (client == null)
            return [];

        var repositories = await client.Repository.GetAllForCurrent(new ApiOptions { PageCount = 10, PageSize = 100 });
        var configured = settings.GitHub.RepositoryWatches
            .Select(w => w.Repository)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return repositories
            .Where(r => r.Private)
            .Select(r => new GitHubRepositoryCatalogItem(r.Id, r.FullName, configured.Contains(r.FullName)))
            .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GitHubWorkflowCatalogItem>> GetWorkflowsAsync(string personalAccessToken, string repository)
    {
        var client = CreateClient(personalAccessToken);
        if (client == null || !TrySplitRepository(repository, out var owner, out var repo))
            return [];

        var workflows = await client.Actions.Workflows.List(owner, repo);
        return workflows.Workflows
            .Select(w => new GitHubWorkflowCatalogItem(
                string.IsNullOrWhiteSpace(w.Name) ? Path.GetFileNameWithoutExtension(w.Path) : w.Name,
                Path.GetFileName(w.Path)))
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GitHubTeamCatalogItem>> GetCurrentUserTeamsForRepositoryAsync(string personalAccessToken, string repository)
    {
        var client = CreateClient(personalAccessToken);
        if (client == null || !TrySplitRepository(repository, out var owner, out _))
            return [];

        var teams = await client.Organization.Team.GetAllForCurrent(new ApiOptions { PageCount = 10, PageSize = 100 });
        return teams
            .Where(t => string.Equals(t.Organization.Login, owner, StringComparison.OrdinalIgnoreCase))
            .Select(t => new GitHubTeamCatalogItem(t.Id, t.Organization.Login, t.Slug, t.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TrySplitRepository(string repository, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (string.IsNullOrWhiteSpace(repository))
            return false;

        var parts = repository.Split('/');
        if (parts.Length != 2)
            return false;

        owner = parts[0];
        repo = parts[1];
        return true;
    }
}
