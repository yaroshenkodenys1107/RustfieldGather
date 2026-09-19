# RustfieldGather

**Gathering rebuilt around a fixed number of hits: every node, tree, bush and barrel pays exactly what the config says, over exactly as many swings as you ask for, with the break stages of the model laid out across those swings.**

Vanilla gathering scales the payout by the tool, the hit spot and the node's remaining resources, which
stops making sense the moment a server multiplies the rates. This plugin replaces that with a flat
contract: a stone node is four swings and a fixed pile of stone, a tree is four swings and a fixed pile
of wood, a barrel is one hit and its contents land in the inventory. Tools no longer matter — a rock
and a jackhammer take the same number of swings — so nobody has to grind for a tier of pickaxe to farm
at a sensible pace.

The hit itself is never cancelled, so the sparks, the sound, the animation and the tool wear all stay
exactly as the game plays them. Only the payout and the moment of death are ours.

Author: **Denys Yaroshenko** · Rust (Carbon / Oxide) · **v1.1.0**

> **Beta.** Runs on a live public server. Config keys may still be renamed between minor versions;
> every rename is listed in the changelog.

---

## What it does

| Section | Covers | Summary |
|---|---|---|
| **Hittable** | ore nodes, trees, dead logs, driftwood, wood piles, cacti | A fixed number of hits and a fixed payout per section. The visible break stages of the model are spread over those hits. |
| **Collectible** | hemp, ore/wood/stone pickups, diesel barrels, mushrooms, berries, crops | A fixed payout on pickup. The whole pickup is taken over, so no stray vanilla item comes with it. |
| **Corpses** | animal and player corpses | A fixed number of hits and a fixed payout, same rules as Hittable. |
| **Barrels** | loot and oil barrels | The contents go straight into the inventory and the barrel gibs. Melee-only or any damage. |

---

## Requirements

- Rust dedicated server with **Carbon** (or Oxide — the plugin uses no Carbon-only API).
- No dependencies.

## Installation

1. Drop `RustfieldGather.cs` into `carbon/plugins/`.
2. On first load the plugin writes `carbon/configs/RustfieldGather.json` with a small demo config.
   **Nothing is active until you fill it in** — every section carries its own `enable` flag.
3. Fill the config in-game with `/gather`, or edit the file and run `rustfieldgather.reload`.
4. Only then unload whatever gathering plugin you are replacing. An enabled section takes over an
   object completely; a disabled one leaves it entirely vanilla, so the two can be swapped over one
   section at a time.

`config/RustfieldGather.json` in this repository is the live configuration of a x1000 server, kept
here as a reference for the shape of the file, not as a recommended set of numbers.

---

## The three payout modes

Every Hittable section — and the Corpses section — carries a `mode`. It decides what the numbers in
`rewards` actually mean:

| `mode` | `hits` | What one object pays |
|---|---|---|
| `"instant"` | ignored, always 1 | The written amount, on a single hit. The object breaks at once. |
| `"per hit"` | used | The written amount on **every** hit — the object pays `amount × hits` in total. |
| `"total"` | used | The written amount **is** the whole object, cut into one share per hit. |

```json
"trees": {
  "enable": true,
  "hits": 4,
  "mode": "per hit",
  "rewards": [ { "shortname": "wood", "amount": 50000 } ]
}
```

Four swings, 50 000 wood each, 200 000 wood for the tree. Switch `mode` to `"total"` and the same
tree pays 50 000 wood in four portions of 12 500.

The shares of a `"total"` split add up to exactly the configured amount even when it does not divide
evenly: each hit is handed the gap between two running fractions rather than a rounded share of its
own, so 50 001 over four hits is 12 500 + 12 500 + 12 500 + 12 501. A total smaller than the hit count
is accepted but warned about — the first swings then pay nothing.

`"per hit"` is the default, and it is what a config written before v1.1.0 keeps doing when the `mode`
key is added to it.

---

## Break stages

Ore nodes and wood piles carry a table of destruction stages baked into the prefab. The server picks
one from the node's remaining HP: `StagedResourceEntity.FindBestStage` walks the table from the top
and takes the first stage whose threshold the current HP fraction still reaches.

| Object | Stages | Thresholds |
|---|---|---|
| stone / metal / sulfur ore (all biomes, plus the radtown variants) | 4 | 75% → 50% → 25% → 0% |
| high quality metal ore | 4 | 75% → 50% → 25% → 0% |
| wood pile | 3 | 75% → 50% → 25% |
| trees, dead logs, driftwood, cacti, every collectible, barrels, corpses | none | — |

**The stage count cannot be changed.** It is prefab data; all a server can do is choose which stages
are shown and for how long. Exactly 24 prefabs in the game carry a stage table at all: the 14
gatherable ones above, and 10 `monumentblocker_*` props, which yield nothing and are left alone.

So the hits are laid over the stages instead of over the HP bar. The stage table has one entry per
step, the last step being the break itself, and the swings are divided between the steps with the
remainder going to the **first** ones:

| Hits on a 4-stage node | Steps | What the player sees |
|---|---|---|
| 1 | 0+0+0+1 | breaks |
| 2 | 1+0+0+1 | stage 1 → breaks |
| 3 | 1+1+0+1 | stage 1 → stage 2 → breaks |
| 4 | 1+1+1+1 | stage 1 → stage 2 → stage 3 → breaks |
| 5 | 2+1+1+1 | stage 1 ×2 → stage 2 → stage 3 → breaks |
| 6 | 2+2+1+1 | stage 1 ×2 → stage 2 ×2 → stage 3 → breaks |
| 9 | 3+2+2+2 | stage 1 ×3 → stage 2 ×2 → stage 3 ×3 → breaks |

