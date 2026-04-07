# On Deck: macOS Menu Bar App

**Date**: 2026-04-06
**Status**: Design

## Overview

A native macOS menu bar app (Swift/SwiftUI) that monitors live MLB games and alerts you when your fantasy players are batting or pitching. Connects to your Fantrax roster via a public league URL and uses the MLB Stats API for real-time game data.

- **Platform**: macOS 13+ (Ventura) - requires `MenuBarExtra` API
- **Tech stack**: Swift / SwiftUI
- **Standalone project** - no shared code with the browser extension

## Architecture

```
┌─────────────┐     ┌──────────────┐     ┌──────────────────┐
│ Fantrax API │────>│ RosterManager│────>│  ScheduleManager  │
│ (daily sync)│     │              │     │(MLB Schedule API) │
└─────────────┘     └──────────────┘     └────────┬─────────┘
                                                   │
                                          game times + team IDs
                                                   │
                                         ┌─────────▼──────────┐
                                         │    GameMonitor      │
                                         │ (WebSocket primary, │
                                         │  REST fallback)     │
                                         └─────────┬──────────┘
                                                   │
                                          player state changes
                                                   │
                                    ┌──────────────▼──────────────┐
                                    │         StateManager         │
                                    │  (active/upcoming/inactive)  │
                                    └──────┬──────────────┬───────┘
                                           │              │
                                  ┌────────▼───┐   ┌──────▼──────┐
                                  │  Menu Bar  │   │Notifications│
                                  │    UI      │   │             │
                                  └────────────┘   └─────────────┘
```

## Components

### RosterManager

- Parses league ID and team ID from user-provided Fantrax roster URL
- Calls `POST /fxpa/req?leagueId={id}` with `getTeamRosterInfo` to fetch roster
- Resolves each player to an MLB ID via `GET statsapi.mlb.com/api/v1/people/search?names={name}&hydrate=currentTeam`
- Caches roster locally; auto-refreshes daily on app launch
- Tracks each player's: name, MLB ID, team, position (hitter vs pitcher)
- URL is editable in settings (single league, no multi-league support)

### ScheduleManager

- Fetches today's schedule: `GET statsapi.mlb.com/api/v1/schedule?sportId=1&date={today}&hydrate=team,broadcasts,linescore`
- Filters to games involving your players' teams
- Provides game start times and gamePk IDs to GameMonitor
- Detects exclusive broadcasts via `availabilityCode: "exclusive"` for stream link routing
- Re-fetches on day rollover (midnight local time)

### GameMonitor

**Primary: WebSocket** - Single connection to `ws.statsapi.mlb.com`, subscribes to relevant gamePk feeds.

**Fallback: REST polling** - 5-second interval on `GET statsapi.mlb.com/api/v1.1/game/{gamePk}/feed/live` if WebSocket fails or is unavailable. Automatic fallback, no user intervention needed.

**Connection lifecycle:**
1. Idle until ~5 min before first relevant game
2. Opens connection and subscribes to games as they enter the window
3. Processes live play data to identify current batter and pitcher
4. Detects lineup changes and substitutions
5. Unsubscribes from a game when no roster players remain active in it (pitcher pulled, batter substituted, etc.)
6. Closes connection after last relevant game has no active roster players

**Key data from live feed:**
- `liveData.plays.currentPlay` - current batter and pitcher (MLB ID + name)
- `liveData.linescore` - inning, score, count, outs
- `liveData.boxscore.teams.{home,away}.players` - active lineup (detect substitutions)

### StateManager

Maintains three player lists:

- **Active** - Currently batting or pitching right now
- **Upcoming** - Game hasn't started yet, or player is in the lineup but not currently at bat/on the mound
- **Inactive** - Game over, day off, or removed from game (substitution, pitcher pulled)

Publishes state changes to drive UI updates and notification triggers.

### Menu Bar UI

**Menu bar text** (always shows icon):
- No active players: icon only
- 1 player: `baseball-icon Rice`
- 2 players: `baseball-icon Rice | Yamamoto`
- 3 players: `baseball-icon Rice | Yamamoto | Pena`
- 4+ players: `baseball-icon Rice | Yamamoto | Pena +2`

**Dropdown sections:**

| Section | Content | Click action |
|---------|---------|--------------|
| **Active Now** | Players currently batting/pitching. Shows game context (count, inning, score). | Opens game stream |
| **Upcoming** | Players with games that haven't started. Shows start time. | Opens stream when live |
| **Done / Off** | Players whose games are over or who have no game today. | - |
| **Settings** | Roster URL (editable), notification toggles, quit. | - |

### Notifications (UserNotifications)

All types individually toggleable. All **on by default**.

| Type | Example |
|------|---------|
| Stepping up to bat | "Rice is batting - NYY vs BOS, 3rd inning" |
| Taking the mound | "Yamamoto is taking the mound - LAD vs SF, Top 5th" |
| At-bat result | "Rice doubled to left - NYY 3, BOS 2" |
| Pitching result | "Yamamoto struck out Turner - 5K, 2nd inning" |

Clicking a notification opens the relevant game stream.

### Stream Links

Default to MLB.tv game link. Route to the correct platform for exclusive broadcasts based on the `callSign` from the schedule API:

