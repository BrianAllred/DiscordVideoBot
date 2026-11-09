using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using DiscordVideoBot.Services;
using static DiscordVideoBot.Utilities.Enums;

namespace DiscordVideoBot.Workers;

public class DownloadManager
{
    private readonly Channel<DownloadInfo> downloads;
    private readonly ulong userId;
    private readonly IDiscordClient client;
    private readonly ILogger logger;
    private readonly S3StorageService s3;

    public DownloadManager(IDiscordClient client, ulong userId, int queueLimit, S3StorageService s3, ILogger logger)
    {
        this.client = client;
        this.userId = userId;
        this.s3 = s3;
        this.logger = logger;

        // The bounded channel enforces the queue limit, and its single reader keeps each user's downloads sequential.
        downloads = Channel.CreateBounded<DownloadInfo>(queueLimit);
        _ = Task.Run(ProcessDownloads);
    }

    public DownloadQueueStatus QueueDownload(DownloadInfo download)
    {
        if (!Uri.TryCreate(download.VideoUrl, UriKind.Absolute, out var uriResult) || !(uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)) return DownloadQueueStatus.InvalidUrl;

        return downloads.Writer.TryWrite(download) ? DownloadQueueStatus.Success : DownloadQueueStatus.QueueFull;
    }

    private async Task ProcessDownloads()
    {
        await foreach (var download in downloads.Reader.ReadAllAsync())
        {
            await Download(download);
        }
    }

    /// <summary>
    /// Sends an informational (non-result) message only the requester sees.
    /// Goes through <paramref name="interaction"/> (default: the /download command) while its token is valid, ephemeral in guilds.
    /// Otherwise it goes to the user's DMs, replying to the request if it was made there.
    /// </summary>
    private async Task SendInfo(DownloadInfo download, string text, MessageComponent? components = null, SocketInteraction? interaction = null)
    {
        if (await TryFollowup(interaction ?? download.Command, i => i.FollowupAsync(text, components: components, ephemeral: !i.IsDMInteraction)))
            return;

        var user = await client.GetUserAsync(download.UserId);
        if (user == null) return;
        var dmChannel = await user.CreateDMChannelAsync();
        await dmChannel.SendMessageAsync(text,
            messageReference: download.IsDm && download.ReplyId != 0 ? new MessageReference(download.ReplyId) : null,
            components: components);
    }

    // Interaction tokens expire after 15 minutes. Returns false when the interaction can't be used, so the caller falls back.
    private async Task<bool> TryFollowup(SocketInteraction? interaction, Func<SocketInteraction, Task> followup)
    {
        if (interaction is not { IsValidToken: true }) return false;

        try
        {
            await followup(interaction);
            return true;
        }
        // Only an expired token falls back.
        catch (HttpException ex) when (ex.DiscordCode is DiscordErrorCode.InvalidWebhookToken or DiscordErrorCode.UnknownWebhook)
        {
            logger.LogInformation("Interaction no longer usable ({Error}), falling back to a normal message", ex.Message);
            return false;
        }
    }

    // Error replies must not throw, or they'd hide the original error.
    private async Task ReplyError(DownloadInfo download, string text, SocketInteraction? interaction = null)
    {
        try
        {
            await SendInfo(download, text, interaction: interaction);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send error reply");
        }
    }

