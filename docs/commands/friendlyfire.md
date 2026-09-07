# Friendly fire command

`/friendlyfire` toggles, at runtime, whether players in the same player group
can damage each other. It is server side only and needs no custom client.
Implementation:
[`StratumFriendlyFireSystem.cs`](../../sources/VintagestoryLib/Vintagestory.Server/StratumFriendlyFireSystem.cs),
[`StratumPlayerGroups.cs`](../../sources/VintagestoryApi/Server/StratumPlayerGroups.cs),
and the `ShouldReceiveDamage` hunk in
[`EntityPlayer.cs.patch`](../../patches/VintagestoryApi/Common/Entity/EntityPlayer.cs.patch).

## At a glance

| Command | Audience | Access config key | Default privilege | Storage |
| --- | --- | --- | --- | --- |
| `/friendlyfire` | Staff | `Commands.FriendlyFire` | `stratum.friendlyfire` | `stratum.json` (`FriendlyFire.AllowGroupDamage`) |

## What "on" and "off" mean

`/friendlyfire` follows the same convention as every other server platform:
**on** means group members can hurt each other (the vanilla behaviour),
**off** protects the group. The default is on, so nothing changes until an
admin turns it off.

`AllowGroupDamage` defaults to `true`. Set it to `false` (or run
`/friendlyfire off`) and a hit from one group member to another is dropped in
`EntityPlayer.ShouldReceiveDamage`, before the health behaviour runs: no
health change, no knockback, no hurt animation, no `DidAttack` bookkeeping,
no death.

## Syntax

```
/friendlyfire
/friendlyfire status
/friendlyfire on
/friendlyfire off
/friendlyfire toggle
```

| Argument | Meaning |
| --- | --- |
| _(none)_ or `status` | Report the current state without changing it. |
| `on` | Group members can damage each other. |
| `off` | Group members cannot damage each other. |
| `toggle` | Flip the current state. |

Any other word is rejected by the game's own argument parser.

| Situation | Message |
| --- | --- |
| `status`, currently on | `Group friendly fire is on, players in the same group can damage each other.` |
| `status`, currently off | `Group friendly fire is off, players in the same group cannot damage each other.` |
| `toggle`/`on` turning it on | `Group friendly fire enabled, players in the same group can damage each other again.` |
| `toggle`/`off` turning it off | `Group friendly fire disabled, players in the same group can no longer damage each other.` |
| No permission | `You do not have permission to use /friendlyfire.` |
| Disabled in config | `/friendlyfire is disabled.` |
| Command cooldown active | `Wait <N>s before using /friendlyfire again.` |

Every state change is written to `stratum.json` and to the audit log, and is
re-applied on `/stratum reload`.

## What counts as "the same group"

A group here is a player-created group, one made with `/group create`. Two
players are on the same group when they both hold a live membership (any
level above `None`) in the same group. The default chat groups (general,
server info, and so on) never count. Membership is read from
`IServerPlayer.ServerData.PlayerGroupMemberships`, the same records the group
chat and `/group` commands use.

## What the toggle does and does not cover

- **Melee and projectiles:** covered. Both resolve through
  `DamageSource.GetCauseEntity()`.
- **Healing:** never blocked. A group member can always heal another, toggle
  or not.
- **Self damage:** never blocked. Fall, drowning, hunger, and a player's own
  hits always apply.
- **Explosions:** covered when the igniter is known (a bomb records who lit
  it). The igniter still takes damage from their own blast.
- **Damage over time (bleeding, burning):** the toggle is checked when the
  hit that starts the effect lands, not per tick. An effect already running
  when you flip the toggle keeps ticking. Vanilla has no player-inflicted
  damage over time, so this only matters with mods.
- **Mods that patch the damage path with Harmony:** a mod that replaces
  `Entity.ReceiveDamage`, `ShouldReceiveDamage`, the melee interaction
  handler, or the projectile impact path, and does not call the original,
  bypasses this. Stratum enforces the rule at several independent points to
  narrow that, and logs a warning naming any mod that patches one of them
  while the toggle is on, but it cannot promise a mod will not override it.
  No server platform can.

## Prerequisites

- `Commands.Enabled` and `Commands.FriendlyFire.Enabled` both default to
  `true`. Setting `Commands.FriendlyFire.Enabled` to `false` skips
  registering the command; the change takes effect on the next server
  restart.
- `stratum.friendlyfire` is **not** granted to any default role. Grant it
  with `/roles grant <role> stratum.friendlyfire` or by editing
  `serverroles.json`.
- The server console always passes Stratum's access check, so
  `/friendlyfire` works from the console for diagnostics.

## Configuration reference

| Key | Default | Effect |
| --- | --- | --- |
| `FriendlyFire.AllowGroupDamage` | `true` | `false` blocks damage between members of the same player group. |
| `Commands.Enabled` | `true` | Master switch for every Stratum command. |
| `Commands.FriendlyFire.Enabled` | `true` | Registers `/friendlyfire`. |
| `Commands.FriendlyFire.Privilege` | `stratum.friendlyfire` | Privilege required to use `/friendlyfire`. |
| `Commands.FriendlyFire.CooldownSeconds` | `0` | Command-level cooldown. |

Use `/stratum access command friendlyfire` to see the effective privilege
and cooldown on a running server.

## Keeping this page in sync

| Documented behavior | Owning symbol |
| --- | --- |
| Registration, privilege, argument words | `StratumFriendlyFireSystem` constructor |
| Status and toggle wording | `StratumFriendlyFireSystem.HandleToggle` |
| Config default | `StratumFriendlyFireConfig.AllowGroupDamage` |
| Group membership rule | `StratumPlayerGroups.SharesGroup` |
| The blocking hit | `EntityPlayer.ShouldReceiveDamage` (`patches/VintagestoryApi/...`) |

`scripts/smoke-test.sh` pipes `/friendlyfire` commands into a running
server's console and asserts the exact status and toggle strings above. The
group-membership rule and the damage-path blocking are proven against real
connected players by the private Atlas regression suite
(`research/atlas-tests/stratum-pr-validation/FriendlyFireScenarios.cs`), not
by the public smoke test, which has no second player.
