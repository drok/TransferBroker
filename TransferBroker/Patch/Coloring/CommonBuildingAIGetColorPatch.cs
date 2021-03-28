namespace TransferBroker.Coloring {
    using ColossalFramework;
    using HarmonyLib;
    using JetBrains.Annotations;
    using UnityEngine;
    using System.Collections.Generic;
    using System.Reflection;
    using CSUtil.Commons;
    using ColossalFramework.Math;

    [HarmonyPatch]
    internal static class CommonBuildingAIGetColorPatch {

        /* Buildings are colored when they've made an offer which
         * was considered by the matchmaker.
         * Color intensity (between neutral/full saturation) is
         * on a priority scale. Higher priority = higher intensity.
         * Matched buildings are additionally mesh highlighted.
         * Blue = Outgoing offer, Green = Incoming Offer.
         */

        const float FULL_LERP_INTENSITY = TransferManager.TRANSFER_PRIORITY_COUNT >> 1;
        const float MIN_LERP_INTENSITY = 2;

        private static readonly Color OutgoingColor = new Color(0.1f, 0.1f, 0.8f, 1f);
        private static readonly Color IncomingColor = new Color(0.1f, 0.5f, 0.1f, 1f);
        private static readonly Color WarehousingColor = new Color(1f, 0.547f, 0.379f, 1f);
        // private static readonly Color WarehousingColor = new Color(1f, 1f, 1f, 1f);

        public static IEnumerable<MethodBase> TargetMethods() {
            var args = new System.Type[] { typeof(ushort), typeof(Building).MakeByRefType(), typeof(InfoManager.InfoMode) };
            yield return AccessTools.Method(typeof(CommonBuildingAI), "GetColor", args); /* All buildings */
        }

        [HarmonyPrefix]
        [UsedImplicitly]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.NamingRules", "SA1313:Parameter names should begin with lower-case letter", Justification = "Output argument is __result as required by Harmony")]
        public static bool CommonBuildingAI_Color(ref CommonBuildingAI __instance, ushort buildingID, ref Building data, InfoManager.InfoMode infoMode, ref UnityEngine.Color __result) {

            switch (infoMode) {
                case InfoManager.InfoMode.TrafficRoutes:

                    // Log.Info($"GetColor({buildingID}) = {Singleton<InfoManager>.instance.m_properties.m_neutralColor}");
                    // __result = Singleton<InfoManager>.instance.m_properties.m_neutralColor;
                    // __result = OutgoingColor;
                    //                    OutgoingColor

                    InstanceID b = InstanceID.Empty;
                    b.Building = buildingID;
                    if (TransferBrokerMod.Installed.broker.GetColoringData(b, out var transfer)) {
                        if (transfer.offerIn.Building == buildingID) {
                            float lerp;
                            Color targetColor;//  = transfer.amount != 0 ? Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Industry].m_activeColor :
                                    // transfer.offerOut.Building == buildingID ? WarehousingColor : IncomingColor;
                            if (transfer.offerOut.Building == buildingID) {
                                /* Ie, warehouse that did not fill any orders,
                                 * colored as active but dim.
                                 */
                                lerp = 0.75f;
//                                Log.Info($"Coloring Cache 1 #{buildingID} {lerp} (warehouse)");
                                // targetColor = Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Industry].m_activeColor;
                                targetColor = WarehousingColor;
                                // targetColor = Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Industry].m_inactiveColor;
                            } else {
                                lerp = 1f - Mathf.Clamp01((float)(transfer.offerIn.Priority + MIN_LERP_INTENSITY) / (float)(FULL_LERP_INTENSITY + MIN_LERP_INTENSITY));
                                targetColor = transfer.amount != 0 ? Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Industry].m_activeColor :
                                    IncomingColor;
//                                Log.Info($"Coloring Cache 2 #{buildingID} {lerp} out {transfer.offerOut.m_object.Type} {transfer.offerOut.Building}");
                            }

                            __result = Color.Lerp(targetColor,
                                 Singleton<InfoManager>.instance.m_properties.m_neutralColor,
                                 lerp);
//                            Log.InfoFormat($"Color in  #{buildingID} {lerp} {targetColor} lerped={__result}");
                        } else {
                            var targetColor = transfer.amount != 0 ? Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Industry].m_activeColor :
                                OutgoingColor;

                            var lerp = 1f - Mathf.Clamp01((float)(transfer.offerOut.Priority + MIN_LERP_INTENSITY) / (float)(FULL_LERP_INTENSITY + MIN_LERP_INTENSITY));
//                            Log.Info($"Coloring Cache 3 #{buildingID} {lerp} in {transfer.offerIn.m_object.Type} {transfer.offerIn.Building}  out {transfer.offerOut.m_object.Type} {transfer.offerOut.Building}");
                            __result = Color.Lerp(targetColor,
                                 Singleton<InfoManager>.instance.m_properties.m_neutralColor,
                                 lerp);
                        }
                        return false;
                    }
                    // Log.Info($"GetColor({buildingID}) = {prio}");

                    // InstanceID instance = InstanceID.Empty;
                    // instance.Vehicle = vehicleID;
                    // if (!Singleton<NetManager>.instance.PathVisualizer.IsPathVisible(instance)) {
                    //     var path = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleID].m_path;
                    //     if (path != 0) {
                    //         var pathUnit = Singleton<PathManager>.instance.m_pathUnits.m_buffer[path];
                    //         __result = Color.Lerp(Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Traffic].m_targetColor, Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Traffic].m_negativeColor, Mathf.Clamp01((float)(int)(pathUnit.m_length - MIN_LERP_DISTANCE) * (1 / (FULL_LERP_DISTANCE - MIN_LERP_DISTANCE))));
                    //         return false;
                    //     }
                    // }
                    break;
            }

            return true;
        }
    }
}