    // Must not throw: that would end ProcessDownloads and strand the user's queue.
    private async Task Download(DownloadInfo download)
    {
        // Every file this download creates (yt-dlp parts, the video, transcode output) starts with this prefix.
        var filePrefix = $"{userId}_{Guid.NewGuid().ToString("N")[..8]}";
        var awaitingChoice = false;

        try
        {
            var downloadProcInfo = new ProcessStartInfo("yt-dlp")
            {
                // adding remote-components here as per https://github.com/yt-dlp/yt-dlp/wiki/EJS
                // NOTE that this adds a dependency on deno or similar EJS runtime
                Arguments = $"-f \"bv*+ba/b\" --remote-components ejs:github -o {filePrefix}.%(ext)s {download.VideoUrl}",
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var downloadProc = new Process
            {
                StartInfo = downloadProcInfo,
                EnableRaisingEvents = true
            };

            var output = string.Empty;
            downloadProc.ErrorDataReceived += (sender, o) =>
            {
                output += o.Data;
                logger.LogError(o.Data);
            };
            downloadProc.OutputDataReceived += (sender, o) =>
            {
                output += o.Data;
                logger.LogInformation(o.Data);
            };

            downloadProc.Start();
            downloadProc.BeginErrorReadLine();
            downloadProc.BeginOutputReadLine();
            await downloadProc.WaitForExitAsync();

            if (output.Contains("Unsupported URL"))
            {
                var replyBuilder = new StringBuilder("Failed to find video, are you sure this website/format is supported?\n\n");
                replyBuilder.AppendLine("Please check the list of supported sites [here](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md).");
                await SendInfo(download, replyBuilder.ToString());
                return;
            }

            var filePath = Directory.GetFiles("./").First(file => file.StartsWith($"./{filePrefix}"))[2..];
            var needTranscode = NeedTranscode(new FileInfo(filePath), download.FileSizeLimit);

            if (needTranscode && s3.IsEnabled)
            {
                var pendingId = Guid.NewGuid().ToString("N")[..8];
                PendingTranscodeChoices[pendingId] = new PendingTranscode
                {
                    FilePath = filePath,
                    Download = download
                };
                awaitingChoice = true;
                _ = ExpirePendingChoice(pendingId);

                var builder = new ComponentBuilder()
                    .WithButton("Transcode for Discord", $"transcode:{pendingId}", ButtonStyle.Primary)
                    .WithButton("Get original", $"original:{pendingId}", ButtonStyle.Secondary);

                await SendInfo(download,
                    "This video needs transcoding to send via Discord.\n\nTranscoding will reduce quality, especially for large/long videos.\n\nWhat would you like to do?",
                    builder.Build());
                return;
            }

            if (needTranscode)
            {
                await SendInfo(download, $"Video `{download.VideoUrl}` must be transcoded, please wait.");
                filePath = await TranscodeVideo(filePath, download.FileSizeLimit);
            }

            await SendVideo(download, filePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ex.Message);
            var replyBuilder = new StringBuilder($"Sorry, something went wrong downloading `{download.VideoUrl}`. I can't (currently!) access private videos, so please make sure it's available to the public.\n\n");
            replyBuilder.AppendLine("If that's not the problem, contact the bot owner for more help.");
            await ReplyError(download, replyBuilder.ToString());
        }
        finally
        {
            // A file waiting for the user's choice now belongs to that choice.
            if (!awaitingChoice) DeleteFiles(filePrefix);
        }
    }

    private async Task SendVideo(DownloadInfo download, string filePath)
    {
        // The /download reply is ephemeral, so the video goes out as its own followup that everyone sees.
        if (await TryFollowup(download.Command, i => i.FollowupWithFileAsync(filePath)))
            return;

        var channel = await client.GetChannelAsync(download.ChannelId) as IMessageChannel;
        if (channel == null) throw new Exception("Channel not found");

        await channel.SendFileAsync(filePath, messageReference: download.ReplyId != 0 ? new MessageReference(download.ReplyId) : null);
    }

    /// <summary>
    /// The caller must have removed <paramref name="pending"/> from <see cref="PendingTranscodeChoices"/>.
    /// <paramref name="choice"/> is the button press, used to reply to the requester.
    /// </summary>
    public async Task HandleTranscodeChoice(PendingTranscode pending, bool transcode, SocketInteraction choice)
    {
        var download = pending.Download;
        try
        {
            if (transcode)
            {
                await SendVideo(download, await TranscodeVideo(pending.FilePath, download.FileSizeLimit));
                return;
            }

            var objectKey = $"{download.UserId}/{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Path.GetFileName(pending.FilePath)}";
            var presignedUrl = await s3.UploadAndGetPresignedUrl(pending.FilePath, objectKey);
            var expiryDays = s3.PresignExpiryDays;

            // Anyone with the link can download the video, so it only goes to the requester.
            await SendInfo(download, $"Here's your original video. This link expires in {expiryDays} day{(expiryDays != 1 ? "s" : "")}:\n{presignedUrl}", interaction: choice);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle transcode choice");
            await ReplyError(download, "Sorry, something went wrong processing your choice.", choice);
        }
        finally
        {
            DeleteFiles(Path.GetFileNameWithoutExtension(pending.FilePath));
        }
    }

    public static readonly ConcurrentDictionary<string, PendingTranscode> PendingTranscodeChoices = new();

    // The prompt may be ephemeral and vanish, so an unanswered choice must not keep its file forever.
    private static readonly TimeSpan PendingChoiceExpiry = TimeSpan.FromHours(1);

    private async Task ExpirePendingChoice(string pendingId)
    {
        await Task.Delay(PendingChoiceExpiry);
        // TryRemove loses to the button handler if the user already chose, so a file in use is never deleted here.
        if (PendingTranscodeChoices.TryRemove(pendingId, out var pending))
            DeleteFiles(Path.GetFileNameWithoutExtension(pending.FilePath));
    }

    private void DeleteFiles(string filePrefix)
    {
        foreach (var file in Directory.GetFiles(".", $"{filePrefix}*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete {File}", file);
            }
        }
    }

