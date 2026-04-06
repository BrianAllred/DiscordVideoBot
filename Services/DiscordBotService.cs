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

        await _client.StopAsync();
        await base.StopAsync(cancellationToken);
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
