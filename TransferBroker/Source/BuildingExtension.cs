namespace TransferBroker {
    using ColossalFramework;
    using ColossalFramework.UI;
    using ColossalFramework.Plugins;
    using ColossalFramework.Threading;
    using CSUtil.Commons;
    using ICities;
    using JetBrains.Annotations;
    using System;
    using System.Threading;
    using System.Collections.Generic;
    using System.Reflection;
    using TransferBroker.API.Manager;
    using TransferBroker.Manager.Impl;
    using TransferBroker.Util;
    using UnityEngine;
    using UnityEngine.Assertions;

    using UnityEngine.SceneManagement;
    using static ColossalFramework.Plugins.PluginManager;

    /* Track when the _Unique_ building used for ACTIVATION is created
     * and destroyed.
     * This must be a unique building, otherwise TB is deactivated when
     * first instance is deleted.
     */
    [UsedImplicitly]
    public class BuildingExtension : BuildingExtensionBase {

        private TransferBrokerMod mod;

        private IBuilding building;

        public BuildingExtension() {
#if DEBUG
            Log.Info($"{GetType().Name}..ctor() {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            mod = Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly()).userModInstance as TransferBrokerMod;

            Assert.IsTrue(mod != null,
                $"An instance of {mod.GetType().Name} should already exist when {GetType().Name} is instantiated");
        }

#if DEBUG
        ~BuildingExtension() {
            Log.Info($"{GetType().Name}..~dtor() {Assembly.GetExecutingAssembly().GetName().Version}");
        }
#endif

        [UsedImplicitly]
        public override void OnCreated(IBuilding _building) {
#if DEBUG
            Log.Info($"{GetType().Name}.OnCreated({_building}) called - {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            building = _building;
            base.OnCreated(_building);
        }

        [UsedImplicitly]
        public override void OnReleased() {

#if DEBUG
            Log.Info($"{GetType().Name}.OnReleased() called - {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            building = null;

            base.OnReleased();
        }

        // public void OnModOutdated() {
        //     Log.Info($"OnModOutdated {Assembly.GetExecutingAssembly().GetName().Version}");
        // }
        [UsedImplicitly]
        public override void OnBuildingCreated(ushort id) {

#if DEBUG
            Log.Info($"{GetType().Name}.OnBuildingCreated({id}) called - {Assembly.GetExecutingAssembly().GetName().Version}");
#endif

            if (TransferBroker.IsActivatorBuilding(id)) {
                mod.NotifyManagers(TransferBrokerMod.Notification.Activated, id);
            }
        }

        [UsedImplicitly]
        public override void OnBuildingReleased(ushort id) {
#if DEBUG
            Log.Info($"{GetType().Name}.OnBuildingReleased({id}) called - {Assembly.GetExecutingAssembly().GetName().Version}");
#endif

            mod.NotifyManagers(TransferBrokerMod.Notification.Deactivated, id);
//            var building = Singleton<BuildingManager>.instance.m_buildings.m_buffer[id];
////            Log.Info($"{GetType().Name}.OnBuildingReleased({id}) flags={building.m_flags}");
//            if (building.Info != null) {
//                var prefabName = PrefabCollection<BuildingInfo>.PrefabName((uint)building.Info.m_prefabDataIndex);
//                if (prefabName == TransferBroker.ACTIVATION_BUILDING ||
//                    prefabName == TransferBroker.ACTIVATION_TRAFFIC_PREFAB) {
//                }
//            }
        }

    }
}