    // Pending choices only live in memory, so download files left over from a previous run are orphaned.
    public static void DeleteLeftoverFiles()
    {
        foreach (var file in Directory.GetFiles(".").Where(file => Regex.IsMatch(Path.GetFileName(file), @"^\d+_[0-9a-f]{8}[._]")))
        {
            File.Delete(file);
        }
    }

    // https://unix.stackexchange.com/questions/520597/how-to-reduce-the-size-of-a-video-to-a-target-size
    // Returns the path of the transcoded video.
    private async Task<string> TranscodeVideo(string filePath, int fileSizeLimit)
    {
        var newFilePath = $"{Path.GetFileNameWithoutExtension(filePath)}_new.mp4";

        var targetSizeInKiloBits = (fileSizeLimit - 5) * 1000 * 8;
        var mediaInfo = await FFProbe.AnalyseAsync(filePath);
        var totalBitRate = (targetSizeInKiloBits / mediaInfo.Duration.TotalSeconds) + 1;
        var audioBitRate = 128;
        var videoBitRate = (int)(totalBitRate - audioBitRate);

        try
        {
            if (File.Exists(newFilePath))
            {
                File.Delete(newFilePath);
            }

            await FFMpegArguments.FromFileInput(filePath, false, options => options
                        .WithHardwareAcceleration())
                        .OutputToFile(newFilePath, false, options => options
                        .WithVideoCodec(VideoCodec.LibX264)
                        .WithAudioCodec(AudioCodec.Aac)
                        .WithArgument(new CustomArgument($"-maxrate:v {videoBitRate}k"))
                        .WithArgument(new CustomArgument($"-bufsize:v {targetSizeInKiloBits * 1000 / 20}"))
                        .WithFastStart())
                        .NotifyOnStandardError((err) => { logger.LogError(err); })
                        .NotifyOnStandardOutput((output) => { logger.LogInformation(output); })
                        .ProcessAsynchronously();
        }
        catch (FFMpegException ex)
        {
            logger.LogError(ex, ex.Message);
        }

        if (Path.GetExtension(filePath) != ".mp4")
        {
            File.Delete(filePath);
            filePath = $"{Path.GetFileNameWithoutExtension(filePath)}.mp4";
        }

        File.Move(newFilePath, filePath, true);
        return filePath;
    }

    // Check if video needs to be transcoded.
    // NOTE that ALL (tested) iOS and some Mac devices need videos to be h264/aac
    // Also check file size and container format
    private bool NeedTranscode(FileInfo fileInfo, int fileSizeLimit)
    {
        var mediaInfo = FFProbe.Analyse(fileInfo.Name);
        if (mediaInfo == null || mediaInfo.PrimaryAudioStream == null || mediaInfo.PrimaryVideoStream == null)
        {
            throw new Exception("Unable to analyze video metadata");
        }

        return fileInfo.Extension != ".mp4"
        || mediaInfo.PrimaryVideoStream.CodecName != "h264"
        || mediaInfo.PrimaryAudioStream.CodecName != "aac"
        || fileInfo.Length > fileSizeLimit * 1000 * 1000;
    }
}
