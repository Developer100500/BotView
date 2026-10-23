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
                ?? throw new InvalidDataException("Файл quik.json не содержит настроек подключения.")
            : new QuikConnectionOptions();
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65534 || RequestTimeoutSeconds is < 1 or > 300)
            throw new InvalidDataException("Проверьте quik.json: Host не должен быть пустым, Port — от 1 до 65534, RequestTimeoutSeconds — от 1 до 300.");
    }
}
