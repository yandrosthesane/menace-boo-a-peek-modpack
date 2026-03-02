[size=5][b]BooAPeek — AI Awareness Rework for Menace[/b][/size]

[b]Version:[/b] 2.2.0 — [i]"Equal Opportunity Paranoia"[/i]
[b]Author:[/b] YandrosTheSane

[size=4][b]What It Does[/b][/size]

BooAPeek reworks the AI's awareness of opponents in Menace's tactical combat.

The vanilla game gives every AI faction full knowledge of all opponent positions at mission start and relies on the AI to behave as if it doesn't know.
BooAPeek takes a different approach: it strips opponent knowledge and rebuilds it based on what each faction can actually observe through line-of-sight.

When enemies lose sight of any opponent — player units, civilians, or allies — they investigate the last-known position instead of instantly forgetting.

[size=4][b]High Level Features Overview[/b][/size]

[size=3][b]Ghost Awareness (v2.1.0, expanded in v2.2.0)[/b][/size]

When the AI loses line-of-sight on a previously spotted opponent, BooAPeek creates a "ghost" at the last-known position.

This injects a bonus into the AI's tile scoring (via ConsiderZones.Evaluate postfix), nudging nearby enemies to investigate rather than instantly forgetting.

As of v2.2.0, ghost pursuit covers [b]all non-hostile factions[/b] — player units, civilians, and allies alike. Previously only player units triggered ghost creation; now wildlife will investigate a civilian that ducked behind cover just as it would a player unit.

[list]
[*][b]Auto-calibrating bonus:[/b] Scales to the spread of the AI's existing tile scores. Minimum floor of 20.0 ensures ghosts matter even in zoneless kill missions.
  Objectives based missions have not yet been balanced, but utility values observed were up to 10000 so our meagre influence shouldn't be felt too much.
[*][b]Decay over time:[/b] Ghost priority halves each round (3 rounds max), so the AI doesn't fixate forever.
[*][b]Cancellation on re-sight:[/b] If any enemy spots the opponent again, the ghost is immediately cancelled.
[*][b]Per-unit tracking:[/b] Each opponent is tracked independently — the AI can ghost one unit while actively engaging another.
[*][b]Waypoint advancement:[/b] Ghost position advances toward the nearest AI unit each round, guiding investigation movement.
[/list]

[size=3][b]Opponent List Filtering (v1.x)[/b][/size]

On each AI unit's turn start, BooAPeek:

[list=1]
[*]Gets the faction's opponent list directly
[*]For each opponent, checks if [b]any[/b] living enemy in that faction has line-of-sight to that player unit
[*]Builds a new list containing only opponents that are actually visible
[*]Swaps the opponent list to the filtered list
[/list]

The filtering runs before the AI thinks, with guaranteed timing. The game naturally rebuilds the opponent list at the start of the next turn, so no cleanup is needed.

[size=4][b]Settings[/b][/size]

Configurable via the in-game Modkit settings panel:

I haven't had the time to play with those, feel free to experiment and report :)

[list]
[*][b]Debug Logging[/b] (Default: Off) — Per-actor score ranges, top tiles, ghost zone diagnostics
[*][b]Ghost Zone Size[/b] (Default: 5) — Width/height of the ghost influence zone (tiles)
[*][b]Initial Priority[/b] (Default: 20) — Base ghost priority (pre-multiplied to survive first decay)
[*][b]Decay Per Round[/b] (Default: 0.5) — Priority multiplier each round
[*][b]Max Rounds[/b] (Default: 3) — Rounds before ghost expires
[*][b]Waypoint Distance[/b] (Default: 6) — Max tiles the ghost waypoint advances toward nearest AI
[*][b]Spread Fraction[/b] (Default: 0.33) — Ghost bonus as fraction of observed score spread
[*][b]Minimum Bonus[/b] (Default: 20) — Floor for ghost bonus when spread is low/zero
[/list]

Log output should include turn transitions and filtering results (e.g. "Wildlife: stripped 1, kept 1, ghosts +1/-0").

[size=4][b]Changelog[/b][/size]

[size=3][b]v2.2.0 -- Equal Opportunity Paranoia[/b][/size]
Ghost pursuit and awareness tracking now cover all non-hostile factions — civilians, allies, and player units. Previously the AI only ghosted player units on LOS break; now any opponent that disappears triggers investigation.

