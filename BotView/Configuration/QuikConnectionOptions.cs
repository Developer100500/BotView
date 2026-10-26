using System.IO;
using System.Text.Json;

namespace BotView.Configuration;

public sealed record QuikConnectionOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 34130;
    public int RequestTimeoutSeconds { get; init; } = 5;

    public static QuikConnectionOptions Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "quik.json");
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<QuikConnectionOptions>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The quik.json file contains no connection settings.")
            : new QuikConnectionOptions();
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65534 || RequestTimeoutSeconds is < 1 or > 300)
            throw new InvalidDataException("Check quik.json: Host must not be empty, Port must be between 1 and 65534, and RequestTimeoutSeconds between 1 and 300.");
    }
}
