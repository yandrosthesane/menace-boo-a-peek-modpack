# Changelog

## v1.1.1 -- Housekeeping

### Changed

- Removed the Enabled toggle from settings (mod is always active when installed). Only Debug Logging remains as a setting.
- Settings header now shows mod version (e.g. "BooAPeek v1.1.1") for easy deploy verification.
- Added release script with zip output and .gitignore for release artifacts.
- Restructured README with complementary mod links, updated file structure, and documentation.
- Added BBCode README for Nexus, CHANGELOG, and media directory.

## v1.1.0 -- Opponent List Filtering

### Added

- Core fog-of-war fix: on each AI faction's turn start, filters `m_Opponents` to only include player units that are actually visible to at least one living enemy in that faction.
- Turn detection via `TacticalController.GetCurrentFaction()` polling.
- Reflection cache for all type lookups, resolved once per tactical scene.
- Debug Logging setting for detailed actor counts and init info.

### Known Limitations

- Pure fog-of-war filter -- binary visible/invisible, no awareness persistence.
- No TTL decay, no last-known-position, no faction memory.
