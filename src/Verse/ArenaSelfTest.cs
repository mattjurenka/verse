using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Checks the server-side half of the arena on an empty server, because the alternative is
    /// finding out with a player standing in the ring.
    ///
    /// <para>Four of the arena's spikes are answerable with no client at all, and those are
    /// exactly the four that fail silently: a chest whose inventory was written in a format
    /// nothing can read, a creature with no prefab field (real data, invisible for ever - the
    /// trap <see cref="BossStones"/> already fell into once), a site scan that returns the sea,
    /// and a hide mask that cannot be undone. Each of those produces a working-looking server
    /// and a broken arena.</para>
    ///
    /// <para><b>What it cannot answer</b> is whether a client accepts any of it: whether the
    /// chest opens and shows the kit, and whether a hand-written creature actually fights. Those
    /// are spikes A and B in docs/arena-design.md and they need somebody in the game. This exists
    /// so that when they are run, the thing being tested is the client's behaviour and not a
    /// typo on this side.</para>
    ///
    /// <para>Everything it creates is destroyed in a <c>finally</c>, so a failure part-way
    /// through still leaves the world clean - the same rule <see cref="SelfTest"/> follows.</para>
    /// </summary>
    internal static class ArenaSelfTest
    {
        private static readonly List<string> Results = new List<string>();
        private static int _failed;
        private static bool _ran;

        private static System.Reflection.MethodInfo _destroy;
        private static readonly object[] DestroyArgs = new object[1];

        /// <summary>
        /// Runs once, and only with nobody connected: it writes real ZDOs into the live object
        /// table, and doing that underneath a player is not a test, it is a bug.
        /// </summary>
        internal static void Run()
        {
            if (_ran) return;
            if (!VersePlugin.ArenaEnabled.Value) return;
            if (ZDOMan.instance == null || ZNetScene.instance == null || ObjectDB.instance == null) return;

            if (ZNet.instance != null && ZNet.instance.GetPeers().Count > 0)
            {
                VersePlugin.Log.LogInfo("arena self-test skipped: somebody is connected");
                return;
            }

            _ran = true;
            Results.Clear();
            _failed = 0;

            var litter = new List<ZDO>();
            try
            {
                CheckSite();
                CheckRested();
                CheckFactions();
                CheckKit(litter);
                CheckDeck(litter);
                CheckGround();
                CheckVenue();
                CheckCreature(litter);
                CheckReveal(litter);
            }
            catch (System.Exception e)
            {
                Check("the self-test itself did not throw: " + e.Message, false);
            }
            finally
            {
                foreach (ZDO zdo in litter) Destroy(zdo);
                Report(litter.Count);
            }
        }

        /// <summary>
        /// Spike D. The site has to be dry, in the biome asked for, and flat enough to fight on.
        /// A scan that returns the middle of the ocean is the failure that would otherwise be
        /// found by teleporting a player into it.
        /// </summary>
        private static void CheckSite()
        {
            if (!ArenaSite.Resolve(out Vector3 centre))
            {
                Check("a site was found", false);
                return;
            }

            Check("a site was found", true);

            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            Check("it is above the water line", centre.y > water);

            // And the ground a client will actually draw there is above it too, which is not the
            // same assertion: the site's own y comes from WorldGenerator.GetHeight, and a zone
            // that straddles a shore is built from a blend of its four corner biomes instead -
            // a difference of tens of metres. See Ground.Blended.
            float real = ArenaSite.HeightAt(centre.x, centre.z);
            Check($"and so is the ground the client builds there ({real:0.0} m, water at " +
                  $"{water:0.0} m)", real > water);

            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI * 2f / 12f;
                float h = ArenaSite.HeightAt(centre.x + Mathf.Cos(a) * ArenaSite.Radius,
                                             centre.z + Mathf.Sin(a) * ArenaSite.Radius);
                if (h < lo) lo = h;
                if (h > hi) hi = h;
            }

            // Not a promise of a billiard table - this is Valheim - but a 20 m cliff through
            // the middle of the ring is not an arena.
            Check($"the ring is fightable ({hi - lo:0.0} m of height across it)", hi - lo < 20f);
            Check("the centre is inside its own ring", ArenaSite.Inside(centre));
            Check("a point well outside is not",
                  !ArenaSite.Inside(centre + Vector3.right * (ArenaSite.Radius + 50f)));

            // A fighter at their own chests must not read as having walked out of the run: the
            // gate is outside the wall by design, and the stray tolerance exists to cover it.
            Check("a fighter at their own gate still counts as being at the arena",
                  Arena.AtArena(centre + Vector3.right * Arena.GateDistance));
            Check("a fighter who has really walked off does not",
                  !Arena.AtArena(centre + Vector3.right * (Arena.GateDistance + 40f)));

            // The cleared apron has to reach past the gates, or there are trees standing where
            // players sort their gear and the wall has a wood behind it in every shot. It is a
            // config key, so this checks the configured value rather than a constant.
            Check($"the clearing ({Arena.ClearRadius:0} m) covers the gates and the floor",
                  Arena.InClearing(centre + Vector3.right * Arena.GateDistance) &&
                  Arena.ClearRadius >= ArenaSite.Radius);
        }

        /// <summary>
        /// The Rested buff the arena grants has to actually exist in <c>ObjectDB</c>, or the RPC
        /// is sent into nothing and the only symptom is a player with no stamina wondering why.
        /// The name is a config key for exactly that reason, so this checks the configured one
        /// rather than a constant.
        /// </summary>
        private static void CheckRested()
        {
            if (!VersePlugin.ArenaRested.Value) return;

            string want = string.IsNullOrWhiteSpace(VersePlugin.ArenaRestedEffect.Value)
                ? "Rested"
                : VersePlugin.ArenaRestedEffect.Value.Trim();

            bool found = false;
            if (ObjectDB.instance?.m_StatusEffects != null)
            {
                foreach (StatusEffect se in ObjectDB.instance.m_StatusEffects)
                {
                    if (se == null) continue;
                    if (se.name != want && se.name.GetStableHashCode() != want.GetStableHashCode()) continue;
                    found = true;
                    break;
                }
            }
            Check($"the status effect '{want}' exists on this server", found);
        }


        /// <summary>
        /// Every wave must be a single faction.
        ///
        /// <para>Valheim creatures belong to factions and different factions are hostile to each
        /// other, so a wave of greydwarves and stone golems fights itself instead of the player -
        /// which is what the first playtest reported, and it is invisible from the server unless
        /// somebody checks. <c>Character.m_faction</c> is a field on the prefab, so the server
        /// can read it, and this reports the faction of every wave as well as failing on a mixed
        /// one. Which means the groupings in the wave table are verified rather than assumed.</para>
        /// </summary>
        private static void CheckFactions()
        {
            var mixed = new List<string>();
            var summary = new List<string>();
            var absent = new List<string>();

            for (int wave = 1; wave <= ArenaRules.Waves; wave++)
            {
                var factions = new List<string>();

                foreach (ArenaGroup group in ArenaRules.Scale(wave, 1))
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(group.Prefab.GetStableHashCode());
                    Character character = prefab?.GetComponent<Character>();

                    // A name that is not on this server at all used to leave no trace here but a
                    // "?" in the summary line, and the wave would simply spawn fewer creatures
                    // than the table asks for. Named out loud instead: a typo in the wave table
                    // and a creature a game update renamed look identical from here, and both are
                    // worth one line at boot rather than a quiet wave.
                    if (character == null)
                    {
                        string miss = "wave " + wave + " " + group.Prefab;
                        if (!absent.Contains(miss)) absent.Add(miss);
                        continue;
                    }

                    string faction = character.m_faction.ToString();
                    if (!factions.Contains(faction)) factions.Add(faction);
                }

                if (factions.Count > 1)
                    mixed.Add("wave " + wave + " (" + string.Join(" + ", factions.ToArray()) + ")");

                summary.Add(wave + ":" + (factions.Count == 0 ? "?" : factions[0]));
            }

            Check(mixed.Count == 0
                      ? "every wave is a single faction, so nothing fights itself"
                      : "every wave is a single faction - MIXED: " + string.Join("; ", mixed.ToArray()),
                  mixed.Count == 0);

            Check(absent.Count == 0
                      ? "every creature in the wave table exists on this server"
                      : "every creature in the wave table exists on this server - MISSING: " +
                        string.Join("; ", absent.ToArray()),
                  absent.Count == 0);

            VersePlugin.Log.LogInfo("arena factions by wave: " + string.Join(" ", summary.ToArray()));

            // And the per-prefab map, because "wave 13 is mixed" does not say which creature is
            // the odd one out, and guessing cost a deploy.
            var seen = new List<string>();
            for (int wave = 1; wave <= ArenaRules.Waves; wave++)
            {
                foreach (ArenaGroup group in ArenaRules.Scale(wave, 1))
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(group.Prefab.GetStableHashCode());
                    Character character = prefab?.GetComponent<Character>();
                    if (character == null) continue;

                    string entry = group.Prefab + "=" + character.m_faction;
                    if (!seen.Contains(entry)) seen.Add(entry);
                }
            }

            VersePlugin.Log.LogInfo("arena creature factions: " + string.Join(" ", seen.ToArray()));
        }


        /// <summary>
        /// Spike A, as far as this side can take it: every kit item resolves to a real item in
        /// <c>ObjectDB</c>, and a chest written with it reads back as the same number of stacks.
        /// A kit naming a prefab this build does not have would otherwise be a quietly short kit.
        /// </summary>
        private static void CheckKit(List<ZDO> litter)
        {
            KitItem[] kit = ArenaRules.Kit(ArenaSite.Cold);
            Check($"the kit has {kit.Length} entries", kit.Length > 0);

            var missing = new List<string>();
            foreach (KitItem item in kit)
            {
                bool found = false;
                foreach (GameObject go in ObjectDB.instance.m_items)
                {
                    if (go == null || go.name != item.Prefab) continue;
                    found = go.GetComponent<ItemDrop>() != null;
                    break;
                }
                if (!found) missing.Add(item.Prefab);
            }

            Check(missing.Count == 0
                      ? "every kit item exists on this server"
                      : "every kit item exists on this server - missing " + string.Join(", ", missing.ToArray()),
                  missing.Count == 0);

            // The deck is what stops the chests breaking, and a typo in the prefab name fails
            // silently into "chests on bare ground" - which is the bug it was written to fix.
            string deckPrefab = VersePlugin.ArenaDeckPrefab.Value;
            if (!string.IsNullOrWhiteSpace(deckPrefab))
            {
                int deckHash = deckPrefab.GetStableHashCode();
                GameObject floor = ZNetScene.instance.GetPrefab(deckHash);
                Check($"'{deckPrefab}' is a building piece this server knows, for the chest deck",
                      floor != null && floor.GetComponent<Piece>() != null);

                // The two numbers that decide whether any of this ends up on the ground, and
                // the only place they can be read without a client in the world. A prefab
                // nothing can be measured off is placed by its origin as it was before, which
                // is the floating deck in the screenshot - so this says so rather than
                // leaving it to be seen in the game.
                bool measured = Footing.Of(deckHash, out float deckBottom, out float deckTop);
                Check(measured
                          ? $"the deck tile measures {deckBottom:0.00} m to {deckTop:0.00} m " +
                            "around its own origin, so its surface can be aimed at the ground"
                          : $"'{deckPrefab}' has geometry to measure - without it the deck is " +
                            "placed by its origin and may hang in the air",
                      measured);
            }

            string chestPrefab = VersePlugin.ArenaChestPrefab.Value;
            int chestHash = chestPrefab.GetStableHashCode();
            GameObject prefab = ZNetScene.instance.GetPrefab(chestHash);

            Check($"'{chestPrefab}' is a prefab this server knows",
                  prefab != null && prefab.GetComponent<Container>() != null);
            if (prefab == null || prefab.GetComponent<Container>() == null) return;

            bool chestMeasured = Footing.Of(chestHash, out float chestBottom, out float chestTop);
            Check(chestMeasured
                      ? $"the chest measures {chestBottom:0.00} m to {chestTop:0.00} m around " +
                        "its own origin, so its base can be stood on the deck"
                      : $"'{chestPrefab}' has geometry to measure - without it the chest is " +
                        "placed by its origin and may hover over the deck",
                  chestMeasured);

            // Thrown away afterwards, in a verse id and under a player id nothing else uses.
            // Deliberately built through Loadout.Chests rather than by hand, so the capacity
            // arithmetic and the multi-chest layout are what is being tested: one private chest
            // does not hold a fourteen-stack kit, and the first version of this test did not
            // notice because it made exactly one chest and then blamed the kit.
            const int fakeVerse = 999999;
            const long fakePlayer = 4242L;
            var at = new Vector3(0f, -5000f, 0f);

            List<ZDO> chests = Loadout.Chests(chestPrefab, fakeVerse, fakePlayer, 0L,
                Loadout.Kit, at, Vector3.right, kit.Length);
            litter.AddRange(chests);

            Check($"{chests.Count} chest(s) were laid out for a {kit.Length}-stack kit", chests.Count > 0);
            if (chests.Count == 0) return;

            ZDO chest = chests[0];
            Check("the chest kept the prefab it was given", chest.GetPrefab() == chestHash);
            Check("and the player id that locks it", chest.GetLong(ZDOVars.s_creator, 0L) == fakePlayer);
            Check("and is tagged to a verse rather than shared", ZdoVerse.Of(chest) == fakeVerse);
            Check("a fresh chest reads as empty", Loadout.Empty(chest));

            bool filled = Loadout.Fill(chests, chestPrefab, kit, out string problem);
            Check(filled ? "the whole kit was written in" : "the whole kit was written in - " + problem,
                  filled);

            // Read exactly the way Container.Load reads it - GetByteArray, not GetString. An
            // earlier version of this test wrote a base64 string and read that same string back,
            // so it round-tripped through its own wrong format and reported 13 of 13 while every
            // chest in the game was empty. A round trip through your own writer proves nothing
            // about the field's contract; this now reads it the way the game does.
            Check("the first chest holds a serialised inventory, as a byte array",
                  chest.GetByteArray(ZDOVars.s_items) != null);
            Check("which no longer reads as empty", !Loadout.Empty(chest));

            // The real check, and the one that caught the bug this file exists for: vanilla's
            // own Inventory.Load has to accept what we wrote, and the items have to still have
            // their identity when it does. A clone of a prefab's m_itemData has no m_dropPrefab,
            // and Save writes the item's name from it - so the first run of this wrote a package
            // of fourteen nameless items that Load silently discarded, every assertion above
            // still passing.
            Loadout.Grid(chestPrefab, out int pw, out int ph, out int _);
            Check($"the chest grid is {pw}x{ph}", pw > 0 && ph > 0);

            // What the kit asked for, to compare against what came back. Loadout.Build clamps
            // every stack to the item's own m_maxStackSize, so an entry asking for "a full
            // stack" is handed the real ceiling instead - which is the intended behaviour, but
            // the number is worth printing rather than discovering in the chest.
            var wanted = new Dictionary<string, int>();
            foreach (KitItem k in kit)
                wanted[k.Prefab] = k.Stack;

            int readBack = 0, broken = 0;
            var clamped = new List<string>();
            try
            {
                foreach (ZDO one in chests)
                {
                    byte[] bytes = one.GetByteArray(ZDOVars.s_items);
                    if (bytes == null || bytes.Length == 0) continue;

                    var probe = new Inventory("probe", null, pw, ph);
                    probe.Load(new ZPackage(bytes));
                    readBack += probe.NrOfItems();

                    foreach (ItemDrop.ItemData item in probe.GetAllItems())
                    {
                        if (item.m_shared.m_useDurability && item.m_durability <= 0f) broken++;

                        string name = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
                        if (name != null && wanted.TryGetValue(name, out int ask) && item.m_stack < ask)
                            clamped.Add($"{name} {item.m_stack}/{ask} (max {item.m_shared.m_maxStackSize})");
                    }
                }
            }
            catch (System.Exception e)
            {
                Check("vanilla can read it back: " + e.Message, false);
            }

            Check($"vanilla reads back {readBack} of {kit.Length} stacks", readBack == kit.Length);
            Check($"and none of it is already broken ({broken} at zero durability)", broken == 0);

            // Not a failure: a clamped stack is the kit asking for more than the game allows,
            // and being given the most it can. Logged so the real ceiling is on the record.
            if (clamped.Count > 0)
                VersePlugin.Log.LogInfo(
                    "arena kit: stacks clamped to the item's own maximum - " +
                    string.Join(", ", clamped.ToArray()));
            else
                VersePlugin.Log.LogInfo("arena kit: every stack was handed out at full size");

            Loadout.Clear(chest);
            Check("and it can be emptied again", Loadout.Empty(chest));
        }

        /// <summary>
        /// The deck and a chest standing on it, measured against each other and against the
        /// ground.
        ///
        /// <para><b>This is the check a screenshot earned.</b> Everything involved was already
        /// "working": the deck was laid, the chests were made, the kit went into them - and the
        /// whole assembly hung in the air with daylight under it, because a ZDO's position is
        /// the prefab's origin and nothing here had ever compared an origin with a surface. It
        /// took a player standing in front of it to see. Three numbers on an empty server see
        /// it instead: where the tile's top really is, where the chest's base really is, and
        /// how either stands against the ground.</para>
        ///
        /// <para>Laid well away from the site, and destroyed in <see cref="Run"/>'s
        /// <c>finally</c> like everything else here: <see cref="ArenaDeck.Ensure"/> re-levels
        /// any tile it finds near where it was going to lay one, so run on a real gate it would
        /// shuffle the floor somebody's deposit is sitting on.</para>
        /// </summary>
        private static void CheckDeck(List<ZDO> litter)
        {
            string deckPrefab = VersePlugin.ArenaDeckPrefab.Value;
            if (string.IsNullOrWhiteSpace(deckPrefab)) return;   // bare ground, by choice

            string chestPrefab = VersePlugin.ArenaChestPrefab.Value;
            if (!ArenaSite.Resolve(out Vector3 centre)) return;   // CheckSite has already said so
            if (ZNetScene.instance.GetPrefab(deckPrefab.GetStableHashCode()) == null) return;

            // Far enough out that the scan for tiles already standing cannot reach the arena's
            // own, and that these cannot be mistaken for them either.
            Vector3 where = centre + Vector3.right * 700f;
            Vector3 along = Vector3.forward;
            float span = Loadout.RowSpan(chestPrefab, 36);

            bool laid = ArenaDeck.Ensure(where, along, span, out float surface);
            List<ZDO> tiles = ArenaDeck.Tiles(where, 8f);
            litter.AddRange(tiles);

            Check($"a deck can be laid on real ground ({tiles.Count} tile(s))", laid && tiles.Count > 0);
            if (!laid || tiles.Count == 0) return;

            // Where the tiles' top surfaces actually ended up, which is the position they were
            // given plus the piece's own thickness above its origin.
            bool measured = Footing.Of(deckPrefab.GetStableHashCode(), out float _, out float tileTop);
            float highest = float.MinValue;
            foreach (ZDO tile in tiles)
                highest = Mathf.Max(highest, tile.GetPosition().y + (measured ? tileTop : 0f));

            Check($"the tiles' tops are the surface the deck reported ({highest:0.00} m vs " +
                  $"{surface:0.00} m)", Mathf.Abs(highest - surface) < 0.01f);

            Check("and TopAt agrees, so a player sent to the gate lands on the floor and not " +
                  "inside it",
                  ArenaDeck.TopAt(where, out float told) && Mathf.Abs(told - surface) < 0.01f);

            // The ground under the chests, sampled more finely than the deck samples it. Its
            // maximum is therefore at least the one the deck levelled itself against, so a
            // surface above it means the deck was levelled against ground the chests do not
            // stand on - which is the slope that lifted the deck off the grass.
            Vector3 across = Vector3.Cross(Vector3.up, along.normalized) * 0.5f;
            float under = float.MinValue;
            for (int i = 0; i <= 16; i++)
            {
                Vector3 on = where + along.normalized * (span * i / 16f);
                foreach (Vector3 probe in new[] { on, on + across, on - across })
                    under = Mathf.Max(under, ArenaSite.HeightAt(probe.x, probe.z));
            }

            float row = ArenaSite.HeightAt(where.x, where.z);
            Check($"the surface is on the ground under the chests, not over it " +
                  $"({surface - under:0.00} m above the highest of it, {surface - row:0.00} m " +
                  "above the row)",
                  surface - under < 0.1f && surface - row > -0.01f);

            // A chest stood on that deck, under ids nothing else uses, and then the one number
            // that was wrong: the gap between a chest's base and the floor it stands on.
            const int fakeVerse = 999995;
            const long fakePlayer = 4243L;

            List<ZDO> onDeck = Loadout.Chests(chestPrefab, fakeVerse, fakePlayer, 0L,
                Loadout.Deposit, where, along, 1, surface);
            litter.AddRange(onDeck);

            Check("a chest was stood on the deck", onDeck.Count > 0);
            if (onDeck.Count == 0) return;

            float baseY = onDeck[0].GetPosition().y +
                          (Footing.Of(chestPrefab.GetStableHashCode(), out float bottom, out float _)
                              ? bottom
                              : 0f);

            Check($"and its base rests on that surface rather than hovering over it " +
                  $"({baseY - surface:0.00} m gap)", Mathf.Abs(baseY - surface) < 0.01f);
        }

        /// <summary>
        /// The terrain writer, exercised on real ground and then undone.
        ///
        /// <para><b>Why this gets a check of its own.</b> <see cref="Ground"/> writes a byte
        /// array that only a client ever reads, and <c>TerrainComp.Load</c>'s response to a
        /// payload it does not like is one line in the client's log and an abandoned array -
        /// which on this side looks exactly like success. So the two things that would be
        /// invisible are asserted here: that the ground really reads back at the height it was
        /// told to be, from a cold decode off the ZDO rather than out of this process's own
        /// arrays, and that the payload declares the two array lengths <c>Load</c> checks before
        /// it trusts anything.</para>
        ///
        /// <para>Done 500 m from the site, on ground nothing of ours stands on, and restored in
        /// full - <see cref="Ground.Restore"/> removes the compiler object as well as the
        /// deltas, so a passing test leaves the world byte-for-byte as it found it.</para>
        /// </summary>
        private static void CheckGround()
        {
            string trouble = Ground.Resolve();
            Check(trouble == null
                      ? $"the zone grid and terrain compiler prefab resolve ({Ground.Vertices} " +
                        "vertices a zone)"
                      : "the terrain writer resolves - " + trouble,
                  trouble == null);
            if (trouble != null) return;

            if (!ArenaSite.Resolve(out Vector3 centre)) return;

            Vector3 where = centre + Vector3.forward * 500f;
            float was = ArenaSite.HeightAt(where.x, where.z);
            float want = was + 1.5f;                 // well inside vanilla's 8 m ceiling

            try
            {
                int moved = Ground.Level(where, 12f, want, 4f);
                Check($"ground can be levelled ({moved} vertex/vertices moved 1.5 m)", moved > 0);
                if (moved == 0) return;

                Check("levelling it again writes nothing, so a re-run is not a re-dig",
                      Ground.Level(where, 12f, want, 4f) == 0);

                // The real check: forget everything and read it back off the object table, the
                // way a client that has never seen this process would.
                Ground.Forget();

                float got = ArenaSite.HeightAt(where.x, where.z);
                Check($"and it reads back at the height it was given ({got:0.00} m vs " +
                      $"{want:0.00} m, from {was:0.00} m)", Mathf.Abs(got - want) < 0.05f);

                // Out at the rim the taper is nearly spent, so the ground there has to be
                // between the height it was and the height the middle was given - whichever way
                // round those two are, because the hillside may well rise away from the middle.
                float edge = ArenaSite.HeightAt(where.x, where.z + 11.5f);
                float hill = Ground.Blended(where.x, where.z + 11.5f);
                Check($"the rim eases back towards the hillside rather than standing on a step " +
                      $"({edge:0.00} m, between the hillside's {hill:0.00} m and the new " +
                      $"{want:0.00} m)",
                      edge >= Mathf.Min(hill, want) - 0.1f && edge <= Mathf.Max(hill, want) + 0.1f);

                bool read = Ground.Audit(where, out int heights, out int paints, out int edited);
                Check($"the payload declares {heights} heights and {paints} paint entries, and " +
                      $"this grid has {Ground.Vertices} - which is what TerrainComp.Load checks",
                      read && heights == Ground.Vertices && paints == Ground.Vertices);
                Check($"{edited} vertex/vertices are marked edited in it", edited > 0);
            }
            finally
            {
                Ground.Restore(where, 12f);
                Ground.Forget();

                float back = ArenaSite.HeightAt(where.x, where.z);
                Check($"the test ground was put back as the seed describes it ({back:0.00} m vs " +
                      $"{was:0.00} m)", Mathf.Abs(back - was) < 0.01f);
            }
        }

        /// <summary>
        /// The venue the players actually stand on: the levelled floor, the boardwalk, the
        /// gallery on top of the wall, its railing and the way up to it.
        ///
        /// <para>Asserted on the real thing rather than on a throwaway copy, because all of it
        /// is built at boot before this runs and all of it is one shared structure. The one
        /// assertion worth more than the counts is the railing's height: a rail shorter than a
        /// jump is decoration, and the whole reason the gallery can exist is that a dead fighter
        /// cannot drop back onto the floor from it.</para>
        /// </summary>
        private static void CheckVenue()
        {
            if (!ArenaSite.Resolve(out Vector3 centre)) return;

            if (VersePlugin.ArenaLevelGround.Value)
            {
                // The floor is a fill now, not a level, so "is it flat" is the wrong question -
                // flat was the complaint. Two things have to hold instead, and they are the two
                // halves of what a fill promises: nothing is left in a hollow, and nothing that
                // was already high enough has been touched. The second is what keeps the ground
                // the ground.
                //
                // Vanilla will not move a vertex more than 8 m from the seed's own height, so a
                // hollow deeper than that stays a hollow; those points are counted separately
                // rather than failed, and Ground.Level logs a warning of its own when it
                // happens.
                float lowest = float.MaxValue, deepest = 0f;
                int raised = 0, kept = 0, pinned = 0;

                for (int ring = 1; ring <= 4; ring++)
                {
                    for (int i = 0; i < 12; i++)
                    {
                        float a = i * Mathf.PI * 2f / 12f + ring * 0.13f;
                        float r = (ArenaSite.Radius - 1f) * ring / 4f;
                        float x = centre.x + Mathf.Cos(a) * r;
                        float z = centre.z + Mathf.Sin(a) * r;

                        float here = ArenaSite.HeightAt(x, z);
                        float seed = Ground.Blended(x, z);

                        lowest = Mathf.Min(lowest, here);
                        if (here - seed > 0.01f) { raised++; deepest = Mathf.Max(deepest, here - seed); }
                        else kept++;

                        if (here - seed > 7.9f) pinned++;
                    }
                }

                Check($"the floor keeps the ground it was given ({kept} of {kept + raised} " +
                      $"points untouched, {raised} filled, deepest fill {deepest:0.00} m)",
                      kept > 0);

                // A fill has no rim to walk up: at the edge of a hollow the filled surface and
                // the natural ground are the same height, so there is no step anywhere. This is
                // the check for the cliff the levelled version left round the outside.
                float worstRim = 0f;
                for (int i = 0; i < 24; i++)
                {
                    float a = i * Mathf.PI * 2f / 24f;
                    float inner = ArenaSite.HeightAt(centre.x + Mathf.Cos(a) * (ArenaSite.Radius + 3f),
                                                     centre.z + Mathf.Sin(a) * (ArenaSite.Radius + 3f));
                    float outer = ArenaSite.HeightAt(centre.x + Mathf.Cos(a) * (ArenaSite.Radius + 4f),
                                                     centre.z + Mathf.Sin(a) * (ArenaSite.Radius + 4f));

                    worstRim = Mathf.Max(worstRim, Mathf.Abs(inner - outer));
                }

                Check($"and leaves no step at its edge for anybody to walk up " +
                      $"({worstRim:0.00} m over the metre outside the fill)", worstRim < 1f);

                if (pinned > 0)
                    VersePlugin.Log.LogInfo(
                        $"arena: {pinned} sampled point(s) of floor are still more than 8 m " +
                        "below the fill line, which is vanilla's ceiling and not ours");
            }

            if (VersePlugin.ArenaBoardwalk.Value)
            {
                List<ZDO> boards = ArenaApron.Standing();
                Check($"a boardwalk of {boards.Count} board(s) is down outside the wall",
                      boards.Count > 0);

                foreach (ZDO board in boards)
                {
                    if (ArenaSite.Inside(board.GetPosition())) continue;

                    Check("none of it is inside the ring", true);
                    break;
                }
            }

            if (!VersePlugin.ArenaGallery.Value) return;

            List<ZDO> gallery = ArenaStand.Standing();
            Check($"the gallery is up ({gallery.Count} piece(s))", gallery.Count > 0);
            if (gallery.Count == 0) return;

            // The wall's top on the ladder's own bearing, which is the walkway the ladder has to
            // reach - not the nominal height at the middle of the arena, where there is no wall.
            float top = ArenaRing.WallTopY(ArenaStand.StairFoot(centre));

            int rails = 0;
            int railHash = (VersePlugin.ArenaRailPrefab.Value ?? "").Trim().GetStableHashCode();
            int boardHash = (VersePlugin.ArenaBoardPrefab.Value ?? "").Trim().GetStableHashCode();

            // The stair is made of the same boards as the walkway and carries its own marker,
            // which is the only reliable way to tell them apart - an earlier version of this
            // went by height and counted 164 steps in a ten-step stair.
            var climb = new List<Vector3>();

            foreach (ZDO piece in gallery)
            {
                if (piece.GetPrefab() == railHash) rails++;
                if (piece.GetPrefab() != boardHash) continue;

                if (ArenaStand.IsStep(piece)) climb.Add(piece.GetPosition());
            }

            Check($"{rails} piece(s) of railing stand along the inside of its top", rails > 0);

            // Valheim's jump clears about 1.2 m, so this is the number that decides whether the
            // railing is a barrier or a kerb.
            bool measured = Footing.Box(railHash, out Bounds rail);
            Check(measured
                      ? $"the railing is {rail.size.y:0.0} m tall, which is more than a jump"
                      : "the railing could be measured",
                  measured && rail.size.y > 1.5f);

            // The way up, and the one number that decides whether it is a way up at all: a step
            // a player cannot walk over is a wall. The ladder this replaced failed in the game
            // for want of exactly this check - and it could not have had one, because whether a
            // ladder is climbable is a fact about a collider in a Unity scene, while whether a
            // step is walkable is arithmetic.
            climb.Sort((a, b) => a.y.CompareTo(b.y));

            float worstStep = 0f;
            for (int i = 1; i < climb.Count; i++)
                worstStep = Mathf.Max(worstStep, climb[i].y - climb[i - 1].y);

            Check($"a stair of {climb.Count} step(s) climbs to the gallery", climb.Count >= 2);
            Check($"and no step is taller than a player walks up ({worstStep:0.00} m at worst)",
                  climb.Count >= 2 && worstStep <= 0.5f);

            // Against the walkway above the top step itself, not the walkway where the stair
            // started: the flight sweeps around the ring as it climbs and the wall's top follows
            // the ground, so those are two different heights. Measuring the wrong one is what
            // made this check fail on a stair that was in fact meeting the walkway.
            if (climb.Count >= 2)
            {
                Vector3 last = climb[climb.Count - 1];
                float board = Footing.Box(boardHash, out Bounds plank) ? plank.max.y : 0f;
                float meets = ArenaRing.WallTopY(last) + 0.02f;

                Check($"and the top step meets the walkway above it ({last.y + board:0.00} m vs " +
                      $"{meets:0.00} m)", Mathf.Abs(last.y + board - meets) < 0.5f);
            }

            // Where watch() puts a spectator: outside the wall, inside the cleared venue, and
            // never on the fighting floor.
            Vector3 foot = ArenaStand.StairFoot(centre);
            Check("a spectator lands outside the ring", !ArenaSite.Inside(foot));
            Check("and still inside the cleared venue", Arena.InClearing(foot));

            // A spectator in a freezing biome arrives from their own bed with whatever they
            // respawned in, so the weather is the thing most likely to kill them. The effect is
            // read off the kit's own mead rather than named here; if that lookup ever stops
            // working the symptom is somebody dying of cold on a wall.
            // Checked whatever the biome, reported either way, and only a failure where it
            // matters: the name is the thing that could quietly stop resolving, and a warm
            // biome is no reason not to notice.
            string frost = Loadout.Effect("MeadFrostResist");
            Check(string.IsNullOrEmpty(frost)
                      ? "the frost resistance a spectator is given resolves - it does not" +
                        (ArenaSite.Cold ? ", and this site freezes" : ", but this site is warm")
                      : $"a spectator is kept warm with '{frost}', read off the kit's own mead" +
                        (ArenaSite.Cold ? "" : " (not needed at this site, which does not freeze)"),
                  !ArenaSite.Cold || !string.IsNullOrEmpty(frost));

            if (!VersePlugin.ArenaUnbreakable.Value) return;

            int soft = 0;
            foreach (ZDO piece in gallery)
                if (piece.GetFloat(ZDOVars.s_health, 0f) < Fixture.Health) soft++;

            Check($"every gallery piece is unbreakable ({soft} with ordinary health)", soft == 0);
        }

        /// <summary>
        /// Spike B's server-side half: a hand-written creature keeps its prefab, its verse and
        /// its stars, and is persistent. Persistence is not a detail -
        /// <c>ZDOMan.ReleaseNearbyZDOS</c> only ever reassigns persistent ZDOs, and ownership is
        /// what makes a client run the AI, so a non-persistent creature stands inert for ever.
        /// </summary>
        private static void CheckCreature(List<ZDO> litter)
        {
            const string prefabName = "Greyling";
            const int fakeVerse = 999998;

            int hash = prefabName.GetStableHashCode();
            GameObject prefab = ZNetScene.instance.GetPrefab(hash);
            Check($"'{prefabName}' is a prefab this server knows", prefab != null);
            if (prefab == null) return;

            Check("and it really is a creature", prefab.GetComponent<Character>() != null);

            var at = new Vector3(0f, -5000f, 0f);
            Authorship.Suspend();
            ZDO creature;
            try
            {
                creature = ZDOMan.instance.CreateNewZDO(at, hash);
                creature.SetPrefab(hash);
                creature.Persistent = true;
                ZdoVerse.Set(creature, fakeVerse);
                creature.Set("verse.arena".GetStableHashCode(), 1, okForNotOwner: true);
                creature.Set(ZDOVars.s_level, 3);
            }
            finally
            {
                Authorship.Resume();
            }
            litter.Add(creature);

            // The BossStones trap: CreateNewZDO's prefab argument does not set the prefab field.
            Check("the creature kept its prefab field", creature.GetPrefab() == hash);
            Check("it is persistent, so ownership can be handed to a client",
                  creature.Persistent);
            Check("it belongs to one verse rather than all of them", ZdoVerse.Of(creature) == fakeVerse);
            Check("it is two-star", creature.GetInt(ZDOVars.s_level, 1) == 3);
            Check("the arena recognises it as its own", Arena.Ours(creature));

            // Authorship must not have claimed it: the creation was bracketed in Suspend().
            Check("creating it did not tag it as a player's own work",
                  ZdoVerse.Of(creature) == fakeVerse);
        }

        /// <summary>
        /// Spike E. The ring is one shared structure, so a wall broken in one verse is masked
        /// there for good unless the mask can be undone - and before this there was no way to
        /// undo it. Without this the second run in a verse happens in a broken ring.
        /// </summary>
        private static void CheckReveal(List<ZDO> litter)
        {
            const int a = 999997, b = 999996;

            int hash = "piece_chest_wood".GetStableHashCode();
            if (ZNetScene.instance.GetPrefab(hash) == null)
            {
                Check("a prefab to mask was available", false);
                return;
            }

            Authorship.Suspend();
            ZDO wall;
            try
            {
                wall = ZDOMan.instance.CreateNewZDO(new Vector3(0f, -5000f, 0f), hash);
                wall.SetPrefab(hash);
            }
            finally
            {
                Authorship.Resume();
            }
            litter.Add(wall);

            Check("a shared object starts visible to everybody",
                  !HideMask.HiddenFrom(wall, a) && !HideMask.HiddenFrom(wall, b));

            HideMask.Hide(wall, a);
            Check("hiding it from one verse hides it from that verse", HideMask.HiddenFrom(wall, a));
            Check("and leaves it alone for the other", !HideMask.HiddenFrom(wall, b));

            Check("revealing it reports that it did something", HideMask.Reveal(wall, a));
            Check("the verse can see it again", !HideMask.HiddenFrom(wall, a));
            Check("the other verse was never affected", !HideMask.HiddenFrom(wall, b));
            Check("revealing it twice is a no-op rather than a mess", !HideMask.Reveal(wall, a));

            // The case that would corrupt the list: two masks, remove the first.
            HideMask.Hide(wall, a);
            HideMask.Hide(wall, b);
            HideMask.Reveal(wall, a);
            Check("removing one of two masks keeps the other",
                  !HideMask.HiddenFrom(wall, a) && HideMask.HiddenFrom(wall, b));
        }

        private static void Check(string what, bool passed)
        {
            if (!passed) _failed++;
            Results.Add((passed ? "  PASS  " : "  FAIL  ") + what);
        }

        private static void Report(int objects)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- Verse: arena self-test ---");
            foreach (string line in Results) sb.AppendLine(line);
            sb.AppendLine(_failed == 0
                ? $"  all {Results.Count} checks passed; {objects} throwaway object(s) destroyed"
                : $"  {_failed} of {Results.Count} FAILED - the arena will not behave as designed");

            if (_failed == 0) VersePlugin.Log.LogInfo(sb.ToString());
            else VersePlugin.Log.LogError(sb.ToString());
        }

        /// <summary>
        /// Destroys a ZDO, taking ownership of it first.
        ///
        /// <para><b>The ownership claim is the whole method.</b> <c>ZDOMan.DestroyZDO</c> is
        /// <c>if (zdo.IsOwner()) m_destroySendList.Add(...)</c> - a silent no-op for anything
        /// the server does not own. And <c>ReleaseNearbyZDOS</c> hands every persistent ZDO in
        /// a player's active area to that player every two seconds, so essentially nothing in
        /// the ring is ever the server's: dropped loot, wandering wildlife, scenery, even the
        /// arena's own creatures once somebody stands near them. Without the claim, every cull
        /// this plugin makes does nothing, the swept objects come straight back, and the only
        /// evidence is a sweep that keeps finding the same things - which is how this was
        /// found, after a session of counting culls that never happened.</para>
        /// </summary>
        private static void Destroy(ZDO zdo)
        {
            if (zdo == null) return;

            _destroy = _destroy ?? AccessTools.Method(typeof(ZDOMan), "DestroyZDO", new[] { typeof(ZDO) });
            if (_destroy == null) return;

            zdo.SetOwner(ZDOMan.GetSessionID());

            DestroyArgs[0] = zdo;
            _destroy.Invoke(ZDOMan.instance, DestroyArgs);
        }
    }
}