| callSign | Platform | URL |
|----------|----------|-----|
| `Peacock` | NBC Peacock (Sunday Night Baseball) | `peacocktv.com/sports/mlb` |
| `Apple TV` | Apple TV+ (Friday Night Baseball) | `tv.apple.com/us/room/edt.item.62327df1-...` |
| `ESPN` | ESPN (weeknight exclusives) | `espn.com/watch/` |
| `Netflix` | Netflix (Opening Night, HR Derby, Field of Dreams) | `netflix.com` |
| `TBS` | TBS (Tuesday games, ALDS/ALCS) | `tbs.com/mlb-on-tbs` |

Unknown exclusive broadcasters fall back to MLB.tv. Deep-linking to specific games isn't possible on these platforms - links go to each platform's MLB landing page.

## Data Flow Timeline (Typical Day)

1. **App launch / morning** - RosterManager syncs roster from Fantrax, resolves MLB IDs. ScheduleManager fetches today's games. App is idle, menu bar shows icon only.
2. **~5 min before first game** - GameMonitor opens WebSocket, subscribes to relevant games.
3. **Game in progress** - WebSocket pushes play updates. StateManager updates player states. Menu bar text and notifications fire as players step up or get results.
4. **Player removed from game** - StateManager moves them to inactive. If no other roster players remain active in that game, GameMonitor unsubscribes.
5. **All relevant players done** - WebSocket closed. Menu bar returns to icon only.
6. **Day rollover** - ScheduleManager re-fetches tomorrow's games.

## Persistence

| Data | Storage | Refresh |
|------|---------|---------|
| Fantrax roster URL | UserDefaults | User-editable |
| Notification toggle states | UserDefaults | User-editable |
| Roster player list + MLB IDs | UserDefaults (cached) | Daily on launch |
| Game schedule | In-memory | Daily + day rollover |
| Live game state | In-memory | Real-time via WebSocket/REST |

## Known Edge Cases & Caveats

These are all real issues discovered and solved in the FantraxBaseball+ browser extension. The menu bar app will encounter the same problems.

### Shohei Ohtani (Two-Way Player)

Fantrax lists Ohtani twice: "Shohei Ohtani-P" (pitcher) and "Shohei Ohtani" or "Shohei Ohtani-H" (hitter). Must strip `-P`, `-H`, `-DH` suffixes before MLB API lookup (regex: `/-(P|H|DH)$/i`).

Both entries resolve to the same MLB ID. The app needs to track them as separate roster entries but understand they're the same person in the live game feed. Notification logic must handle this - don't double-notify when the same person appears as both pitcher and hitter.

### Max Muncy Problem (Same Name, Different Teams)

Multiple active MLB players can share the same name (Max Muncy on Athletics vs Dodgers). The MLB search API returns multiple results and must be disambiguated using team.

- Use `hydrate=currentTeam` on the search endpoint
- Match against the player's `teamShortName` from Fantrax
- Cache key must include team: `"Max Muncy|Athletics"` not just `"Max Muncy"`
- Fantrax `teamShortName` (e.g., "ATH") needs mapping to MLB API full team names (e.g., "Athletics")

### Athletics Rebrand (ATH/OAK)

Athletics rebranded post-2025 (Sacramento). Fantrax may use either "ATH" or "OAK". Both abbreviations must map to "Athletics" for disambiguation.

### Periods in Names (T.J. Rumfield, C.J. Abrams)

The MLB Stats API doesn't match periods in player names. Strip all periods before searching: "T.J. Rumfield" -> "TJ Rumfield".

### Abbreviated Names

Fantrax sometimes abbreviates first names in certain views (e.g., "C. Emerson"). The `getTeamRosterInfo` API response includes full names in `scorer.name`, so this should be a non-issue since the menu bar app reads from the API directly. Worth validating during development.

### Pitcher vs Hitter Detection

Positions `SP`, `RP`, or `P` = pitcher. Everything else = hitter. Fantrax `posShortNames` can be comma-separated (e.g., "C,1B") - split and check each position.

This determines:
- Notification wording ("is batting" vs "is taking the mound")
- Which live game feed field to monitor (current batter vs current pitcher)
- Menu bar display context

### Exclusive Broadcast Detection

Uses the MLB Schedule API `hydrate=team,broadcasts` response. Look for `availabilityCode: "exclusive"` in TV broadcast entries. Route the `callSign` to the correct streaming platform URL. Unknown call signs fall back to MLB.tv.

### Fantrax API Fragility

The Fantrax API is undocumented and reverse-engineered. The `getTeamRosterInfo` endpoint works without auth for public leagues (confirmed 2026-04-06), but this could change at any time.

Mitigations:
- Cache the last successful roster to UserDefaults so the app still works if the API changes
- Show a clear error message if roster fetch fails
- Allow manual retry

### MLB WebSocket Fragility

`ws.statsapi.mlb.com` is also undocumented and reverse-engineered. The REST polling fallback at 5-second intervals is not optional - it's expected to be needed. The app should switch to REST silently without user intervention.

## Error Handling

| Scenario | Behavior |
|----------|----------|
| WebSocket fails to connect or drops | Automatic fallback to REST polling at 5s intervals |
| Fantrax API fails | Show last cached roster, display error in settings, allow retry |
| MLB player search returns no match | Skip that player, show warning in dropdown |
| MLB player search returns ambiguous results without team match | Fall back to first result, log warning |
| No internet | Show offline indicator in menu bar, retry on connectivity change |
| No games today | Menu bar shows icon only, dropdown shows "No games today" |

## Out of Scope

- Multi-league support (single league only)
- iPhone / Apple Watch companion
- Historical stats or player performance data
- Fantasy scoring or projections
- Fantrax roster management (trades, adds/drops) from the app
