using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class LayerClassifierTests
{
    [Theory]
    [Trait("scenario", "US2-AS6")]
    [InlineData("Sample.Domain")]
    [InlineData("Sample.Application")]
    public void Core_by_name_suffix(string project)
    {
        var c = LayerClassifier.Classify(project);
        c.Layer.Should().Be(Layer.Core);
        c.Unclassified.Should().BeFalse();
    }

    [Theory]
    [Trait("scenario", "US2-AS7")]
    [InlineData("src/Core/Sample.Rules/Sample.Rules.csproj")]
    [InlineData("src\\Core\\Sample.Rules\\Sample.Rules.csproj")]
    [InlineData("Core/Sample.Rules/Sample.Rules.csproj")]
    public void Core_by_folder(string path)
    {
        var c = LayerClassifier.Classify("Sample.Rules", path);
        c.Layer.Should().Be(Layer.Core);
        c.Unclassified.Should().BeFalse();
    }

    [Fact]
    [Trait("scenario", "US2-AS8")]
    public void Unmatched_is_adapter_and_unclassified()
    {
        var c = LayerClassifier.Classify("Sample.Api", "src/Sample.Api/Sample.Api.csproj");
        c.Layer.Should().Be(Layer.Adapter);
        c.Unclassified.Should().BeTrue();
        LayerClassifier.Classify("Sample.Domain").Unclassified.Should().BeFalse();
    }

    [Theory]
    [Trait("scenario", "US2-AS9")]
    [InlineData("Sample.Tests")]
    [InlineData("Sample.Domain.Tests")]
    [InlineData("Sample.IntegrationTests")]
    public void Test_projects_are_test(string project)
    {
        var c = LayerClassifier.Classify(project);
        c.Layer.Should().Be(Layer.Test);
        c.Unclassified.Should().BeFalse();
    }

    [Theory]
    [Trait("scenario", "US2-AS10")]
    [InlineData("Sample.Api", Layer.Core)]
    [InlineData("Sample.Domain", Layer.Adapter)]
    [InlineData("Sample.Tests", Layer.Excluded)]
    public void Override_wins(string project, Layer forced)
    {
        var overrides = new Dictionary<string, Layer> { [project] = forced };
        var c = LayerClassifier.Classify(project, null, overrides);
        c.Layer.Should().Be(forced);
        c.Unclassified.Should().BeFalse();
    }
}
