namespace ImmichDuplicateReview.Tests.Deployment;

public sealed class ContainerConfigurationTests
{
    [Fact]
    public void Runtime_image_grants_application_user_access_to_persistent_data_directory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "Dockerfile"));

        var ownership = dockerfile.IndexOf("chown -R $APP_UID:$APP_UID /data", StringComparison.Ordinal);
        var nonRootUser = dockerfile.IndexOf("USER $APP_UID", StringComparison.Ordinal);

        Assert.True(ownership >= 0, "Dockerfile must grant the application user ownership of /data.");
        Assert.True(ownership < nonRootUser, "Data-directory ownership must be set before switching to the non-root user.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dockerfile"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
