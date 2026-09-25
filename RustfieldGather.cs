using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RustfieldGather", "Rustfield", "1.2.0")]
    [Description("Reworked gathering: instant / per hit / total payouts, hits laid over the break stages, instant barrels.")]
    public class RustfieldGather : RustPlugin
    {
        #region Configuration

        private class Reward
        {
            [JsonProperty("shortname")] public string Shortname = string.Empty;
            [JsonProperty("amount")] public int Amount;
        }

        private class HittableEntry
        {
            [JsonProperty("enable")] public bool Enable;
            [JsonProperty("hits")] public int Hits = 1;

            // What the amounts below mean:
            //   "instant" - all of it on a single hit, the object breaks at once (hits ignored);
            //   "per hit" - all of it on EVERY hit, so the object pays amount x hits in total;
            //   "total"   - the amount IS the whole object, cut into one share per hit.
            [JsonProperty("mode")] public string Mode = ModePerHit;

            [JsonProperty("rewards", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<Reward> Rewards = new List<Reward>();
        }

        private class CollectibleEntry
        {
            [JsonProperty("enable")] public bool Enable;

            [JsonProperty("rewards", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<Reward> Rewards = new List<Reward>();
        }

        private class BarrelEntry
        {
            [JsonProperty("enable")] public bool Enable;
            [JsonProperty("mode")] public int Mode;
        }

        private enum PayMode { Instant, PerHit, Total }

        private const string ModeInstant = "instant";
        private const string ModePerHit = "per hit";
        private const string ModeTotal = "total";

        private static string ModeName(PayMode mode)
        {
            switch (mode)
            {
                case PayMode.Instant: return ModeInstant;
                case PayMode.Total: return ModeTotal;
                default: return ModePerHit;
            }
        }

        // Silent on purpose: this runs on every panel redraw. An unreadable value is reported
        // once, by Validate, when the rules are laid out.
        private static PayMode ModeOf(string raw)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' '))
            {
                case ModeInstant:
                case "one":
                case "one hit": return PayMode.Instant;
                case ModeTotal:
                case "split":
                case "capacity": return PayMode.Total;
                case "perhit":
                case "hit":
                case "fixed": return PayMode.PerHit;
                default: return PayMode.PerHit;
            }
        }

        private static bool KnownMode(string raw)
        {
            var text = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');
            return text == ModeInstant || text == ModePerHit || text == ModeTotal ||
                   text == "one" || text == "one hit" || text == "split" || text == "capacity" ||
                   text == "perhit" || text == "hit" || text == "fixed";
        }

        private class Configuration
        {
            [JsonProperty("Hittable", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, HittableEntry> Hittable = new Dictionary<string, HittableEntry>();

            [JsonProperty("Collectible", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, CollectibleEntry> Collectible = new Dictionary<string, CollectibleEntry>();

            // Players and every NPC on one side, animals (horses included) on the other.
            [JsonProperty("HumanCorpses")] public HittableEntry HumanCorpses;
            [JsonProperty("AnimalCorpses")] public HittableEntry AnimalCorpses;

            [JsonProperty("Barrels")] public BarrelEntry Barrels = new BarrelEntry();

            // Up to 1.1.0 one rule served every corpse. It is only read, never written: a file
            // that still has it hands it to both sides the first time it loads.
            [JsonProperty("Corpses")] public HittableEntry LegacyCorpses;
            public bool ShouldSerializeLegacyCorpses() => false;
        }

        private Configuration _config;

        private static Reward R(string shortname, int amount) => new Reward { Shortname = shortname, Amount = amount };

        private static HittableEntry Node(int hits, params Reward[] rewards) => new HittableEntry
        {
            Enable = true, Hits = hits, Mode = ModeTotal, Rewards = rewards.ToList()
        };

        private static CollectibleEntry Pick(params Reward[] rewards) => new CollectibleEntry
        {
            Enable = true, Rewards = rewards.ToList()
        };

        private static HittableEntry DefaultCorpse() => new HittableEntry
        {
            Enable = true, Hits = 1, Mode = ModePerHit, Rewards = new List<Reward> { R("largemedkit", 25) }
        };

        // The Rustfield x1000000 values, tuned on the live server.
        protected override void LoadDefaultConfig()
        {
            _config = new Configuration
            {
                Hittable = new Dictionary<string, HittableEntry>
                {
                    ["stone-nodes"] = Node(4, R("stones", 10000)),
                    ["metal-nodes"] = Node(4, R("metal.fragments", 5000), R("metal.refined", 200)),
                    ["sulfur-nodes"] = Node(4, R("gunpowder", 1500), R("sulfur", 500)),
                    ["hqm-nodes"] = Node(4, R("metal.refined", 800), R("metal.fragments", 5000)),
                    ["trees"] = Node(5, R("wood", 10000)),
                    ["dead-logs"] = Node(3, R("wood", 7500)),
                    ["driftwood"] = Node(3, R("wood", 7500)),
                    ["wood-pile"] = Node(3, R("wood", 7500)),
                    ["cactus"] = Node(1, R("cloth", 1000), R("largemedkit", 25))
                },
                Collectible = new Dictionary<string, CollectibleEntry>
                {
                    ["hemp-collectable"] = Pick(R("lowgradefuel", 250), R("cloth", 500)),
                    ["wood-collectable"] = Pick(R("wood", 15000)),
                    ["stone-collectable"] = Pick(R("stones", 5000)),
                    ["metal-collectable"] = Pick(R("metal.fragments", 3000)),
                    ["sulfur-collectable"] = Pick(R("sulfur", 500), R("gunpowder", 1500)),
                    ["hqm-collectable"] = Pick(R("metal.refined", 150)),
                    ["mushrooms"] = Pick(R("mushroom", 25)),
                    ["diesel_collectable"] = Pick(R("diesel_barrel", 1), R("lowgradefuel", 10000)),
                    ["coconut-spawn"] = Pick(R("coconut", 25)),
                    ["corn-collectable"] = Pick(R("corn", 25)),
                    ["potato-collectable"] = Pick(R("potato", 25)),
                    ["pumpkin-collectable"] = Pick(R("pumpkin", 25)),
                    ["wheat-collectable"] = Pick(R("wheat", 25)),
                    ["sunflower-collectable"] = Pick(R("sunflower", 25)),
                    ["orchid-collectable"] = Pick(R("orchid", 25)),
                    ["rose-collectable"] = Pick(R("rose", 25)),
                    ["berry-black-collectable"] = Pick(R("black.berry", 25)),
                    ["berry-blue-collectable"] = Pick(R("blue.berry", 25)),
                    ["berry-green-collectable"] = Pick(R("green.berry", 25)),
                    ["berry-red-collectable"] = Pick(R("red.berry", 25)),
                    ["berry-white-collectable"] = Pick(R("white.berry", 25)),
                    ["berry-yellow-collectable"] = Pick(R("yellow.berry", 25)),
                    ["halloween-bone-collectable"] = Pick(R("bone.fragments", 1000)),
                    ["halloween-metal-collectable"] = Pick(R("metal.ore", 2500)),
                    ["halloween-stone-collectable"] = Pick(R("stones", 5000)),
                    ["halloween-sulfur-collectible"] = Pick(R("sulfur", 500), R("gunpowder", 1500)),
                    ["halloween-wood-collectable"] = Pick(R("wood", 10000))
                },
                HumanCorpses = DefaultCorpse(),
                AnimalCorpses = DefaultCorpse(),
                Barrels = new BarrelEntry { Enable = true, Mode = 0 }
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new JsonException("config is empty");
                if (Normalize())
                {
                    Puts("Corpse rules split into HumanCorpses and AnimalCorpses - config file updated.");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                PrintError($"Config unreadable ({e.Message}) - the plugin runs vanilla mechanics, the file was NOT overwritten.");
                _config = new Configuration { HumanCorpses = new HittableEntry(), AnimalCorpses = new HittableEntry() };
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        // Any section can arrive from the file as null, which would take the whole plugin down.
        // True when a corpse section had to be filled in, so the file lacks what is now in memory.
        private bool Normalize()
        {
            bool corpsesAdded = _config.HumanCorpses == null || _config.AnimalCorpses == null;
            if (_config.Hittable == null) _config.Hittable = new Dictionary<string, HittableEntry>();
            if (_config.Collectible == null) _config.Collectible = new Dictionary<string, CollectibleEntry>();
            if (_config.Barrels == null) _config.Barrels = new BarrelEntry();

            var legacy = _config.LegacyCorpses;
            _config.LegacyCorpses = null;
            if (_config.HumanCorpses == null) _config.HumanCorpses = legacy != null ? Copy(legacy) : DefaultCorpse();
            if (_config.AnimalCorpses == null) _config.AnimalCorpses = legacy != null ? Copy(legacy) : DefaultCorpse();

            foreach (var kv in _config.Hittable)
                if (kv.Value != null && kv.Value.Rewards == null) kv.Value.Rewards = new List<Reward>();
            foreach (var kv in _config.Collectible)
                if (kv.Value != null && kv.Value.Rewards == null) kv.Value.Rewards = new List<Reward>();
            if (_config.HumanCorpses.Rewards == null) _config.HumanCorpses.Rewards = new List<Reward>();
            if (_config.AnimalCorpses.Rewards == null) _config.AnimalCorpses.Rewards = new List<Reward>();
            return corpsesAdded;
        }

        private static HittableEntry Copy(HittableEntry source) => new HittableEntry
        {
            Enable = source.Enable, Hits = source.Hits, Mode = source.Mode,
            Rewards = (source.Rewards ?? new List<Reward>())
                .Where(r => r != null)
                .Select(r => R(r.Shortname, r.Amount))
                .ToList()
        };

        #endregion

        #region Prefab groups

        // Group membership is hard-coded on purpose: the config should hold a group name and its
        // rewards, not a wall of a hundred and fifty prefab names. A section key that is absent
        // here still works on its own: as a single prefab name, or as a gathered resource.
        private static readonly Dictionary<string, string[]> PrefabGroups =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // stone ore
            ["stone-nodes"] = new[]
            {
                "ore_stone", "stone-ore"
            },
            // metal ore
            ["metal-nodes"] = new[]
            {
                "ore_metal", "metal-ore"
            },
            // sulfur ore
            ["sulfur-nodes"] = new[]
            {
                "ore_sulfur", "sulfur-ore"
            },
            // hqm ore
            ["hqm-nodes"] = new[]
            {
                "hqm-ore"
            },
            // standing trees of every species and size
            ["trees"] = new[]
            {
                "american_beech_a", "american_beech_a_dead", "american_beech_b", "american_beech_c",
                "american_beech_d", "american_beech_e", "american_beech_e_dead", "birch_big_temp",
                "birch_big_tundra", "birch_large_temp", "birch_large_tundra", "birch_medium_temp",
                "birch_medium_tundra", "birch_small_temp", "birch_small_tundra", "birch_tiny_temp",
                "birch_tiny_tundra", "douglas_fir_a", "douglas_fir_a_snow", "douglas_fir_b",
                "douglas_fir_b_snow", "douglas_fir_c", "douglas_fir_c_snow", "douglas_fir_d",
                "douglas_fir_d_small", "hura_crepitans_a", "hura_crepitans_b", "hura_crepitans_c",
                "hura_crepitans_d", "hura_crepitans_e", "hura_crepitans_sapling_a",
                "hura_crepitans_sapling_b", "hura_crepitans_sapling_c", "hura_crepitans_sapling_d",
                "mauritia_flexuosa_l", "mauritia_flexuosa_m", "mauritia_flexuosa_s",
                "mauritia_flexuosa_sapling", "mauritia_flexuosa_xs", "oak_b", "oak_c", "oak_d", "oak_e",
                "oak_f", "palm_tree_med_a_entity", "palm_tree_short_a_entity", "palm_tree_short_b_entity",
                "palm_tree_short_c_entity", "palm_tree_small_a_entity", "palm_tree_small_b_entity",
                "palm_tree_small_c_entity", "palm_tree_tall_a_entity", "palm_tree_tall_b_entity",
                "palm_tree_tropical_med_a_entity", "palm_tree_tropical_med_b_entity",
                "palm_tree_tropical_short_a_entity", "palm_tree_tropical_short_b_entity",
                "palm_tree_tropical_short_c_entity", "palm_tree_tropical_short_d_entity",
                "palm_tree_tropical_short_e_entity", "palm_tree_tropical_small_a_entity",
                "palm_tree_tropical_small_b_entity", "palm_tree_tropical_small_c_entity",
                "palm_tree_tropical_tall_a_entity", "palm_tree_tropical_tall_b_entity", "pine_a",
                "pine_a_snow", "pine_b", "pine_b snow", "pine_c", "pine_c_snow", "pine_d", "pine_d_snow", "pine_dead_a",
                "pine_dead_b", "pine_dead_c", "pine_dead_d", "pine_dead_e", "pine_dead_f",
                "pine_dead_snow_a", "pine_dead_snow_b", "pine_dead_snow_c", "pine_dead_snow_d",
                "pine_dead_snow_e", "pine_dead_snow_f", "pine_sapling_a", "pine_sapling_a_snow",
                "pine_sapling_b", "pine_sapling_b_snow", "pine_sapling_c", "pine_sapling_c_snow",
                "pine_sapling_d", "pine_sapling_d_snow", "pine_sapling_e", "pine_sapling_e_snow",
                "schizolobium_a", "schizolobium_b", "schizolobium_c", "schizolobium_sapling_a",
                "swamp_tree_a", "swamp_tree_b", "swamp_tree_c", "swamp_tree_d", "swamp_tree_e",
                "swamp_tree_f", "trumpet_tree_a", "trumpet_tree_b", "trumpet_tree_c", "trumpet_tree_d",
                "trumpet_tree_sapling_a", "trumpet_tree_sapling_b", "trumpet_tree_sapling_c",
                "trumpet_tree_sapling_d", "trumpet_tree_sapling_e", "vineswingingtree02",
                "vineswingingtree03", "vineswingingtreeprefab"
            },
            // fallen logs
            ["dead-logs"] = new[]
            {
                "dead_log_a", "dead_log_b", "dead_log_c"
            },
            // driftwood on the shore
            ["driftwood"] = new[]
            {
                "driftwood_1", "driftwood_2", "driftwood_3", "driftwood_4", "driftwood_5",
                "driftwood_set_1", "driftwood_set_2", "driftwood_set_3"
            },
            // wood pile
            ["wood-pile"] = new[]
            {
                "wood-pile"
            },
            // cacti
            ["cactus"] = new[]
            {
                "cactus-1", "cactus-2", "cactus-3", "cactus-4", "cactus-5", "cactus-6", "cactus-7"
            },
            // mushrooms
            ["mushrooms"] = new[]
            {
                "mushroom-cluster-5", "mushroom-cluster-6"
            },
            // halloween props
            ["halloween-props"] = new[]
            {
                "halloween-bone-collectable", "halloween-metal-collectable", "halloween-stone-collectable",
                "halloween-sulfur-collectible", "halloween-wood-collectable"
            },
        };

        // Barrels that break straight into the inventory. The diesel one is not here: it has its own.
        private static readonly string[] BarrelPrefabs =
        {
            "loot_barrel_1", "loot_barrel_2", "loot-barrel-1", "loot-barrel-2", "oil_barrel"
        };


        private static IEnumerable<string> Targets(string key)
        {
            string[] group;
            if (PrefabGroups.TryGetValue(key, out group)) return group;
            return new[] { key };
        }

        #endregion

        #region State

        private class Portion
        {
            public ItemDefinition Def;
            public int Amount;
        }

        // One prepared hittable rule: what to hand out, over how many hits, and whether the
        // amounts are per hit or the total for the whole object.
        private class Rule
        {
            public List<Portion> Portions;
            public PayMode Mode;
            public int Hits;    // what the section asked for
            public int Swings;  // what that means here: instant is always a single hit
        }

        private readonly Dictionary<string, Rule> _hittable =
            new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Portion>> _collectiblePortions =
            new Dictionary<string, List<Portion>>(StringComparer.OrdinalIgnoreCase);
        private Rule _humanCorpseRule;
        private Rule _animalCorpseRule;
        private readonly HashSet<string> _barrelPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly FieldInfo ResourceHealth =
            typeof(ResourceEntity).GetField("health",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly MethodInfo ResourceHealthChanged =
            typeof(ResourceEntity).GetMethod("OnHealthChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly MethodInfo StagedGetInfo =
            typeof(StagedResourceEntity).GetMethod("GetInfo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo CombatHealth =
            typeof(BaseCombatEntity).GetField("_health",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Anything human-shaped carries an inventory, so its corpse is lootable: players, every
        // scientist generation (scientist2 is a bare LootableCorpse, not a PlayerCorpse), murderers,
        // gingerbread men. Animal corpses are plain BaseCorpse, save the horse and its saddlebags.
        private static bool IsHumanCorpse(BaseEntity corpse)
            => corpse is LootableCorpse && !(corpse is HorseCorpse);

        private Rule CorpseRule(BaseEntity corpse)
            => IsHumanCorpse(corpse) ? _humanCorpseRule : _animalCorpseRule;

        // Headroom for the duration of the native hit. Vanilla ResourceDispenser.DoGather decides
        // the damage itself: it looks at what fraction of containedItems the hit consumed and deals
        // (fraction * MaxHealth). When that fraction hits zero - and on a tree, which carries a
        // single resource, that happens on the very first swing - it deals exactly MaxHealth and the
        // node dies on the spot, never reaching our NextTick. So HP is raised before the hit:
        // even a full killing blow cannot bring it to zero, and the moment of death stays ours.
        private const float HealthHeadroom = 8f;

        private readonly Dictionary<ulong, int> _hitsDone = new Dictionary<ulong, int>();
        private readonly Dictionary<string, string> _kindByPrefab = new Dictionary<string, string>();

        // Break-stage thresholds per prefab, read once off the prefab's StagedDestructionEntityInfo.
        // A prefab with no stages is cached as null so it is not looked up again.
        private readonly Dictionary<string, float[]> _stagesByPrefab =
            new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Lifecycle

        private void Init() => permission.RegisterPermission(PermAdmin, this);

        private void OnServerInitialized()
        {
            if (ResourceHealth == null || ResourceHealthChanged == null)
                PrintError("ResourceEntity.health / OnHealthChanged not found - node break stages will not work.");
            Rebuild();
        }

        private void Unload()
        {
            _hitsDone.Clear();
            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, RootUi);
            _ui.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null) _ui.Remove(player.userID);
        }

        [ConsoleCommand("rustfieldgather.reload")]
        private void CmdReload(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            LoadConfig();
            Rebuild();
            Puts("Config reloaded and applied.");
        }

        [ConsoleCommand("rustfieldgather.list")]
        private void CmdList(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var kinds = new Dictionary<string, int>();
            var collect = new Dictionary<string, int>();
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                if (ent is CollectibleEntity c)
                {
                    collect.TryGetValue(c.ShortPrefabName, out var n);
                    collect[c.ShortPrefabName] = n + 1;
                }
                else if (ent is ResourceEntity r)
                {
                    var kind = KindOf(r.GetComponent<ResourceDispenser>());
                    if (kind == null) continue;
                    kinds.TryGetValue(kind, out var n);
                    kinds[kind] = n + 1;
                }
            }

            Puts("----- Hittable: config keys (by gathered resource) -----");
            foreach (var kv in kinds.OrderByDescending(k => k.Value))
                Puts($"  {kv.Key,-16} nodes on map: {kv.Value}");

            Puts("----- Collectible: config keys (by prefab) -----");
            foreach (var kv in collect.OrderByDescending(k => k.Value))
                Puts($"  {kv.Key,-28} on map: {kv.Value}");
        }

        // What break stages an object carries, and - the useful half - which stage each of its
        // configured hits will actually land on. The stage table is baked into the prefab and
        // cannot be added to: all that can be chosen is how the hits are spread over it.
        [ConsoleCommand("rustfieldgather.stages")]
        private void CmdStages(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;
            if (StagedGetInfo == null) { PrintError("StagedResourceEntity.GetInfo not found."); return; }

            var seen = new Dictionary<string, string>();
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var staged = ent as StagedResourceEntity;
                if (staged == null || seen.ContainsKey(staged.ShortPrefabName)) continue;

                var thresholds = StageThresholds(staged);
                if (thresholds == null) continue;

                var bands = new List<string>();
                for (int i = 0; i < thresholds.Length; i++)
                    bands.Add($"{i}:{thresholds[i] * 100f:0.#}%");

                string plan = "no rule of ours - vanilla";
                Rule rule;
                if (TryHittable(staged.ShortPrefabName, KindOf(staged.GetComponent<ResourceDispenser>()), out rule))
                {
                    var swings = new List<string>();
                    for (int hit = 1; hit <= rule.Swings; hit++)
                        swings.Add(hit == rule.Swings
                            ? $"{hit}:breaks"
                            : $"{hit}:stage {StageForHit(hit, rule.Swings, thresholds.Length)}");
                    plan = $"{ModeName(rule.Mode)}, {string.Join(" ", swings.ToArray())}";
                }

                seen[staged.ShortPrefabName] =
                    $"{thresholds.Length} stages, {staged.MaxHealth():0.#} HP, stage starts at " +
                    $"{string.Join(", ", bands.ToArray())}  |  {plan}";
            }

            Puts("----- Break stages -----");
            foreach (var kv in seen.OrderBy(k => k.Key))
                Puts($"  {kv.Key,-16} {kv.Value}");
        }

        // Cross-checks the config and the group table against what actually stands on the map.
        [ConsoleCommand("rustfieldgather.audit")]
        private void CmdAudit(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var nodes = new Dictionary<string, int>();
            var kinds = new Dictionary<string, int>();
            var collect = new Dictionary<string, int>();
            var barrels = new Dictionary<string, int>();

            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var name = ent.ShortPrefabName;
                if (ent is CollectibleEntity)
                {
                    collect.TryGetValue(name, out var n); collect[name] = n + 1;
                }
                else if (ent is ResourceEntity res)
                {
                    nodes.TryGetValue(name, out var n); nodes[name] = n + 1;
                    var kind = KindOf(res.GetComponent<ResourceDispenser>());
                    if (kind != null) { kinds.TryGetValue(kind, out var k); kinds[kind] = k + 1; }
                }
                else if (ent is LootContainer && name.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    barrels.TryGetValue(name, out var n); barrels[name] = n + 1;
                }
            }

            Puts("===== RustfieldGather: audit =====");
            Puts($"On map: {nodes.Values.Sum()} nodes ({nodes.Count} prefabs), {collect.Values.Sum()} collectible " +
                 $"({collect.Count} prefabs), {barrels.Values.Sum()} barrels ({barrels.Count} prefabs).");

            // 1. Duplicates inside the group table
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int dupes = 0;
            foreach (var group in PrefabGroups)
                foreach (var prefab in group.Value)
                {
                    if (owner.TryGetValue(prefab, out var first))
                    {
                        Puts($"  [DUPLICATE] '{prefab}' is in both '{first}' and '{group.Key}'");
                        dupes++;
                    }
                    else owner[prefab] = group.Key;
                }
            Puts(dupes == 0 ? "  Prefab duplicates between groups: none." : $"  Duplicates: {dupes}");

            // 2. Group prefabs that exist neither in the game registry nor on the map
            int ghosts = 0;
            foreach (var group in PrefabGroups)
                foreach (var prefab in group.Value)
                {
                    bool live = nodes.ContainsKey(prefab) || collect.ContainsKey(prefab);
                    if (live) continue;
                    Puts($"  [NOT ON MAP] {group.Key} -> {prefab}");
                    ghosts++;
                }
            Puts(ghosts == 0 ? "  Every group prefab is present on the map." : $"  Missing from map: {ghosts} (can be normal - biome/event)");

            // 3. What stands on the map but is covered by no enabled rule
            var uncovered = new List<string>();
            foreach (var kv in nodes)
                if (!_hittable.ContainsKey(kv.Key))
                    uncovered.Add($"{kv.Key} x{kv.Value}");
            Puts(uncovered.Count == 0
                ? "  Hittable nodes: all covered."
                : $"  Hittable nodes with no rule ({uncovered.Count}): {string.Join(", ", uncovered.ToArray())}");

            var uncoveredCol = new List<string>();
            foreach (var kv in collect)
                if (!_collectiblePortions.ContainsKey(kv.Key))
                    uncoveredCol.Add($"{kv.Key} x{kv.Value}");
            Puts(uncoveredCol.Count == 0
                ? "  Collectible: all covered."
                : $"  Collectible with no rule ({uncoveredCol.Count}): {string.Join(", ", uncoveredCol.ToArray())}");

            // 4. Config keys that match nothing at all
            foreach (var kv in _config.Hittable)
            {
                var targets = Targets(kv.Key).ToList();
                int hit = targets.Count(t => nodes.ContainsKey(t) || kinds.ContainsKey(t));
                if (hit == 0)
                    Puts($"  [DEAD KEY] Hittable '{kv.Key}' matched no object on the map");
            }
            foreach (var kv in _config.Collectible)
            {
                var targets = Targets(kv.Key).ToList();
                if (!targets.Any(t => collect.ContainsKey(t)))
                    Puts($"  [DEAD KEY] Collectible '{kv.Key}' matched no object on the map");
            }

            // 5. Barrels
            var missBarrel = barrels.Keys.Where(b => !_barrelPrefabs.Contains(b)).ToList();
            Puts($"  Barrels on map: {string.Join(", ", barrels.Select(k => k.Key + " x" + k.Value).ToArray())}");
            Puts(missBarrel.Count == 0
                ? "  Every barrel on the map is covered by a rule."
                : $"  Barrels with no rule: {string.Join(", ", missBarrel.ToArray())}");

            // 6. Rewards
            int bad = 0;
            foreach (var kv in _config.Hittable) bad += CheckRewards("Hittable/" + kv.Key, kv.Value.Rewards);
            foreach (var kv in _config.Collectible) bad += CheckRewards("Collectible/" + kv.Key, kv.Value.Rewards);
            bad += CheckRewards("HumanCorpses", _config.HumanCorpses.Rewards);
            bad += CheckRewards("AnimalCorpses", _config.AnimalCorpses.Rewards);
            Puts(bad == 0 ? "  Every reward shortname exists." : $"  Broken rewards: {bad}");

            // 7. Which side every butcherable corpse prefab in the game falls on
            var humans = new List<string>();
            var animals = new List<string>();
            foreach (var path in GameManifest.Current.entities)
            {
                if (path.IndexOf("corpse", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var prefab = GameManager.server.FindPrefab(path);
                var corpse = prefab?.GetComponent<BaseCorpse>();
                if (corpse == null || prefab.GetComponent<ResourceDispenser>() == null) continue;
                (IsHumanCorpse(corpse) ? humans : animals).Add(corpse.ShortPrefabName);
            }
            Puts($"  Human corpses ({humans.Count}): {string.Join(", ", humans.OrderBy(n => n).ToArray())}");
            Puts($"  Animal corpses ({animals.Count}): {string.Join(", ", animals.OrderBy(n => n).ToArray())}");

            Puts($"  Active hittable targets {_hittable.Count}, collectible {_collectiblePortions.Count}.");
            Puts("===== end of audit =====");
        }

        private int CheckRewards(string where, List<Reward> rewards)
        {
            if (rewards == null) return 0;
            int bad = 0;
            if (rewards.Count == 0) Puts($"  [EMPTY] {where}: reward list is empty");
            foreach (var reward in rewards)
            {
                if (reward == null || string.IsNullOrEmpty(reward.Shortname) ||
                    ItemManager.FindItemDefinition(reward.Shortname) == null)
                {
                    Puts($"  [BROKEN ITEM] {where}: '{reward?.Shortname}'");
                    bad++;
                }
                else if (reward.Amount <= 0)
                {
                    Puts($"  [ZERO] {where}: '{reward.Shortname}' amount {reward.Amount}");
                    bad++;
                }
            }
            return bad;
        }

        private void Rebuild(bool quiet = false)
        {
            _hittable.Clear();
            _collectiblePortions.Clear();
            _barrelPrefabs.Clear();
            _humanCorpseRule = null;
            _animalCorpseRule = null;

            foreach (var kv in _config.Hittable)
            {
                if (!Validate(kv.Key, kv.Value, out var rule)) continue;
                foreach (var target in Targets(kv.Key))
                {
                    if (_hittable.ContainsKey(target))
                        PrintWarning($"'{kv.Key}': prefab '{target}' is already claimed by another enabled section - it will be overridden.");
                    _hittable[target] = rule;
                }
            }

            foreach (var kv in _config.Collectible)
            {
                if (!kv.Value.Enable) continue;
                var portions = Resolve(kv.Key, kv.Value.Rewards);
                if (portions.Count == 0)
                {
                    PrintWarning($"Collectible '{kv.Key}': customisation is on but the reward list is empty - the object is left vanilla.");
                    continue;
                }
                foreach (var target in Targets(kv.Key))
                {
                    if (_collectiblePortions.ContainsKey(target))
                        PrintWarning($"Collectible '{kv.Key}': prefab '{target}' is already claimed by another enabled section - it will be overridden.");
                    _collectiblePortions[target] = portions;
                }
            }

            if (Validate("HumanCorpses", _config.HumanCorpses, out var human))
                _humanCorpseRule = human;
            if (Validate("AnimalCorpses", _config.AnimalCorpses, out var animal))
                _animalCorpseRule = animal;

            if (_config.Barrels.Enable)
                foreach (var p in BarrelPrefabs) _barrelPrefabs.Add(p);

            if (quiet) return;
            Puts($"Active: hittable targets {_hittable.Count}, collectible {_collectiblePortions.Count}, " +
                 $"human corpses {(_humanCorpseRule != null ? "yes" : "no")}, " +
                 $"animal corpses {(_animalCorpseRule != null ? "yes" : "no")}, " +
                 $"barrels {(_barrelPrefabs.Count > 0 ? "mode " + _config.Barrels.Mode : "no")}.");
        }

        private bool Validate(string key, HittableEntry entry, out Rule rule)
        {
            rule = null;
            if (entry == null || !entry.Enable) return false;

            if (entry.Hits < 1)
            {
                PrintWarning($"'{key}': hits={entry.Hits} with customisation on - not allowed, the object is left vanilla. File unchanged.");
                return false;
            }

            var portions = Resolve(key, entry.Rewards);
            if (portions.Count == 0)
            {
                PrintWarning($"'{key}': customisation is on but the reward list is empty - the object is left vanilla.");
                return false;
            }

            if (!KnownMode(entry.Mode))
                PrintWarning($"'{key}': mode '{entry.Mode}' is none of {ModeInstant} / {ModePerHit} / {ModeTotal} - " +
                             $"reading it as '{ModePerHit}'. File unchanged.");

            var mode = ModeOf(entry.Mode);
            int swings = mode == PayMode.Instant ? 1 : entry.Hits;

            // A total smaller than the hit count cannot reach every hit: the early swings come
            // out empty. Worth saying out loud, but not worth refusing the section over.
            if (mode == PayMode.Total)
                foreach (var portion in portions)
                    if (portion.Amount < swings)
                        PrintWarning($"'{key}': '{portion.Def.shortname}' {portion.Amount} split over {swings} hits - " +
                                     "the first swings pay nothing.");

            rule = new Rule { Portions = portions, Mode = mode, Hits = entry.Hits, Swings = swings };
            return true;
        }

        private List<Portion> Resolve(string key, List<Reward> rewards)
        {
            var result = new List<Portion>();
            if (rewards == null) return result;
            foreach (var reward in rewards)
            {
                if (reward == null || string.IsNullOrEmpty(reward.Shortname)) continue;
                if (reward.Amount <= 0) continue;
                var def = ItemManager.FindItemDefinition(reward.Shortname);
                if (def == null)
                {
                    PrintWarning($"'{key}': item '{reward.Shortname}' does not exist - skipped.");
                    continue;
                }
                result.Add(new Portion { Def = def, Amount = reward.Amount });
            }
            return result;
        }

        #endregion

        #region Hittable nodes and corpses

        // A hit is NEVER cancelled: the native path has to run in full, or the sparks, the sound
        // and the tool wear all vanish. Vanilla payout is muted further down, on the dispenser's
        // own hooks - the item does not exist yet there, so no zeroes reach the notice either.
        private object OnMeleeAttack(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null) return null;
            var target = info.HitEntity;
            if (target == null) return null;

            var melee = info.Weapon as BaseMelee;
            if (melee == null) return null;

            ResourceDispenser dispenser;
            float maxHealth;
            Rule rule;

            if (target is ResourceEntity resource)
            {
                dispenser = resource.GetComponent<ResourceDispenser>();
                maxHealth = resource.MaxHealth();
                if (!TryHittable(target.ShortPrefabName, KindOf(dispenser), out rule)) return null;
            }
            else if (target is BaseCorpse corpse)
            {
                dispenser = corpse.GetComponent<ResourceDispenser>();
                maxHealth = corpse.MaxHealth();
                rule = CorpseRule(corpse);
            }
            else return null;

            if (dispenser == null) return null;
            if (rule?.Portions == null) return null;
            int hits = Mathf.Max(1, rule.Swings);

            var gather = melee.GetGatherInfoFromIndex(dispenser.gatherType);
            if (gather == null || gather.gatherDamage <= 0f) return null;

            ulong id = target.net?.ID.Value ?? 0UL;
            if (id == 0UL) return null;

            _hitsDone.TryGetValue(id, out var done);
            done++;
            _hitsDone[id] = done;

            foreach (var portion in rule.Portions)
            {
                int amount = AmountForHit(rule, portion.Amount, done);
                if (amount > 0) Give(player, portion.Def, amount);
            }

            // Vanilla damage now lands on the inflated HP pool and cannot kill the node, and on the
            // next tick we set exactly the fraction that matches this swing's number.
            // No network update here, or the client sees the node whole again for one frame.
            SetHealth(target, maxHealth * HealthHeadroom, false);

            var entity = target;
            int reached = done;
            NextTick(() =>
            {
                if (entity == null || entity.IsDestroyed) return;
                if (reached >= hits)
                {
                    _hitsDone.Remove(id);
                    try
                    {
                        if (entity is ResourceEntity dying) dying.OnDied(info);
                        else if (entity is BaseCombatEntity combat) combat.Die(info);
                        else entity.Kill(BaseNetworkable.DestroyMode.Gib);
                    }
                    catch { if (!entity.IsDestroyed) entity.Kill(BaseNetworkable.DestroyMode.Gib); }
                    return;
                }
                SetHealth(entity, Mathf.Max(1f, maxHealth * BreakFraction(entity, reached, hits)), true);
            });

            return null;
        }

        // A specific key (prefab name) outranks a general one (gathered resource). A disabled
        // prefab section overrides nothing - the object simply falls back to the general rule.
        private bool TryHittable(string prefab, string kind, out Rule rule)
        {
            if (!string.IsNullOrEmpty(prefab) && _hittable.TryGetValue(prefab, out rule)) return true;
            if (!string.IsNullOrEmpty(kind) && _hittable.TryGetValue(kind, out rule)) return true;
            rule = null;
            return false;
        }

        // Vanilla items are blocked here: a non-null return cancels the payout before item creation.
        private object OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
            => IsManaged(dispenser) ? (object)true : null;

        private object OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
            => IsManaged(dispenser) ? (object)true : null;

        private bool IsManaged(ResourceDispenser dispenser)
        {
            if (dispenser == null) return false;
            var owner = dispenser.GetComponent<BaseEntity>();
            if (owner is BaseCorpse) return CorpseRule(owner) != null;
            Rule rule;
            return TryHittable(owner?.ShortPrefabName, KindOf(dispenser), out rule);
        }

        // Where the HP has to sit after this hit for the client to show the right break stage.
        // Vanilla picks the stage in StagedResourceEntity.FindBestStage: it walks the prefab's
        // stage table from the top and takes the first stage whose HP threshold the current
        // fraction still reaches. Ore carries four stages at 75/50/25/0 %, the wood pile three at
        // 75/50/25. Stepping the HP down linearly lands it exactly ON those thresholds, so with
        // four hits the first swing leaves 75 % - which is still the intact rock - and the most
        // broken stage is never seen at all, because the node dies at 0 instead of stopping
        // there. The stage is therefore chosen first, and the HP is then placed in the MIDDLE of
        // that stage's band, where nothing can round it into a neighbour.
        private float BreakFraction(BaseEntity entity, int hit, int hits)
        {
            var thresholds = StageThresholds(entity);
            if (thresholds != null)
            {
                int stage = StageForHit(hit, hits, thresholds.Length);
                float low = thresholds[stage];
                float high = stage > 0 ? thresholds[stage - 1] : 1f;
                if (high > low) return (low + high) * 0.5f;
            }
            // No stages on this prefab (trees, logs, driftwood, cactus): nothing is shown either
            // way, so the plain linear fall is kept.
            return 1f - (float)hit / hits;
        }

        // The hits are laid over the object's own stages, weighted towards the FIRST ones. The
        // last entry in the stage table stands for the break itself, every entry before it is a
        // visible step. More hits than stages and the extra swings pile onto the early stages
        // (nine hits over four steps is 3-2-2-2); fewer hits than stages and it is the late
        // stages that are skipped (three hits: stage one, stage two, break).
        private static int StageForHit(int hit, int hits, int stages)
        {
            int steps = Mathf.Max(2, stages);
            int step;

            if (hits >= steps)
            {
                int each = hits / steps, extra = hits % steps, cumulative = 0;
                step = steps;
                for (int i = 1; i <= steps; i++)
                {
                    cumulative += each + (i <= extra ? 1 : 0);
                    if (hit <= cumulative) { step = i; break; }
                }
            }
            else step = hit < hits ? hit : steps;

            // The step that stands for the break holds the last visible stage: the node is only
            // destroyed on the final swing, and until then it has to look like something.
            return Mathf.Clamp(step, 1, steps - 1);
        }

        // Read once per prefab off its StagedDestructionEntityInfo. A prefab with no stage table
        // is remembered as null so it is not looked up again.
        private float[] StageThresholds(BaseEntity entity)
        {
            var staged = entity as StagedResourceEntity;
            if (staged == null || StagedGetInfo == null) return null;

            var prefab = staged.ShortPrefabName;
            float[] cached;
            if (_stagesByPrefab.TryGetValue(prefab, out cached)) return cached;

            float[] thresholds = null;
            try
            {
                var info = StagedGetInfo.Invoke(staged, null) as StagedDestructionEntityInfo;
                var stages = info?.Stages;
                if (stages != null && stages.Length > 1)
                {
                    thresholds = new float[stages.Length];
                    for (int i = 0; i < stages.Length; i++) thresholds[i] = info.GetHealth(i);
                }
            }
            catch (Exception e)
            {
                PrintWarning($"Break stages of '{prefab}' unreadable ({e.Message}) - its HP falls linearly.");
            }

            _stagesByPrefab[prefab] = thresholds;
            return thresholds;
        }

        private static void SetHealth(BaseEntity entity, float value, bool network)
        {
            if (entity is ResourceEntity res)
            {
                ResourceHealth?.SetValue(res, value);
                if (network) ResourceHealthChanged?.Invoke(res, null);
            }
            else if (entity is BaseCombatEntity combat)
            {
                if (CombatHealth != null) CombatHealth.SetValue(combat, value);
                else combat.health = value;
            }
            if (network) entity.SendNetworkUpdate();
        }

        private string KindOf(ResourceDispenser dispenser)
        {
            if (dispenser == null) return null;
            var owner = dispenser.GetComponent<BaseEntity>();
            var prefab = owner?.ShortPrefabName;
            if (string.IsNullOrEmpty(prefab)) return null;
            if (_kindByPrefab.TryGetValue(prefab, out var cached)) return cached;

            string kind = null;
            try
            {
                var pristine = GameManager.server.FindPrefab(owner.PrefabName)?.GetComponent<ResourceDispenser>();
                kind = FirstResource(pristine);
            }
            catch { }

            if (kind == null) kind = FirstResource(dispenser);
            if (kind != null) _kindByPrefab[prefab] = kind;
            return kind;
        }

        private static string FirstResource(ResourceDispenser dispenser)
        {
            if (dispenser?.containedItems == null) return null;
            foreach (var contained in dispenser.containedItems)
                if (contained?.itemDef != null && contained.amount > 0f)
                    return contained.itemDef.shortname;
            return null;
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var id = entity?.net?.ID.Value ?? 0UL;
            if (id != 0UL) _hitsDone.Remove(id);
        }

        #endregion

        #region Collectible

        // Collecting is taken over whole rather than by swapping itemList.
        // The reason: at the end of native CollectibleEntity.DoPickup the game separately looks
        // for a RandomItemDispenser on the prefab and calls DistributeItems(). That part has
        // nothing to do with itemList, so with the list swapped it kept handing out its own items
        // (a fishing worm, on hemp). By taking all of DoPickup we hand out exactly what the
        // config says, and nothing beyond it.
        private object OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player, bool eat)
        {
            if (collectible == null || player == null || collectible.IsDestroyed) return null;
            // Eating is a separate deliberate act on vanilla contents; we stay out of it.
            if (eat) return null;

            List<Portion> portions;
            if (!_collectiblePortions.TryGetValue(collectible.ShortPrefabName, out portions)) return null;

            foreach (var portion in portions)
                Give(player, portion.Def, portion.Amount, BaseEntity.GiveItemReason.PickedUp);

            var effect = collectible.pickupEffect;
            if (effect != null && effect.isValid)
                Effect.server.Run(effect.resourcePath, collectible.transform.position,
                    Vector3.up, null, false);

            collectible.Kill(BaseNetworkable.DestroyMode.Gib);
            return true;
        }

        #endregion

        #region Barrels

        private object OnEntityTakeDamage(LootContainer container, HitInfo info)
        {
            if (container == null || info == null || container.IsDestroyed) return null;
            if (!_barrelPrefabs.Contains(container.ShortPrefabName)) return null;

            var player = info.InitiatorPlayer;
            if (player == null) return null;

            if (_config.Barrels.Mode == 0 && !(info.Weapon is BaseMelee)) return null;

            LootToPlayer(player, container);
            return true;
        }

        private void LootToPlayer(BasePlayer player, LootContainer container)
        {
            if (container.inventory != null)
            {
                var items = new List<Item>(container.inventory.itemList);
                foreach (var item in items)
                {
                    if (item == null) continue;
                    item.RemoveFromContainer();
                    player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
                }
            }

            if (!container.IsDestroyed) container.Kill(BaseNetworkable.DestroyMode.Gib);
        }

        #endregion

        #region In-game editor

        private const string RootUi = "RFGather.Root";
        private const string PermAdmin = "rustfieldgather.admin";

        private static readonly string[] TabNames = { "NODES", "COLLECT", "CORPSES", "BARRELS" };

        private class UiState
        {
            public int Tab;
            public string Key;
            public int Page;
            public string Error;
            public string Note;
            public bool Open;
        }

        private readonly Dictionary<ulong, UiState> _ui = new Dictionary<ulong, UiState>();

        private UiState State(BasePlayer player)
        {
            UiState st;
            if (!_ui.TryGetValue(player.userID, out st)) _ui[player.userID] = st = new UiState();
            return st;
        }

        // One card serves sections of different shapes: nodes have hits, barrels have a mode,
        // and a plain barrel has no rewards at all. This reduces them to one common form.
        private class EditTarget
        {
            public string Title;
            public Func<bool> Enabled;
            public Action<bool> SetEnabled;
            public Func<int> Hits;
            public Action<int> SetHits;
            public Func<int> Mode;
            public Action<int> SetMode;
            public Func<PayMode> Pay;
            public Action<PayMode> SetPay;
            public List<Reward> Rewards;
        }

        private List<string> KeysOf(int tab)
        {
            switch (tab)
            {
                case 0: return _config.Hittable.Keys.ToList();
                case 1: return _config.Collectible.Keys.ToList();
                case 2: return new List<string> { CorpseHumans, CorpseAnimals };
                default: return new List<string> { "barrels" };
            }
        }

        private const string CorpseHumans = "humans";
        private const string CorpseAnimals = "animals";

        private static string TitleOf(int tab, string key)
        {
            if (tab == 2) return key == CorpseAnimals ? "Animals" : "Players and NPCs";
            if (tab == 3) return "Plain barrels";
            return key;
        }

        private EditTarget Resolve(int tab, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            switch (tab)
            {
                case 0:
                {
                    HittableEntry e;
                    if (!_config.Hittable.TryGetValue(key, out e)) return null;
                    return new EditTarget
                    {
                        Title = key, Rewards = e.Rewards,
                        Enabled = () => e.Enable, SetEnabled = v => e.Enable = v,
                        Hits = () => e.Hits, SetHits = v => e.Hits = v,
                        Pay = () => ModeOf(e.Mode), SetPay = v => e.Mode = ModeName(v)
                    };
                }
                case 1:
                {
                    CollectibleEntry e;
                    if (!_config.Collectible.TryGetValue(key, out e)) return null;
                    return new EditTarget
                    {
                        Title = key, Rewards = e.Rewards,
                        Enabled = () => e.Enable, SetEnabled = v => e.Enable = v
                    };
                }
                case 2:
                {
                    HittableEntry e;
                    if (key == CorpseHumans) e = _config.HumanCorpses;
                    else if (key == CorpseAnimals) e = _config.AnimalCorpses;
                    else return null;
                    return new EditTarget
                    {
                        Title = TitleOf(tab, key), Rewards = e.Rewards,
                        Enabled = () => e.Enable, SetEnabled = v => e.Enable = v,
                        Hits = () => e.Hits, SetHits = v => e.Hits = v,
                        Pay = () => ModeOf(e.Mode), SetPay = v => e.Mode = ModeName(v)
                    };
                }
                default:
                {
                    var b = _config.Barrels;
                    return new EditTarget
                    {
                        Title = "Plain barrels",
                        Enabled = () => b.Enable, SetEnabled = v => b.Enable = v,
                        Mode = () => b.Mode, SetMode = v => b.Mode = v
                    };
                }
            }
        }

        // Server owner (authLevel 2) or an explicitly granted permission. player.IsAdmin is
        // deliberately not used here: it is true for a moderator at authLevel 1 as well.
        private bool HasAccess(BasePlayer player)
        {
            if (player == null) return false;
            if (player.net?.connection != null && player.net.connection.authLevel >= 2) return true;
            return permission.UserHasPermission(player.UserIDString, PermAdmin);
        }

        // A console command with nothing behind it came from the server console or RCON, which
        // is already the highest authority there is. One with a player behind it is a client
        // typing into F1, and that needs the same permission the panel does: three of these sweep
        // every entity on the map, so leaving them open is both an unauthorised action and a way
        // to stall the server by holding down enter.
        private bool ConsoleAllowed(ConsoleSystem.Arg arg)
        {
            var caller = arg?.Player();
            return caller == null || HasAccess(caller);
        }

        [ChatCommand("gather")]
        private void CmdGather(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player))
            {
                SendReply(player, "No access.");
                return;
            }
            var st = State(player);
            if (st.Key == null) st.Key = KeysOf(st.Tab).FirstOrDefault();
            Draw(player, true);
        }

        private void Apply(BasePlayer player)
        {
            SaveConfig();
            Rebuild(true);
            Draw(player);
        }

        private static string[] ArgList(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Args == null) return new string[0];
            var res = new string[arg.Args.Length];
            for (int i = 0; i < res.Length; i++) res[i] = arg.Args[i].ToString();
            return res;
        }

        private BasePlayer UiCaller(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return null;
            if (!HasAccess(player)) return null;
            return player;
        }

        [ConsoleCommand("rfgather.close")]
        private void UiClose(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, RootUi);
            State(player).Open = false;
        }

        // Force the config to disk, read it back from there and lay the rules out again over the
        // live objects. The panel stays open through all of it.
        [ConsoleCommand("rfgather.save")]
        private void UiSave(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            SaveConfig();
            LoadConfig();
            Rebuild(true);
            st.Error = null;
            st.Note = $"Saved and applied: hittable targets {_hittable.Count}, collectible {_collectiblePortions.Count}.";
            Draw(player);
        }

        [ConsoleCommand("rfgather.tab")]
        private void UiTab(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var a = ArgList(arg); if (a.Length < 1) return;
            var st = State(player);
            int tab;
            if (!int.TryParse(a[0], out tab)) return;
            st.Tab = Mathf.Clamp(tab, 0, 3);
            st.Page = 0;
            st.Error = null;
            st.Key = KeysOf(st.Tab).FirstOrDefault();
            Draw(player);
        }

        [ConsoleCommand("rfgather.pick")]
        private void UiPick(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var a = ArgList(arg); if (a.Length < 1) return;
            var st = State(player);
            st.Key = a[0];
            st.Error = null;
            st.Note = null;
            Draw(player);
        }

        [ConsoleCommand("rfgather.page")]
        private void UiPage(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var a = ArgList(arg); if (a.Length < 1) return;
            var st = State(player);
            int delta;
            if (!int.TryParse(a[0], out delta)) return;
            int pages = Mathf.Max(1, Mathf.CeilToInt(KeysOf(st.Tab).Count / (float)RowsPerPage));
            st.Page = Mathf.Clamp(st.Page + delta, 0, pages - 1);
            Draw(player);
        }

        [ConsoleCommand("rfgather.toggle")]
        private void UiToggle(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key); if (target == null) return;
            target.SetEnabled(!target.Enabled());
            st.Error = null;
            st.Note = null;
            Apply(player);
        }

        [ConsoleCommand("rfgather.mode")]
        private void UiMode(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key);
            if (target == null || target.Mode == null) return;
            target.SetMode(target.Mode() == 0 ? 1 : 0);
            Apply(player);
        }

        [ConsoleCommand("rfgather.pay")]
        private void UiPay(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key);
            if (target == null || target.Pay == null) return;
            var a = ArgList(arg);
            int value;
            if (a.Length < 1 || !int.TryParse(a[0], out value)) return;
            target.SetPay((PayMode)Mathf.Clamp(value, 0, 2));
            st.Error = null;
            st.Note = null;
            Apply(player);
        }

        [ConsoleCommand("rfgather.hits")]
        private void UiHits(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key);
            if (target == null || target.Hits == null) return;
            var a = ArgList(arg);
            int value;
            if (a.Length < 1 || !int.TryParse(a[0].Trim(), out value) || value < 1)
            {
                st.Error = "Hits must be a whole number of 1 or more.";
                Draw(player);
                return;
            }
            target.SetHits(value);
            st.Error = null;
            st.Note = null;
            Apply(player);
        }

        [ConsoleCommand("rfgather.item")]
        private void UiItem(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key);
            if (target == null || target.Rewards == null) return;
            var a = ArgList(arg);
            int index;
            if (a.Length < 2 || !int.TryParse(a[0], out index) || index < 0 || index >= target.Rewards.Count) return;

            var shortname = string.Join(" ", a.Skip(1).ToArray()).Trim();
            if (ItemManager.FindItemDefinition(shortname) == null)
            {
                st.Error = $"Item '{shortname}' does not exist - not written.";
                Draw(player);
                return;
            }
            target.Rewards[index].Shortname = shortname;
            st.Error = null;
            st.Note = null;
            Apply(player);
        }

        [ConsoleCommand("rfgather.amount")]
        private void UiAmount(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key);
            if (target == null || target.Rewards == null) return;
            var a = ArgList(arg);
            int index, value;
            if (a.Length < 2 || !int.TryParse(a[0], out index) || index < 0 || index >= target.Rewards.Count) return;
            if (!int.TryParse(a[1].Trim(), out value) || value < 1)
            {
                st.Error = "Amount must be a whole number of 1 or more.";
                Draw(player);
                return;
            }
            target.Rewards[index].Amount = value;
            st.Error = null;
            st.Note = null;
            Apply(player);
        }

        [ConsoleCommand("rfgather.add")]
        private void UiAdd(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key);
            if (target == null || target.Rewards == null) return;
            if (target.Rewards.Count >= 8)
            {
                st.Error = "More than eight items in one section will not fit.";
                Draw(player);
                return;
            }
            target.Rewards.Add(new Reward { Shortname = "scrap", Amount = 1 });
            st.Error = null;
            st.Note = null;
            Apply(player);
        }

        [ConsoleCommand("rfgather.del")]
        private void UiDel(ConsoleSystem.Arg arg)
        {
            var player = UiCaller(arg); if (player == null) return;
            var st = State(player);
            var target = Resolve(st.Tab, st.Key);
            if (target == null || target.Rewards == null) return;
            var a = ArgList(arg);
            int index;
            if (a.Length < 1 || !int.TryParse(a[0], out index) || index < 0 || index >= target.Rewards.Count) return;
            target.Rewards.RemoveAt(index);
            st.Error = null;
            st.Note = null;
            Apply(player);
        }

        #endregion

        #region Editor rendering

        // The panel is built ONCE; after that only targeted updates fly into it
        // (`Update = true`) addressed by element name. Re-sending an element without Update makes
        // the client tear the GameObject down and build a new one - that is the flicker; DestroyUi
        // + AddUi is worse still, with frames of nothing between the two RPCs.
        // The rule: a component in an update is sent WHOLE, which is why both the build and the
        // update go through the same TxtComp/BtnComp/InpComp factories.
        // Hiding via ActiveSelf is out: Update never switches an element back on.
        // Anything that should not show is moved off-screen by its coordinates instead.

        private const int RowsPerPage = 15;
        private const int MaxRewards = 8;
        private const float Hidden = -6000f;

        // Rustfield palette. The two accents are lifted from RustfieldUI unchanged. Red is
        // #CE412C, the red the ".eu" of the wordmark is painted in; green is that same colour's
        // three channel values rearranged - 206/65/44 becomes 44/206/65 - so it is exactly as
        // vivid as the brand red and cannot drift away from it in saturation or brightness the
        // way a separately picked green would. Rotate the channels to derive another one; never
        // hand-tune a single member of the set.
        //
        //   red   #CE412C   206  65  44
        //   green #2CCE41    44 206  65
        private const string ColRed = "0.8078 0.2549 0.1725 1";
        private const string ColGreen = "0.1725 0.8078 0.2549 1";

        // The plates stay neutral grey deliberately. Those two accents are the only colour in
        // the window, and RustfieldUI already found that a warm tint at label alpha reads as
        // yellow next to them.
        private const string ColBack = "0.09 0.09 0.09 0.98";
        private const string ColHead = "0.16 0.16 0.16 1";
        private const string ColRow = "0.15 0.15 0.15 1";
        private const string ColRowSel = "0.24 0.24 0.24 1";
        private const string ColField = "0.20 0.20 0.20 1";
        private const string ColText = "1 1 1 0.85";
        private const string ColDim = "1 1 1 0.45";
        private const string ColDark = "0.09 0.09 0.09 1";
        private const string ColNone = "0 0 0 0";
        private const string Font = "robotocondensed-regular.ttf";
        private const string FontBold = "robotocondensed-bold.ttf";

        #region Building blocks

        // Coordinates inside the parent are measured from the top-left corner; y grows down.
        private static CuiRectTransformComponent Box(float x, float y, float w, float h)
        {
            return new CuiRectTransformComponent
            {
                AnchorMin = "0 1", AnchorMax = "0 1",
                OffsetMin = $"{x} {-(y + h)}", OffsetMax = $"{x + w} {-y}"
            };
        }

        private static CuiRectTransformComponent Pad(float left, float right)
        {
            return new CuiRectTransformComponent
            {
                AnchorMin = "0 0", AnchorMax = "1 1",
                OffsetMin = $"{left} 0", OffsetMax = $"{-right} 0"
            };
        }

        private static CuiTextComponent TxtComp(string text, int size, string color, TextAnchor align, string font)
        {
            return new CuiTextComponent
            {
                Text = text ?? string.Empty, FontSize = size, Font = font,
                Color = color, Align = align
            };
        }

        private static CuiButtonComponent BtnComp(string command, string color)
        {
            return new CuiButtonComponent { Command = command ?? string.Empty, Color = color };
        }

        // Exactly the field shape that works in KitController: the input is a child of its own
        // backing plate, stretched over it, and asks for the keyboard itself via NeedsKeyboard.
        // KeyboardEnabled must not go on the root panel, or the field never takes focus.
        private static CuiInputFieldComponent InpComp(string text, string command, int limit)
        {
            return new CuiInputFieldComponent
            {
                Text = text ?? string.Empty, FontSize = 12, Font = Font, Color = ColText,
                Align = TextAnchor.MiddleLeft, Command = command ?? string.Empty,
                CharsLimit = limit, IsPassword = false, ReadOnly = false, NeedsKeyboard = true
            };
        }

        private static CuiElement El(bool build, string parent, string name)
        {
            return build
                ? new CuiElement { Name = name, Parent = parent }
                : new CuiElement { Name = name, Update = true };
        }

        private static void Txt(CuiElementContainer c, bool build, string parent, string name, string text,
            float x, float y, float w, float h, int size = 12, string color = ColText,
            TextAnchor align = TextAnchor.MiddleLeft, string font = Font)
        {
            var el = El(build, parent, name);
            el.Components.Add(TxtComp(text, size, color, align, font));
            el.Components.Add(Box(x, y, w, h));
            c.Add(el);
        }

        private static void Pnl(CuiElementContainer c, bool build, string parent, string name, string color,
            float x, float y, float w, float h)
        {
            var el = El(build, parent, name);
            el.Components.Add(new CuiImageComponent { Color = color });
            el.Components.Add(Box(x, y, w, h));
            c.Add(el);
        }

        // The button is assembled by hand rather than through CuiButton: that one hands its
        // label a random name, which leaves nothing to update it by later.
        private static void Btn(CuiElementContainer c, bool build, string parent, string name, string text,
            string command, float x, float y, float w, float h, string color,
            int size = 12, string textColor = ColText)
        {
            var el = El(build, parent, name);
            el.Components.Add(BtnComp(command, color));
            el.Components.Add(Box(x, y, w, h));
            c.Add(el);

            var lbl = El(build, name, name + ".t");
            lbl.Components.Add(TxtComp(text, size, textColor, TextAnchor.MiddleCenter, Font));
            lbl.Components.Add(Pad(2, 2));
            c.Add(lbl);
        }

        private static void Inp(CuiElementContainer c, bool build, string parent, string name, string text,
            string command, float x, float y, float w, float h, int limit)
        {
            var bg = El(build, parent, name);
            bg.Components.Add(new CuiImageComponent { Color = ColField });
            bg.Components.Add(Box(x, y, w, h));
            c.Add(bg);

            var field = El(build, name, name + ".i");
            field.Components.Add(InpComp(text, command, limit));
            field.Components.Add(Pad(6, 6));
            c.Add(field);
        }

        #endregion

        private void Draw(BasePlayer player, bool build = false)
        {
            var st = State(player);
            if (!st.Open) build = true;

            var c = new CuiElementContainer();
            if (build)
            {
                // No separate DestroyUi call is needed: the client tears the old tree down
                // in the same message that brings it the new one.
                c.Add(new CuiElement
                {
                    Name = RootUi,
                    Parent = "Overlay",
                    DestroyUi = RootUi,
                    Components =
                    {
                        new CuiImageComponent { Color = ColBack },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
                            OffsetMin = "-420 -270", OffsetMax = "420 270"
                        },
                        new CuiNeedsCursorComponent()
                    }
                });

                Pnl(c, true, RootUi, "rfg.head", ColHead, 0, 0, 840, 36);
                Txt(c, true, RootUi, "rfg.title", "RUSTFIELD<color=#ce412c>  GATHER</color>", 14, 7, 300, 22,
                    15, ColText, TextAnchor.MiddleLeft, FontBold);
                Txt(c, true, RootUi, "rfg.sub", "changes are applied and saved immediately",
                    330, 9, 400, 18, 11, ColDim);
                Btn(c, true, RootUi, "rfg.close", "✕", "rfgather.close", 800, 6, 28, 24, ColRed, 14);
                Btn(c, true, RootUi, "rfg.save", "SAVE AND APPLY", "rfgather.save",
                    696, 502, 132, 26, ColGreen, 11, ColDark);
            }

            Head(c, build, st);
            List(c, build, st);
            Card(c, build, st);

            CuiHelper.AddUi(player, c);
            st.Open = true;
        }

        private static void Head(CuiElementContainer c, bool build, UiState st)
        {
            for (int i = 0; i < TabNames.Length; i++)
                Btn(c, build, RootUi, "rfg.tab" + i, TabNames[i], "rfgather.tab " + i,
                    10 + i * 124, 44, 120, 24,
                    st.Tab == i ? ColGreen : ColRow, 12, st.Tab == i ? ColDark : ColText);

            string status = st.Error;
            string color = ColRed;
            if (string.IsNullOrEmpty(status)) { status = st.Note; color = ColGreen; }
            Txt(c, build, RootUi, "rfg.status", status ?? string.Empty, 258, 504, 430, 22, 12, color);
        }

        private void List(CuiElementContainer c, bool build, UiState st)
        {
            var keys = KeysOf(st.Tab);
            int pages = Mathf.Max(1, Mathf.CeilToInt(keys.Count / (float)RowsPerPage));
            st.Page = Mathf.Clamp(st.Page, 0, pages - 1);

            for (int i = 0; i < RowsPerPage; i++)
            {
                string name = "rfg.L" + i;
                int index = st.Page * RowsPerPage + i;
                bool used = index < keys.Count;
                string key = used ? keys[index] : string.Empty;
                var target = used ? Resolve(st.Tab, key) : null;
                bool on = target != null && target.Enabled();
                bool selected = used && key == st.Key;

                Pnl(c, build, RootUi, name, used ? (selected ? ColRowSel : ColRow) : ColNone,
                    used ? 10 : Hidden, 78 + i * 27, 238, 25);
                Txt(c, build, name, name + ".d", on ? "●" : "○", 8, 0, 16, 25, 12,
                    on ? ColGreen : ColDim);
                Txt(c, build, name, name + ".n", used ? TitleOf(st.Tab, key) : string.Empty,
                    26, 0, 206, 25, 12, selected ? "1 1 1 1" : ColText);
                // A transparent button goes over the labels: text has raycast enabled, and
                // without this it swallows the click on the row.
                Btn(c, build, name, name + ".b", string.Empty,
                    used ? "rfgather.pick " + key : string.Empty, 0, 0, 238, 25, ColNone);
            }

            bool paged = pages > 1;
            Btn(c, build, RootUi, "rfg.pgp", "◀", "rfgather.page -1",
                paged ? 10 : Hidden, 486, 40, 24, ColRow);
            Txt(c, build, RootUi, "rfg.pgl", paged ? $"{st.Page + 1} / {pages}" : string.Empty,
                paged ? 54 : Hidden, 486, 150, 24, 12, ColDim, TextAnchor.MiddleCenter);
            Btn(c, build, RootUi, "rfg.pgn", "▶", "rfgather.page 1",
                paged ? 208 : Hidden, 486, 40, 24, ColRow);
        }

        private static readonly string[] PayNames = { "INSTANT", "PER HIT", "TOTAL" };

        private static string AmountHeader(EditTarget target, PayMode pay, int swings)
        {
            if (target == null || target.Pay == null) return "AMOUNT";
            switch (pay)
            {
                case PayMode.Instant: return "AMOUNT - ALL OF IT, ON THE ONE HIT";
                case PayMode.Total: return $"AMOUNT - THE WHOLE OBJECT, SPLIT OVER {swings} HITS";
                default: return swings == 1
                    ? "AMOUNT - ON THE ONE HIT"
                    : $"AMOUNT - ON EVERY ONE OF THE {swings} HITS";
            }
        }

        private void Card(CuiElementContainer c, bool build, UiState st)
        {
            var target = Resolve(st.Tab, st.Key);
            bool has = target != null;
            var rewards = has ? target.Rewards : null;

            Txt(c, build, RootUi, "rfg.ct", has ? target.Title.ToUpper() : string.Empty,
                has ? 258 : Hidden, 78, 570, 24, 15, "1 1 1 1", TextAnchor.MiddleLeft, FontBold);

            bool on = has && target.Enabled();
            Btn(c, build, RootUi, "rfg.tgl", on ? "ENABLED" : "DISABLED", "rfgather.toggle",
                has ? 258 : Hidden, 108, 130, 26, on ? ColGreen : ColRow, 12, on ? ColDark : ColDim);

            float x = 398;
            bool paying = has && target.Pay != null;
            var pay = paying ? target.Pay() : PayMode.PerHit;
            int swings = has && target.Hits != null ? Mathf.Max(1, target.Hits()) : 1;
            if (pay == PayMode.Instant) swings = 1;

            // In instant mode the hit count means nothing, so the field goes away rather than
            // sitting there inviting a number that would be ignored. The buttons keep their
            // place either way.
            bool hits = has && target.Hits != null && pay != PayMode.Instant;
            Txt(c, build, RootUi, "rfg.hitsl", "hits", hits ? x : Hidden, 108, 54, 26, 12, ColDim);
            Inp(c, build, RootUi, "rfg.hits", hits ? target.Hits().ToString() : string.Empty,
                "rfgather.hits", hits ? x + 54 : Hidden, 108, 56, 26, 4);
            if (has && target.Hits != null) x += 124;

            for (int i = 0; i < PayNames.Length; i++)
            {
                bool active = paying && (int)pay == i;
                Btn(c, build, RootUi, "rfg.pay" + i, PayNames[i], "rfgather.pay " + i,
                    paying ? x + i * 99 : Hidden, 108, 95, 26,
                    active ? ColGreen : ColRow, 11, active ? ColDark : ColText);
            }
            if (paying) x += PayNames.Length * 99;

            bool mode = has && target.Mode != null;
            Txt(c, build, RootUi, "rfg.model", "mode", mode ? x : Hidden, 108, 50, 26, 12, ColDim);
            Btn(c, build, RootUi, "rfg.modeb",
                mode && target.Mode() == 1 ? "1 - any damage" : "0 - melee only",
                "rfgather.mode", mode ? x + 50 : Hidden, 108, 190, 26, ColRow);

            string note = null;
            if (!has) note = "Pick a section on the left.";
            else if (rewards == null)
                note = "Barrel contents pour into the inventory as-is - there is nothing to override.";
            Txt(c, build, RootUi, "rfg.note", note ?? string.Empty,
                note != null ? 258 : Hidden, note != null && !has ? 78 : 150, 570, 24, 12, ColDim);

            bool table = rewards != null;
            Txt(c, build, RootUi, "rfg.h1", "ITEM", table ? 258 : Hidden, 146, 240, 20, 11, ColDim);
            Txt(c, build, RootUi, "rfg.h2", table ? AmountHeader(target, pay, swings) : string.Empty,
                table ? 508 : Hidden, 146, 320, 20, 11, ColDim);

            int count = table ? Mathf.Min(rewards.Count, MaxRewards) : 0;
            for (int i = 0; i < MaxRewards; i++)
            {
                string name = "rfg.W" + i;
                bool used = i < count;
                var reward = used ? rewards[i] : null;
                bool known = used && ItemManager.FindItemDefinition(reward.Shortname ?? string.Empty) != null;

                Pnl(c, build, RootUi, name, ColNone, used ? 258 : Hidden, 170 + i * 32, 570, 26);
                Inp(c, build, name, name + ".it", used ? reward.Shortname ?? string.Empty : string.Empty,
                    "rfgather.item " + i, 0, 0, 240, 26, 32);
                Inp(c, build, name, name + ".am", used ? reward.Amount.ToString() : string.Empty,
                    "rfgather.amount " + i, 250, 0, 110, 26, 9);
                string hint = string.Empty;
                string hintColor = ColRed;
                if (used && !known) hint = "no such item";
                else if (used && paying && pay == PayMode.Total && swings > 1)
                {
                    int per = SplitPart(reward.Amount, swings, 1);
                    hint = (reward.Amount % swings == 0 ? string.Empty : "~") + per + " per hit";
                    hintColor = ColDim;
                }
                Txt(c, build, name, name + ".w", hint, 370, 0, 150, 26, 11, hintColor);
                Btn(c, build, name, name + ".x", "✕", "rfgather.del " + i, 532, 0, 26, 26, ColRed);
            }

            Btn(c, build, RootUi, "rfg.add", "+ add item", "rfgather.add",
                table ? 258 : Hidden, 170 + count * 32, 160, 26, ColRow);
        }

        #endregion

        #region Payout

        // The three payout modes meet here. Instant and per hit hand the written amount over
        // as it stands - instant simply has a single swing to do it on. Total treats the amount
        // as the whole object and gives this swing its share of it.
        private static int AmountForHit(Rule rule, int amount, int hit)
            => rule.Mode == PayMode.Total ? SplitPart(amount, rule.Swings, hit) : amount;

        // The shares add up to EXACTLY the configured total even when it does not divide evenly:
        // each hit gets the gap between two running quarters (or thirds, or sevenths) rather than
        // a rounded share of its own, so nothing is lost and nothing is invented on the last swing.
        // 50001 over four hits is 12500 + 12500 + 12500 + 12501.
        private static int SplitPart(int amount, int hits, int hit)
        {
            if (hits < 1) hits = 1;
            if (hit < 1 || hit > hits) return 0;
            long total = amount;
            return (int)(total * hit / hits - total * (hit - 1) / hits);
        }

        private void Give(BasePlayer player, ItemDefinition def, int amount,
            BaseEntity.GiveItemReason reason = BaseEntity.GiveItemReason.ResourceHarvested)
        {
            var item = ItemManager.Create(def, amount);
            if (item == null) return;
            // This particular overload is the one that sends the bottom-right notice and drops
            // the overflow on the ground. player.inventory.GiveItem does neither.
            player.GiveItem(item, reason);
        }

        #endregion
    }
}
