using System.Text.Json;
using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SpectralTV;

/// <summary>
/// Registers SpectralTV services with the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        ConfigureJsonOptions(serviceCollection);

        serviceCollection.AddDbContext<SpectralTvDbContext>((sp, options) =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("SpectralTV plugin not initialized.");
            Directory.CreateDirectory(plugin.DataFolder);
            options.UseSqlite($"Data Source={plugin.DatabasePath}");
        });

        serviceCollection.AddScoped<ChannelService>();
        serviceCollection.AddScoped<WeightedProgrammingService>();
        serviceCollection.AddScoped<OnDemandSequenceService>();
        serviceCollection.AddScoped<LineupGeneratorService>();
        serviceCollection.AddScoped<EpgService>();
        serviceCollection.AddScoped<GuideMetadataService>();
        serviceCollection.AddScoped<LogoSetService>();
        serviceCollection.AddSingleton<Streaming.JellyfinFfmpegEncodingService>();
        serviceCollection.AddSingleton<StreamService>();
        serviceCollection.AddSingleton<Streaming.FfmpegCommandBuilder>();
        serviceCollection.AddSingleton<PlayoutBuilderService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlayoutBuilderService>());
        serviceCollection.AddHostedService<DatabaseInitializer>();
    }

    private static void ConfigureJsonOptions(IServiceCollection serviceCollection)
    {
        serviceCollection.Configure<JsonOptions>(options =>
        {
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.AllowTrailingCommas = true;
        });

        serviceCollection.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.AllowTrailingCommas = true;
        });
    }
}
