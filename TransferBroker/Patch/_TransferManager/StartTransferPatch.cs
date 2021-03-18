#if DEBUG
namespace TransferBroker.Patch._TransferManager {
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;
    using ColossalFramework;
    using ColossalFramework.Plugins;
    using UnityEngine.Assertions;
    using System.Reflection;

    [HarmonyPatch(typeof(TransferManager), "StartTransfer")]
    internal class StartTransferPatch {
        /* <summary>
         * This patch is only used in the debug build to monitor matches made by the vanilla
         * MatchOffer() and hopefully other Mods (like More Effective Transfer) which use
         * the existing StartTransfer() instead of providing their own private copy.
         * </summary>
         */

        [HarmonyPrefix]
        [UsedImplicitly]
        public static bool Prefix(TransferManager __instance, TransferManager.TransferReason material, TransferManager.TransferOffer offerOut, TransferManager.TransferOffer offerIn, int delta) {

            Log.Warning("StartTransferPatch() Prefix() - SeenXFER");
            TransferBrokerMod.Installed.broker.LogTransfer("SeenXFER", material, offerOut, offerIn, delta);

            return true;
        }
    }
}
#endif