[size=3][b]v2.1.0 -- I Saw You There[/b][/size]
Ghost awareness system: AI investigates last-known opponent positions instead of instantly forgetting on LOS break. Auto-calibrating UtilityScore injection via ConsiderZones postfix, with spread-based scaling, per-round decay, and per-unit tracking. Codebase split into 6 concern-based files. [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v2.1.0_ghost_awareness.md]Design notes & test results[/url]

[size=3][b]v2.0.0 -- Under the Hood[/b][/size]
Complete architecture rewrite: direct Il2Cpp types + Harmony OnTurnStart prefix replaces reflection + frame polling. Same filtering logic, guaranteed timing, zero per-frame overhead. [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v2.0.0_harmony_migration.md]Migration details[/url]

[size=3][b]v1.2.0 -- Who Goes There?[/b][/size]
Factions are now discovered at runtime instead of hardcoded. Allied AI factions (Civilian, Allied Local Forces) are correctly skipped — v1.1.x incorrectly stripped their opponents too. [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.2.0_better_filtering.md]Design notes & analysis[/url]

[size=3][b]v1.1.1 -- Housekeeping[/b][/size]
Settings cleanup, release tooling, documentation.

[size=3][b]v1.1.0 -- Opponent List Filtering[/b][/size]
Core fog-of-war rework: on each AI faction's turn, filters the opponent list to only include player units visible to at least one living enemy in that faction. Pure binary filter — no awareness persistence, no TTL decay, no last-known-position. [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.1.1_AI_LEAK_ANALYSIS.md]Investigation & analysis[/url]

[size=4][b]Background: The AI Information Leak (v1.x Investigation)[/b][/size]

This section documents the original investigation that motivated BooAPeek's awareness rework.

[size=3][b]The Problem[/b][/size]

At mission start, the game pre-populates every AI faction's opponent list with [b]all[/b] player units, each containing a live reference to the actual unit object. The AI's tile scoring system reads opponent positions from this list to evaluate threat, cover, and flee calculations — but [b]never checks whether the opponent is known[/b] before doing so.

The result: enemies react to concealed player units they have zero legitimate knowledge of. They flee from unseen threats, freeze behind high-cover tiles relative to invisible positions, and optimally reposition against ghosts causing the well known "herding" pattern.

[size=3][b]Confirming the Leak[/b][/size]

Tested with a concealed player unit (Concealment=3) against 26 pirates across 12 rounds, using live REPL inspection of AI state at each turn boundary. Full round-by-round data, position tables, and architecture notes in [url=https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.1.1_AI_LEAK_ANALYSIS.md]v1.1.1_AI_LEAK_ANALYSIS.md[/url].

With the leak active, enemies unanimously flee from a concealed unit they have never seen — even when all gating fields (TTL=-2, IsKnown=False, Assessment=zeros) confirm "unknown." At close range, enemies take optimal cover positions relative to the player's exact live position despite zero LOS.

[size=3][b]Ruled-Out Approaches[/b][/size]

Three runtime approaches were tested before arriving at the list swap:

[list]
[*][b]Set TTL=0[/b] — Makes IsKnown=True — worse, not better
[*][b]Clear list size to 0[/b] — Game rebuilds the list mid-turn before AI thinks
[*][b]Null the Actor reference[/b] — Game freeze — native code has no null checks
[/list]

[size=3][b]Validation (4 Controlled Tests)[/b][/size]

[list]
[*][b]Baseline[/b] (26 enemies, no contact) — Normal patrol — random movement, no coordinated fleeing
[*][b]Close proximity[/b] (2 enemies at distance 5) — One walked straight into the player (d=5→2), the other patrolled away. Zero awareness.
[*][b]TTL lifecycle[/b] (Enemy spots player, then list swapped) — Complete amnesia — TTL=-2, Threat=0 after swap. No persistence outside opponent list.
[*][b]Before/after[/b] (Same enemies, same position, leak on then off) — Enemy that fled +5.6 over 2 turns reversed direction (-1.0 toward) immediately after swap.
[/list]

[size=4][b]Complementary Mods[/b][/size]

[list]
[*][url=https://www.nexusmods.com/menace/mods/36]Wake Up ~ By Pylkij[/url]
[*][url=https://www.nexusmods.com/menace/mods/69]PeekABoo ~ By YandrosTheSane[/url]
[/list]

[size=4][b]Installation[/b][/size]

Use the [url=https://github.com/p0ss/MenaceAssetPacker/releases]MenaceAssetPacker[/url] to deploy (build the sources) and activate the mod.

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
