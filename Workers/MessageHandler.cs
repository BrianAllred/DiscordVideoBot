using System.Text;
using Discord;
using Discord.WebSocket;
using DiscordVideoBot.Services;
using DiscordVideoBot.Utilities;

namespace DiscordVideoBot.Workers;

public class MessageHandler
{
    private readonly Dictionary<ulong, DownloadManager> downloadManagers = [];
    private readonly string botName;
    private readonly ILogger<MessageHandler> logger;
    private readonly EnvironmentConfig config;
    private readonly S3StorageService s3;
    private readonly DiscordSocketClient client;

    public MessageHandler(EnvironmentConfig config, S3StorageService s3, DiscordSocketClient client, ILogger<MessageHandler> logger)
    {
        this.config = config;
        this.s3 = s3;
        this.client = client;
        this.logger = logger;
        botName = config.BotName is { Length: > 0 } name ? name : "Frozen's Video Bot";
    }

    public Task HandleMessage(SocketMessage socketMessage)
    {
        if (socketMessage is not SocketUserMessage message) return Task.CompletedTask;
        if (message.Author.IsBot) return Task.CompletedTask;
        if (message.Author.Id == client.CurrentUser.Id) return Task.CompletedTask;

        var messageText = message.Content;
        if (string.IsNullOrWhiteSpace(messageText)) return Task.CompletedTask;

        // Only respond in DMs or when mentioned in a guild
        var isDm = message.Channel is IDMChannel;
        var isMentioned = !isDm && message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id);

        if (!isDm && !isMentioned) return Task.CompletedTask;

        // Strip the bot mention from the message text if mentioned in a guild
        if (isMentioned)
        {
            messageText = messageText
                .Replace($"<@{client.CurrentUser.Id}>", "")
                .Replace($"<@!{client.CurrentUser.Id}>", "")
                .Trim();
        }

        var splitMessage = messageText.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Fire off actual work on a background thread to avoid blocking the gateway
        _ = Task.Run(async () =>
        {
            try
            {
                // If the message has no URLs and is a reply, use URLs from the referenced message
                var referencedText = GetReferencedMessageUrls(message);

                if (splitMessage.Length < 1)
                {
                    if (referencedText != null)
                    {
                        await HandleDownload(message, referencedText);
                        return;
                    }
                    if (isMentioned) await HandleHelp(message);
                    return;
                }

                if (splitMessage[0].StartsWith("/download") || splitMessage[0].StartsWith("!download"))
                {
                    var downloadText = messageText;
                    // If the only content is the command itself with no URLs, check the reply
                    var commandArgs = splitMessage.Length > 1 ? splitMessage[1..] : [];
                    if (!commandArgs.Any(HasUrl) && referencedText != null)
                        downloadText = $"{splitMessage[0]} {referencedText}";
                    await HandleDownload(message, downloadText);
                    return;
                }

                if (splitMessage[0].StartsWith('/') || splitMessage[0].StartsWith('!'))
                {
                    await HandleHelp(message);
                    return;
                }

                // If the message has no URLs but is a reply, use the referenced message
                if (!splitMessage.Any(HasUrl) && referencedText != null)
                    messageText = referencedText;

                await HandleDownload(message, messageText);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling message");
            }
        });

