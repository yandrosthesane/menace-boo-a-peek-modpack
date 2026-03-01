# Changelog

## v1.2.0 -- Who Goes There?

Factions are now discovered at runtime instead of hardcoded. Allied AI factions (Civilian, Allied Local Forces) are correctly skipped — v1.1.x incorrectly stripped their opponents too.

[Design notes & analysis](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.2.0_better_filtering.md)

## v1.1.1 -- Housekeeping

Settings cleanup, release tooling, documentation.

## v1.1.0 -- Opponent List Filtering

Core fog-of-war fix: on each AI faction's turn, filters `m_Opponents` to only include player units visible to at least one living enemy in that faction. Pure binary filter — no awareness persistence, no TTL decay, no last-known-position.

[Investigation & analysis](https://github.com/yandrosthesane/menace-boo-a-peek-modpack/blob/main/docs/v1.1.1_AI_LEAK_ANALYSIS.md)
