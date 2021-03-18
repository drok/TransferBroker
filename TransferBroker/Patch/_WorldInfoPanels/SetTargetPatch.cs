namespace TransferBroker.Coloring {
    using TransferBroker.Manager.Impl;
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;
    using System;

    [HarmonyPatch(typeof(WorldInfoPanel), "SetTarget", new Type[] { typeof(UnityEngine.Vector3),  typeof(InstanceID), })]
    internal class SetTargetPatch {
        /// <summary>
        /// when loading asset from a file, IAssetData.OnAssetLoaded() is called for all assets but the one that is loaded from the file.
        /// this postfix calls IAssetData.OnAssetLoaded() for asset loaded from file.
        /// </summary>
        [HarmonyPrefix]
        [UsedImplicitly]
        public static void Prefix(WorldInfoPanel __instance, UnityEngine.Vector3 worldMousePosition, InstanceID id ) {
#if DEBUG
            Log.Info($"WorldInfoPanel.SetTarget({id.Type} = {TransferBroker.InstanceName(id)})");
#endif
            TransferBrokerMod.Installed.broker.SetInfoViewTarget(id);
        }
    }
}
