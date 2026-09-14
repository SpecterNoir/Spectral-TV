# Weighted Virtual Channels

This branch adds a simpler continuous-programming mode to the recovered SpectralTV Jellyfin 12 plugin. It is intended for channels that should feel like television rather than a manually populated 48-slot schedule.

## Programming pool

A channel can contain any mix of:

- entire TV series
- individual seasons
- individual episodes
- movies

Each source receives a target airtime value. The values are normalized automatically, so `50 / 30 / 20` and `5 / 3 / 2` represent the same mix.

Selection is based on **actual scheduled runtime**, not a naive random roll per program. The scheduler chooses the source that is currently furthest below its target share. This keeps a channel balanced even when source runtimes differ.

For a series or season, **Sequential** mode remembers the next episode for that source. **Random** mode chooses an episode with a deterministic channel seed.

## Filler pool

Filler is deliberately separate from programming and never changes the programming percentages. Supported roles are:

- Promo
- Bumper
- Commercial
- Station ID

Per-channel controls include insertion probability, minimum/maximum items per break, maximum break duration, item weights, and repeat protection.

Filler plays in the live stream between programs, but XMLTV folds it into the preceding programme window so short bumpers do not clutter the Jellyfin guide.

## Jellyfin integration

Weighted mode still uses the recovered SpectralTV Live TV infrastructure:

- M3U channel feed
- XMLTV guide
- continuous FFmpeg transport stream
- mid-program tune-in based on current wall-clock time
- Jellyfin Live TV playback on clients such as Web, desktop, Android/Fire TV, and Roku

The single **Spectral TV** dashboard workspace provides channel creation, programming and break search, airtime shares, playback order, logo upload, rebuild controls, and Jellyfin Live TV setup.

## Compatibility

Weighted programming is the only supported scheduling model. Legacy database tables are retained only so an existing installation can upgrade without startup failure; their old editors and runtime services are not exposed.
