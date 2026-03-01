[size=5][b]BooAPeek — Fixes AI Knowing about Concealed Player Units[/b][/size]

[b]Version:[/b] 1.2.0 — [i]"Who Goes There?"[/i]
[b]Author:[/b] YandrosTheSane

[size=4][b]What It Does[/b][/size]

BooAPeek tries to fix a fundamental AI information leak in Menace's tactical combat. Without this mod, the AI knows the exact real-time position of all player units — even concealed ones it has never seen — and uses that knowledge to flee, take cover, and position optimally against invisible threats.

[size=4][b]The Problem[/b][/size]

At mission start, the game pre-populates every AI faction's opponent list with [b]all[/b] player units, each containing a live reference to the actual unit object. The AI's tile scoring system reads opponent positions from this list to evaluate threat, cover, and flee calculations — but [b]never checks whether the opponent is known[/b] before doing so.

The result: enemies react to concealed player units they have zero legitimate knowledge of.

They flee from unseen threats, freeze behind high-cover tiles relative to invisible positions, and optimally reposition against ghosts causing the well known "herding" pattern.

[size=4][b]How It Currently Works[/b][/size]

[size=3][b]Core Mechanism: Opponent List Filtering[/b][/size]

On each AI faction's turn start, BooAPeek:

[list=1]
[*]Gets the faction's opponent list via reflection
[*]For each opponent, checks if [b]any[/b] living enemy in that faction has line-of-sight to that player unit
[*]Builds a new list containing only opponents that are actually visible
[*]Swaps the opponent list to the filtered list
[/list]

The AI's tile scorer then sees only legitimately visible opponents. The game naturally rebuilds the list at the start of the next turn, so no cleanup is needed.

[size=3][b]How the Leak Supposedly Works[/b][/size]

[code]
Mission Start:
  Opponents populated with ALL player units
  Each entry: Actor = live reference, TTL = -2, IsKnown = False

During AI Turn (Think / tile scoring):
  Tile scorer reads each opponent's position for EVERY opponent
  → Gets player's CURRENT real-time position
  → Never checks IsKnown() or TTL
  → Evaluates threat/cover/flee against that position

Result:
  AI reacts to concealed units it has never seen
[/code]

[size=4][b]Changelog[/b][/size]

[size=3][b]v1.2.0 -- Who Goes There?[/b][/size]
Factions are now discovered at runtime instead of hardcoded. Allied AI factions (Civilian, Allied Local Forces) are correctly skipped — v1.1.x incorrectly stripped their opponents too. [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.2.0_better_filtering.md]Design notes & analysis[/url]

[size=3][b]v1.1.1 -- Housekeeping[/b][/size]
Settings cleanup, release tooling, documentation.

[size=3][b]v1.1.0 -- Opponent List Filtering[/b][/size]
Core fog-of-war fix: on each AI faction's turn, filters the opponent list to only include player units visible to at least one living enemy in that faction. Pure binary filter — no awareness persistence, no TTL decay, no last-known-position. [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.1.1_AI_LEAK_ANALYSIS.md]Investigation & analysis[/url]

[size=4][b]Complementary Mods[/b][/size]

[list]
[*][url=https://www.nexusmods.com/menace/mods/69]PeekABoo ~ By YandrosTheSane[/url] — Fixes the mirror-image problem: the player's illegitimate knowledge of hidden enemy positions via the concealment icon.
[*][url=https://www.nexusmods.com/menace/mods/36]Wake Up ~ By Pylkij[/url]
[/list]

With those 3 mods (AI is active, You don't get free intel, They don't get free intel) you can get into situation like this in one turn.

Later down the road there will be a need for rebalance.

[size=4][b]Installation[/b][/size]

Use the [url=https://github.com/p0ss/MenaceAssetPacker/releases]MenaceAssetPacker[/url] to deploy (build the sources) and activate the mod.

[size=4][b]Current State & Known Limitations[/b][/size]

[size=3][b]What v1.2.0 Does (Supposedly) Well[/b][/size]

(at the time of release I have played legitimately 5 full operations with the mods above and feel very confident about it being a better player experience)

[list]
[*]Eliminates the AI's illegitimate knowledge of concealed player positions
[*]Enemies behave more naturally when they haven't spotted you: patrolling, wandering, even walking past or into you.
[*]When enemies do have LOS, they react normally (opponent stays in the list)
[*]Zero gameplay impact outside of fog-of-war — no behavior changes for visible encounters
[*]Stable across all tested scenarios — no crashes, no freezes, no list corruption
[/list]

It's not yet satisfying but way better than before, no more herding.

[size=3][b]What v1.2.0 Does NOT Do: Awareness Persistence[/b][/size]

The current version is a [b]pure fog-of-war filter[/b] — binary visible/invisible, evaluated fresh each turn. The system has no built-in awareness persistence:

[list]
[*][b]No TTL decay:[/b] TTL is either refreshed by sighting or reset to -2 on list rebuild. It never naturally decays. We never observed natural decay because the leak kept enemies close enough to always re-spot.
[*][b]No last-known-position:[/b] The only position data in the opponent entry is the live actor reference. There is no snapshot of where a player was last seen.
[*][b]No faction memory:[/b] Swapping the list causes total amnesia. I did not find anything outside the opponent list. It may be there but we're in the dark.
[/list]

This means that once [b]all[/b] enemies in a faction lose line-of-sight, the faction [b]instantly forgets[/b] the player existed. An enemy that was actively chasing you will suddenly wander aimlessly next turn if it rounds a corner and loses LOS.

[size=4][b]Settings[/b][/size]

Configurable via the in-game Modkit settings panel:

[list]
[*][b]Debug Logging[/b] (Default: Off) — Logs actor counts and detailed init info
[/list]

The settings header shows the mod version (e.g. "BooAPeek v1.2.0") so you can verify which version is deployed.

Log output always includes turn transitions and filtering results (e.g. "stripped 1 unseen opponent(s), kept 0").

[size=4][b]Credits[/b][/size]

[list]
[*][b]Rat[/b] — [url=https://www.nexusmods.com/menace/mods/34]Fight You Cowards[/url] mod. Its ThreatFromOpponents=0 template patch was the starting point that revealed the AI's opponent-list-driven behavior and led to the full investigation of the information leak.
[/list]

[size=4][b]Requirements[/b][/size]

[list]
[*]Menace with MelonLoader
[*]Menace ModpackLoader
[/list]

Source code: [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack]GitHub[/url]
