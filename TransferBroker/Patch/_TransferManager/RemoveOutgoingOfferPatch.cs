namespace TransferBroker.Patch._TransferManager {
    using TransferBroker.Manager.Impl;
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;

    [HarmonyPatch(typeof(TransferManager), "RemoveOutgoingOffer")]
    internal class RemoveOutgoingOfferPatch {
        /// <summary>
        /// when loading asset from a file, IAssetData.OnAssetLoaded() is called for all assets but the one that is loaded from the file.
        /// this postfix calls IAssetData.OnAssetLoaded() for asset loaded from file.
        /// </summary>
        [HarmonyPrefix]
        [UsedImplicitly]
        public static bool Prefix(TransferManager __instance, TransferManager.TransferReason material, TransferManager.TransferOffer offer) {
#if DEBUG
            TransferBroker.LogOffer("-Out", 0, material, offer);
#endif
            TransferBrokerMod.Installed.broker.RemoveOffers(material, offer);
            return false;
        }
    }
}
