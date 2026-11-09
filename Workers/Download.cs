using Discord.WebSocket;

namespace DiscordVideoBot.Workers;

public record DownloadInfo
{
    public ulong ChannelId { get; set; }
    // 0 for /download: there's no message to reply to.
    public ulong ReplyId { get; set; }
    public ulong UserId { get; set; }
    public string? VideoUrl { get; set; }
    public int FileSizeLimit { get; set; }
    public bool IsDm { get; set; }
    // The /download command. While its token is valid (15 minutes), replies go through it, ephemeral in guilds.
    public SocketInteraction? Command { get; set; }
}

public class PendingTranscode
{
    public required string FilePath { get; set; }
    public required DownloadInfo Download { get; set; }
}