Fewer hits than stages and it is the **late** stages that get skipped; more hits than stages and the
extra swings pile onto the early ones, with the node holding its last visible stage until the final
swing kills it.

The HP is then placed in the **middle** of the chosen stage's band rather than stepped down linearly.
A linear fall lands exactly ON the thresholds: with four hits the first swing would leave the node at
75% — still the intact rock — and the most broken stage would never be seen at all, because the node
dies at 0 instead of stopping there.

`rustfieldgather.stages` prints the table for every staged prefab on the map together with the plan
its current config produces, so a hit count can be checked without going outside.

---

## Configuration

```json
{
  "Hittable": {
    "stone-nodes": {
      "enable": true,
      "hits": 4,
      "mode": "per hit",
      "rewards": [ { "shortname": "stones", "amount": 10000 } ]
    }
  },
  "Collectible": {
    "hemp-collectable": {
      "enable": true,
      "rewards": [
        { "shortname": "lowgradefuel", "amount": 1000 },
        { "shortname": "cloth", "amount": 5000 }
      ]
    }
  },
  "Corpses": { "enable": true, "hits": 1, "mode": "per hit", "rewards": [ … ] },
  "Barrels": { "enable": true, "mode": 0 }
}
```

### Hittable keys

A key is looked up in three ways, in this order:

1. **A group name.** The plugin ships the groups below, each covering every prefab of that kind, so
   the config holds six lines instead of two hundred prefab names:
   `stone-nodes`, `metal-nodes`, `sulfur-nodes`, `hqm-nodes`, `trees`, `dead-logs`, `driftwood`,
   `wood-pile`, `cactus`, `mushrooms`, `halloween-props`.
2. **A single prefab name** (`ore_stone`), which outranks the group it belongs to.
3. **A gathered resource shortname** (`wood`, `stones`, `metal.ore`, `sulfur.ore`, `hq.metal.ore`,
   `cloth`), which catches anything that yields that resource and has no rule of its own.

Ore prefabs exist in biome variants — desert, snow, radtown — that share one short prefab name, so a
group covers all of them and they cannot be given separate rules.

### Collectible keys

Prefab names (`hemp-collectable`, `wood-collectable`, `diesel_collectable`, …). Collectibles have no
hit count. Eating a pickup is left alone: it is a deliberate act on the vanilla contents.

### Barrels

`mode` `0` breaks a barrel on melee only, `1` on any damage including bullets. The barrel's own loot
is what lands in the inventory — there is no reward list to override it with.

### Rules and rejections

- A section with `enable: false` is not merely empty: the object goes back to being completely vanilla.
- An empty reward list, an unknown item shortname, a zero amount or `hits` below 1 are all refused,
  loudly, and the section is left vanilla. **The config file is never overwritten on a rejection.**
- A config that cannot be parsed at all leaves the plugin running vanilla mechanics with the file
  untouched, rather than replacing it with defaults.

---

## The in-game editor

`/gather` opens a panel with four tabs — NODES, COLLECT, CORPSES, BARRELS. Pick a section on the
left, and the card on the right holds its enable switch, its hit count, the three mode buttons and
its reward rows. Every change is applied and written to disk immediately; **SAVE AND APPLY** forces
the file to disk, reads it back and lays the rules over the live objects again.

The amount column header says what the numbers mean in the current mode, and in `total` mode each row
shows what one swing actually pays.

## Permissions

| Permission | Grants |
|---|---|
| `rustfieldgather.admin` | The `/gather` panel and the console commands. |

Server owners at auth level 2 have access without the permission. Auth level 1 (moderator) does not —
three of the console commands walk every entity on the map.

## Commands

### Chat

| Command | Does |
|---|---|
| `/gather` | Opens the editor panel. |

### Console

| Command | Does |
|---|---|
| `rustfieldgather.reload` | Re-reads the config file and applies it. |
| `rustfieldgather.list` | Every hittable kind and collectible prefab on the map, with counts. |
| `rustfieldgather.stages` | Break-stage tables, max HP, and the hit-by-hit plan the config produces. |
| `rustfieldgather.audit` | Cross-checks the config and the group table against what is on the map: duplicate prefabs, dead keys, uncovered objects, broken item shortnames. |

Run from the server console or RCON they need no permission; typed into F1 by a player they need
`rustfieldgather.admin`.

---

## How it holds the node together

Vanilla `ResourceDispenser.DoGather` decides the damage of a gathering hit itself, from the fraction
of the node's contents the hit consumed — and on a tree, which carries a single resource, that
fraction is the whole node on the first swing, so the tree dies immediately. The plugin therefore
raises the node's HP to eight times its maximum for the duration of the native hit: the vanilla
damage lands on the inflated pool and cannot kill anything, and on the next tick the HP is set to the
value the swing count calls for. The moment of death stays with the plugin, and the hit still plays
out in full.

The vanilla payout is muted on the dispenser's own hooks, where the item does not exist yet, so no
zero-amount pickup notice ever reaches the player.

## Compatibility

The plugin claims `OnMeleeAttack`, `OnDispenserGather`, `OnDispenserBonus`, `OnCollectiblePickup` and
`OnEntityTakeDamage` (barrels only). Run it alongside another gathering plugin and whichever hooks
first wins, unpredictably — disable the other one section by section as this one takes over.

Collectible pickups are taken over whole rather than by swapping the item list, because vanilla
`CollectibleEntity.DoPickup` separately looks for a `RandomItemDispenser` on the prefab and hands out
its items too. With the list swapped, hemp still produced a fishing worm.

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## Licence

Proprietary. See [LICENSE](LICENSE).
