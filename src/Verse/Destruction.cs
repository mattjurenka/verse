using System.Reflection;
using HarmonyLib;

namespace Verse
{
    /// <summary>
    /// Copy-on-write, destruction half: a verse removing a shared world object removes it
    /// only for itself.
    ///
    /// A client that fells a tree destroys its own copy and broadcasts <c>DestroyZDO</c>. Left
    /// alone, the server's <c>HandleDestroyedZDO</c> drops the one shared ZDO out of
    /// <c>m_objectsByID</c> and the tree is gone from every verse at once - confirmed in
    /// testing, where trees chopped in verse 2 were missing from verse 3, and the verse that
    /// had not chopped got the hole without the stump or the logs (those being client-created,
    /// and so correctly private).
    ///
    /// So a destroy is sorted by who owns the thing:
    ///
    /// - tagged to the sender's own verse - their building, their kill - destroy it for real;
    /// - untagged, meaning shared world content - keep it and hide it from that verse alone;
    /// - tagged to another verse - they should never have been able to see it, so ignore it.
    ///
    /// The destroy the sender's client already broadcast reaches only its own verse, because
    /// <see cref="Isolation"/> limits a peer's broadcasts to its verse. Nothing re-sends the
    /// object to the verse that hid it, because the mask is read in <c>ShouldSend</c>.
    /// </summary>
    internal static class Destruction
    {
        private static MethodInfo _handleDestroyed;
        private static readonly object[] Args = new object[1];

        internal static string Resolve()
        {
            _handleDestroyed = AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) });
            return _handleDestroyed == null ? "ZDOMan.HandleDestroyedZDO(ZDOID) not found" : null;
        }

        [HarmonyPatch(typeof(ZDOMan), "RPC_DestroyZDO")]
        internal static class ZDOMan_RPC_DestroyZDO_Patch
        {
            /// <summary>
            /// Reads the package itself and so always replaces the original. Every early-out
            /// happens before the first read, because a half-consumed package handed back to
            /// vanilla would be worse than either outcome.
            /// </summary>
            private static bool Prefix(ZDOMan __instance, long sender, ZPackage pkg)
            {
                if (_handleDestroyed == null || pkg == null) return true;
                if (!VersePlugin.Isolate.Value) return true;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

                int verse = Peers.VerseOf(sender);
                // The server destroying its own things, or a peer we never placed: leave
                // vanilla to it rather than guess.
                if (verse == Verses.None) return true;

                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    ZDOID uid = pkg.ReadZDOID();
                    ZDO zdo = __instance.GetZDO(uid);
                    if (zdo == null) continue;

                    int owner = ZdoVerse.Of(zdo);

                    if (owner == verse)
                    {
                        Args[0] = uid;
                        _handleDestroyed.Invoke(__instance, Args);
                        Metrics.Destroyed++;
                    }
                    else if (owner == Verses.None)
                    {
                        HideMask.Hide(zdo, verse);
                        Metrics.Masked++;
                    }
                    // else: another verse's object. Not ours to destroy and not theirs to ask.
                }

                return false;
            }
        }
    }
}
