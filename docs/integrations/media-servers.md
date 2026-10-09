# Media-server integrations

Optimisarr supports configured Plex, Jellyfin, and Emby activity watchers to
pause new work while a service is active. Unreachable watchers do not wedge the
queue. After a replacement or rollback it asks each connected server to rescan:
a changed-folder refresh for Jellyfin/Emby, and a section refresh for Plex.

Configure Plex, Jellyfin, and Emby under **Settings → Media servers**. Configure Sonarr and
Radarr under **Settings → Download managers**.

Screenshots in this page use fabricated dummy media created for documentation.
No copyrighted material is used.

![Media servers settings showing a fabricated Jellyfin connection and playback-pause controls](../images/optimisarr-settings-connections-dark.png)

| Service | Use it for | Connection method |
|---|---|---|
| Plex | Pause new work during active sessions; refresh libraries after replacement or rollback. | Plex sign-in/PIN flow, then choose a discovered server or enter the URL manually. |
| Jellyfin | Pause new work during active sessions; refresh changed folders after replacement or rollback. | Quick Connect or API key. |
| Emby | Pause new work during active sessions; refresh changed folders after replacement or rollback. | API key. |
| Sonarr | Avoid immediately reprocessing recently imported TV files. | Base URL and API key. |
| Radarr | Avoid immediately reprocessing recently imported movie files. | Base URL and API key. |

Test each connection before enabling it. Keep only the pause and refresh
behaviour you actually need.

### What a playback pause shows

While playback holds the queue, Optimisarr says what is playing and who is
watching: the series, season, episode and title for TV, the title and year for a
film, and the track and artist for music, followed by the viewer and device, and
"(paused)" for a paused stream. The status bar shows the first playback and how
many more. Click or tap it to open Queue. The Queue and Schedule pages show up to
eight playbacks, with an expandable list for the rest. Film and TV posters and
square album covers come from the exact item, show or album reported by that
server. Images are proxied by Optimisarr so credentials never reach the browser;
unavailable art leaves a fixed-size placeholder. A detail the server does not report is left out rather than
guessed; if a server reports none, the pause reads as before, with a stream count.

![Queue paused for three fabricated playbacks, showing a film, a paused TV episode and a music track](../images/optimisarr-queue-playback-dark.png)

Turn off **Show who is watching** on a watcher and choose **Save changes** to name
only what is playing, which suits a shared server. The viewer and device are then dropped before they leave
the server. Names and titles are only shown to people who can open Optimisarr,
which is protected by the admin token when one is set. They are never written to
logs or diagnostics bundles. They only explain a pause: which sessions pause the
queue is unchanged.

![Unsaved media-server edit with Show who is watching turned off, ready to save](../images/optimisarr-settings-viewer-privacy-dark.png)

After saving, the pause still identifies the titles while omitting viewers and devices:

![Queue playback hold listing only the fabricated titles with viewer names and devices hidden](../images/optimisarr-queue-playback-private-dark.png)

## Sonarr and Radarr

Configure import-aware exclusions under **Settings → Download managers**.

![Download managers settings showing a fabricated Radarr connection and its connection controls](../images/optimisarr-settings-downloads-dark.png)

## Notifications

Notification targets live under **Settings → Notifications**. Supported targets
are generic webhook, Discord, Telegram, ntfy, and Apprise. Discord webhook URLs
are detected automatically and sent as embeds. Telegram sends through the official
Bot API and opportunistically includes artwork Optimisarr already knows: a film/TV
poster, embedded audio cover, or image thumbnail. Artwork lookup has a short budget;
if no suitable image is available or it is too large, Optimisarr sends plain text instead.
If Telegram explicitly rejects an uploaded image as invalid or unsupported, Optimisarr retries the
same notification as text. It does not retry ambiguous timeouts, rate limits, or server failures,
because the photo request may already have succeeded. Targets can notify on
replacement and on job failure. After saving a target, use **Test** to send a clearly
labelled test through that provider immediately. Optimisarr reports success or a short
failure reason inline.

### Telegram setup

1. Create a bot with [@BotFather](https://t.me/BotFather) using `/newbot`, and keep
   the generated bot token secret.
2. Start a private chat with the bot, add it to the target group, or add it to a
   channel with permission to post. Bots cannot initiate a private conversation
   before the user starts them.
3. In Optimisarr, choose **Telegram**, enter the numeric chat ID (for a private chat
   or group) or a public channel username such as `@my_channel`, then paste the bot
   token. For a numeric ID, send the bot a message and inspect `message.chat.id` in
   the official Bot API `getUpdates` response.
4. Choose whether the target receives replacement notifications, failure
   notifications, or both, then save it.

The token is write-only in the Optimisarr API and UI. Do not paste it into logs,
screenshots, support issues, or a notification target's Chat ID field. See
Telegram's official [BotFather guide](https://core.telegram.org/bots/features#botfather),
[`sendMessage` reference](https://core.telegram.org/bots/api#sendmessage), and
[`sendPhoto` reference](https://core.telegram.org/bots/api#sendphoto).

## Backup warning

Exported configuration includes provider secrets so it can restore a working
setup. Treat the JSON file as sensitive material and never commit or share it.
