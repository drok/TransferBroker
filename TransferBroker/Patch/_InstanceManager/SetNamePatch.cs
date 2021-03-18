namespace TransferBroker.Patch._InstanceManager {
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;
    using UnityEngine;

    [HarmonyPatch(typeof(InstanceManager), "SetName")]
    internal class SetNamePatch {
        /// <summary>
        /// when loading asset from a file, IAssetData.OnAssetLoaded() is called for all assets but the one that is loaded from the file.
        /// this postfix calls IAssetData.OnAssetLoaded() for asset loaded from file.
        /// </summary>
        [HarmonyPostfix]
        [UsedImplicitly]
        public static void Postfix(InstanceManager __instance, InstanceID id, string newName) {
            TransferBrokerMod.Installed.broker.OnSetInstanceName(id, newName);
        }
    }
}
