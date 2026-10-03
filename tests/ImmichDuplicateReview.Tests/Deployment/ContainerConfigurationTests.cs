namespace ImmichDuplicateReview.Tests.Deployment;

public sealed class ContainerConfigurationTests
{
    [Fact]
    public void Runtime_image_grants_application_user_access_to_persistent_data_directory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "Dockerfile"));

        var ownership = dockerfile.IndexOf("COPY --from=build --chown=$APP_UID:$APP_UID /container-data /data", StringComparison.Ordinal);
        var nonRootUser = dockerfile.IndexOf("USER $APP_UID", StringComparison.Ordinal);

        Assert.True(ownership >= 0, "Dockerfile must grant the application user ownership of /data.");
        Assert.True(ownership < nonRootUser, "Data-directory ownership must be set before switching to the non-root user.");
    }

    [Fact]
    public void Deployment_uses_chiseled_runtime_and_keeps_memory_limit_optional()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "Dockerfile"));
        var compose = File.ReadAllText(Path.Combine(repositoryRoot, "compose.yaml"));
        var memoryCompose = File.ReadAllText(Path.Combine(repositoryRoot, "compose.memory-limit.yml"));
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));

        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN mkdir -p /data", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("mem_limit:", compose, StringComparison.Ordinal);
        Assert.Contains("mem_limit: 384m", memoryCompose, StringComparison.Ordinal);
        Assert.Contains("repair-data:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("--entrypoint sh", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuous_integration_reports_required_check_for_every_pull_request()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "build-and-test.yml"));

        Assert.DoesNotContain("paths-ignore:", workflow, StringComparison.Ordinal);
        Assert.Contains("push:\n    branches: [main]", workflow, StringComparison.Ordinal);
        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dockerfile"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
