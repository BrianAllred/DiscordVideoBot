namespace DiscordVideoBot.Workers;

public class DownloadInfo
{
    public ulong ChannelId { get; set; }
    public ulong ReplyId { get; set; }
    public ulong UserId { get; set; }
    public string? VideoUrl { get; set; }
    public int FileSizeLimit { get; set; }
    public bool IsDm { get; set; }
}

public class PendingTranscode
{
    public required string FilePath { get; set; }
    public ulong ChannelId { get; set; }
    public ulong ReplyId { get; set; }
    public ulong UserId { get; set; }
    public int FileSizeLimit { get; set; }
    public bool IsDm { get; set; }
}
