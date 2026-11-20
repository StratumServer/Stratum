# Role name prefixes

Stratum can put a coloured tag in front of a player's name in chat and above
their character, chosen by the player's role. A role can carry more than one
tag, and they stack in a fixed order. Implementation:
[`StratumChatFormatter.cs`](../sources/VintagestoryLib/Vintagestory.Server/StratumChatFormatter.cs)
for chat, [`StratumNametags.cs`](../sources/VintagestoryLib/Vintagestory.Server/StratumNametags.cs)
for the in-world nametag, both reading
[`StratumAppearanceConfig.cs`](../sources/VintagestoryLib/Vintagestory.Server/StratumAppearanceConfig.cs).

Server side only. No custom client: the tags ride in on the normal chat and
nametag protocol, so every stock client renders them.

## Where the config lives

`<dataPath>/stratum.json`, under `Appearance.RolePrefixes`:

```json
"RolePrefixes": {
  "Enabled": true,
  "Format": "[{tag}]",
  "Roles": {
    "admin": { "Tag": "Admin", "Color": "#ff5f57", "Bold": true, "Priority": 100 },
    "villagemod": [
      { "Tag": "Moderator", "Color": "#4cc9f0", "Bold": true, "Priority": 100 },
      { "Tag": "XYZ Villager", "Color": "#9bd77e", "Bold": false, "Priority": 10 }
    ]
  }
}
```

| Key | Meaning |
| --- | --- |
| `Enabled` | Master switch for chat prefixes. `false` turns every role's chat tag off. The nametag has its own switch, `Nametags.Enabled` / `Nametags.ApplyRolePrefix`. |
| `Format` | Wraps each tag. `{tag}` is the placeholder. `"[{tag}]"` gives `[Admin]`, `"{tag} "` gives `Admin `. |
| `Roles` | Role code to prefix, or role code to a list of prefixes. |

Each prefix entry:

| Field | Default | Meaning |
| --- | --- | --- |
| `Tag` | `Staff` | The text inside `Format`. |
| `Color` | `#ffffff` | `#rrggbb`, or a plain colour word. Chat only (see below). |
| `Bold` | `true` | Chat only. |
| `Priority` | `0` | Higher renders closer to the name's left. |
| `Enabled` | `true` | `false` drops just this one tag. |

## One tag or several

A role's value is written as a bare object for a single tag and as an array for
several. Both forms are always accepted on read. On save Stratum writes a
one-tag role back as a bare object, so a config that predates this feature is
left exactly as it was. Only a role you actually give a second tag turns into an
array.

To add a second tag to a role, change its `{ ... }` to `[ { ... }, { ... } ]`.

## Order

Tags render highest `Priority` first, left to right, then the name. With the
example above, a player in `villagemod` shows as `[Moderator][XYZ Villager]
Name`. Two tags at the same `Priority` keep the order they appear in the config
file.

## Spacing

The gap between the tags and the name comes from `Format`. `"[{tag}]"` gives
`[A][B] Name`; `"[{tag}] "` gives `[A] [B] Name`. There is no separate spacing
key. The nametag has its own `Nametags.PrefixFormat`, which defaults to
`"[{tag}] "`.

## Colour

Chat colours each tag on its own, from that tag's `Color` and `Bold`.

The nametag is one colour for the whole string. That colour is not per tag: it
comes from `Nametags.EntitlementColorByRole`, mapped from the player's role to a
Vintage Story entitlement code, because the stock client has no way to colour
part of a nametag. `Color` and `Bold` on a prefix entry do nothing for the
nametag.

## When a role has no prefix

If a role is absent from `Roles`, or every one of its tags is `Enabled: false`,
that player renders with no prefix at all: a bare name in chat and above their
head. A player who changes to such a role loses the tag their old role carried.

## Inspecting a running server

`/stratum chat` prints each configured role, the tag stack a player in that role
sees, and every tag's priority and colour, including disabled ones.

## Scope

This is one role's tags stacking. Giving a single player more than one role is a
separate, larger change, tracked in
[#274](https://github.com/StratumServer/Stratum/issues/274).

Keep a stack to two or three tags. A longer one crowds the chat line and widens
the in-world tag; nothing enforces a limit.

## Keeping this page in sync

| Documented behaviour | Owning symbol |
| --- | --- |
| Config shape, one-or-many, save form | `StratumRolePrefixList`, `StratumRolePrefixListConverter` |
| Which tags apply and their order | `StratumRolePrefixesConfig.ResolveFor` |
| Chat render | `StratumChatFormatter.TryFormat` |
| Nametag render | `StratumNametags.ApplyNametagPrefix` |
| Nametag colour | `StratumNametags.MaybeInjectEntitlement`, `StratumNametagsConfig.EntitlementColorByRole` |
| `/stratum chat` output | `CmdStratum.HandleChat` |

`scripts/smoke-test.sh` seeds a two-prefix role, boots the server, and asserts
`/stratum chat` renders the stack in `Priority` order and that the config
rewrite keeps the one-tag and many-tag shapes. A change here that is not
mirrored there fails the smoke test.
