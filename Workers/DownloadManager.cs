using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Discord;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using DiscordVideoBot.Services;
using static DiscordVideoBot.Utilities.Enums;

namespace DiscordVideoBot.Workers;

public class DownloadManager(IDiscordClient client, ulong userId, int queueLimit, S3StorageService s3, ILogger logger)
{
    private readonly ConcurrentQueue<DownloadInfo> downloads = new();
    private readonly ulong userId = userId;
    private readonly IDiscordClient client = client;
    private readonly int queueLimit = queueLimit;
    private readonly ILogger logger = logger;
    private readonly S3StorageService s3 = s3;

    private bool downloading;

    public DownloadQueueStatus QueueDownload(DownloadInfo download)
    {
        if (downloads.Count >= queueLimit) return DownloadQueueStatus.QueueFull;

        if (!Uri.TryCreate(download.VideoUrl, UriKind.Absolute, out var uriResult) || !(uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)) return DownloadQueueStatus.InvalidUrl;

        downloads.Enqueue(download);

        _ = Task.Run(() => StartDownloads());

        return DownloadQueueStatus.Success;
    }

    /// Get the channel to send informational (non-result) messages to.
    /// In DMs, this is the same channel. In guilds, this is the user's DM channel.
    private async Task<IMessageChannel?> GetInfoChannel(DownloadInfo download)
    {
        if (download.IsDm)
            return await client.GetChannelAsync(download.ChannelId) as IMessageChannel;

        var user = await client.GetUserAsync(download.UserId);
        if (user == null) return null;
        return await user.CreateDMChannelAsync();
    }

    private async Task<IMessageChannel?> GetInfoChannelForPending(PendingTranscode pending)
    {
        if (pending.IsDm)
            return await client.GetChannelAsync(pending.ChannelId) as IMessageChannel;

        var user = await client.GetUserAsync(pending.UserId);
        if (user == null) return null;
        return await user.CreateDMChannelAsync();
    }

    private async Task StartDownloads()
    {
        if (downloading) return;

        while (downloads.TryDequeue(out var download))
        {
            try
            {
                downloading = true;
                foreach (var existingFile in Directory.GetFiles("./").Where(file => file.StartsWith($"./{userId}")))
                {
                    var path = existingFile[2..];
                    // Don't delete files that have a pending transcode choice
                    if (PendingTranscodeChoices.Values.Any(p => p.FilePath == path))
                        continue;
                    if (File.Exists(path))
                        File.Delete(path);
                }

                var downloadId = Guid.NewGuid().ToString("N")[..8];
                var downloadProcInfo = new ProcessStartInfo("yt-dlp")
                {
                    // adding remote-components here as per https://github.com/yt-dlp/yt-dlp/wiki/EJS
                    // NOTE that this adds a dependency on deno or similar EJS runtime
                    Arguments = $"-f \"bv*+ba/b\" --remote-components ejs:github -o {userId}_{downloadId}.%(ext)s {download.VideoUrl}",
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
                    var infoChannel = await GetInfoChannel(download);
                    if (infoChannel != null)
                    {
                        var replyBuilder = new StringBuilder("Failed to find video, are you sure this website/format is supported?\n\n");
                        replyBuilder.AppendLine("Please check the list of supported sites [here](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md).");
                        await infoChannel.SendMessageAsync(replyBuilder.ToString(),
                            messageReference: download.IsDm ? new MessageReference(download.ReplyId) : null);
                    }
                    continue;
                }

                var filePath = Directory.GetFiles("./").First(file => file.StartsWith($"./{userId}_{downloadId}"))[2..];

                var videoFileInfo = new FileInfo(filePath);

                if (NeedTranscode(videoFileInfo, download.FileSizeLimit) && s3.IsEnabled)
                {
                    var pendingId = Guid.NewGuid().ToString("N")[..8];
                    PendingTranscodeChoices[pendingId] = new PendingTranscode
                    {
                        FilePath = filePath,
                        ChannelId = download.ChannelId,
                        ReplyId = download.ReplyId,
                        UserId = userId,
                        FileSizeLimit = download.FileSizeLimit,
                        IsDm = download.IsDm
                    };

                    var builder = new ComponentBuilder()
                        .WithButton("Transcode for Discord", $"transcode:{pendingId}", ButtonStyle.Primary)
                        .WithButton("Get original", $"original:{pendingId}", ButtonStyle.Secondary);

                    var infoChannel = await GetInfoChannel(download);
                    if (infoChannel != null)
                    {
                        await infoChannel.SendMessageAsync(
                            "This video needs transcoding to send via Discord.\n\nTranscoding will reduce quality, especially for large/long videos.\n\nWhat would you like to do?",
                            messageReference: download.IsDm ? new MessageReference(download.ReplyId) : null,
                            components: builder.Build());
                    }
                    continue;
                }

                if (NeedTranscode(videoFileInfo, download.FileSizeLimit))
                {
                    var infoChannel = await GetInfoChannel(download);
                    if (infoChannel != null)
                    {
                        await infoChannel.SendMessageAsync($"Video `{download.VideoUrl}` must be transcoded, please wait.",
                            messageReference: download.IsDm ? new MessageReference(download.ReplyId) : null);
                    }
                    TranscodeVideo(filePath, download.FileSizeLimit);
                    filePath = $"{Path.GetFileNameWithoutExtension(filePath)}.mp4";
                }

                await SendVideo(download.ChannelId, download.ReplyId, filePath);
            }
            catch (Exception ex)
            {
                try
                {
                    var infoChannel = await GetInfoChannel(download);
                    if (infoChannel != null)
                    {
                        var replyBuilder = new StringBuilder($"Sorry, something went wrong downloading `{download.VideoUrl}`. I can't (currently!) access private videos, so please make sure it's available to the public.\n\n");
                        replyBuilder.AppendLine("If that's not the problem, contact the bot owner for more help.");
                        await infoChannel.SendMessageAsync(replyBuilder.ToString(),
                            messageReference: download.IsDm ? new MessageReference(download.ReplyId) : null);
                    }
                }
                catch (Exception innerEx)
                {
                    logger.LogError(innerEx, innerEx.Message);
                }
                logger.LogError(ex, ex.Message);
            }
            finally
            {
                downloading = false;
            }
        }
    }

