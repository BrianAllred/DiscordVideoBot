# Discord Video Bot

[![Docker](https://img.shields.io/docker/pulls/brianallred/discord-video-bot)](https://hub.docker.com/r/brianallred/discord-video-bot/)

Discord bot that facilitates downloading videos from various websites and services in order to more easily to distribute them. This bot is mostly intended for shorter videos (for example, TikTok and Twitter). Videos exceeding the server's upload limit are automatically transcoded to fit. This means long videos will take a long time to process and will be of (potentially greatly) reduced quality.

The bot automatically detects the server's boost level and uses the corresponding upload limit:

| Server Boost Tier | Upload Limit |
|---|---|
| No boost / Tier 1 | 25 MB |
| Tier 2 | 50 MB |
| Tier 3 | 100 MB |
| DMs | Configurable via `FILE_SIZE_LIMIT` (default 25 MB) |

Optionally, S3-compatible storage (AWS S3, Garage, MinIO, Backblaze B2, etc.) can be configured so users can retrieve the original, untranscoded video. When a video needs transcoding, the bot offers a choice between "Transcode for Discord" and "Get original". Choosing the latter uploads the original file to S3 and sends back a time-limited presigned download URL.

Uses YT-DLP and FFMpeg under the hood. Supports hardware acceleration, but it will fall back to software transcoding.

A dependency has been added on [Deno](https://deno.com/) (or similar EJS) as per the [YT-DLP documentation](https://github.com/yt-dlp/yt-dlp/wiki/EJS). The Discord Video Bot Docker image handles this dependency automatically.

## Usage

### Environment Variables

- `DISCORD_BOT_TOKEN`: Bot token obtained from Botfather. Required.
- `DISCORD_BOT_NAME`: Name used in `/help` and `/start` command text. Optional, defaults to `Frozen's Video Bot`.
- `UPDATE_YTDLP_ON_START`: Update the local installation of YT-DLP on start. Optional, defaults to false. Highly recommended in container deployments.
- `YTDLP_UPDATE_BRANCH`: The code branch to use when YT-DLP updates on start (if `UPDATE_YTDLP_ON_START` is true). Optional, defaults to `release`.
- `DOWNLOAD_QUEUE_LIMIT`: Number of videos allowed in each user's download queue. Optional, defaults to 5.
- `FILE_SIZE_LIMIT`: File size limit of videos in megabytes for DMs. In guild channels, the limit is auto-detected from the server's boost tier. Optional, defaults to 25.
- `TZ`: Timezone. Optional, defaults to UTC.

#### S3 Storage (optional)

When configured, users can retrieve the original untranscoded video via a presigned download URL.

- `S3_ENDPOINT`: S3-compatible endpoint URL (e.g. `https://s3.garage.example.com`). Optional, omit for AWS S3.
- `S3_ACCESS_KEY`: S3 access key. Required to enable S3.
- `S3_SECRET_KEY`: S3 secret key. Required to enable S3.
- `S3_BUCKET`: Bucket name for video storage. Required to enable S3.
- `S3_REGION`: Region. Optional, defaults to `us-east-1`.
- `S3_FORCE_PATH_STYLE`: Force path-style addressing. Optional, defaults to `false`. Required for many S3-compatible services (Garage, MinIO, etc.).
- `S3_DISABLE_PAYLOAD_SIGNING`: Disable chunked payload signing for uploads. Optional, defaults to `false`. Required for some S3-compatible services (Garage, MinIO, etc.) that don't support streaming signatures.
- `S3_PRESIGN_EXPIRY_DAYS`: Presigned URL expiry in days. Optional, defaults to `3`.

### Docker Compose

```Docker
version: '3.8'

services:
  video-bot:
    image: brianallred/discord-video-bot
    container_name: discord-video-bot
    environment:
      - TZ=America/Chicago
      - DISCORD_BOT_TOKEN=<bot token>
      - DISCORD_BOT_NAME=<bot name>
      - UPDATE_YTDLP_ON_START=true
      - DOWNLOAD_QUEUE_LIMIT=5
      # Optional: S3 storage for original videos
      # - S3_ENDPOINT=https://s3.garage.example.com
      # - S3_ACCESS_KEY=<access key>
      # - S3_SECRET_KEY=<secret key>
      # - S3_BUCKET=videos
      # - S3_FORCE_PATH_STYLE=true
      # - S3_DISABLE_PAYLOAD_SIGNING=true
      # - S3_PRESIGN_EXPIRY_DAYS=3
    deploy:
      resources:
        reservations:
          devices:
            - capabilities: [gpu]
    devices:
      - /dev/dri:/dev/dri
      - /dev/nvidia0:/dev/nvidia0
      - /dev/nvidiactl:/dev/nvidiactl
      - /dev/nvidia-modeset:/dev/nvidia-modeset
      - /dev/nvidia-uvm:/dev/nvidia-uvm
      - /dev/nvidia-uvm-tools:/dev/nvidia-uvm-tools
```

This example enables hardware acceleration via `/dev/dri` (for VAAPI) and via the various nvidia settings (for CUDA). FFMpeg is set to auto detect the best method.

### Docker Run

`docker run -d --name discord-video-bot -e DISCORD_BOT_TOKEN=<bot token>`

For help exposing hardware using `docker run`, refer to [nvidia's documentation](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/user-guide.html).
