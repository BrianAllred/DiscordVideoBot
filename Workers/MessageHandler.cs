using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.WebSocket;
using DiscordVideoBot.Services;
using DiscordVideoBot.Utilities;

namespace DiscordVideoBot.Workers;

public class MessageHandler
{
    private readonly ConcurrentDictionary<ulong, DownloadManager> downloadManagers = new();
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

        const string choiceGone = "This choice is no longer available.";

        if (!DownloadManager.PendingTranscodeChoices.TryGetValue(pendingId, out var pending))
        {
            await component.RespondAsync(choiceGone, ephemeral: true);
            return;
        }

        var userId = pending.Download.UserId;
        if (component.User.Id != userId)
        {
            await component.RespondAsync("Only the original requester can do this.", ephemeral: true);
            return;
        }

        // Taking the choice stops the expiry from deleting the file while it's in use, and makes a double click a no-op.
        if (!DownloadManager.PendingTranscodeChoices.TryRemove(pendingId, out _))
        {
            await component.RespondAsync(choiceGone, ephemeral: true);
            return;
        }

        var isTranscode = action == "transcode";

        // Acknowledge the interaction by swapping the buttons for a status,
        // then do the heavy work off the gateway task
        try
        {
            await component.UpdateAsync(msg =>
            {
                msg.Content = isTranscode ? "Transcoding, please wait." : "Uploading the original video, please wait.";
                msg.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            // The choice is already taken, so carry on: replies fall back to DMs.
            logger.LogError(ex, "Failed to update the transcode prompt");
        }

        _ = Task.Run(() => GetManager(userId).HandleTranscodeChoice(pending, isTranscode, component));
    }

    public Task HandleSlashCommand(SocketSlashCommand command)
    {
        // Fire off actual work on a background thread to avoid blocking the gateway
        _ = Task.Run(async () =>
        {
            try
            {
                // Ephemeral replies are only seen by the requester. In DMs they'd just vanish on reload, so DMs get normal replies.
                var ephemeral = !command.IsDMInteraction;

                if (command.CommandName == "download")
                {
                    var reply = QueueDownloads(new DownloadInfo
                    {
                        ChannelId = command.ChannelId ?? 0,
                        UserId = command.User.Id,
                        FileSizeLimit = GetFileSizeLimit(command.GuildId),
                        IsDm = command.IsDMInteraction,
                        Command = command
                    }, (string)command.Data.Options.First().Value);
                    await command.RespondAsync(reply, ephemeral: ephemeral);
                    return;
                }

                var help = HelpMessages(command.GuildId);
                await command.RespondAsync(help[0], ephemeral: ephemeral);
                await command.FollowupAsync(help[1], ephemeral: ephemeral);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling slash command");
            }
        });

        return Task.CompletedTask;
    }

    private DownloadManager GetManager(ulong userId) =>
        downloadManagers.GetOrAdd(userId, id => new(client, id, config.DownloadQueueLimit, s3, logger));

    private async Task HandleDownload(SocketUserMessage message, string messageText)
    {
        var isDm = message.Channel is IDMChannel;
        var reply = QueueDownloads(new DownloadInfo
        {
            ChannelId = message.Channel.Id,
            ReplyId = message.Id,
            UserId = message.Author.Id,
            FileSizeLimit = GetFileSizeLimit((message.Channel as SocketGuildChannel)?.Guild.Id),
            IsDm = isDm
        }, messageText);

        // Messages can't get ephemeral replies, so guild requests get their status in DMs.
        if (isDm)
        {
            await message.ReplyAsync(reply);
        }
        else
        {
            var dmChannel = await message.Author.CreateDMChannelAsync();
            await dmChannel.SendMessageAsync(reply);
        }
    }

    // Queues every URL in the text and returns the status reply.
    private string QueueDownloads(DownloadInfo request, string messageText)
    {
        var downloadUrls = messageText.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (downloadUrls.Length == 0 || (downloadUrls.Length == 1 && (downloadUrls[0].StartsWith("/download") || downloadUrls[0].StartsWith("!download"))))
            return "No URL included in message.";

        if (downloadUrls[0].StartsWith("/download") || downloadUrls[0].StartsWith("!download"))
        {
            downloadUrls = downloadUrls[1..];
        }

        var manager = GetManager(request.UserId);

        var queueStatuses = new Dictionary<string, Enums.DownloadQueueStatus>();

        foreach (var url in downloadUrls)
        {
            if (!queueStatuses.ContainsKey(url))
            {
                queueStatuses.Add(url, manager.QueueDownload(request with { VideoUrl = url }));
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

        return replyBuilder.ToString();
    }

    private int GetFileSizeLimit(ulong? guildId)
    {
        if (guildId is { } id && client.GetGuild(id) is { } guild)
        {
            return guild.PremiumTier switch
            {
                PremiumTier.Tier2 => 50,
                PremiumTier.Tier3 => 100,
                _ => 10
            };
        }

        // DMs or unknown guilds fall back to the configured limit
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
        foreach (var text in HelpMessages((message.Channel as SocketGuildChannel)?.Guild.Id))
        {
            try
            {
                await message.ReplyAsync(text);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
            }
        }
    }

    private string[] HelpMessages(ulong? guildId)
    {
        var fileSizeLimit = GetFileSizeLimit(guildId);
        var replyBuilder = new StringBuilder($"Hello there, I'm {botName}! I download videos from URLs you send me and send them back to you as video files.");
        replyBuilder.AppendLine();
        replyBuilder.AppendLine();
        replyBuilder.AppendLine($"Please note that Discord limits me to {fileSizeLimit} MB attachments per message, so long videos may take longer to process due to compression. **Please be patient!**");
        replyBuilder.AppendLine();
        replyBuilder.AppendLine("To get started, send a message starting with `/download` followed by a URL to a video, and I'll do my best!");
        replyBuilder.AppendLine();
        replyBuilder.AppendLine("(I also work without the `/download` command in case you want to use a video app's share feature to send me a video URL directly!)");

        var limits = $"Also, each user can queue up to {config.DownloadQueueLimit} videos at a time. You can do this by sending multiple messages or alternatively sending multiple video links within the same message separated by line breaks or spaces.";

        return [replyBuilder.ToString(), limits];
    }
}
