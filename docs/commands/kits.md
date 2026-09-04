# Kit commands

Stratum ships two commands for staff-authored, in-game item kits: `/kit` for
players and `/kitedit` for staff. Both are server side only and stay
compatible with the stock client: there is no custom UI, so every `/kitedit`
step that would otherwise need a typed item code instead snapshots a live
`ItemStack` off the caller. Implementation:
[`CmdStratumKits.cs`](../../sources/VintagestoryLib/Vintagestory.Server/CmdStratumKits.cs).

## At a glance

| Command | Audience | Access config key | Default privilege | Storage |
| --- | --- | --- | --- | --- |
| `/kit` | Players | `Commands.Kits` | `stratum.kits` | `stratum-kits.json` |
| `/kitedit` | Staff | `Commands.KitEdit` | `stratum.kitedit` | `stratum-kits.json` |

## Prerequisites

- `Commands.Enabled`, `Commands.Kits.Enabled`, and `Commands.KitEdit.Enabled`
  all default to `true`. Setting one to `false` skips registering that
  command entirely; the change takes effect on the next server restart,
  since registration happens once when the server boots.
- `stratum.kits` and `stratum.kitedit` are **not** granted to any default
  role, including `admin`. Grant them with `/roles grant <role>
  stratum.kitedit` (see the roles command reference) or by editing
  `serverroles.json` directly.
- The server console always passes Stratum's own access check (see
  `StratumCommandAccessCatalog.CallerHasAccess`), so both commands can be run
  from the console for diagnostics. A console caller has no player entity,
  so `create`, `additem`, and `/kit` itself (which needs a connected player)
  will report that instead of doing anything.
- Kit definitions live in a `stratum-kits.json` sidecar in the server's data
  path, written on every mutation.

## `/kit`

```
/kit
/kit <name>
```

| Argument | Required | Meaning |
| --- | --- | --- |
| `name` | No | The kit to redeem. Omit it to list the kits assigned to you. |

Running `/kit` with no argument lists every kit you are assigned, each with
its item count, cooldown (if any), and a "once per life" marker, followed by
a reminder of the redeem syntax. Running `/kit <name>` redeems that kit
immediately if you are allowed to.

**Assignment.** A kit with an empty assignment list is available to every
player with kit access. A kit assigned to one or more roles is available
only to players currently holding one of those roles.

**Cooldowns.** Each kit has its own `CooldownSeconds` (`0` disables it),
tracked per player per kit, in memory only — it resets when the server
restarts. Staff holding the `Commands.StaffChat` access also bypass every
per-kit cooldown, the same rule `/kit` and `/kitedit` themselves use for
their own command-level cooldown (`Commands.Kits.CooldownSeconds` /
`Commands.KitEdit.CooldownSeconds`), which is checked separately, before the
per-kit one. The server console bypasses cooldowns entirely.

**Once per life.** A kit flagged `oneperlife` can only be redeemed once
between respawns; a second attempt is rejected until you die and respawn.

**Give on respawn.** A kit flagged `onrespawn` is handed out automatically
after you respawn, once your entity is actually alive again (Stratum polls
for that, giving up after roughly 10 seconds if it never happens), and
counts as redeemed for that life the same as a manual `/kit <name>` would.

**Delivery.** Armor pieces are placed directly into a compatible, empty
character inventory slot. Everything else is given through the same
`Entity.TryGiveItemStack` path the `/giveitem` command uses; anything that
does not fit is dropped at your feet instead of being discarded, and an item
whose original definition no longer resolves is reported as failed rather
than silently skipped. The result message says how many items were given,
dropped, and failed.

| Situation | Message |
| --- | --- |
| Not assigned the kit | `You are not assigned the kit '<name>'.` |
| No such kit | `No kit named '<name>'. Run /kit to see the kits you can redeem.` |
| Command or per-kit cooldown active | `Wait <N>s before ...` |
| Already redeemed this life | `You have already redeemed '<name>' this life.` |
| Not a connected player (e.g. console) | `Only a connected player can use /kit.` |
| More than one argument | rejected by the game's own command parser |

