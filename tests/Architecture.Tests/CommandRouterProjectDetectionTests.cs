using DenMcp.Cli;

namespace Architecture.Tests;

public class CommandRouterProjectDetectionTests
{
    [Fact]
    public void DetectProjectId_UsesRepositoryRootNameWhenCurrentDirectoryIsNested()
    {
        var temp = CreateTempDirectory();
        try
        {
            var repo = Path.Combine(temp, "den-mcp");
            var nested = Path.Combine(repo, "src", "DenMcp.Cli");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(repo, ".git"));

            Assert.Equal("den-mcp", CommandRouter.DetectProjectId(nested));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void DetectProjectId_FallsBackToCurrentDirectoryNameOutsideRepository()
    {
        var temp = CreateTempDirectory();
        try
        {
            var dir = Path.Combine(temp, "loose-dir");
            Directory.CreateDirectory(dir);

            Assert.Equal("loose-dir", CommandRouter.DetectProjectId(dir));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "den-cli-detect-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
