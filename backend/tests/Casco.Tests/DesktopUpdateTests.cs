using Casco.Api.Features.Desktop;

namespace Casco.Tests;

public class DesktopUpdateTests
{
    [Theory]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.3.0", true)]
    [InlineData("0.3.0", "0.2.0", false)]
    [InlineData("0.2", "0.2.1", true)]
    [InlineData("1.0.0", "0.9.9", false)]
    [InlineData("", "0.2.0", true)]
    public void OlderClientsAreBlocked(string client, string required, bool older) =>
        Assert.Equal(older, DesktopUpdate.IsOlder(client, required));
}
