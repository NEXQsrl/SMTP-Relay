using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace SmtpRelay.Configuration;

/// <summary>Builds and saves the configuration shared by the service, the CLI and the management console.</summary>
public static class ConfigLoader
{
    public const string FileName = "appsettings.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IConfiguration Build(string basePath)
    {
        var environment =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            "Production";

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile(FileName, optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "SMTPRELAY_")
            .Build();
    }

    public static RelayOptions BindOptions(IConfiguration configuration)
    {
        var options = new RelayOptions();
        configuration.GetSection(RelayOptions.SectionName).Bind(options);
        return options;
    }

    public static RelayOptions Load(string basePath) => BindOptions(Build(basePath));

    /// <summary>
    /// Writes the options to appsettings.json, keeping any other root section of the file.
    /// The file is replaced atomically.
    /// </summary>
    public static void Save(string basePath, RelayOptions options)
    {
        var path = Path.Combine(basePath, FileName);

        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        root[RelayOptions.SectionName] = JsonSerializer.SerializeToNode(options, WriteOptions);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
