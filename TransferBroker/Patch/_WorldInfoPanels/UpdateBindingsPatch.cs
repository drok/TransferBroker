namespace TransferBroker.Patch._WorldInfoPanels {
    using TransferBroker.Manager.Impl;
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;
    using System;

    [HarmonyPatch]
    internal class WorldInfoPanelsPatch {
        /// <summary>
        /// when loading asset from a file, IAssetData.OnAssetLoaded() is called for all assets but the one that is loaded from the file.
        /// this postfix calls IAssetData.OnAssetLoaded() for asset loaded from file.
        /// </summary>
        [HarmonyPatch(typeof(CityServiceVehicleWorldInfoPanel), "UpdateBindings")]
        [HarmonyPostfix]
        [UsedImplicitly]
        public static void ServiceVehiclePostfix() {
#if CATCH_UI_NOT_INSTALLED
            try {

                TransferBrokerMod.Installed.serviceVehicleUI.UpdateBindings();
            }
            catch (Exception e) {
                if (TransferBrokerMod.Installed != null) {
                    Log.Info($"TransferBrokerMod.Installed.serviceVehicleUI={TransferBrokerMod.Installed.serviceVehicleUI != null}");
                }
                Log.Warning($"Exception in UpdateBindings patch (TransferBrokerMod.Installed={TransferBrokerMod.Installed != null}) ({e.Message})\n{e.StackTrace}");
            }
#else
            TransferBrokerMod.Installed.serviceVehicleUI.UpdateBindings();
#endif
        }

        [HarmonyPatch(typeof(CitizenVehicleWorldInfoPanel), "UpdateBindings")]
        [HarmonyPostfix]
        [UsedImplicitly]
        public static void CitizenVehiclePostfix() {

            TransferBrokerMod.Installed.citizenVehicleUI.UpdateBindings();
        }

        [HarmonyPatch(typeof(CitizenWorldInfoPanel), "UpdateBindings")]
        [HarmonyPostfix]
        [UsedImplicitly]
        public static void CitizenPostfix() {

            TransferBrokerMod.Installed.citizenUI.UpdateBindings();
        }

        [HarmonyPatch(typeof(TouristWorldInfoPanel), "UpdateBindings")]
        [HarmonyPostfix]
        [UsedImplicitly]
        public static void TouristPostfix() {

            TransferBrokerMod.Installed.touristUI.UpdateBindings();
        }
    }
}
