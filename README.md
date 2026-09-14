# Spectral TV

Spectral TV is a native Jellyfin 12 plugin for building always-on virtual television channels from your Jellyfin library.

## What it does

- Weighted programming by actual airtime
- Whole-series, season, episode, and movie sources
- Sequential or random episode progression
- Separate promo, bumper, commercial, and station-ID pools
- Shared wall-clock schedules with M3U and XMLTV Live TV integration
- Tune-in mid-program from normal Jellyfin clients
- One guided Jellyfin dashboard workspace for the complete channel workflow

## Admin workflow

1. Create the channel and choose its name, number, picture format, and logo.
2. Add programming from the Jellyfin library and set relative airtime shares.
3. Optionally add promos, bumpers, commercials, or station IDs between programs.
4. Build the schedule and connect the M3U/XMLTV addresses to Jellyfin Live TV.

The inherited ready-made lineup, 48-slot editor, AI scheduler, WeatherStar, music channels,
Playwright/Docker controls, EBS configuration, playlist registry, and third-party commercial
catalog were intentionally retired. See [PRODUCT_AUDIT.md](PRODUCT_AUDIT.md).

## Status

Spectral TV is under active development for Jellyfin 12 / .NET 10. Development builds are produced automatically through GitHub Actions and should be treated as test builds until runtime validation is complete.

## Project history

Spectral TV began from earlier open-source virtual-TV plugin work and has since been substantially reworked around its own channel model, interface, plugin identity, and release line. Original attribution remains in [NOTICE.md](NOTICE.md).
