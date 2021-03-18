namespace TransferBroker.Patch._TransferManager {
    using TransferBroker.Manager.Impl;
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;
    using ColossalFramework;
    using UnityEngine;
    /* CAUTION: This patch is not reentrant due to the avoidRecursion static
     */

    [HarmonyPatch(typeof(TransferManager), "AddIncomingOffer")]
    internal class AddIncomingOfferPatch {
        /* <summary>
         * BUG FIX: In CS 1.13.1-f1-steam-win, in IndustrialBuildingAI.SimulationStepActive,
         * there is a duplicate call to AddIncomingOffer. These vars track the requests made and remove
         * consecutive duplicates
         * </summary>
         */
        private static TransferManager.TransferReason lastMaterial = TransferManager.TransferReason.None;
        private static TransferManager.TransferOffer lastOffer = default(TransferManager.TransferOffer);
        private static uint lastSimulationTick;

        private enum DetectionBool {
            True = 1,
            False = 2,
            Unknown = 3,
            Step2 = 4,
        };
        private static DetectionBool buggedIndustrialBuildingAI = DetectionBool.Unknown;

        private static bool avoidRecursion = false;

        /// <summary>
        /// when loading asset from a file, IAssetData.OnAssetLoaded() is called for all assets but the one that is loaded from the file.
        /// this postfix calls IAssetData.OnAssetLoaded() for asset loaded from file.
        /// </summary>
        [HarmonyPrefix]
        [UsedImplicitly]
        public static bool Prefix(TransferManager __instance, TransferManager.TransferReason material, TransferManager.TransferOffer offer) {

            switch (buggedIndustrialBuildingAI) {
                case DetectionBool.True:
                    /* Implement duplicate offer bug workaround, drop first offer of the tick */
                    switch (material) {
                        case TransferManager.TransferReason.Logs:
                        case TransferManager.TransferReason.Grain:
                        case TransferManager.TransferReason.Oil:
                        case TransferManager.TransferReason.Ore:
                        case TransferManager.TransferReason.Lumber:
                        case TransferManager.TransferReason.Food:
                        case TransferManager.TransferReason.Petrol:
                        case TransferManager.TransferReason.Coal:
                            if (offer.m_object.Type == InstanceType.Building) {
                                if (lastSimulationTick != Singleton<SimulationManager>.instance.m_currentTickIndex) {
                                    if (Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building].Info.m_class.m_service == ItemClass.Service.Industrial) {
#if DEBUG
                                        TransferBroker.LogOffer("+In/Bug", 0, material, offer);
#endif
                                        lastSimulationTick = Singleton<SimulationManager>.instance.m_currentTickIndex;
                                        /* Skip first offer */
                                        return false;
                                    }
                                }
                            }
                            break;
                    }
                    break;
                case DetectionBool.False:
                    break;
                case DetectionBool.Unknown:
                    /* Detect if the game has the duplicate offer bug, step1 - wait for an affected offer, ie not workers */
                    switch (material) {
                        case TransferManager.TransferReason.Logs:
                        case TransferManager.TransferReason.Grain:
                        case TransferManager.TransferReason.Oil:
                        case TransferManager.TransferReason.Ore:
                        case TransferManager.TransferReason.Lumber:
                        case TransferManager.TransferReason.Food:
                        case TransferManager.TransferReason.Petrol:
                        case TransferManager.TransferReason.Coal:
                            if (offer.m_object.Type == InstanceType.Building) {
                                if (lastSimulationTick != Singleton<SimulationManager>.instance.m_currentTickIndex) {
                                    if (Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building].Info.m_class.m_service == ItemClass.Service.Industrial) {
                                        buggedIndustrialBuildingAI = DetectionBool.Step2;
                                        lastOffer = offer;
                                        lastMaterial = material;
                                        lastSimulationTick = Singleton<SimulationManager>.instance.m_currentTickIndex;
                                    }
                                }
                            }
                            break;
                    }
                    break;
                case DetectionBool.Step2:
                    /* Detect if the game has the duplicate offer bug, ie second offer at same time will be for either the same material or an Industries material */
                    if (lastSimulationTick == Singleton<SimulationManager>.instance.m_currentTickIndex && offer.m_object == lastOffer.m_object) {
                        switch (material) {
                            case TransferManager.TransferReason.PlanedTimber:
                            case TransferManager.TransferReason.Paper:
                            case TransferManager.TransferReason.Flours:
                            case TransferManager.TransferReason.AnimalProducts:
                            case TransferManager.TransferReason.Petroleum:
                            case TransferManager.TransferReason.Plastics:
                            case TransferManager.TransferReason.Metals:
                            case TransferManager.TransferReason.Glass:
                                buggedIndustrialBuildingAI = DetectionBool.True;
                                break;
                            default:
                                buggedIndustrialBuildingAI = material == lastMaterial ? DetectionBool.True : DetectionBool.False;
                                break;
                        }
                    } else {
                        buggedIndustrialBuildingAI = DetectionBool.False;
                    }
                    if (buggedIndustrialBuildingAI == DetectionBool.True) {
#if DEBUG
                        TransferBroker.LogOffer($"+In/BugDetectedNow", 0, material, offer);
#endif
                        /* Skip second offer when the bug is first detected (ie, the buggy offer already went through) */
                        return false;
                    }
                    break;
            }

#if DEBUG
            TransferBroker.LogOffer($"+In", 0, material, offer);
#endif

            if (!avoidRecursion &&
                offer.Exclude &&
                offer.Priority <= 2 &&
                offer.Priority >= 1 &&
                offer.Amount > 1 &&
                offer.m_object.Type == InstanceType.Building &&
                Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building].Info.m_buildingAI.GetType() == typeof(WarehouseAI)) /* Incoming offer by Warehouse, needs to be split into priority bands */
            {
                avoidRecursion = true;
                if ((Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building].m_flags & Building.Flags.Filling) == 0) {

                    /* Ok, building is in balancing mode, and is nearly empty
                        * Split up the offer into 3 bands, priorities 2, 1 and 0
                        * as each priority is matched differently.
                        * The entire offered amount if not urgent to fetch of.
                        * Ie, once the buffer reaches 25%, I should not request trucks
                        * from all incoming connections at the same time. Only enough
                        * imports to cover the deficit amount (up to 25%)
                        */
                    var building = Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building];
                    var ai = building.Info.m_buildingAI as WarehouseAI;
                    // offer.Exclude = false; /* avoid recursive call */
                    int capacity_in_loads = ai.m_storageCapacity / TransferBrokerMod.WarehouseAI_maxLoadSize;
                    int balance = offer.Amount;
                    int buffer = building.m_customBuffer1 * 100;
                    int buffer_in_loads = buffer / TransferBrokerMod.WarehouseAI_maxLoadSize;

