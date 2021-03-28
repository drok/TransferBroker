/*(
 * Supply Chain Coloring - Cities Skylines mod
 *  Copyright (C) 2020 Radu Hociung <radu.csmods@ohmi.org>
 *  
 *  This program is free software; you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation; either version 2 of the License, or
 *  (at your option) any later version.
 *  
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *  
 *  You should have received a copy of the GNU General Public License along
 *  with this program; if not, write to the Free Software Foundation, Inc.,
 *  51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA.
 */
namespace TransferBroker.Coloring {
    using ColossalFramework;
    using HarmonyLib;
    using JetBrains.Annotations;
    using UnityEngine;
    using System.Collections.Generic;
    using System.Reflection;

    [HarmonyPatch]
    internal static class PedestrianGetColorPatch {

        /* Pedestrians are colored according to the distance they travel,
         * on the same color gradient as the Traffic info panel.
         * While pedestrians don't create congestion, they are an untapped
         * opportunity for revenue via public transport tickets.
         */

        /* Mark cims in full red if they travel further than 3km */
        const float FULL_LERP_DISTANCE = 3000f;

        public static IEnumerable<MethodBase> TargetMethods() {
            var args = new System.Type[] { typeof(ushort), typeof(CitizenInstance).MakeByRefType(), typeof(InfoManager.InfoMode) };
            yield return AccessTools.Method(typeof(CitizenAI), "GetColor", args); /* Pedestrians */
        }

        [HarmonyPrefix]
        [UsedImplicitly]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.NamingRules", "SA1313:Parameter names should begin with lower-case letter", Justification = "Output argument is __result as required by Harmony")]
        public static bool CargoTruckAI_Color(ref VehicleAI __instance, ushort instanceID, ref CitizenInstance data, InfoManager.InfoMode infoMode, ref UnityEngine.Color __result) {
            InfoManager.SubInfoMode filterByResource = InfoManager.SubInfoMode.None;

            switch (infoMode) {
                case InfoManager.InfoMode.TrafficRoutes:
                    InstanceID instance = InstanceID.Empty;
                    instance.CitizenInstance = instanceID;
                    if (!Singleton<NetManager>.instance.PathVisualizer.IsPathVisible(instance)) {
                        var path = Singleton<CitizenManager>.instance.m_instances.m_buffer[instanceID].m_path;

                        if (path != 0) {
                            var pathUnit = Singleton<PathManager>.instance.m_pathUnits.m_buffer[path];
                            /* Mark vehicles in full red if they travel further than 3km */
                            const float FULL_LERP_DISTANCE = 3000f;
                            __result = Color.Lerp(Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Traffic].m_targetColor, Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Traffic].m_negativeColor, Mathf.Clamp01((float)(int)pathUnit.m_length * (1 / FULL_LERP_DISTANCE)));
                            return false;
                        }
                    }
                    break;
            }

            return true;
        }
    }
}
