namespace Sample.Tests;

public class MarkerTests
{
    [Fact]
    public void Domain_marker_is_named()
    {
        Assert.Equal("Domain", Sample.Domain.Marker.Name);
    }
}
