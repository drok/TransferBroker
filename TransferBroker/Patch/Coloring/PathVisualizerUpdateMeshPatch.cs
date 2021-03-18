namespace TransferBroker.Coloring {
    using TransferBroker.Manager.Impl;
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;
    using System;

    [HarmonyPatch]
    internal class PathVisualizerUpdateMeshPatch {
        /// <summary>
        /// when loading asset from a file, IAssetData.OnAssetLoaded() is called for all assets but the one that is loaded from the file.
        /// this postfix calls IAssetData.OnAssetLoaded() for asset loaded from file.
        /// </summary>
        [HarmonyPatch(typeof(PathVisualizer), "UpdateMesh")]
        [UsedImplicitly]
        public static void Prefix() {
            TransferBrokerMod.Installed.updatingPathMesh = true;
        }

        [HarmonyPatch(typeof(PathVisualizer), "UpdateMesh")]
        [UsedImplicitly]
        public static void Postfix() {

            TransferBrokerMod.Installed.updatingPathMesh = false;
        }
    }
}
