# Changelog

All notable changes to RustfieldGather. Versions follow `major.minor.patch`.

While the plugin is in beta, config keys may be renamed between minor versions.
Every rename is listed here under **Config**.

---

## 1.2.0 — Beta

**Added**

- Corpses are split in two: one rule for players and every NPC, another for
  animals. Human-shaped corpses all carry an inventory, so the split follows
  the game's own classes: a lootable corpse is human (`player_corpse`, both
  scientist generations — `scientist2` is a bare `LootableCorpse`, not a
  `PlayerCorpse` — murderers, gingerbread men, Frankenstein's pet), and
  everything else is an animal, the horse included despite its saddlebags.
  No prefab list to keep up to date.
- The CORPSES tab in `/gather` lists the two rules, **Players and NPCs** and
  **Animals**, each with its own switch, hit count, mode and rewards.
- `rustfieldgather.audit` prints every butcherable corpse prefab in the game
  and which of the two rules it falls under.

**Changed**

- The built-in defaults are now the Rustfield x1000000 configuration, active
  out of the box — 9 hittable groups, 27 collectibles, both corpse rules and
  barrels. The old demo config, with every section switched off, is gone. An
  existing config file still outranks the defaults, as before.

**Config**

- `Corpses` is replaced by `HumanCorpses` and `AnimalCorpses`, same shape.
  A file that still has `Corpses` is migrated on load: its rule is copied into
  both new sections, so nothing changes in game until one of them is edited,
  and the file is rewritten once without the old key.

---

## 1.1.0 — Beta

**Added**

- A payout mode on every Hittable section and on Corpses, chosen with three
  buttons in the panel or the new `mode` config key. `"per hit"` is what the
  plugin has always done — the written amount on every swing, so the object
  pays `amount × hits` in total. `"total"` reads the same number as the whole
  object and cuts it into one share per hit: 50 000 wood over two hits is
  25 000 and 25 000, and the player ends up with exactly 50 000. `"instant"`
  hands the amount over on a single hit and breaks the object at once, and
  hides the hit count, which means nothing in that mode.
  The shares of a `"total"` split add up to exactly the configured amount even
  when it does not divide evenly — each hit gets the gap between two running
  fractions rather than a rounded share, so 50 001 over four hits is
  12 500 + 12 500 + 12 500 + 12 501.
- `rustfieldgather.stages` now reports each prefab's maximum HP and the
  hit-by-hit stage plan its current config produces, not just the stage
  thresholds. A hit count can be checked without going outside.
- The amount column in the panel says what the number means in the current mode,
  and in `"total"` mode every reward row shows what one swing actually pays.

**Fixed**

- Break stages were being skipped. The HP was stepped down linearly
  (`1 - hit / hits`), which lands it exactly ON the stage thresholds baked into
  the prefab: ore carries four stages at 75 / 50 / 25 / 0 %, so with four hits
  the first swing left the node at 75 % — still the intact rock, no visible
  change — and the most broken stage was never shown at all, because the node
  died at 0 % instead of stopping there. Two visible changes out of four swings.
  The stage is now chosen first, from how far through the swings the player is,
  and the HP is placed in the middle of that stage's band where no rounding can
  push it into a neighbour.
  The hits are spread over the stage table with the remainder going to the
  first stages: four hits on a rock are stage 1, 2, 3, break; nine hits are
  3 + 2 + 2 + 2. Fewer hits than stages and the late stages are the ones
  skipped; more hits than stages and the node holds its last visible stage
  until the final swing. Objects with no stage table — trees, dead logs,
  driftwood, cacti, corpses — keep the plain linear fall, which nothing renders
  either way.

**Config**

- New key `mode` on every `Hittable` section and on `Corpses`:
  `"instant"`, `"per hit"` or `"total"`. A file written before this version has
  no such key and reads as `"per hit"`, which is exactly what it did before, so
  nothing changes until the key is set.

---

## 1.0.0 — Beta

First release. Gathering rebuilt around a fixed number of hits, replacing the
vanilla model where the payout depends on the tool, the hit spot and the node's
remaining contents.

**Added**

- **Hittable** sections: a fixed hit count and a fixed payout for ore nodes,
  trees, dead logs, driftwood, wood piles and cacti. Group keys
  (`stone-nodes`, `trees`, …) cover every prefab of a kind, so the config holds
  a handful of lines instead of two hundred prefab names; a single prefab name
  or a gathered resource shortname works as a key too.
- **Collectible** sections: a fixed payout for hemp, ore and wood pickups,
  diesel barrels and the rest. The pickup is taken over whole rather than by
  swapping the item list, because vanilla `DoPickup` separately hands out the
  prefab's `RandomItemDispenser` items — with the list swapped, hemp still
  produced a fishing worm.
- **Corpses** and **Barrels** sections. A barrel's contents go straight into the
  inventory and the barrel gibs, on melee only or on any damage.
- `/gather`, an in-game editor for the whole config with immediate apply, and
  `rustfieldgather.list` / `.audit` / `.stages` for checking a config against
  what actually stands on the map.

**Notes**

- The hit is never cancelled, so the sparks, the sound, the animation and the
  tool wear play exactly as the game plays them; only the payout and the moment
  of death are the plugin's.
- The node's HP is raised to eight times its maximum for the duration of the
  native hit. Vanilla `ResourceDispenser.DoGather` derives its damage from the
  fraction of the node's contents the swing consumed, and on a tree — a single
  resource — that is the whole node on the first swing, which would kill it
  before the plugin could count anything.
- A rejected section is left completely vanilla and the config file is never
  overwritten, so the plugin can take over one section at a time while the
  gathering plugin it replaces is still loaded.
