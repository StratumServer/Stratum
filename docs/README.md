# Stratum documentation

Pages in this folder, by audience.

## Building and contributing

- [BUILDING.md](BUILDING.md): bootstrap, build, embed the patched files, smoke test.
- [../CONTRIBUTING.md](../CONTRIBUTING.md): repository workflow, patch markers, style, pull request checklist.
- [../SECURITY.md](../SECURITY.md): how to report a security bug.
- [../CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md): how contributors treat each other, and how to report a problem.

## Running a server

- [commands/kits.md](commands/kits.md): `/kit` and `/kitedit`.
- [commands/friendlyfire.md](commands/friendlyfire.md): `/friendlyfire`, the runtime group friendly-fire toggle.
- [commands/groups.md](commands/groups.md): `/group admin`, group kinds, membership locks, roster freeze, relations and group tags.
- [commands/inventory-privacy.md](commands/inventory-privacy.md): the `hardening.inventoryGuards` inventory privacy setting.
- [commands/vanish.md](commands/vanish.md): `/vanish`, what it hides, and the blocks, particles and sounds it does not.
- [role-prefixes.md](role-prefixes.md): stacking role name prefixes in chat and nametags.

Config keys live in `stratum.json`, `stratum-commands.json` and `stratum-performance.json` next to the world data; `/stratum get` and `/stratum set` read and write them on a running server.
