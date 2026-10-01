using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// What Mark knows about this world, taken from the world itself.
    ///
    /// A model asked "how much iron is a longship" will answer confidently and often wrongly,
    /// because item numbers are exactly the sort of thing that does not survive training. So
    /// rather than trust it, the plugin reads the running game: <c>ObjectDB</c> has every item
    /// and every recipe, and <c>ZNetScene.m_prefabs</c> has every creature and every building
    /// piece, with their real values for this version. A question is matched against that
    /// index and the matching entries ride along with it as reference material.
    ///
    /// The display names come from the game's own localization table, which is *not* reachable
    /// here - <c>Localization</c> is client-only and is not in the dedicated server's assembly
    /// at all. <c>tools/extract-localization.sh</c> pulls the English strings out of
    /// <c>resources.assets</c> into a TSV instead. Without that file everything still works,
    /// using prefab names split into words, which matches player questions surprisingly well.
    /// </summary>
    internal static class Knowledge
    {
        private sealed class Fact
        {
            public string Name = "";        // what a player would call it
            public string Prefab = "";
            public string Text = "";        // the line handed to the model
            public string Haystack = "";    // normalized, for matching
            public int Rank;                // ties break towards things players ask about
        }

        private static readonly List<Fact> Facts = new List<Fact>();
        private static readonly Dictionary<string, string> Strings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static bool _built;
        internal static int Count => Facts.Count;
        internal static int StringCount => Strings.Count;

        /// <summary>Words that match everything and so tell us nothing.</summary>
        private static readonly HashSet<string> Stop = new HashSet<string>
        {
            "a","an","the","is","are","was","were","do","does","did","how","what","which","who",
            "whom","why","when","where","much","many","i","you","he","she","it","we","they","me",
            "my","your","of","for","to","in","on","at","by","with","from","and","or","but","if",
            "can","could","should","would","will","shall","have","has","had","get","got","make",
            "made","need","needs","needed","want","take","takes","about","tell","know","there",
            "this","that","these","those","be","been","being","not","no","yes","any","some","one",
            "mark","please","thanks","thank"
        };

        // --- building the index -------------------------------------------------------

        /// <summary>
        /// Built on first use rather than at startup, so a server's boot is never held up by
        /// it and so it lands after ObjectDB is certain to be populated.
        /// </summary>
        internal static void EnsureBuilt()
        {
            if (_built) return;

            // Called every frame until it can actually succeed: ObjectDB and ZNetScene are
            // scene singletons and are not up as early as the network is.
            if (ObjectDB.instance == null || ZNetScene.instance == null) return;
            _built = true;

            try
            {
                LoadStrings();

                int items = AddItems();
                int creatures = AddCreatures();
                int pieces = AddPieces();
                int wiki = LoadWiki();
                int notes = LoadNotes();

                Diagnostics.Log(
                    $"knowledge: {Facts.Count} entries ({items} items, {creatures} creatures, " +
                    $"{pieces} pieces, {wiki} from the wiki index, {notes} from notes), " +
                    $"{Strings.Count} localized strings");

                if (Strings.Count == 0)
                    Diagnostics.Log(
                        "no localization TSV found, so he will use prefab names - run " +
                        "tools/extract-localization.sh to give him the names players see");

                Dump();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("could not build the knowledge index: " + e);
            }
        }

        private static int AddItems()
        {
            if (ObjectDB.instance == null) return 0;

            // Recipes indexed by the prefab they produce, so each item can carry its own.
            var recipes = new Dictionary<string, Recipe>(StringComparer.Ordinal);
            foreach (Recipe recipe in ObjectDB.instance.m_recipes)
            {
                if (recipe == null || !recipe.m_enabled || recipe.m_item == null) continue;
                recipes[recipe.m_item.gameObject.name] = recipe;
            }

            int added = 0;

            foreach (GameObject prefab in ObjectDB.instance.m_items)
            {
                if (prefab == null) continue;
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;

                ItemDrop.ItemData.SharedData s = drop.m_itemData.m_shared;
                string name = Localize(s.m_name, prefab.name);

                var sb = new StringBuilder();
                sb.Append(name).Append(", ").Append(ItemKind(s.m_itemType));

                Damage(sb, s.m_damages);

                // SharedData carries defaults for armour, block and durability on everything,
                // so a log of wood claims 10 armour and 100 durability. Only report each
                // where the item type actually uses it.
                if (s.m_armor > 0f && IsWorn(s.m_itemType)) Number(sb, "armor", s.m_armor);
                if (s.m_blockPower > 0f && s.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                    Number(sb, "block", s.m_blockPower);
                if (s.m_food > 0f)
                {
                    Number(sb, "health", s.m_food);
                    Number(sb, "stamina", s.m_foodStamina);
                    if (s.m_foodEitr > 0f) Number(sb, "eitr", s.m_foodEitr);
                    if (s.m_foodBurnTime > 0f)
                        sb.Append("; lasts ").Append(Num(s.m_foodBurnTime / 60f)).Append(" minutes");
                }
                if (s.m_useDurability && s.m_maxDurability > 0f)
                    Number(sb, "durability", s.m_maxDurability);
                if (s.m_weight > 0f) Number(sb, "weight", s.m_weight);
                if (s.m_maxStackSize > 1) sb.Append("; stacks to ").Append(s.m_maxStackSize);
                if (s.m_maxQuality > 1) sb.Append("; upgrades to level ").Append(s.m_maxQuality);
                if (!s.m_teleportable) sb.Append("; cannot go through a portal");

                if (recipes.TryGetValue(prefab.name, out Recipe r)) Craft(sb, r);

                string description = Localize(s.m_description, "");
                if (!string.IsNullOrEmpty(description) && description.Length < 200)
                    sb.Append(". Its own description reads: \"").Append(description).Append('"');

                Add(name, prefab.name, sb.ToString(), rank: 3);
                added++;
            }

            return added;
        }

        private static int AddCreatures()
        {
            if (ZNetScene.instance == null) return 0;

            int creatures = 0;

            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null) continue;

                var character = prefab.GetComponent<Character>();
                if (character == null) continue;

                // The Player prefab is the chassis Mark himself is built on, not a creature.
                if (prefab.name == "Player") continue;

                string name = Localize(character.m_name, prefab.name);
                var sb = new StringBuilder();
                sb.Append(name).Append(", creature");
                if (character.m_boss) sb.Append(", a boss");
                Number(sb, "health", character.m_health);
                sb.Append("; ").Append(Disposition(character.m_faction));
                if (prefab.GetComponent<Tameable>() != null) sb.Append("; can be tamed");

                var drops = prefab.GetComponent<CharacterDrop>();
                if (drops != null && drops.m_drops != null && drops.m_drops.Count > 0)
                {
                    sb.Append("; drops ");
                    bool first = true;
                    foreach (CharacterDrop.Drop d in drops.m_drops)
                    {
                        if (d == null || d.m_prefab == null) continue;
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(Localize(NameOf(d.m_prefab), d.m_prefab.name));
                        if (d.m_amountMax > 1) sb.Append(' ').Append(d.m_amountMin).Append('-').Append(d.m_amountMax);
                        if (d.m_chance < 1f) sb.Append(" (").Append(Percent(d.m_chance)).Append(')');
                    }
                }

                Add(name, prefab.name, sb.ToString(), rank: 3);
                creatures++;
            }

            return creatures;
        }

        /// <summary>
        /// Building pieces, from <c>ObjectDB.GetAllBuildPieces</c> rather than by scanning
        /// prefabs for a <c>Piece</c> component.
        ///
        /// That method walks the piece tables of the hammer, hoe and cultivator, which is the
        /// definition of "a thing a player can build" - so it leaves out the legacy prefabs
        /// nobody can construct. Scanning for the component instead turns up a second, cheaper
        /// "Portal" that exists only in the files, which is exactly the sort of answer that
        /// would send somebody off to look for a recipe that is not in the game.
        /// </summary>
        private static int AddPieces()
        {
            if (ObjectDB.instance == null) return 0;

            int pieces = 0;

            foreach (Piece piece in ObjectDB.instance.GetAllBuildPieces())
            {
                if (piece == null || piece.m_resources == null || piece.m_resources.Length == 0) continue;

                string name = Localize(piece.m_name, piece.gameObject.name);
                var sb = new StringBuilder();
                sb.Append(name).Append(", a building piece, built from ");
                Requirements(sb, piece.m_resources);
                if (piece.m_craftingStation != null)
                    sb.Append("; needs ").Append(Localize(piece.m_craftingStation.m_name,
                        piece.m_craftingStation.name)).Append(" nearby");

                Add(name, piece.gameObject.name, sb.ToString(), rank: 2);
                pieces++;
            }

            return pieces;
        }

        /// <summary>
        /// The factual index built by tools/fetch-wiki.sh - which biome a thing is found in,
        /// what kind of thing it is, what summons a boss. The game files record none of that,
        /// and it is most of what players actually ask.
        ///
        /// Ranked below the game data, and every line says where it came from, because this is
        /// player-written and can be out of date while the game's own numbers never are.
        /// </summary>
        private static int LoadWiki()
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "HallPatton.wiki.tsv");

            try
            {
                if (!File.Exists(path)) return 0;

                int added = 0;
                foreach (string raw in File.ReadAllLines(path))
                {
                    if (raw.Length == 0 || raw[0] == '#') continue;
                    int tab = raw.IndexOf('\t');
                    if (tab <= 0 || tab == raw.Length - 1) continue;

                    Add(raw.Substring(0, tab), "", raw.Substring(tab + 1) + " [community wiki]",
                        rank: 1);
                    added++;
                }
                return added;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("could not read " + path + ": " + e.Message);
                return 0;
            }
        }

        private static int LoadNotes()
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "HallPatton.notes.md");

            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "# Extra facts for Mark Hall-Patton\n" +
                        "#\n" +
                        "# One fact per line. Every line is searched alongside what the plugin reads\n" +
                        "# out of the game itself, and matching lines are handed to him as reference\n" +
                        "# material when somebody asks. Lines starting with # are ignored.\n" +
                        "#\n" +
                        "# This is the place for anything the game files do not hold - wiki details,\n" +
                        "# how your server does things, where your base is.\n" +
                        "#\n" +
                        "# Eikthyr is the first boss, summoned at the Sacrificial Stones with two deer trophies.\n");
                    return 0;
                }

                int added = 0;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    Add("", "", line, rank: 4);       // owner's own notes outrank generated lines
                    added++;
                }
                return added;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("could not read " + path + ": " + e.Message);
                return 0;
            }
        }

        private static void LoadStrings()
        {
            string[] candidates =
            {
                Path.Combine(BepInEx.Paths.ConfigPath, "HallPatton.localization.tsv"),
                Path.Combine(BepInEx.Paths.PluginPath, "HallPatton/HallPatton.localization.tsv")
            };

            foreach (string path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0 || tab == line.Length - 1) continue;
                        string token = line.Substring(0, tab);
                        if (!Strings.ContainsKey(token)) Strings[token] = line.Substring(tab + 1);
                    }
                    Diagnostics.Log($"loaded {Strings.Count} localized strings from {path}");
                    return;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("could not read " + path + ": " + e.Message);
                }
            }
        }

        /// <summary>Writes the whole index out, so it can be read and grepped.</summary>
        private static void Dump()
        {
            if (!Plugin.KnowledgeDump.Value) return;

            string path = Path.Combine(BepInEx.Paths.ConfigPath, "HallPatton.knowledge.txt");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Everything Mark can look up. Regenerated on every server start;");
                sb.AppendLine("# editing it achieves nothing. Put your own facts in");
                sb.AppendLine("# HallPatton.notes.md, which is searched alongside this.");
                sb.AppendLine("#");
                sb.AppendLine("# The prefab name in [brackets] is here for you, not for him: it is");
                sb.AppendLine("# matched against questions but never given to the model, which only");
                sb.AppendLine("# ever sees the names players see.");
                sb.AppendLine();
                foreach (Fact fact in Facts)
                    sb.AppendLine(fact.Prefab.Length > 0 ? fact.Text + "  [" + fact.Prefab + "]" : fact.Text);
                File.WriteAllText(path, sb.ToString());
                Diagnostics.Log("wrote the knowledge index to " + path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("could not write " + path + ": " + e.Message);
            }
        }

        // --- looking things up --------------------------------------------------------

        /// <summary>
        /// The entries worth putting in front of the model for this question, best first.
        /// Empty when nothing matches, which is the honest answer and leaves him to say so.
        /// </summary>
        internal static List<string> Lookup(string question, int max)
        {
            var hits = new List<string>();
            if (!Plugin.KnowledgeEnabled.Value || string.IsNullOrWhiteSpace(question)) return hits;

            EnsureBuilt();
            if (Facts.Count == 0) return hits;

            string padded = Matcher.Normalize(question);
            var words = new List<string>();
            foreach (string word in padded.Split(' '))
                if (word.Length > 2 && !Stop.Contains(word)) words.Add(word);

            if (words.Count == 0) return hits;

            var scored = new List<KeyValuePair<int, Fact>>();

            foreach (Fact fact in Facts)
            {
                int score = 0;

                // The whole name appearing in the question is the strongest signal there is:
                // "how much iron for an iron sword" contains "iron sword".
                if (fact.Name.Length > 2 &&
                    padded.IndexOf(" " + Matcher.Normalize(fact.Name).Trim() + " ", StringComparison.Ordinal) >= 0)
                    score += 20;

                foreach (string word in words)
                {
                    bool inName = fact.Name.Length > 0 &&
                                  Matcher.Normalize(fact.Name).IndexOf(" " + word, StringComparison.Ordinal) >= 0;
                    bool inPrefab = fact.Prefab.Length > 0 &&
                                    Matcher.Normalize(Words(fact.Prefab)).IndexOf(" " + word, StringComparison.Ordinal) >= 0;

                    if (inName || inPrefab) score += 6;
                    else if (fact.Haystack.IndexOf(" " + word + " ", StringComparison.Ordinal) >= 0) score += 1;
                }

                if (score > 0) scored.Add(new KeyValuePair<int, Fact>(score + fact.Rank, fact));
            }

            if (scored.Count == 0) return hits;

            scored.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0; i < scored.Count && hits.Count < max; i++) hits.Add(scored[i].Value.Text);
            return hits;
        }

        // --- helpers ------------------------------------------------------------------

        /// <summary>Worn on the body, so an armour value on it means something.</summary>
        private static bool IsWorn(ItemDrop.ItemData.ItemType type) =>
            type == ItemDrop.ItemData.ItemType.Helmet ||
            type == ItemDrop.ItemData.ItemType.Chest ||
            type == ItemDrop.ItemData.ItemType.Legs ||
            type == ItemDrop.ItemData.ItemType.Shoulder ||
            type == ItemDrop.ItemData.ItemType.Hands;

        private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

        private static void Add(string name, string prefab, string text, int rank)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Variants that differ only by prefab name - Troll and Troll_sleeping - say the
            // same thing twice and crowd out a real second match.
            string withoutPrefab = string.IsNullOrEmpty(prefab) ? text : text.Replace("(" + prefab + ")", "");
            if (!Seen.Add(withoutPrefab)) return;

            Facts.Add(new Fact
            {
                Name = name ?? "",
                Prefab = prefab ?? "",
                Text = text,
                Haystack = Matcher.Normalize(text),
                Rank = rank
            });
        }

        /// <summary>Resolves a "$token" through the localization table, or falls back.</summary>
        private static string Localize(string token, string fallbackPrefab)
        {
            if (string.IsNullOrEmpty(token)) return Words(fallbackPrefab);

            string key = token[0] == '$' ? token.Substring(1) : token;
            if (Strings.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))
                return value;

            // No table: "SwordIron" reads as "Sword Iron", which still matches "iron sword"
            // once both sides are reduced to words.
            return token[0] == '$' ? Words(fallbackPrefab) : token;
        }

        private static string NameOf(GameObject prefab)
        {
            var drop = prefab.GetComponent<ItemDrop>();
            return drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null
                ? drop.m_itemData.m_shared.m_name
                : prefab.name;
        }

        /// <summary>"ArmorBronzeChest" -> "Armor Bronze Chest".</summary>
        internal static string Words(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return "";

            var sb = new StringBuilder(prefabName.Length + 8);
            for (int i = 0; i < prefabName.Length; i++)
            {
                char c = prefabName[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(prefabName[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static void Damage(StringBuilder sb, HitData.DamageTypes d)
        {
            var parts = new List<string>(6);
            if (d.m_damage > 0f) parts.Add("generic " + Num(d.m_damage));
            if (d.m_blunt > 0f) parts.Add("blunt " + Num(d.m_blunt));
            if (d.m_slash > 0f) parts.Add("slash " + Num(d.m_slash));
            if (d.m_pierce > 0f) parts.Add("pierce " + Num(d.m_pierce));
            if (d.m_chop > 0f) parts.Add("chop " + Num(d.m_chop));
            if (d.m_pickaxe > 0f) parts.Add("pickaxe " + Num(d.m_pickaxe));
            if (d.m_fire > 0f) parts.Add("fire " + Num(d.m_fire));
            if (d.m_frost > 0f) parts.Add("frost " + Num(d.m_frost));
            if (d.m_lightning > 0f) parts.Add("lightning " + Num(d.m_lightning));
            if (d.m_poison > 0f) parts.Add("poison " + Num(d.m_poison));
            if (d.m_spirit > 0f) parts.Add("spirit " + Num(d.m_spirit));

            if (parts.Count > 0) sb.Append("; damage ").Append(string.Join(", ", parts.ToArray()));
        }

        private static void Craft(StringBuilder sb, Recipe recipe)
        {
            sb.Append("; crafted");
            if (recipe.m_amount > 1) sb.Append(" ").Append(recipe.m_amount).Append(" at a time");

            if (recipe.m_craftingStation != null)
            {
                sb.Append(" at ").Append(Localize(recipe.m_craftingStation.m_name, recipe.m_craftingStation.name));
                if (recipe.m_minStationLevel > 1) sb.Append(" level ").Append(recipe.m_minStationLevel);
            }
            else
            {
                sb.Append(" by hand");
            }

            if (recipe.m_resources != null && recipe.m_resources.Length > 0)
            {
                sb.Append(" from ");
                Requirements(sb, recipe.m_resources);
            }
        }

        private static void Requirements(StringBuilder sb, Piece.Requirement[] requirements)
        {
            bool first = true;
            foreach (Piece.Requirement req in requirements)
            {
                // Upgrader resources are not part of the base recipe, only of levelling it up.
                if (req == null || req.m_resItem == null || req.m_amount <= 0) continue;
                if (req.m_upgraderResource) continue;
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(Localize(req.m_resItem.m_itemData.m_shared.m_name, req.m_resItem.name))
                  .Append(" x").Append(req.m_amount);
            }
        }

        private static void Number(StringBuilder sb, string label, float value)
        {
            if (value == 0f) return;
            sb.Append("; ").Append(label).Append(' ').Append(Num(value));
        }

        private static string Num(float value) =>
            value == Mathf.Floor(value)
                ? ((int)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Percent(float chance) =>
            Mathf.RoundToInt(chance * 100f) + "% chance";

        /// <summary>
        /// The enum names are internal, and some of them are not English: "TwoHandedWeaponLeft"
        /// and "AmmoNonEquipable" are not things a player has ever seen written down.
        /// </summary>
        private static string ItemKind(ItemDrop.ItemData.ItemType type)
        {
            switch (type)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon: return "a one-handed weapon";
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon: return "a two-handed weapon";
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft: return "a two-handed weapon";
                case ItemDrop.ItemData.ItemType.Bow: return "a bow";
                case ItemDrop.ItemData.ItemType.Shield: return "a shield";
                case ItemDrop.ItemData.ItemType.Ammo: return "ammunition";
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable: return "ammunition";
                case ItemDrop.ItemData.ItemType.Helmet: return "a helmet";
                case ItemDrop.ItemData.ItemType.Chest: return "chest armour";
                case ItemDrop.ItemData.ItemType.Legs: return "leg armour";
                case ItemDrop.ItemData.ItemType.Shoulder: return "a cape";
                case ItemDrop.ItemData.ItemType.Hands: return "gloves";
                case ItemDrop.ItemData.ItemType.Utility: return "a utility item";
                case ItemDrop.ItemData.ItemType.Trophy: return "a trophy";
                case ItemDrop.ItemData.ItemType.Consumable: return "food or a potion";
                case ItemDrop.ItemData.ItemType.Material: return "a material";
                case ItemDrop.ItemData.ItemType.Tool: return "a tool";
                case ItemDrop.ItemData.ItemType.Torch: return "a torch";
                case ItemDrop.ItemData.ItemType.Fish: return "a fish";
                case ItemDrop.ItemData.ItemType.Trinket: return "a trinket";
                case ItemDrop.ItemData.ItemType.Customization: return "a customization option";
                default: return "an item";
            }
        }

        /// <summary>What the faction means to somebody standing in front of it.</summary>
        private static string Disposition(Character.Faction faction)
        {
            switch (faction)
            {
                case Character.Faction.Players:
                case Character.Faction.PlayerSpawned: return "friendly, on your side";
                case Character.Faction.AnimalsVeg: return "a wild animal, not hostile unless provoked";
                case Character.Faction.Boss: return "a boss";
                case Character.Faction.Dverger: return "neutral unless attacked";
                case Character.Faction.TrainingDummy: return "harmless";
                default: return "hostile";
            }
        }
    }
}
