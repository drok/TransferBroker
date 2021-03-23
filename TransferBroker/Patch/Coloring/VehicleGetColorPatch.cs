/*(
 * Supply Chain Coloring - Cities Skylines mod
 *  Copyright (C) 2021 Radu Hociung <radu.csmods@ohmi.org>
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
    public static class VehicleGetColorPatch {

        /* Vehicles are colored according to the distance they travel,
         * on the same color gradient as the Traffic info panel.
         * In other words, vehicles which travel further generate more
         * congestion, and are shown in a brighter red.
         */

        /* Mark vehicles in full red if they travel further than 4km */
        const float FULL_LERP_DISTANCE = 4000f;
        const float MIN_LERP_DISTANCE = 800;

        public static IEnumerable<MethodBase> TargetMethods() {
            var args = new System.Type[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(InfoManager.InfoMode) };
            yield return AccessTools.Method(typeof(CargoTruckAI), "GetColor", args); /* Cargo Trucks */
            yield return AccessTools.Method(typeof(VehicleAI), "GetColor", args); /* Service vehicles */
            yield return AccessTools.Method(typeof(PassengerCarAI), "GetColor", args); /* private vehicles */
        }

        [HarmonyPrefix]
        [UsedImplicitly]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.NamingRules", "SA1313:Parameter names should begin with lower-case letter", Justification = "Output argument is __result as required by Harmony")]
        public static bool Vehicle_Color(ref VehicleAI __instance, ushort vehicleID, ref Vehicle data, InfoManager.InfoMode infoMode, ref UnityEngine.Color __result) {

            /* Use default colors for paths */
            if (TransferBrokerMod.Installed.updatingPathMesh) {
                return true;
            }

            bool colorVehicle;
            var visualizer = Singleton<PathVisualizer>.instance;
            var vehicleInfo = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleID].Info;
            switch (vehicleInfo.m_class.m_service) {
                case ItemClass.Service.Residential:
                    colorVehicle = visualizer.showPrivateVehicles;
                    break;
                case ItemClass.Service.PublicTransport:
                    if (vehicleInfo.m_class.m_subService == ItemClass.SubService.PublicTransportPost) {
                        colorVehicle = visualizer.showCityServiceVehicles;
                    } else {
                        colorVehicle = visualizer.showPublicTransport;
                    }
                    break;
                case ItemClass.Service.Fishing:
                    if (vehicleInfo.m_vehicleAI is FishingBoatAI) {
                        colorVehicle = visualizer.showPublicTransport;
                    } else {
                        colorVehicle = visualizer.showTrucks;
                    }
                    break;
                case ItemClass.Service.Industrial:
                case ItemClass.Service.PlayerIndustry:
                    colorVehicle = visualizer.showTrucks;
                    break;
                default:
                    colorVehicle = visualizer.showCityServiceVehicles;
                    break;
            }

            if (!colorVehicle)
                return true;

            /* Vehicles themselves are colored by distance they must travel */
            switch (infoMode) {
                case InfoManager.InfoMode.TrafficRoutes:
                    InstanceID instance = InstanceID.Empty;
                    instance.Vehicle = vehicleID;

                    var path = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleID].m_path;
                    if (path != 0) {
                        var pathUnit = Singleton<PathManager>.instance.m_pathUnits.m_buffer[path];
                        __result = Color.Lerp(Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Traffic].m_targetColor, Singleton<InfoManager>.instance.m_properties.m_modeProperties[(int)InfoManager.InfoMode.Traffic].m_negativeColor, Mathf.Clamp01((float)(int)(pathUnit.m_length - MIN_LERP_DISTANCE) * (1/(FULL_LERP_DISTANCE - MIN_LERP_DISTANCE))));
                        return false;
                    }
                    break;
            }

            return true;
        }
    }
}
