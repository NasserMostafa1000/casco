using Casco.Api.Features.Desktop;

namespace Casco.Tests;

public class DesktopTrialTests
{
    [Fact]
    public void FiveDistinctGroupsFillTheMask()
    {
        var mask = 0;
        for (var slot = 1; slot <= DesktopTrial.Groups; slot++)
            mask |= 1 << (slot - 1);
        Assert.Equal(5, DesktopTrial.CountShares(mask));
    }

    [Fact]
    public void RepeatingAGroupDoesNotCountTwice()
    {
        var mask = 0;
        mask |= 1 << 0;
        mask |= 1 << 0;
        Assert.Equal(1, DesktopTrial.CountShares(mask));
    }

    [Theory]
    [InlineData("not-a-machine")]
    [InlineData("")]
    [InlineData("123")]
    public void RejectsMachineIdsThatAreNotStable(string machine) =>
        Assert.Null(DesktopTrial.Hash(machine));

    [Fact]
    public void SameComputerKeepsTheSameHash()
    {
        var id = "8f1c2a40-6b7e-4d2a-9c11-0a1b2c3d4e5f";
        Assert.Equal(DesktopTrial.Hash(id), DesktopTrial.Hash(id.ToUpperInvariant()));
    }
}
