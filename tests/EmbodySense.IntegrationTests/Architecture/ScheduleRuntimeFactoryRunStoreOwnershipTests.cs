namespace EmbodySense.IntegrationTests.Architecture;

public sealed class ScheduleRuntimeFactoryRunStoreOwnershipTests
{
    [Fact]
    public void Production_schedule_factory_never_constructs_an_independent_custom_loop_run_store()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbodySense.Core.Startup",
            "Triggers",
            "Schedules",
            "ScheduleRuntimeFactory.cs"));

        Assert.DoesNotContain("new CustomLoopRunStore", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