        return Task.CompletedTask;
    }

    public async Task HandleButtonAsync(SocketMessageComponent component)
    {
        var data = component.Data.CustomId;

        var parts = data.Split(':', 2);
        if (parts.Length != 2) return;

        var action = parts[0];
        var pendingId = parts[1];

        if (action is not ("transcode" or "original")) return;

        if (!DownloadManager.PendingTranscodeChoices.TryGetValue(pendingId, out var pending))
        {
            await component.RespondAsync("This choice is no longer available.", ephemeral: true);
            return;
        }

        if (component.User.Id != pending.UserId)
        {
            await component.RespondAsync("Only the original requester can do this.", ephemeral: true);
            return;
        }

        // Acknowledge the interaction immediately by removing the buttons,
        // then do the heavy work off the gateway task
        await component.UpdateAsync(msg => msg.Components = new ComponentBuilder().Build());

        var isTranscode = action == "transcode";

        _ = Task.Run(async () =>
        {
            try
            {
                if (!downloadManagers.TryGetValue(pending.UserId, out var manager))
                {
                    manager = new(client, pending.UserId, config.DownloadQueueLimit, s3, logger);
                    downloadManagers[pending.UserId] = manager;
                }

                await manager.HandleTranscodeChoice(pendingId, isTranscode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to handle transcode choice");
                try
                {
                    await component.FollowupAsync("Sorry, something went wrong processing your choice.");
                }
                catch (Exception innerEx)
                {
                    logger.LogError(innerEx, "Failed to send error followup");
                }
            }
        });
    }

    private async Task HandleDownload(SocketUserMessage message, string messageText)
    {
        var userId = message.Author.Id;
        var isDm = message.Channel is IDMChannel;

        var downloadUrls = messageText.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (downloadUrls.Length == 0 || (downloadUrls.Length == 1 && (downloadUrls[0].StartsWith("/download") || downloadUrls[0].StartsWith("!download"))))
        {
            IMessageChannel noUrlChannel = isDm ? message.Channel : await message.Author.CreateDMChannelAsync();
            await noUrlChannel.SendMessageAsync("No URL included in message.",
                messageReference: isDm ? new MessageReference(message.Id) : null);
            return;
        }

        if (downloadUrls[0].StartsWith("/download") || downloadUrls[0].StartsWith("!download"))
        {
            downloadUrls = downloadUrls[1..];
        }

        if (!downloadManagers.TryGetValue(userId, out var manager))
        {
            manager = new(client, userId, config.DownloadQueueLimit, s3, logger);
            downloadManagers.Add(userId, manager);
        }

        var fileSizeLimit = GetFileSizeLimit(message.Channel);

        var queueStatuses = new Dictionary<string, Enums.DownloadQueueStatus>();

        foreach (var url in downloadUrls)
        {
            if (!queueStatuses.ContainsKey(url))
            {
                queueStatuses.Add(url, manager.QueueDownload(new DownloadInfo
                {
                    ChannelId = message.Channel.Id,
                    ReplyId = message.Id,
                    UserId = userId,
                    VideoUrl = url,
                    FileSizeLimit = fileSizeLimit,
                    IsDm = isDm
                }));
            }
        }

        var replyBuilder = new StringBuilder();
        if (queueStatuses.Values.Any(status => status == Enums.DownloadQueueStatus.Success))
        {
            replyBuilder.AppendLine("Queueing the following videos:");
            replyBuilder.AppendJoin('\n', queueStatuses.Where(pair => pair.Value == Enums.DownloadQueueStatus.Success).Select(pair => $"`{pair.Key}`"));
        }

        replyBuilder.AppendLine();

        if (queueStatuses.Values.Any(status => status == Enums.DownloadQueueStatus.InvalidUrl))
        {
            replyBuilder.AppendLine("The following video URLs are invalid:");
            replyBuilder.AppendJoin('\n', queueStatuses.Where(pair => pair.Value == Enums.DownloadQueueStatus.InvalidUrl).Select(pair => $"`{pair.Key}`"));
        }

        replyBuilder.AppendLine();

        if (queueStatuses.Values.Any(status => status == Enums.DownloadQueueStatus.QueueFull))
        {
            replyBuilder.AppendLine("The following video URLs weren't queued due to a full queue:");
            replyBuilder.AppendJoin('\n', queueStatuses.Where(pair => pair.Value == Enums.DownloadQueueStatus.QueueFull).Select(pair => $"`{pair.Key}`"));
        }

        replyBuilder.AppendLine();

        if (queueStatuses.Values.Any(status => status == Enums.DownloadQueueStatus.UnknownError))
        {
            replyBuilder.AppendLine("The following video URLs weren't queued due to an unknown error:");
            replyBuilder.AppendJoin('\n', queueStatuses.Where(pair => pair.Value == Enums.DownloadQueueStatus.UnknownError).Select(pair => $"`{pair.Key}`"));
        }

        if (isDm)
        {
            await message.ReplyAsync(replyBuilder.ToString());
        }
        else
        {
            var dmChannel = await message.Author.CreateDMChannelAsync();
            await dmChannel.SendMessageAsync(replyBuilder.ToString());
        }
    }

    private int GetFileSizeLimit(IChannel channel)
    {
        if (channel is SocketGuildChannel guildChannel)
        {
            return guildChannel.Guild.PremiumTier switch
            {
                PremiumTier.Tier2 => 50,
                PremiumTier.Tier3 => 100,
                _ => 10
            };
        }

        // DMs or unknown channel types fall back to the configured limit
        return config.FileSizeLimit;
    }

    private static bool HasUrl(string token) =>
        Uri.TryCreate(token, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// If the message is a reply to another message, extract any URLs from the referenced message.
    /// Returns a space-separated string of URLs, or null if there are none.
    /// </summary>
    private static string? GetReferencedMessageUrls(SocketUserMessage message)
    {
        if (message.ReferencedMessage is not { Content: { Length: > 0 } content })
            return null;

        var urls = content
            .Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(HasUrl)
            .ToArray();

        return urls.Length > 0 ? string.Join(' ', urls) : null;
    }

    private async Task HandleHelp(SocketUserMessage message)
    {
        var fileSizeLimit = GetFileSizeLimit(message.Channel);
        var replyBuilder = new StringBuilder($"Hello there, I'm {botName}! I download videos from URLs you send me and send them back to you as video files.");
        replyBuilder.AppendLine();
        replyBuilder.AppendLine();
        replyBuilder.AppendLine($"Please note that Discord limits me to {fileSizeLimit} MB attachments per message, so long videos may take longer to process due to compression. **Please be patient!**");
        replyBuilder.AppendLine();
        replyBuilder.AppendLine("To get started, send a message starting with `/download` followed by a URL to a video, and I'll do my best!");
        replyBuilder.AppendLine();
        replyBuilder.AppendLine("(I also work without the `/download` command in case you want to use a video app's share feature to send me a video URL directly!)");

        try
        {
            await message.ReplyAsync(replyBuilder.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ex.Message);
        }

        replyBuilder = new StringBuilder($"Also, each user can queue up to {config.DownloadQueueLimit} videos at a time. You can do this by sending multiple messages or alternatively sending multiple video links within the same message separated by line breaks or spaces.");

        try
        {
            await message.ReplyAsync(replyBuilder.ToString());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ex.Message);
        }
    }
}
