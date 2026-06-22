# Media-server integrations

Optimisarr supports configured Plex, Jellyfin, and Emby activity watchers to
pause new work while a service is active. Unreachable watchers do not wedge the
queue. After a replacement or rollback it asks each connected server to rescan:
a changed-folder refresh for Jellyfin/Emby, and a section refresh for Plex.

Plex supports PIN/OAuth connection flow; Jellyfin supports Quick Connect or API
key connection; Emby uses an API key. Configure each in the UI, test the
connection, then enable only the pause and refresh behaviour you need.

Sonarr and Radarr connections provide import-aware exclusions so recently
imported media is not immediately reprocessed. Notification targets support
webhook, ntfy, and Apprise. Exported configuration deliberately excludes secrets;
re-enter credentials after importing it elsewhere.
