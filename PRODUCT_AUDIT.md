# Spectral TV product audit

This audit defines the supported product after the ground-up interface overhaul. A feature stays only when it helps someone create or watch an always-on Jellyfin channel.

## Retained and rebuilt

| Capability | Decision | Current experience |
|---|---|---|
| Channel creation | Rebuilt | Name, number, aspect ratio, enabled state, optional scanlines, and logo placement in one form |
| Jellyfin library selection | Rebuilt | Search and add a series, season, episode, or movie directly from the Programming step |
| Weighted programming | Retained | Relative shares are normalized and the interface shows the effective percentage |
| Episode progression | Retained | Each source can advance sequentially or choose episodes randomly |
| Promos, bumpers, commercials, station IDs | Rebuilt | One optional Breaks step with plain-language frequency and timing controls |
| Channel logos | Rebuilt | Direct PNG/JPG/WebP upload for the guide icon and on-screen network bug |
| Wall-clock playout | Retained | A background builder maintains future schedules and supports mid-program tune-in |
| M3U and XMLTV | Rebuilt | Addresses, copy controls, and the four Jellyfin setup actions live together in the final step |
| Server settings | Simplified | Time zone, guide horizon, verbose logging, and rebuild-all sit under one advanced disclosure |
| Failure fallback | Reworked | Missing or unplayable content produces internal color bars with silence instead of a configurable EBS subsystem |

## Retired

| Legacy capability | Why it was removed |
|---|---|
| Ready-made third-party channels and legacy numbering | They exposed another project's personal lineup, tags, names, and missing-status table instead of helping create the user's channels |
| Manual 48-slot lineup grid and overrides | It duplicated the continuous programming engine and made the basic workflow much harder to understand |
| AI lineup generator and auto-tagging | It added API keys, background jobs, hidden catalog rules, and failure modes without being necessary to build a channel |
| WeatherStar and Playwright/Docker sidecars | Weather channels are outside the product and the sidecars created substantial installation and startup risk |
| Music and music-video channels | Explicitly outside the desired TV/movie product |
| EBS configuration | Replaced by an automatic internal fallback; administrators should not have to configure error playback |
| CommercialBrainz and YouTube streaming | Network-dependent catalog and yt-dlp behavior were unnecessary when break clips can come directly from Jellyfin |
| Playlist registry | Added an extra abstraction before a source could be used; the current workflow selects library content directly |
| Special Presentation editor | Depended on the retired 48-slot scheduler; seasons and individual episodes can be selected directly for themed channels |
| Logo catalog synchronization | Replaced by direct user-owned logo upload |
| Maintenance pages full of one-off repair actions | Current maintenance is limited to rebuilding one channel or every enabled channel |

## Compatibility boundary

Some columns and tables from older builds remain in the SQLite model so Jellyfin can open an existing plugin database without a destructive migration. They are inert compatibility data: no dashboard page, API workflow, scheduled task, or startup service uses them.
