using System.Globalization;
using System.Security.Cryptography;

namespace PalPeek.Core;

public static class MoonlightCommandLine
{
    public const string AppName = "PalPeek Watch";

    public static string GeneratePairingPin() =>
        RandomNumberGenerator.GetInt32(10_000).ToString("D4", CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> Pair(string host, string pin)
    {
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
            throw new ArgumentException("Pairing PIN must contain four digits.", nameof(pin));

        return ["pair", host, "--pin", pin];
    }

    public static IReadOnlyList<string> Stream(string host, StreamQuality quality)
    {
        var resolution = quality == StreamQuality.P1080_60 ? "1920x1080" : "1280x720";
        var bitrate = quality == StreamQuality.P1080_60 ? "8000" : "4000";
        return
        [
            "stream",
            host,
            AppName,
            "--resolution", resolution,
            "--fps", "60",
            "--bitrate", bitrate,
            "--packet-size", "1024",
            "--video-codec", "H.264",
            "--no-hdr",
            "--display-mode", "windowed"
        ];
    }

}
