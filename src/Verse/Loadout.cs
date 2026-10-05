using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Verse
{
    /// <summary>
    /// Chests at the arena gate: one set a player leaves their own gear in, one set holding the
    /// loaner kit.
    ///
    /// <para><b>This class exists because of one asymmetry.</b> A player's inventory lives in
    /// their character profile on their own machine and no server-side code can read or write
    /// it - docs/verse-design.md calls that out as unfixable, and it is why the arena cannot
    /// simply hand somebody a sword. A <i>container's</i> inventory is different: it is a
    /// field on the container's ZDO (<c>ZDOVars.s_items</c>, a base64 <c>ZPackage</c>), which
    /// is how a chest's contents survive a restart and reach other clients. So a chest is the
    /// only inventory the server can both read and write, and therefore the only way to give a
    /// player an item or to take one off them.</para>
    ///
    /// <para><b>The chests are private, and the lock is vanilla's.</b>
    /// <c>piece_chest_private</c> refuses to open for anybody but the player whose id is in
    /// its <c>creator</c> field, and that id is readable server-side off the peer's character
    /// ZDO - <see cref="Warp"/> already does exactly that lookup for <c>!warp bed</c>. So the
    /// server stamps the chest and vanilla enforces it; there is no access check of ours to get
    /// wrong, and the other eleven members of a verse cannot help themselves to somebody's
    /// deposit.</para>
    ///
    /// <para><b>They are tagged to the verse at creation</b>, deliberately and not as a
    /// formality. An untagged chest is shared by every verse (verse-design.md: anything the
    /// server creates is untagged), and a shared chest holding a player's whole inventory would
    /// be lootable from eighteen other worlds. Tagging also keeps these objects out of
    /// <see cref="Divergence"/>'s copy-on-write path entirely, which matters because
    /// verse-design.md's one outstanding known risk there is precisely "needs checking for a
    /// chest being looted".</para>
    /// </summary>
    internal static class Loadout
    {
        /// <summary>A full player inventory is 32 slots; leave a little room above it.</summary>
        private const int DepositSlotsWanted = 36;

        /// <summary>However big one chest is, never place more than this many of either kind.</summary>
        private const int MaxChests = 4;

        /// <summary>Metres between chests in a row.</summary>
        private const float Spacing = 1.4f;

        /// <summary>
        /// How far above bare terrain a chest's base sits, so it does not sink into it.
        ///
        /// <para>Only used when there is no deck to stand it on. <c>WorldGenerator</c>'s height
        /// is the generated ground and the real thing is a smoothed mesh over a snowdrift, so a
        /// chest placed exactly at that number is the one the first live test found buried to
        /// its waist. On a deck the clearance is zero: the surface is known to the centimetre
        /// and a chest floating over a floor is the bug this number used to hide.</para>
        /// </summary>
        private const float Clearance = 0.3f;

        private static System.Reflection.FieldInfo _byId;

        private static readonly Dictionary<string, GameObject> Items =
            new Dictionary<string, GameObject>();

        /// <summary>Confirms the one reflective lookup; reported by the startup check.</summary>
        internal static string Resolve()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId == null ? "ZDOMan.m_objectsByID not found (arena chests)" : null;
        }

        private static Dictionary<ZDOID, ZDO> All()
        {
            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            return _byId?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }
        /// <summary>
        /// The item prefab for a name, out of <c>ObjectDB</c>'s own list.
        ///
        /// <para>Deliberately matched by walking <c>m_items</c> rather than through a lookup
        /// helper: that is the route <c>HallPatton.Knowledge</c> already uses to read 1,519
        /// items off the live dedicated server, so it is known to work there, and a dedicated
        /// server is missing enough of the client's surface that "known to work" is worth more
        /// than "tidier".</para>
        /// </summary>
        private static GameObject Prefab(string prefab)
        {
            if (Items.TryGetValue(prefab, out GameObject cached)) return cached;

            GameObject found = null;
            if (ObjectDB.instance?.m_items != null)
            {
                foreach (GameObject go in ObjectDB.instance.m_items)
                {
                    if (go == null || go.name != prefab) continue;
                    if (go.GetComponent<ItemDrop>()?.m_itemData?.m_shared == null) break;
                    found = go;
                    break;
                }
            }

            Items[prefab] = found;
            return found;
        }

        /// <summary>
        /// The name of the status effect a consumable grants, read off the item itself.
        ///
        /// <para>Here rather than written down anywhere, because the server can only apply a
        /// status effect by name hash and a name hard-coded in this plugin is one the game can
        /// rename without telling anybody - the same reason <c>ArenaRestedEffect</c> is a config
        /// key. Taking it off the very mead the kit hands out means a spectator's frost
        /// resistance and a fighter's cannot disagree about what it is.</para>
        /// </summary>
        internal static string Effect(string itemPrefab)
        {
            StatusEffect effect = Prefab(itemPrefab)?.GetComponent<ItemDrop>()?
                                  .m_itemData?.m_shared?.m_consumeStatusEffect;

            return effect == null ? null : effect.name;
        }

        /// <summary>
        /// One kit item, built from its prefab and made fit to be handed to a player.
        ///
        /// <para><b>Two fields have to be set by hand, and the first one is why the first run of
        /// the self-test wrote a chest that read back empty.</b> <c>m_dropPrefab</c> is
        /// assigned by <c>ItemDrop</c> when an item is instantiated in the world, so on a
        /// prefab asset that has never been instantiated it is null - and
        /// <c>Inventory.Save</c> writes the item's identity from it. Saving a clone of the
        /// prefab's own <c>m_itemData</c> therefore produced a package with fourteen nameless
        /// items in it, every one of which <c>Inventory.Load</c> then silently dropped. The
        /// package was valid; the items had no names.</para>
        ///
        /// <para>Durability is the same shape of problem with a worse symptom: it is normally
        /// filled in when the item is spawned, so a straight clone can arrive at zero, and a
        /// kit of gear that is already broken is harder to notice than a kit that is
        /// empty.</para>
        /// </summary>
        private static ItemDrop.ItemData Build(KitItem want)
        {
            GameObject prefab = Prefab(want.Prefab);
            if (prefab == null) return null;

            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();

            item.m_dropPrefab = prefab;

            // Clamped to what the item can actually hold, so a kit entry may ask for "a full
            // stack" without having to hard-code each prefab's ceiling - and so it can never
            // write an over-stacked ItemData, which is the same class of mistake as putting
            // m_stack = 3 on a one-per-slot weapon.
            int ceiling = Mathf.Max(1, item.m_shared?.m_maxStackSize ?? 1);
            item.m_stack = Mathf.Clamp(want.Stack, 1, ceiling);
            item.m_quality = Mathf.Max(1, want.Quality);
            item.m_variant = 0;
            item.m_equipped = false;
            item.m_crafterID = 0L;
            item.m_crafterName = "";
            item.m_durability = item.GetMaxDurability();

            return item;
        }

        /// <summary>
        /// The real item grid of one of these chests, read off the prefab's own
        /// <c>Container</c>.
        ///
        /// <para><b>Both dimensions matter, which is not obvious and cost a live test.</b> An
        /// <c>Inventory</c> stores each item's grid position, and <c>Inventory.Load</c> drops
        /// any item whose position falls outside the inventory it is loading into. Writing the
        /// kit into a <c>slots x 1</c> grid therefore put items at x=0..12 - coordinates that do
        /// not exist in a chest six columns wide - and a client would have loaded the first row
        /// and silently discarded the rest. The self-test caught it as "reads back 8 of 13".</para>
        /// </summary>
        internal static bool Grid(string chestPrefab, out int width, out int height, out int prefabHash)
        {
            width = 0;
            height = 0;
            prefabHash = chestPrefab.GetStableHashCode();

            GameObject prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            if (prefab == null) return false;

            Container container = prefab.GetComponent<Container>();
            if (container == null) return false;

            width = Mathf.Max(1, container.m_width);
            height = Mathf.Max(1, container.m_height);
            return true;
        }

        /// <summary>How many item slots one of these chests has, read off the real prefab.</summary>
        private static bool Capacity(string chestPrefab, out int slots, out int prefabHash)
        {
            slots = 0;
            if (!Grid(chestPrefab, out int width, out int height, out prefabHash)) return false;

            slots = width * height;
            return true;
        }

        /// <summary>Which set a gate chest belongs to, so the two cannot be confused.</summary>
        private static readonly int ChestKind = "verse.arena.chest".GetStableHashCode();

        internal const int Deposit = 1;
        internal const int Kit = 2;

        /// <summary>
        /// Finds this player's chests of one kind at the gate, or makes them.
        ///
        /// <para>Found rather than only remembered, because the chests are persistent and
        /// outlive the server process: after a restart a player's deposit is still sitting in a
        /// chest at the gate, and creating a second set beside it would be both untidy and
        /// alarming. The scan is one pass of the object table per player per server run.</para>
        ///
        /// <para><b>The kind marker is not decoration.</b> The first live test put three chests
        /// at the gate instead of six, because the search radius is wider than the gap between
        /// the two sets: the kit call found the deposit chests, decided they were already its
        /// own, and wrote the kit into the chests the player was supposed to empty their
        /// pockets into. Matching on a marker rather than on position is the fix; the extra
        /// separation below just makes it legible.</para>
        ///
        /// <para><b>Ownership is handed to the player, deliberately.</b> A ZDO the server
        /// created is owned by the server, and the server holds no GameObjects out where the
        /// players are - so a container RPC addressed to its owner arrives nowhere and the
        /// chest simply does not open. <c>ReleaseNearbyZDOS</c> would hand it over within two
        /// seconds, but "within two seconds" is not what somebody standing in front of a chest
        /// experiences, and in the Deep North two seconds is health.</para>
        /// </summary>
        /// <summary>
        /// How long a row of chests for this many slots will be, in metres, without building
        /// anything. <see cref="ArenaDeck"/> has to lay the floor before the chests stand on it,
        /// and only this class knows how many chests a kit needs or how far apart they go.
        /// </summary>
        /// <summary>
        /// Where to position a chest so that its base stands on <paramref name="surfaceY"/> -
        /// the deck's top - or on the terrain at this point when there is no deck.
        ///
        /// <para>The prefab's origin is not its base, so the two are not the same height; see
        /// <see cref="Footing"/>. Positioning by the origin is what left the gate chests
        /// hovering over their own floor.</para>
        /// </summary>
        private static float Stand(int prefabHash, float x, float z, float surfaceY)
        {
            bool deck = !float.IsNaN(surfaceY);
            float on = deck ? surfaceY : ArenaSite.HeightAt(x, z) + Clearance;

            if (Footing.Of(prefabHash, out float bottom, out float _)) return on - bottom;

            // Nothing measurable to go on - back to the old guess, which hovers rather than
            // sinks, because a chest half inside a floor cannot be opened and one a few
            // centimetres over it only looks silly.
            return deck ? on + Clearance : on;
        }

        internal static float RowSpan(string chestPrefab, int wantSlots)
        {
            if (!Capacity(chestPrefab, out int slots, out int _)) return 0f;

            int want = Mathf.Clamp(Mathf.CeilToInt(wantSlots / (float)slots), 1, MaxChests);
            return (want - 1) * Spacing;
        }

        internal static List<ZDO> Chests(string chestPrefab, int verse, long playerId, long peerUid,
                                         int kind, Vector3 origin, Vector3 along, int wantSlots,
                                         float surfaceY = float.NaN)
        {
            var mine = new List<ZDO>();
            if (ZDOMan.instance == null || ZNetScene.instance == null) return mine;
            if (!Capacity(chestPrefab, out int slots, out int prefabHash))
            {
                VersePlugin.Log.LogError(
                    $"arena: '{chestPrefab}' is not a container prefab this server knows about");
                return mine;
            }

            int want = Mathf.Clamp(Mathf.CeilToInt(wantSlots / (float)slots), 1, MaxChests);

            Dictionary<ZDOID, ZDO> all = All();
            if (all != null)
            {
                foreach (ZDO zdo in all.Values)
                {
                    if (zdo.GetPrefab() != prefabHash) continue;
                    if (ZdoVerse.Of(zdo) != verse) continue;
                    if (zdo.GetLong(ZDOVars.s_creator, 0L) != playerId) continue;
                    if (zdo.GetInt(ChestKind, 0) != kind) continue;
                    if (Vector3.Distance(zdo.GetPosition(), origin) > MaxChests * Spacing + 4f) continue;

                    // Re-asserted every time, because ownership drifts: ReleaseNearbyZDOS hands
                    // persistent ZDOs to whoever is standing nearest, and after a restart the
                    // server owns these again.
                    if (peerUid != 0L) zdo.SetOwner(peerUid);
                    mine.Add(zdo);
                }
            }

            // Stood back on the row, when there is a deck to stand them on. Two things make this
            // necessary rather than tidy, and both are about chests that already exist: one made
            // before there was a deck is at terrain height, which is the breakage the deck is
            // for, and one made before `ArenaRadius` changed is at the old gate - the search
            // above is wide enough to find it, which is what we want, but leaving it there would
            // mean a deck with nothing on it and a chest still standing on nothing. Only the
            // position moves. The contents are a field on the same ZDO and come with it.
            if (!float.IsNaN(surfaceY))
            {
                for (int i = 0; i < mine.Count; i++)
                {
                    Vector3 spot = origin + along.normalized * (i * Spacing);
                    spot.y = Stand(prefabHash, spot.x, spot.z, surfaceY);

                    if (Vector3.Distance(mine[i].GetPosition(), spot) > 0.05f)
                        mine[i].SetPosition(spot);
                }
            }

            Authorship.Suspend();
            try
            {
                while (mine.Count < want)
                {
                    Vector3 at = origin + along.normalized * (mine.Count * Spacing);

                    // Standing on whatever is under it: the deck's top when there is a deck -
                    // one height for the whole row, and something solid under every chest's
                    // footprint - and terrain, with clearance, otherwise.
                    at.y = Stand(prefabHash, at.x, at.z, surfaceY);

                    // CreateNewZDO's prefab argument does not set the ZDO's prefab field - it
                    // only feeds an internal portal index - so without SetPrefab this is real
                    // data that no client can instantiate. BossStones learned this the
                    // expensive way; see its comment and HANDOFF.md.
                    ZDO chest = ZDOMan.instance.CreateNewZDO(at, prefabHash);
                    chest.SetPrefab(prefabHash);
                    chest.SetRotation(Quaternion.LookRotation(-along.normalized));
                    ZdoVerse.Set(chest, verse);
                    chest.Set(ChestKind, kind, okForNotOwner: true);

                    // Marked as the arena.s own, so the site-occupancy check does not mistake
                    // the arena.s own furniture for somebody.s base and refuse to rebuild the
                    // ring around it.
                    chest.Set("verse.arena".GetStableHashCode(), 1, okForNotOwner: true);

                    // The lock, when the configured prefab is the private chest.
                    chest.Set(ZDOVars.s_creator, playerId);

                    // Stops Container.Load deciding this is a fresh world chest and adding its
                    // own default loot on top of whatever we put in.
                    chest.Set(ZDOVars.s_addedDefaultItems, true);

                    if (peerUid != 0L) chest.SetOwner(peerUid);

                    mine.Add(chest);
                }
            }
            finally
            {
                Authorship.Resume();
            }

            return mine;
        }

        /// <summary>
        /// Writes <paramref name="items"/> into <paramref name="chests"/>, filling each in turn.
        /// Reports what it could not place rather than silently dropping it - an arena that
        /// hands out eleven of fourteen kit items is worse than one that says so in the log.
        /// </summary>
        internal static bool Fill(List<ZDO> chests, string chestPrefab, KitItem[] items, out string problem)
        {
            problem = null;
            if (chests == null || chests.Count == 0) { problem = "no chest to fill"; return false; }
            if (!Grid(chestPrefab, out int width, out int height, out int _))
            {
                problem = $"'{chestPrefab}' is not a container";
                return false;
            }

            int slots = width * height;

            var missing = new List<string>();
            int index = 0;

            foreach (ZDO chest in chests)
            {
                // The container.s real grid, not slots x 1: Inventory.Load discards any item
                // whose saved grid position falls outside the inventory being loaded into.
                var inventory = new Inventory("arena", null, width, height);

                int placed = 0;
                while (index < items.Length && placed < slots)
                {
                    KitItem want = items[index];
                    index++;

                    ItemDrop.ItemData item = Build(want);
                    if (item == null)
                    {
                        missing.Add(want.Prefab);
                        continue;
                    }
                    if (!inventory.AddItem(item)) { missing.Add(want.Prefab); continue; }
                    placed++;
                }

                // A byte array, not a base64 string. Container.Save writes ZPackage.GetArray() and
                // Container.Load reads GetByteArray and gives up if it is null, so a string here
                // is a chest that is genuinely empty on every client. The first two live tests
                // were this bug, and the self-test missed it by reading the field back the same
                // wrong way it was written.
                var pkg = new ZPackage();
                inventory.Save(pkg);
                chest.Set(ZDOVars.s_items, pkg.GetArray());
            }

            if (index < items.Length)
                for (int i = index; i < items.Length; i++) missing.Add(items[i].Prefab);

            if (missing.Count > 0)
            {
                problem = "could not place " + string.Join(", ", missing.ToArray());
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a chest currently holds nothing. Used to tell "they deposited their gear"
        /// from "they walked straight past it", which is the only part of the deposit the
        /// server can actually check - their pockets stay invisible either way.
        /// </summary>
        internal static bool Empty(ZDO chest)
        {
            byte[] items = chest?.GetByteArray(ZDOVars.s_items);
            if (items == null || items.Length == 0) return true;

            try
            {
                var inventory = new Inventory("probe", null, 8, 8);
                inventory.Load(new ZPackage(items));
                return inventory.NrOfItems() == 0;
            }
            catch
            {
                // An unreadable payload is not an empty chest, and guessing that it is would
                // be the expensive way round.
                return false;
            }
        }

        /// <summary>Clears a chest's contents. Only ever used on a kit chest.</summary>
        internal static void Clear(ZDO chest)
        {
            if (chest == null) return;
            var empty = new Inventory("arena", null, 8, 1);
            var pkg = new ZPackage();
            empty.Save(pkg);
            chest.Set(ZDOVars.s_items, pkg.GetArray());
        }
    }
}
