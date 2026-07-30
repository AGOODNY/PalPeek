using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace PalPeek.Core;

public sealed record SteamApp(uint AppId, string Name, string InstallDirectory);

public sealed class SteamCatalog
{
    private static readonly Regex PairRegex =
        new("\"(?<key>(?:\\\\.|[^\"])*)\"\\s+\"(?<value>(?:\\\\.|[^\"])*)\"",
            RegexOptions.Compiled);

    private readonly Dictionary<string, SteamApp> _byInstallDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<SteamApp> Apps => _byInstallDirectory.Values;

    public static SteamCatalog Discover()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamPath = ReadSteamPath();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            roots.Add(Path.GetFullPath(steamPath));
            var libraries = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraries))
            {
                foreach (var pair in ParsePairs(File.ReadAllText(libraries)))
                {
                    if (pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase))
                        roots.Add(Path.GetFullPath(pair.Value.Replace(@"\\", @"\")));
                }
            }
        }

        return FromLibraries(roots);
    }

    public static SteamCatalog FromLibraries(IEnumerable<string> libraryRoots)
    {
        var catalog = new SteamCatalog();
        foreach (var root in libraryRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamApps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamApps))
                continue;

            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var pairs = ParsePairs(File.ReadAllText(manifest))
                        .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
                    if (!pairs.TryGetValue("appid", out var idText) ||
                        !uint.TryParse(idText, out var appId) ||
                        !pairs.TryGetValue("installdir", out var installDir))
                        continue;

                    var name = pairs.GetValueOrDefault("name") ?? $"Steam {appId}";
                    var fullPath = Normalize(Path.Combine(steamApps, "common", installDir));
                    catalog._byInstallDirectory[fullPath] = new SteamApp(appId, name, fullPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return catalog;
    }

    public SteamApp? MatchExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;
        var normalized = Normalize(executablePath);
        return _byInstallDirectory
            .Where(pair => IsUnder(normalized, pair.Key))
            .OrderByDescending(pair => pair.Key.Length)
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }

    internal SteamApp? MatchExecutableName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return null;
        var fileName = Path.GetFileName(executableName);
        if (!fileName.Equals(executableName, StringComparison.Ordinal) ||
            !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        var matches = _byInstallDirectory.Values
            .Where(app => ContainsExecutable(app.InstallDirectory, fileName))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal SteamApp? FindByAppId(uint appId) =>
        _byInstallDirectory.Values.FirstOrDefault(app => app.AppId == appId);

    internal static IReadOnlyList<KeyValuePair<string, string>> ParsePairs(string text) =>
        PairRegex.Matches(text)
            .Select(m => new KeyValuePair<string, string>(
                Unescape(m.Groups["key"].Value),
                Unescape(m.Groups["value"].Value)))
            .ToArray();

    private static string? ReadSteamPath()
    {
        foreach (var keyPath in new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"
        })
        {
            var value = Registry.GetValue(keyPath, "SteamPath", null) as string
                        ?? Registry.GetValue(keyPath, "InstallPath", null) as string;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static bool IsUnder(string file, string directory) =>
        file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool ContainsExecutable(string installDirectory, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(
                    installDirectory,
                    fileName,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MatchCasing = MatchCasing.CaseInsensitive,
                        AttributesToSkip =
                            FileAttributes.Hidden |
                            FileAttributes.System |
                            FileAttributes.ReparsePoint
                    })
                .Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Unescape(string value) =>
        value.Replace(@"\\", @"\").Replace("\\\"", "\"");
}
