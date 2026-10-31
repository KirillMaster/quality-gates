namespace KirillMaster.QualityGates.Build.Tests;

public sealed class RunnerRelaxationTests
{
    private const string CatchAll = """
        namespace Fixture;

        public static class Probe
        {
            public static void Run()
            {
                try
                {
                    System.Console.WriteLine("x");
                }
                catch (System.Exception)
                {
                }
            }
        }
        """;

    [Theory]
    [InlineData("Shop.Runner")]
    [InlineData("Shop.Runners.Host")]
    [InlineData("Runner.Host")]
    public void Runner_projects_get_the_runner_globalconfig(string name)
    {
        using var evaluation = new PropsEvaluation(name);
        Assert.Contains("quality.runners.globalconfig", evaluation.GlobalConfigs());
    }

    [Fact]
    public void Non_runner_projects_do_not_get_the_runner_globalconfig()
    {
        using var evaluation = new PropsEvaluation("Shop.Adapters");
        Assert.DoesNotContain("quality.runners.globalconfig", evaluation.GlobalConfigs());
    }

    [Fact]
    public void Catch_all_is_reported_outside_runners_and_not_in_runners()
    {
        using var adapter = new PropsEvaluation("Shop.Adapters");
        using var runner = new PropsEvaluation("Shop.Runners.Host");
        var adapterLog = adapter.Build(CatchAll);
        var runnerLog = runner.Build(CatchAll);
        Assert.Contains("CA1031", adapterLog, StringComparison.Ordinal);
        Assert.DoesNotContain("CA1031", runnerLog, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidGlobalSectionName", adapterLog, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidGlobalSectionName", runnerLog, StringComparison.Ordinal);
    }
}
