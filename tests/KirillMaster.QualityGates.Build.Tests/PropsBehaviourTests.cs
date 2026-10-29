namespace KirillMaster.QualityGates.Build.Tests;

public sealed class PropsBehaviourTests
{
    private static readonly string[] Names = ["QualityGatesMode", "QualityGatesEffectiveMode", "TreatWarningsAsErrors", "EnforceCodeStyleInBuild", "AnalysisLevel"];

    private static Dictionary<string, string> Msbuild(params (string Key, string Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void Defaults_are_adopt_strict_and_pinned()
    {
        using var evaluation = new PropsEvaluation();
        var p = evaluation.Properties(Names);
        Assert.Equal("adopt", p["QualityGatesMode"]);
        Assert.Equal("adopt", p["QualityGatesEffectiveMode"]);
        Assert.Equal("true", p["TreatWarningsAsErrors"]);
        Assert.Equal("true", p["EnforceCodeStyleInBuild"]);
        Assert.Equal("10.0-all", p["AnalysisLevel"]);
    }

    [Fact]
    public void Legacy_ratchet_pins_recommended()
    {
        using var evaluation = new PropsEvaluation();
        Assert.Equal("10.0-recommended", evaluation.Properties(Names, Msbuild(("QualityGatesProfile", "legacy-ratchet")))["AnalysisLevel"]);
    }

    [Fact]
    public void Consumer_values_win_in_adopt()
    {
        using var evaluation = new PropsEvaluation();
        var p = evaluation.Properties(Names, Msbuild(("AnalysisLevel", "9.0-minimum"), ("EnforceCodeStyleInBuild", "false"), ("TreatWarningsAsErrors", "false")));
        Assert.Equal("9.0-minimum", p["AnalysisLevel"]);
        Assert.Equal("false", p["EnforceCodeStyleInBuild"]);
        Assert.Equal("false", p["TreatWarningsAsErrors"]);
    }

    [Fact]
    public void Mode_is_read_from_environment_when_property_is_empty()
    {
        using var evaluation = new PropsEvaluation();
        var p = evaluation.Properties(Names, environment: new Dictionary<string, string> { ["QUALITYGATES_MODE"] = "refactor" });
        Assert.Equal("refactor", p["QualityGatesMode"]);
        Assert.Equal("refactor", p["QualityGatesEffectiveMode"]);
    }

    [Fact]
    public void Property_beats_environment()
    {
        using var evaluation = new PropsEvaluation();
        var p = evaluation.Properties(Names, Msbuild(("QualityGatesMode", "adopt")), new Dictionary<string, string> { ["QUALITYGATES_MODE"] = "refactor" });
        Assert.Equal("adopt", p["QualityGatesEffectiveMode"]);
    }

    [Fact]
    public void Refactor_forces_warnings_as_errors()
    {
        using var evaluation = new PropsEvaluation(properties: "<TreatWarningsAsErrors>false</TreatWarningsAsErrors>");
        Assert.Equal("true", evaluation.Properties(Names, Msbuild(("QualityGatesMode", "refactor")))["TreatWarningsAsErrors"]);
    }

    [Fact]
    public void Grandfathered_file_is_compiled_in_adopt_only()
    {
        using var evaluation = new PropsEvaluation();
        evaluation.AddGrandfathered();
        Assert.True(evaluation.CompilesGrandfathered());
        Assert.False(evaluation.CompilesGrandfathered(Msbuild(("QualityGatesMode", "refactor"))));
    }

    [Theory]
    [InlineData("Shop.Adapters", "Shop.*", "refactor")]
    [InlineData("Shop.Adapters", "Other.*;Shop.Adapt", "refactor")]
    [InlineData("Shop.Adapters", "Other.*", "adopt")]
    [InlineData("Shop.Adapters", "Adapters", "adopt")]
    [InlineData("Shop.Adapters", "Other.*;;Shop.Adapt", "refactor")]
    [InlineData("Shop.Adapters", "Other.*; Shop.Adapt", "refactor")]
    [InlineData("Shop.Adapters", ";;", "refactor")]
    [InlineData("Shop.Adapters", "Other.*;;", "adopt")]
    public void Refactor_scope_limits_the_mode(string project, string scope, string expected)
    {
        using var evaluation = new PropsEvaluation(project);
        var p = evaluation.Properties(Names, Msbuild(("QualityGatesMode", "refactor"), ("QualityGatesRefactorScope", scope)));
        Assert.Equal(expected, p["QualityGatesEffectiveMode"]);
    }

    [Fact]
    public void Mode_set_in_project_body_counts()
    {
        using var evaluation = new PropsEvaluation(properties: "<QualityGatesMode>refactor</QualityGatesMode>");
        evaluation.AddGrandfathered();
        Assert.Equal("refactor", evaluation.Properties(Names)["QualityGatesEffectiveMode"]);
        Assert.False(evaluation.CompilesGrandfathered());
    }

    [Fact]
    public void Refactor_raises_severity_through_its_globalconfig_for_in_scope_projects_only()
    {
        using var evaluation = new PropsEvaluation();
        Assert.DoesNotContain("quality.refactor.globalconfig", evaluation.GlobalConfigs());
        Assert.Contains("quality.refactor.globalconfig", evaluation.GlobalConfigs(Msbuild(("QualityGatesMode", "refactor"))));
        Assert.DoesNotContain("quality.refactor.globalconfig", evaluation.GlobalConfigs(Msbuild(("QualityGatesMode", "refactor"), ("QualityGatesRefactorScope", "Other"))));
    }

    [Fact]
    public void Out_of_scope_project_keeps_grandfathered_file()
    {
        using var evaluation = new PropsEvaluation();
        evaluation.AddGrandfathered();
        Assert.True(evaluation.CompilesGrandfathered(Msbuild(("QualityGatesMode", "refactor"), ("QualityGatesRefactorScope", "Other.*"))));
    }

    [Fact]
    public void Disabled_gates_set_nothing()
    {
        using var evaluation = new PropsEvaluation();
        var p = evaluation.Properties(["AnalysisLevel", "QualityGatesGrandfatheredFile"], Msbuild(("QualityGatesEnabled", "false")));
        Assert.Equal("latest", p["AnalysisLevel"]);
        Assert.Equal(string.Empty, p["QualityGatesGrandfatheredFile"]);
    }
}
