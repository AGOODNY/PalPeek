using PalPeek.Core;
using System.Text.Json;

namespace PalPeek;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalPeek", "config.json");

    public PalPeekOptions Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<PalPeekOptions>(File.ReadAllText(_path), JsonOptions)
                       ?? new PalPeekOptions();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        var options = new PalPeekOptions();
        Save(options);
        return options;
    }

    public void Save(PalPeekOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(options, JsonOptions));
    }
}
