using Discord;
using Discord.WebSocket;
using DiscordVideoBot.Utilities;
using DiscordVideoBot.Workers;

namespace DiscordVideoBot.Services;

public class DiscordBotService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly MessageHandler _messageHandler;
    private readonly EnvironmentConfig _config;
    private readonly ILogger<DiscordBotService> _logger;

    // Ready fires again after reconnects, but the commands only need registering once per process.
    private bool _commandsRegistered;

    public DiscordBotService(
        DiscordSocketClient client,
        MessageHandler messageHandler,
        EnvironmentConfig config,
        ILogger<DiscordBotService> logger)
    {
        _client = client;
        _messageHandler = messageHandler;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.Log += LogAsync;
        _client.MessageReceived += _messageHandler.HandleMessage;
        _client.ButtonExecuted += _messageHandler.HandleButtonAsync;
        _client.SlashCommandExecuted += _messageHandler.HandleSlashCommand;
        _client.Ready += RegisterCommandsAsync;

        await _client.LoginAsync(TokenType.Bot, _config.DiscordBotToken);
        await _client.StartAsync();

        _logger.LogInformation("Discord bot started");

        // Wait until the service is stopped
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Log -= LogAsync;
        _client.MessageReceived -= _messageHandler.HandleMessage;
        _client.ButtonExecuted -= _messageHandler.HandleButtonAsync;
        _client.SlashCommandExecuted -= _messageHandler.HandleSlashCommand;
        _client.Ready -= RegisterCommandsAsync;

        await _client.StopAsync();
        await base.StopAsync(cancellationToken);
    }

    // Slash commands can be answered with ephemeral messages, which only the requester sees.
    private async Task RegisterCommandsAsync()
    {
        if (_commandsRegistered) return;

        try
        {
            await _client.BulkOverwriteGlobalApplicationCommandsAsync(
            [
                new SlashCommandBuilder()
                    .WithName("download")
                    .WithDescription("Download a video from a URL")
                    .AddOption("url", ApplicationCommandOptionType.String, "Video URL (separate multiple URLs with spaces)", isRequired: true)
                    .Build(),
                new SlashCommandBuilder()
                    .WithName("help")
                    .WithDescription("Show help")
                    .Build()
            ]);
            _commandsRegistered = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands");
        }
    }

    private Task LogAsync(LogMessage log)
    {
        var logLevel = log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(logLevel, log.Exception, "[{Source}] {Message}", log.Source, log.Message);
        return Task.CompletedTask;
    }
}