#if DEBUG
                    Log._DebugFormat(
                        "Incoming offer from warehouse #{0} may be split buffer_in_loads={1} capacity_in_loads={2}",
                        offer.Building,
                        buffer_in_loads,
                        capacity_in_loads);
#endif
                    if (offer.Priority == 2) {
                        TransferManager.TransferOffer offer_2 = offer;
                        offer_2.Priority = 3; /* increase to 3 to avoid recursive call */
                        offer_2.Amount = Mathf.Max(1, balance - (capacity_in_loads >> 2) - (capacity_in_loads >> 1)); // Excess over 75% offered at prio 2
                        balance -= offer_2.Amount;
                        Singleton<TransferManager>.instance.AddIncomingOffer(material, offer_2);

                    }

                    if (balance > 0) {
                        TransferManager.TransferOffer offer_1 = offer;
                        offer_1.Priority = 1;
                        if (offer.Priority == 2) {
                            offer_1.Amount = (capacity_in_loads >> 1); // middle 50% offered at priority 1
                        } else {
                            offer_1.Amount = Mathf.Max(1, balance - (capacity_in_loads >> 2)); // all but bottom 25% offered at priority 1
                        }
                        balance -= offer_1.Amount;
                        /* Avoid recursive call when prio=1. Only send an extra order if split is done */
                        Singleton<TransferManager>.instance.AddIncomingOffer(material, offer_1);
                    }

                    if (balance > 0) {
                        TransferManager.TransferOffer offer_0 = offer;
                        offer_0.Priority = 0;
                        offer_0.Amount = balance; // bottom 25% offered at priority 0
                        Singleton<TransferManager>.instance.AddIncomingOffer(material, offer_0);
                    }

                    avoidRecursion = false;
                    return false;
                }
                avoidRecursion = false;
            }
            TransferBrokerMod.Installed.broker.AddIncomingOffer(material, offer);
            return false;
        }
    }
}
