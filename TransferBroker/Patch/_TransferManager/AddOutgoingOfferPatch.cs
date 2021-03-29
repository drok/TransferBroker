namespace TransferBroker.Patch._TransferManager {
    using TransferBroker.Manager.Impl;
    using HarmonyLib;
    using JetBrains.Annotations;
    using CSUtil.Commons;
    using ColossalFramework;
    using UnityEngine;

    [HarmonyPatch(typeof(TransferManager), "AddOutgoingOffer")]
    internal class AddOutgoingOfferPatch {
        /// <summary>
        /// when loading asset from a file, IAssetData.OnAssetLoaded() is called for all assets but the one that is loaded from the file.
        /// this postfix calls IAssetData.OnAssetLoaded() for asset loaded from file.
        /// </summary>
        [HarmonyPrefix]
        [UsedImplicitly]
        public static bool Prefix(TransferManager __instance, TransferManager.TransferReason material, TransferManager.TransferOffer offer) {
#if DEBUG
            TransferBroker.LogOffer("+Out", 0, material, offer);
            //            var name = Singleton<BuildingManager>.instance.GetBuildingName(offer.Building, InstanceID.Empty);
            //            Log._DebugFormat("SaneTransferManager.AddOutgoingOfferPatch({0} prio {1}, {2} ({3}))", material.ToString(),
            //                offer.Priority, name, offer.Amount);
#endif
            if (material > TransferBroker.LAST_VALID_REASON)
                return true;

            if (offer.Exclude &&
                offer.Priority == 2 &&
                offer.Amount > 1 &&
                offer.m_object.Type == InstanceType.Building &&
                Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building].Info.m_buildingAI.GetType() == typeof(WarehouseAI)) /* Outgoing offer by Warehouse, needs to be split into priority bands */
            {
                if ((Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building].m_flags & Building.Flags.Downgrading) == 0)
                {

                    /* Ok, building is in balancing mode, and is nearly full
                        * Split up the offer into 3 bands, priorities 2, 1 and 0
                        * as each priority is matched differently.
                        * The entire offered amount if not urgent to dispose of.
                        * Ie, once the buffer reaches 75%, I should not send trucks
                        * to all outgoing connections at the same time. Only enough
                        * exports to cover the excess amount (over 75%)
                        */
                    var building = Singleton<BuildingManager>.instance.m_buildings.m_buffer[offer.Building];
                    var ai = building.Info.m_buildingAI as WarehouseAI;
                    // offer.Exclude = false; /* avoid recursive call */
                    TransferManager.TransferOffer offer_0 = offer;
                    TransferManager.TransferOffer offer_2 = offer;
                    int capacity_in_loads = ai.m_storageCapacity / TransferBrokerMod.WarehouseAI_maxLoadSize;
                    int balance = offer.Amount;
                    int buffer = building.m_customBuffer1 * 100;
                    int buffer_in_loads = buffer / TransferBrokerMod.WarehouseAI_maxLoadSize;

#if DEBUG
                    Log._DebugFormat(
                        "Outgoing offer from warehouse #{0} may be split buffer_in_loads={1} capacity_in_loads={2}",
                        offer.Building,
                        buffer_in_loads,
                        capacity_in_loads);
#endif
                    offer_2.Priority = 3; /* increase to 3 to avoid recursive call */
                    offer_2.Amount = Mathf.Min(balance, buffer_in_loads - (capacity_in_loads >> 1) - (capacity_in_loads >> 2)); // Excess over 75% offered at prio 2
                    balance -= offer_2.Amount;
                    Singleton<TransferManager>.instance.AddOutgoingOffer(material, offer_2);

                    if (balance > 0)
                    {
                        TransferManager.TransferOffer offer_1 = offer;
                        offer_1.Priority = 1;
                        offer_1.Amount = Mathf.Min(balance, (capacity_in_loads >> 1)); // middle 50% offered at priority 1
                        balance -= offer_1.Amount;
                        Singleton<TransferManager>.instance.AddOutgoingOffer(material, offer_1);
                    }

                    if (balance > 0)
                    {
                        offer_0.Priority = 0;
                        offer_0.Amount = balance; // bottom 25% offered at priority 0
                        Singleton<TransferManager>.instance.AddOutgoingOffer(material, offer_0);
                    }

                    return false;
                }
            }

            TransferBrokerMod.Installed.broker.AddOutgoingOffer(material, offer);
            return false;
        }
    }

}
