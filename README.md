# Watchlist for Jellyfin

Watchlist treats each user's native Jellyfin **Favorites** as their watchlist.
Star a movie, series, or season anywhere in Jellyfin, and the plugin removes it from that
user's Favorites when they complete it. Star it again and, if it is still fully
watched, the plugin clears its played state so you do not have to unmark everything manually.

There is no separate watchlist, database, custom page, or client modification. The
native star button and Favorites page work on the web, mobile, and TV clients,
and the plugin runs entirely on the server.

## Why Favorites

Jellyfin has no native watchlist concept, and the alternatives do not work
cleanly on every client:

- **Playlists** cannot hold a series as a single entry. Adding a show expands it
  into its episodes, and playlist editing is inconsistent on TV and mobile clients.
- **A custom watchlist page** would only work in the web client unless every
  client app was modified.

Favorites is the only list that is native, per-user, and available on all
official clients. Starring is a single tap, a series stays one entry, and
playback reporting is server-side so cleanup works no matter which client you
watch on. The tradeoff is that the list is still labeled **Favorites** in the
clients; this plugin gives it watchlist behavior but cannot rename it.

## Using it on any client

1. Star a movie, series, or season with the favorite button.
2. Find it later under **Favorites**.
3. Watch it normally. The plugin removes it from Favorites when complete.
4. Star it again to rewatch: if it is still fully watched, its played state is cleared for that user.

No plugin UI is needed for day-to-day use. Configuration lives under
**Dashboard → Plugins → Watchlist**.

## Requirements

- Jellyfin **12.2** (plugin ABI `12.2.0.0`, `net10.0`)
- .NET 10 SDK only if you build from source

## Behavior

### Movies

A favorite movie is removed when it transitions to played. A movie that was
already played when it was favorited is kept.

### Series

The completion rule is configurable:

| Mode | Removes the series when |
| --- | --- |
| **Ended + fully watched** (default) | Jellyfin marks the series `Ended` and every eligible episode is played. Catching up on an ongoing show does not remove it. |
| **Caught up** | Every currently available eligible episode is played, even if more episodes are coming. |
| **Latest season watched** | Every eligible episode of the newest season is played. Earlier seasons are ignored. |

### Seasons

A favorited season is removed when every eligible episode in that season is played
(aired, not missing). A directly favorited season zero is evaluated literally even
when **Ignore season zero specials** is on; that setting remains series-only.

**Eligible episodes** are episodes that:

- exist in the library and are not missing or virtual,
- have already aired,
- are not season zero specials, unless **Ignore season zero specials** is turned off.

Latest-season mode always ignores season zero. Missing episodes never block
completion. Unaired episodes never block completion either, with one exception
in latest-season mode: if the newest season exists but none of its episodes
have aired yet — including announced seasons that only have virtual or missing
placeholders so far — the series is not complete. The plugin does not fall back
to an older season in that case.

### Completion transitions

The plugin removes an item only when it observes a completion *transition*. This
prevents it from treating every favorite as something to delete.

- Favorite added while incomplete: completing it removes it from Favorites.
- Favorite added while already complete: it is kept. This makes first-time
  favorites of old movies safe. Only an item the plugin itself removed gets a
  rewatch reset (see below).
- A kept complete series stays kept through unplay/replay of the same content.
  Marking an episode (or the series) unplayed does not re-arm removal; only new
  episodes, a new season, or replaced episode content do.
- Content shrinking never removes: if episodes disappear from the library
  (offline share, deleted or missing episodes), the kept baseline is preserved
  and nothing is removed. Returning content is tracked against the original
  baseline.
- For movies, an explicit unplay does reset the baseline: marking a kept
  already-played favorite unplayed and replaying it removes it.
- Re-favorite after removal: it is kept until new episodes are completed, unless
  the rewatch reset below applies.
  Re-starring a completed item that was never removed (or rating a favorite) only re-baselines; it
  never removes. Playback, manual mark-played, and scans handle removals.
- A new season or new episodes count as new content. Finishing that content can
  remove the series again.
- Manually unfavoriting an item clears its tracking. Removal tombstones for
  auto-removed items are kept so a later re-add can be distinguished.
- If a series is favorited and completed within the same few seconds, the
  favorite may be recorded after the completion and the series kept until new
  content arrives. Movies do not have this race: the favorite event itself
  records whether the movie was played.

### Rewatch reset

When the plugin auto-removes a favorite for completion and you add that same item
back to Favorites while it is still fully watched, the plugin clears its played
state so you do not have to unmark everything manually:

| Re-added unit | Reset when | Reset scope |
| --- | --- | --- |
| Movie | it is still played | that movie |
| Season | every eligible episode in the season is still played | that season only |
| Series | every eligible episode across **all** seasons is still played (literal fully-watched) | the whole series |

- The reset only triggers after an auto-removal by the plugin. A first-time
  favorite of an already-watched item is still kept.
- A series removed under **Latest season watched** with earlier seasons incomplete
  will **not** reset on re-add. Favorite that season instead; the log names it.
- If new content arrived after removal, the unit is no longer fully watched: no
  reset, normal tracking resumes, and completing the new content can remove it again.
- Only the user who re-adds the item is reset. Other users keep their progress.
- A series or season reset cascades to episodes and clears resume positions.
  Shared favorites in the same scope get their baselines recomputed so they can
  still be removed after the rewatch.
