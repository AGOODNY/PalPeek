using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class MoonlightProfileTests
{
    [Fact]
    public void ReadsCertificateForMatchingManualAddress()
    {
        const string settings = """
            [hosts]
            1\hostname=NY
            1\manualaddress=100.64.0.2
            1\srvcert=@ByteArray(-----BEGIN CERTIFICATE-----\nabc\n-----END CERTIFICATE-----)
            2\manualaddress=100.64.0.3
            2\srvcert=@ByteArray(-----BEGIN CERTIFICATE-----\nother\n-----END CERTIFICATE-----)
            size=2
            """;

        var certificate = MoonlightProfile.GetHostCertificate(settings, "100.64.0.2");

        Assert.NotNull(certificate);
        Assert.Contains("abc", certificate);
    }

    [Fact]
    public void RejectsEmptyCertificateAndCertificateForAnotherHost()
    {
        const string settings = """
            [hosts]
            1\manualaddress=100.64.0.2
            1\srvcert=@ByteArray()
            2\manualaddress=100.64.0.3
            2\srvcert=@ByteArray(-----BEGIN CERTIFICATE-----\nother\n-----END CERTIFICATE-----)
            size=2
            """;

        Assert.Null(MoonlightProfile.GetHostCertificate(settings, "100.64.0.2"));
    }

    [Fact]
    public void MatchesAnyStoredAddressType()
    {
        const string settings = """
            [hosts]
            1\ipv6address=fd7a:115c:a1e0::1234
            1\srvcert=@ByteArray(certificate-data)
            size=1
            """;

        Assert.NotNull(MoonlightProfile.GetHostCertificate(
            settings,
            "fd7a:115c:a1e0::1234"));
    }
}
