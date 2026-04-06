using Discord;
using Discord.WebSocket;
using DiscordVideoBot.Services;
using DiscordVideoBot.Utilities;
using DiscordVideoBot.Workers;

internal class Program
{
    private static void Main(string[] args)
    {
        var config = new EnvironmentConfig();

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<YtDlp>();
        builder.Services.AddSingleton<S3StorageService>();

        var discordConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
                | GatewayIntents.GuildMessages
                | GatewayIntents.DirectMessages
                | GatewayIntents.MessageContent,
            MessageCacheSize = 100
        };

        builder.Services.AddSingleton(discordConfig);
        builder.Services.AddSingleton<DiscordSocketClient>();
        builder.Services.AddSingleton<MessageHandler>();
        builder.Services.AddHostedService<DiscordBotService>();

        var app = builder.Build();

        app.Services.GetRequiredService<YtDlp>();

        app.UseHttpsRedirection();

        app.UseAuthorization();

        app.MapControllers();

        app.Run();
    }
}