- The exempt tag and disabled auto-removal skip the reset. Test mode logs
  "would reset" without changing anything; the reset is retried by the next scan
  after test mode is off. If you start rewatching before the retry, the pending
  reset is dropped so new progress is never wiped.
- An offline re-add (plugin missed the favorite event) is not retroactively
  reset; it is baselined as already complete and kept.
- External trackers (Trakt, Kodi sync) see unplay events; played badges and
  resume positions are cleared for that user.

Disable it with **Start a rewatch when a removed item is re-added** (on by default).

The plugin stores a small observation file at
`{Jellyfin data directory}/data/watchlist/state.json` so it can distinguish these
cases. The Favorites themselves are always the source of truth. Corrupt state
files are preserved with a `.corrupt-<timestamp>` suffix instead of being
overwritten, and transient read failures are retried rather than wiping state.

### Exempt tag

Add the tag `keep-favorite` (configurable) to a movie, series, or season to prevent it
from ever being auto-removed. Tags can be edited from the item's metadata page
in Jellyfin. Removing the tag later does not retroactively remove an
already-complete item: it is re-baselined as already complete and kept until new
content or another transition.

## Configuration

Open **Dashboard → Plugins → Watchlist** and configure:

- **Completion mode** — ended + fully watched, caught up, or latest season watched.
- **Remove movies when played** — on by default.
- **Remove series when complete** — on by default.
- **Remove seasons when fully watched** — on by default.
- **Start a rewatch when a removed item is re-added** — on by default. Clears played
  state and resume positions for the re-adding user only.
- **Ignore season zero specials** — on by default. Directly favorited season zero
  is always evaluated literally.
- **Treat a missing series status as ended** — off by default.
- **Exempt tag** — defaults to `keep-favorite`; leave empty to disable.
- **Run startup and daily reconciliation** — on by default. Turning it off
  disables the startup scan and the **Watchlist cleanup** task, including manual
  runs. Real-time events still work; re-enable the setting to run a scan.
- **Test mode** — log what would be removed or reset without changing Favorites.
  First sightings are baselined even in test mode, so items that complete while
  it is on can be removed or reset on a later scan after it is turned off.

Changing any completion rule (mode, specials, or unknown-status handling)
clears the stored series observations and re-baselines current favorites on the
next scan. Movie and season tracking is unaffected. New toggles never clear
baselines. Turning off movie, series, or season removal does not erase tracking;
re-enabling resumes where it left off.
A long scan re-reads the configuration for every item, so enabling test mode
or disabling reconciliation mid-scan takes effect for the remaining items.

## Scheduled cleanup

Real-time events handle normal playback completion and manual “mark played”
actions. A startup scan and a daily scheduled task catch missed events, metadata
changes such as a series becoming `Ended` after it was watched, and items that
were unfavorited while the server was down.

Run the scan on demand from **Dashboard → Scheduled Tasks → Watchlist cleanup**.
The default trigger is daily at 03:00 local server time; the trigger can be
changed like any other Jellyfin task.

## Install

1. Build the plugin or download a release.
2. Stop Jellyfin.
3. Create a folder named `Watchlist` inside Jellyfin's plugin directory
   (usually `{data directory}/plugins/Watchlist/`).
4. Copy `Jellyfin.Plugin.Watchlist.dll` into that folder.
5. Restart Jellyfin.
6. Open **Dashboard → Plugins** and confirm Watchlist is listed.

## Build and test

```bash
dotnet restore Jellyfin.Plugin.Watchlist.sln
dotnet build Jellyfin.Plugin.Watchlist.sln -c Release
dotnet test Jellyfin.Plugin.Watchlist.sln -c Release
dotnet publish Jellyfin.Plugin.Watchlist/Jellyfin.Plugin.Watchlist.csproj \
  -c Release -o artifacts/Watchlist
```

The publish output is the folder to copy into Jellyfin's plugin directory.

## Notes and limitations

- **The list is still called Favorites.** Clients show the native Favorites page;
  the plugin cannot rename it to "Watchlist" without client changes.
- **Favorites is a general Jellyfin feature.** This plugin deliberately changes
  its meaning for completed items. Use the exempt tag or disable automatic
  removal (movies, series, and/or seasons) if you keep favorites for reasons other than
  a watchlist.
- **Rewatch resets watched state.** Re-adding a removed item clears played badges
  and resume positions for that user, and external trackers see the unplay.
- If a series has no metadata status, it cannot complete in **Ended + fully
  watched** mode unless **Treat a missing series status as ended** is enabled.
  Use **Caught up** or **Latest season watched** instead.
- Season zero specials are ignored by default for series (configurable) except a
  directly favorited season zero, which is always literal. Missing and
  unaired episodes are always ignored. A series can therefore complete while
  those remain unwatched.
- The plugin only reacts to play state reported to Jellyfin. Direct file access
  or a client that never reports playback cannot be observed.
- The daily scan and startup scan are safety nets. If a completion event is
  missed while the server is running, the next scan catches it.
- Upgrading to 0.3.0 migrates state.json to schema v2 (seasons + tombstones).
  Existing season favorites are kept once as already complete, then tracked.
  Items removed before the upgrade have no tombstones and will not reset retroactively.

## License

GPL-3.0. See [LICENSE](LICENSE).