## `/kitedit`

```
/kitedit list
/kitedit create <name>
/kitedit additem <name>
/kitedit removeitem <name> <index>
/kitedit preview <name>
/kitedit delete <name>
/kitedit rename <name> <newName>
/kitedit setrole <name> <role>
/kitedit setcooldown <name> <seconds>
/kitedit onrespawn <name> on|off
/kitedit oneperlife <name> on|off
/kitedit give <name> <player>
```

### Actions

| Action | Arguments | Effect |
| --- | --- | --- |
| `list` | none | Lists every kit and its assignment summary. |
| `create` | `<name>` | Snapshots your whole inventory into a kit, creating it or replacing its item list if it already exists. |
| `additem` | `<name>` | Adds the item in your active hotbar slot to the kit. |
| `removeitem` | `<name> <index>` | Removes one item by its 1-based `preview` index. |
| `preview` | `<name>` | Shows assignment, cooldown, flags, and numbered items. |
| `delete` | `<name>` | Deletes the kit. |
| `rename` | `<name> <newName>` | Renames a kit; fails if the new name is already taken. |
| `setrole` | `<name> <role>` | Toggles a role assignment on the kit (see Assignment below). |
| `setcooldown` | `<name> <seconds>` | Sets the per-kit redeem cooldown; `0` disables it. |
| `onrespawn` | `<name> on\|off` | Toggles give-on-respawn. |
| `oneperlife` | `<name> on\|off` | Toggles the once-per-life redeem limit. |
| `give` | `<name> <player>` | Gives the kit directly to an online player. |

Action words are lowercase and matched case-sensitively (the game's own
`WordRange` parser behavior). Kit names are matched case-insensitively.

### Item snapshot behavior

`create` walks only the hotbar and character inventories. A worn backpack's
contents live inside the backpack's own item stack already, so walking the
backpack's inventory too would capture its contents a second time; it is
skipped deliberately. Each item is stored as a `JsonItemStack` (type, code,
stack size, and attributes), with a human-readable code and quantity mirror
kept alongside it. A kit is a point-in-time snapshot, not a live link to any
item definition: running `create` again on an existing name replaces the
item list but keeps the kit's assignment, cooldown, and flags. `additem`
captures only the item currently in your active hotbar slot.

### Assignment

`AssignedTo` holds a list of scope strings; today the only scope shape is
`role:<code>`. An empty list means every player with kit access can redeem
the kit. `setrole` toggles: running it with a role the kit does not have
adds it, running it again with the same role removes it. Multiple roles can
be assigned to the same kit. Matching is against the player's current role
code at redeem time.

### Flags

`onrespawn` and `oneperlife` each accept only `on` or `off`; anything else
returns the usage line for that action. See `/kit` above for what each flag
does at redeem time.

### Cooldowns

Per-kit cooldowns are set with `setcooldown`. They share the same in-memory,
per-player-per-kit tracking and staff bypass rule described under `/kit`.

### Argument rules

Each action takes exactly the argument count in the table above:

- Too few arguments: the usage line for that action, e.g. `Usage: /kitedit
  create <name>`.
- Too many arguments (three when the action takes fewer): `/kitedit <action>
  takes N argument(s). Usage: ...`. A fourth word on the command line is
  rejected by the game's own command parser before Stratum ever sees it.
- An unknown action: `Unknown /kitedit action '<action>'. Actions: list,
  create, additem, removeitem, preview, delete, rename, setrole,
  setcooldown, onrespawn, oneperlife, give. Run /kitedit list to see
  existing kits.`

### Where changes are saved

Every mutating action writes `stratum-kits.json` immediately. `create` and
`additem` are also written to the audit log.

## Typical workflow