    private async Task SendVideo(ulong channelId, ulong replyId, string filePath)
    {
        var channel = await client.GetChannelAsync(channelId) as IMessageChannel;
        if (channel == null) throw new Exception("Channel not found");

        await channel.SendFileAsync(filePath, messageReference: new MessageReference(replyId));
        File.Delete(filePath);
    }

    public async Task HandleTranscodeChoice(string pendingId, bool transcode)
    {
        if (!PendingTranscodeChoices.TryRemove(pendingId, out var pending))
            throw new InvalidOperationException("Choice no longer available");

        var infoChannel = await GetInfoChannelForPending(pending);

        if (transcode)
        {
            if (infoChannel != null)
                await infoChannel.SendMessageAsync("Transcoding, please wait.");
            TranscodeVideo(pending.FilePath, pending.FileSizeLimit);
            var transcodedPath = $"{Path.GetFileNameWithoutExtension(pending.FilePath)}.mp4";
            await SendVideo(pending.ChannelId, pending.ReplyId, transcodedPath);
        }
        else
        {
            if (infoChannel != null)
                await infoChannel.SendMessageAsync("Uploading, please wait.");
            var objectKey = $"{pending.UserId}/{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Path.GetFileName(pending.FilePath)}";
            var presignedUrl = await s3.UploadAndGetPresignedUrl(pending.FilePath, objectKey);
            var expiryDays = s3.PresignExpiryDays;

            // Send the presigned URL to the original channel (it's the result, not informational)
            var channel = await client.GetChannelAsync(pending.ChannelId) as IMessageChannel;
            if (channel != null)
            {
                await channel.SendMessageAsync(
                    $"Here's your original video. This link expires in {expiryDays} day{(expiryDays != 1 ? "s" : "")}:\n{presignedUrl}",
                    messageReference: new MessageReference(pending.ReplyId));
            }
            File.Delete(pending.FilePath);
        }
    }

    public static readonly ConcurrentDictionary<string, PendingTranscode> PendingTranscodeChoices = new();

    // https://unix.stackexchange.com/questions/520597/how-to-reduce-the-size-of-a-video-to-a-target-size
    private void TranscodeVideo(string filePath, int fileSizeLimit)
    {
        var newFilePath = $"{Path.GetFileNameWithoutExtension(filePath)}_new.mp4";

        var targetSizeInKiloBits = (fileSizeLimit - 5) * 1000 * 8;
        var mediaInfo = FFProbe.Analyse(filePath);
        var totalBitRate = (targetSizeInKiloBits / mediaInfo.Duration.TotalSeconds) + 1;
        var audioBitRate = 128;
        var videoBitRate = (int)(totalBitRate - audioBitRate);

        try
        {
            if (File.Exists(newFilePath))
            {
                File.Delete(newFilePath);
            }

            FFMpegArguments.FromFileInput(filePath, false, options => options
                        .WithHardwareAcceleration())
                        .OutputToFile(newFilePath, false, options => options
                        .WithVideoCodec(VideoCodec.LibX264)
                        .WithAudioCodec(AudioCodec.Aac)
                        .WithArgument(new CustomArgument($"-maxrate:v {videoBitRate}k"))
                        .WithArgument(new CustomArgument($"-bufsize:v {targetSizeInKiloBits * 1000 / 20}"))
                        .WithFastStart())
                        .NotifyOnError((err) => { logger.LogError(err); })
                        .NotifyOnOutput((output) => { logger.LogInformation(output); })
                        .ProcessSynchronously();
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
