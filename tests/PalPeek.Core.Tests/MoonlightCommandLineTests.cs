using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class MoonlightCommandLineTests
{
    [Fact]
    public void PairUsesCallerProvidedPin()
    {
        var arguments = MoonlightCommandLine.Pair("100.64.0.2", "0042");

        Assert.Equal(["pair", "100.64.0.2", "--pin", "0042"], arguments);
    }

    [Fact]
    public void GeneratedPairingPinAlwaysContainsFourAsciiDigits()
    {
        for (var i = 0; i < 256; i++)
        {
            var pin = MoonlightCommandLine.GeneratePairingPin();

            Assert.Equal(4, pin.Length);
            Assert.All(pin, character => Assert.True(char.IsAsciiDigit(character)));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("１２３４")]
    [InlineData("12a4")]
    public void PairRejectsInvalidPin(string pin)
    {
        Assert.Throws<ArgumentException>(
            () => MoonlightCommandLine.Pair("100.64.0.2", pin));
    }

    [Fact]
    public void ListTargetsTheRequestedHost()
    {
        Assert.Equal(["list", "100.64.0.2"], MoonlightCommandLine.List("100.64.0.2"));
    }

    [Fact]
    public void StreamUsesMoonlightSixCompatibleOptions()
    {
        var arguments = MoonlightCommandLine.Stream("100.64.0.2", StreamQuality.P720_60);

        Assert.Equal(MoonlightCommandLine.AppName, arguments[2]);
        Assert.Equal("H.264", ValueAfter(arguments, "--video-codec"));
        Assert.Contains("--no-hdr", arguments);
        Assert.DoesNotContain("H264", arguments);
        Assert.DoesNotContain("--hdr", arguments);
        Assert.Equal("1024", ValueAfter(arguments, "--packet-size"));
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