1. Put the exact items you want in the kit into your inventory.
2. `/kitedit create <name>` to snapshot them.
3. Use `additem` / `removeitem` to adjust the item list as needed.
4. `/kitedit preview <name>` to verify the result.
5. `/kitedit setrole <name> <role>` if the kit should be limited to a role.
6. `/kitedit setcooldown`, `onrespawn`, and `oneperlife` as needed.
7. Test with `/kit <name>` yourself, or hand it to someone with `/kitedit
   give <name> <player>`.

## Examples

```
/kitedit create starter_kit 1     # rejected: create takes 1 argument
/kitedit create starter_kit       # correct: snapshots your inventory
/kitedit preview starter_kit
/kitedit setrole starter_kit newplayer
/kit starter_kit
```

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Programming error: Incomplete command - no name or required privilege has been set` | Running a Stratum build older than this fix | Upgrade; `/kitedit` was missing its base command privilege and could not run at all. |
| `You do not have permission to use /kitedit.` | Your role does not hold the configured privilege | Grant `stratum.kitedit` (or your configured privilege) to the role. |
| `/kitedit is disabled.` | `Commands.KitEdit.Enabled` is `false` | Enable it and restart the server. |
| `Your inventory is empty; nothing to snapshot.` | `create` with nothing in the hotbar or character inventory | Pick up items first. |
| `... partially given: N given, M dropped at feet (inventory full)` | The recipient's inventory could not hold every item | Free up space, or accept the drop at their feet. |

## Configuration reference

| Key | Default | Effect |
| --- | --- | --- |
| `Commands.Enabled` | `true` | Master switch for every Stratum command, including these two. |
| `Commands.Kits.Enabled` | `true` | Registers `/kit`. |
| `Commands.Kits.Privilege` | `stratum.kits` | Privilege required to use `/kit`. |
| `Commands.Kits.CooldownSeconds` | `0` | Command-level cooldown for `/kit`, checked before any per-kit cooldown. |
| `Commands.Kits.CooldownBypassForStaff` | `true` | Staff with `Commands.StaffChat` access skip the command-level cooldown. |
| `Commands.KitEdit.Enabled` | `true` | Registers `/kitedit`. |
| `Commands.KitEdit.Privilege` | `stratum.kitedit` | Privilege required to use `/kitedit`. |
| `Commands.KitEdit.CooldownSeconds` | `0` | Command-level cooldown for `/kitedit`. |
| `Commands.KitEdit.CooldownBypassForStaff` | `true` | Staff with `Commands.StaffChat` access skip it. |

Use `/stratum access command kit` (or `kitedit`) to see the effective
privilege and cooldown for either command on a running server.

## Keeping this page in sync

This page documents behavior owned by specific symbols in
`CmdStratumKits.cs`. If one of these changes, this page needs a matching
edit:

| Documented behavior | Owning symbol |
| --- | --- |
| Registration, privileges, in-game descriptions | `CmdStratumKits` constructor |
| Action list and argument counts | `CmdStratumKits.KitEditActions`, `CmdStratumKits.ActionShape` |
| Argument rejection wording | `CmdStratumKits.CheckActionArgs`, `CmdStratumKits.UnknownAction` |
| Inventory snapshot scope | `CmdStratumKits.SnapshotInventory`, `CmdStratumKits.EncodeStack` |
| Assignment matching | `CmdStratumKits.IsAssignedTo` |
| Cooldown and staff bypass | `StratumCommandCooldowns.TryUse` |
| Once per life | `CmdStratumKits.HasRedeemedThisLife`, `MarkRedeemedThisLife`, `OnPlayerRespawn` |
| Give on respawn | `CmdStratumKits.GiveAssignedRespawnKits` |
| Item delivery and drop-at-feet | `StratumKitGiver.Give`, `TryGiveOne` |
| Storage file and shape | `StratumKitStore` |
| Config defaults | `StratumConfig.StratumCommandsConfig.Kits` / `.KitEdit` |

`scripts/smoke-test.sh` asserts the exact usage and error strings documented
above by piping the corresponding `/kitedit` and `/kit` commands into a
running server's console. A wording change here that is not mirrored there
(or vice versa) fails the smoke test.
