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
    using CSUtil.Commons;
    using System;

    [HarmonyPatch]
    internal static class PathVisualizerAddPathsPatch {

        /* The vanilla visualiser only highlights paths of visiting vehicles
         * and returning own vehicles.
         * This patch also highlights the own vehicles which are outbound,
         * or otherwise belong to the building.
         */

        /* Mark vehicles in full red if they travel further than 4km */
        public static IEnumerable<MethodBase> TargetMethods() {
            if (TransferBrokerMod.Installed.PathVisualizer_AddInstance != null) {
                yield return AccessTools.Method(typeof(PathVisualizer), "AddPaths", new Type[] { typeof(InstanceID), typeof(int), typeof(int), });
            }
        }

        [HarmonyPrefix]
        [UsedImplicitly]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.NamingRules", "SA1313:Parameter names should begin with lower-case letter", Justification = "Output argument is __result as required by Harmony")]
        public static void Prefix(ref PathVisualizer __instance, InstanceID target, int min, int max) {

            if (min != 0) {
                return;
            }
            try {
                if (target.Building != 0) {
                    var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    var ownedID = buildings[target.Building].m_ownVehicles;
                    if (ownedID != 0) {
                        InstanceID vehicle = InstanceID.Empty;
                        var vehicles = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
                        while (ownedID != 0) {
                            if ((vehicles[ownedID].m_flags & (Vehicle.Flags.Created | Vehicle.Flags.Deleted | Vehicle.Flags.WaitingPath)) == Vehicle.Flags.Created) {

                                var info = vehicles[ownedID].Info;
                                bool flag = false;
                                switch (info.m_class.m_service) {
                                    case ItemClass.Service.Residential:
                                        flag = __instance.showPrivateVehicles;
                                        break;
                                    case ItemClass.Service.PublicTransport:
                                        if (info.m_class.m_subService == ItemClass.SubService.PublicTransportPost) {
                                            flag = __instance.showCityServiceVehicles;
                                        } else {
                                            flag = __instance.showPublicTransport;
                                        }
                                        break;
                                    case ItemClass.Service.Fishing:
                                        if (info.m_vehicleAI is FishingBoatAI) {
                                            flag = __instance.showPublicTransport;
                                        } else {
                                            flag = __instance.showTrucks;
                                        }
                                        break;
                                    case ItemClass.Service.Industrial:
                                    case ItemClass.Service.PlayerIndustry:
                                        flag = __instance.showTrucks;
                                        break;
                                    default:
                                        flag = __instance.showCityServiceVehicles;
                                        break;
                                }

                                if (flag) {
                                    vehicle.Vehicle = ownedID;
                                    TransferBrokerMod.Installed.PathVisualizer_AddInstance.Invoke(__instance, new object[] { vehicle, });
                                }
                            }
                            ownedID = vehicles[ownedID].m_nextOwnVehicle;
                        }

                    }
                }
            }
            catch (Exception ex) {
                Log.Info($"Caught Exception while adding instance to visualiser: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
