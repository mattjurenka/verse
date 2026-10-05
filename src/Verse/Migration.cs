using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Turns an existing, pre-verse world into verse 1, once.
    ///
    /// <para>The problem it solves: install this plugin on a world people have already played
    /// and every object in it is untagged, which means "shared by every verse". The base they
    /// built would be visible to strangers, and the first time anyone changed anything
    /// copy-on-write would start forking their home out from under them. Meanwhile their boss
    /// kills would be every new verse's boss kills.</para>
    ///
    /// <para><b>It does not guess what players made.</b> Vanilla already records it. Every
    /// hammer-placed object stores its builder in its own ZDO - <c>Piece.SetCreator</c> writes
    /// <c>ZDOVars.s_creator</c> (<c>Piece.cs:434</c>) - and tamed animals carry
    /// <c>s_tamed</c>. Those two fields are the whole discriminator, and they are vanilla's own
    /// record of authorship, which is the same principle <see cref="Authorship"/> works on.
    /// Guessing from prefabs would have been wrong in a specific and ugly way: village ruins,
    /// stone circles and dungeon walls are all <c>Piece</c>s too, placed by locations rather
    /// than by people, and tagging those would have stolen the ruins out of the shared world.
    /// They have no creator, so they stay shared.</para>
    ///
    /// <para><b>And it does not guess who played.</b> <c>ZNet.World.m_playerHistory</c> is a
    /// list of everyone who has connected, persisted with the world metadata
    /// (<c>World.cs:235</c>), each entry carrying a <c>PlatformUserID</c> whose
    /// <c>m_userID</c> is exactly the string the registry is keyed by. Those accounts become
    /// verse 1's members, so they connect straight into their own world with no command to
    /// type.</para>
    ///
    /// <para>Everyone else keeps the behaviour that was already there: an account the registry
    /// has never seen gets a fresh private verse.</para>
    ///
    /// <para><b>Dry run first.</b> The default is to survey and report without writing
    /// anything, because the tag goes into the ZDO field set and therefore into the world
    /// save - so the only rollback is restoring the world. Read the report, check the player
    /// list is who you think it is, then set <c>MigrateDryRun</c> to false.</para>
    /// </summary>
    internal static class Migration
    {
        private static FieldInfo _byId;

        internal static void Run()
        {
            int into = VersePlugin.MigrateLegacyInto.Value;
            if (into <= 0) return;

            Record already = Verses.LegacyVerse();
            if (already != null)
            {
                VersePlugin.Log.LogInfo(
                    $"world already migrated into verse {already.Id}; nothing to do. Set " +
                    "Verse.MigrateLegacyInto to 0 to stop checking.");
                return;
            }

            int connected = ZNet.instance.GetPeers().Count;
            if (connected > 0)
            {
                VersePlugin.Log.LogError(
                    $"migration refused: {connected} player(s) are connected. It rewrites the " +
                    "world save and must run on an empty server.");
                return;
            }

            _byId = _byId ?? AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (_byId == null)
            {
                VersePlugin.Log.LogError("migration refused: ZDOMan.m_objectsByID did not resolve.");
                return;
            }

            bool dry = VersePlugin.MigrateDryRun.Value;
            var built = new List<ZDO>();
            var tamed = new List<ZDO>();
            int alreadyTagged = 0;
            int total = 0;

            if (!(_byId.GetValue(ZDOMan.instance) is Dictionary<ZDOID, ZDO> all))
            {
                VersePlugin.Log.LogError("migration refused: could not read the object table.");
                return;
            }

            foreach (ZDO zdo in all.Values)
            {
                total++;
                if (ZdoVerse.Of(zdo) != Verses.None) { alreadyTagged++; continue; }

                if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L) built.Add(zdo);
                else if (zdo.GetBool(ZDOVars.s_tamed)) tamed.Add(zdo);
            }

            List<string> accounts = History(out List<string> names);
            List<string> keys = Keys.WorldProgression();

            Report(into, dry, total, built.Count, tamed.Count, alreadyTagged, names, keys);
            if (dry) return;

            Apply(into, built, tamed, accounts, keys);
        }

        /// <summary>The accounts that have played this world, in the registry's own key form.</summary>
        private static List<string> History(out List<string> names)
        {
            var accounts = new List<string>();
            names = new List<string>();

            if (ZNet.World?.m_playerHistory == null) return accounts;

            foreach (ZNet.CrossNetworkUserInfo info in ZNet.World.m_playerHistory)
            {
                string id = info.m_id.m_userID;
                if (string.IsNullOrEmpty(id) || accounts.Contains(id)) continue;

                accounts.Add(id);
                names.Add($"{(string.IsNullOrEmpty(info.m_displayName) ? "?" : info.m_displayName)} ({id})");
            }
            return accounts;
        }

        private static void Apply(int into, List<ZDO> built, List<ZDO> tamed,
                                  List<string> accounts, List<string> keys)
        {
            Record record = Verses.Get(into);
            if (record == null)
            {
                record = Verses.CreateWithId(into, Leader(accounts));
                if (record == null)
                {
                    VersePlugin.Log.LogError(
                        $"migration refused: verse {into} already exists and is not the legacy one. " +
                        "Pick an unused id, or clear the registry.");
                    return;
                }
            }

            foreach (ZDO zdo in built) ZdoVerse.Set(zdo, into);
            foreach (ZDO zdo in tamed) ZdoVerse.Set(zdo, into);

            foreach (string account in accounts) Verses.Enroll(account, into);

            // The progression moves wholesale: this verse earned it, and leaving it in the
            // world set would hand it to every verse made from now on.
            foreach (string key in keys)
            {
                Verses.AddKey(record, key);
                Keys.Forget(key);
            }

            Verses.MarkLegacy(record);
            Verses.SaveIfDirty();

            if (accounts.Count >= Verses.MaxParty)
                VersePlugin.Log.LogWarning(
                    $"verse {into} has {accounts.Count} member(s) but Verse.MaxPartySize is " +
                    $"{Verses.MaxParty}. Seeding ignores the cap, so everyone is in - but " +
                    "anybody who runs leave will not be able to rejoin until you raise it.");

            // Written to disk now rather than at the next twenty-minute autosave. The tags
            // live in ZDO fields and therefore only exist in memory until a save, while the
            // registry was written the moment it changed - so a crash in between would leave
            // a registry claiming the world was migrated and a world that was not, and the
            // migration refuses to run twice. Closing that window matters more than the
            // second it costs.
            if (ZNet.instance != null) ZNet.instance.Save(sync: false);

            VersePlugin.Log.LogInfo(
                $"migrated: verse {into} now owns {built.Count + tamed.Count} object(s), " +
                $"{accounts.Count} member(s) and {keys.Count} progression key(s). " +
                "Set Verse.MigrateLegacyInto to 0; it will not run again either way.");
        }

        /// <summary>
        /// The leader gets the verse's commands, so an admin is the sensible choice. Falls
        /// back to whoever is first in the history.
        /// </summary>
        private static string Leader(List<string> accounts)
        {
            foreach (string account in accounts)
                if (ZNet.instance.IsAdmin(account)) return account;

            return accounts.Count > 0 ? accounts[0] : "";
        }

        private static void Report(int into, bool dry, int total, int built, int tamed,
                                   int alreadyTagged, List<string> names, List<string> keys)
        {
            var sb = new StringBuilder();
            sb.AppendLine(dry
                ? $"--- Verse: migration DRY RUN (nothing will be written) ---"
                : $"--- Verse: migrating this world into verse {into} ---");
            sb.AppendLine($"  objects in world : {total}");
            sb.AppendLine($"  player-built     : {built}   (ZDOs with a creator - everything a hammer placed)");
            sb.AppendLine($"  tamed animals    : {tamed}");
            sb.AppendLine($"  left shared      : {total - built - tamed - alreadyTagged}   " +
                          "(terrain, vegetation, locations, dungeons - what new verses need)");
            if (alreadyTagged > 0)
                sb.AppendLine($"  already in a verse: {alreadyTagged}   (left alone)");

            sb.AppendLine($"  members          : {names.Count}");
            foreach (string name in names) sb.AppendLine($"      {name}");
            if (names.Count == 0)
                sb.AppendLine("      NOBODY - the world has no player history, so verse 1 would " +
                              "have no members and everyone would get a new verse instead.");

            sb.AppendLine($"  progression keys : {keys.Count}");
            foreach (string key in keys) sb.AppendLine($"      {key}");

            if (dry)
                sb.AppendLine("  Check the member list is who you expect, then set " +
                              "Verse.MigrateDryRun = false. Back the world up first: the tag " +
                              "lives in the world save, so the only undo is a restore.");

            VersePlugin.Log.LogInfo(sb.ToString());
        }
    }
}
