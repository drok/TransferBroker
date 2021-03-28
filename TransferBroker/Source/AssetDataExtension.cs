namespace TransferBroker {
    using ColossalFramework;
    using ColossalFramework.UI;
    using ColossalFramework.Plugins;
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

    [UsedImplicitly]
    public class AssetDataExtension : AssetDataExtensionBase {
        private TransferBrokerMod mod;

        /* a lock indicating whether the mod has been activated and not yet deactivated
         * It is entered when app requests activation and released when the app requests release.
         * It is static so only one instance of this assembly is in use at one time, ie,
         * a second copy will block, and freeze the application, rather than corrupt the first.
         */

        /* Is the mod active, as opposed to incompatible and refusing to activate */
        public bool active = false;

        public AssetDataExtension() {
#if DEBUG
            Log.Info($"{GetType().Name}..ctor() {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            mod = Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly()).userModInstance as TransferBrokerMod;

            Assert.IsTrue(mod != null,
                $"An instance of {mod.GetType().Name} should already exist when {GetType().Name} is instantiated");
        }
#if DEBUG
        ~AssetDataExtension() {
            Log.Info($"{GetType().Name}..~dtor() {Assembly.GetExecutingAssembly().GetName().Version}");
        }
#endif

        [UsedImplicitly]
        public override void OnCreated(IAssetData _assetData) {
            base.OnCreated(_assetData);

#if DEBUG
            Log.Info($"{GetType().Name}.OnCreated({_assetData.GetType().Name}) called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version} IsGameLoaded={mod.IsGameLoaded}");
#endif

            mod.assetData = _assetData;
            mod.assetDataExtension = this;
            active = true;
        }

        [UsedImplicitly]
        public override void OnReleased() {

#if DEBUG
            Log.Info($"{GetType().Name}.OnReleased() called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            base.OnReleased();

            if (active) {

                mod.assetData = null;
                mod.assetDataExtension = null;
                active = false;
            }

        }

        public override void OnAssetLoaded(string name, object asset, Dictionary<string, byte[]> userData) {
#if DEBUG || EXPERIMENTAL
            Log.Info($"{GetType().Name}.OnAssetLoaded({name}) called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            base.OnAssetLoaded(name, asset, userData);

            if (name == TransferBrokerMod.PLAYER_IS_INFORMED) {
                mod.OnPlayerInformed(name);
            }
#if DEBUG
            foreach (var i in userData) {
                Log.Info($"{i.Key} => {i}");
            }
#endif
        }
    }
}
