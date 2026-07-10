using Microsoft.Extensions.Configuration;
using SmtpRelay.Configuration;

namespace SmtpRelay.Cli;

/// <summary>Costruisce la configurazione e le opzioni, condivise da host e CLI.</summary>
public static class ConfigLoader
{
    public static IConfiguration Build(string basePath, string[] args)
    {
        var environment =
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            "Production";

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
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
}
