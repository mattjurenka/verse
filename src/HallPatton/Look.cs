using UnityEngine;

namespace HallPatton
{
    /// <summary>
    /// Dresses Mark by writing the fields <c>VisEquipment</c> reads.
    ///
    /// Nothing is instantiated here. Every client renders a character it does not own straight
    /// out of that character's ZDO - model index, skin and hair colour, and one hash per
    /// equipment slot - so setting those fields server-side is the whole of it. Purely
    /// cosmetic: none of it is real equipment and none of it grants armour, which suits a man
    /// whose job is explaining objects rather than swinging them.
    ///
    /// Only the owner may write, so this is called on the server, which owns him.
    /// </summary>
    internal static class Look
    {
        /// <summary>What he is set to, and topped back up to. He is not here to be fought.</summary>
        internal const float Health = 500f;

        internal static void Apply(ZDO zdo)
        {
            if (zdo == null || !zdo.IsOwner()) return;

            zdo.Set(ZDOVars.s_playerName, Identity.Name);
            zdo.Set(ZDOVars.s_modelIndex, Mathf.Clamp(Plugin.LookModel.Value, 0, 1));
            zdo.Set(ZDOVars.s_skinColor,
                Plugin.ParseColor(Plugin.LookSkinColor.Value, new Vector3(1f, 0.88f, 0.78f)));
            zdo.Set(ZDOVars.s_hairColor,
                Plugin.ParseColor(Plugin.LookHairColor.Value, new Vector3(0.88f, 0.88f, 0.86f)));

            zdo.Set(ZDOVars.s_hairItem, Hash(Plugin.LookHair.Value));
            zdo.Set(ZDOVars.s_beardItem, Hash(Plugin.LookBeard.Value));
            zdo.Set(ZDOVars.s_helmetItem, Hash(Plugin.LookHelmet.Value));
            zdo.Set(ZDOVars.s_chestItem, Hash(Plugin.LookChest.Value));
            zdo.Set(ZDOVars.s_legItem, Hash(Plugin.LookLegs.Value));

            // Capes live in the shoulder slot; variant 0, quality 1 is the plain version.
            zdo.Set(ZDOVars.s_shoulderItem, Hash(Plugin.LookShoulder.Value));
            zdo.Set(ZDOVars.s_shoulderItemVariant, 0);
            zdo.Set(ZDOVars.s_shoulderItemQuality, 1);

            zdo.Set(ZDOVars.s_rightItem, Hash(Plugin.LookRightHand.Value));
            zdo.Set(ZDOVars.s_rightItemQuality, 1);
            zdo.Set(ZDOVars.s_leftItem, Hash(Plugin.LookLeftHand.Value));
            zdo.Set(ZDOVars.s_leftItemVariant, 0);
            zdo.Set(ZDOVars.s_leftItemQuality, 1);

            // Players wake up lying down: the flag defaults to true when absent, so every
            // client would play a second of getting-up animation the moment he appears.
            zdo.Set(ZDOVars.s_wakeup, false);

            // Set explicitly so no client ever reads him as a corpse: nothing on the server
            // side simulates him, so nothing would ever write these.
            zdo.Set(ZDOVars.s_maxHealth, Health);
            zdo.Set(ZDOVars.s_health, Health);
        }

        /// <summary>Prefab name to stable hash; empty means "nothing in this slot", which is 0.</summary>
        private static int Hash(string prefabName) =>
            string.IsNullOrWhiteSpace(prefabName) ? 0 : prefabName.Trim().GetStableHashCode();
    }
}
