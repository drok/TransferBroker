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
    using CitiesHarmony.API;
    using UnityEngine;
    using UnityEngine.Assertions;

    using UnityEngine.SceneManagement;
    using static ColossalFramework.Plugins.PluginManager;

    [UsedImplicitly]
    public class LoadingExtension : LoadingExtensionBase {
        private TransferBrokerMod mod;

#if DEBUG
        private static int instances = 0;
        private int id;
#endif
        /* a lock indicating whether the mod has been activated and not yet deactivated
         * It is entered when app requests activation and released when the app requests release.
         * It is static so only one instance of this assembly is in use at one time, ie,
         * a second copy will block, and freeze the application, rather than corrupt the first.
         */
        internal static object inUse_lock = new object();

        /* Is the mod active, as opposed to incompatible and refusing to activate */
        public bool active = false;

        public LoadingExtension() {
#if DEBUG
            id = instances++;
            Log.Info($"{GetType().Name}..ctor() {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
#endif
            mod = Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly()).userModInstance as TransferBrokerMod;

            Assert.IsTrue(mod != null,
                $"An instance of {mod.GetType().Name} should already exist when {GetType().Name} is instantiated");
        }
#if DEBUG
        ~LoadingExtension() {
            Log.Info($"{GetType().Name}..~dtor() {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
            --instances;
        }
#endif
        // internal static LoadingExtension Instance = null;

        // FastList<ISimulationManager> simManager =>
        //     typeof(SimulationManager).GetField("m_managers", BindingFlags.Static | BindingFlags.NonPublic)
        //         ?.GetValue(null) as FastList<ISimulationManager>;

        [UsedImplicitly]
        public override void OnCreated(ILoading loading) {
            base.OnCreated(loading);

            // Log.Warning($"{GetType().Name}.OnCreated(loading not null: {loading != null}) called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances} IsGameLoaded={mod.IsGameLoaded}");
#if DEBUG
            Log.Info($"{GetType().Name}.OnCreated({loading.currentMode}) called. {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances} loadingComplete={loading.loadingComplete} IsGameLoaded={mod.IsGameLoaded}");
#endif

            Monitor.Enter(inUse_lock);

            mod.loading = loading;
            mod.loadingExtension = this;

            Assert.IsFalse(active,
                $"{GetType().Name}.OnCreated() should only be called on inactive mod");

            if (mod.IsEnabled && loading.currentMode == AppMode.Game) {
                ModsCompatibilityChecker mcc = new ModsCompatibilityChecker();
                mod.IsCompatibleWithOtherMods = mcc.PerformModCheck(mod);
                InstallIfPlayerIsInformed();
            }

            if (!mod.IsEnabled || loading.currentMode != AppMode.Game || !mod.IsCompatibleWithOtherMods) {
                active = false;
                if (!mod.IsGameLoaded && (TransferBrokerMod.Installed != null || mod.installPendingOnHarmonyInstallation)) {
                    mod.Uninstall();
                }
                //mod.threading.QueueSimulationThread(new Action(mod.DoUninstall));
                //                installed = false;
                // mod.threading = null;
            } else
            {
                /* FIXME: This happens on notification of mod shakeup, but needs to happen later,
                 * after all mods have had a chance to load. What is that event?
                 */
                mod.CacheVariables();
            }

#if TEST_EXCEPTION_HANDLING
            Assert.IsTrue(false, "Forced Harmony Debug Assertion in TB.LoadingExtension.OnCreate()");
            throw new Exception($"Forced Harmony Debug Exception in TB.LoadingExtension.OnCreate() Assert.raiseExceptions={Assert.raiseExceptions}");
#endif

        }

        [UsedImplicitly]
        public override void OnReleased() {

#if DEBUG
            Log.Info($"{GetType().Name}.OnReleased() called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
#endif
            base.OnReleased();

            if (active) {
#if false
                if (mod.IsGameLoaded) {
                    mod.threading.QueueSimulationThread(new Action(mod.Uninstall));
                } else {
                    mod.Uninstall();
                }

                //                installed = false;
                mod.threading = null;
#endif
//                mod.UnRegisterUI();

                active = false;
            }

            mod.loading = null;

            Monitor.Exit(inUse_lock);
        }

        // public void OnModOutdated() {
        //     Log.Info($"OnModOutdated {Assembly.GetExecutingAssembly().GetName().Version}");
        // }

        [UsedImplicitly]
        public override void OnLevelUnloading() {
#if DEBUG
            Log.Info($"{GetType().Name}.OnLevelUnloading {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
#endif

            if (active) {
                mod.NotifyManagers(TransferBrokerMod.Notification.LevelUnloading);

                mod.UnRegisterUI();
            }
            mod.IsGameLoaded = false;
        }

        [UsedImplicitly]
        public override void OnLevelLoaded(LoadMode mode) {
            SimulationManager.UpdateMode updateMode = SimulationManager.instance.m_metaData.m_updateMode;
            /* FIXME: this it not true when there are incompatible mods.
             */

            switch (mode) {
                case LoadMode.LoadGame:
                case LoadMode.NewGame:
                case LoadMode.NewGameFromScenario:
                    if (active) {
                        // mod.CheckDependencies();
                        mod.NotifyManagers(TransferBrokerMod.Notification.LevelLoaded);
                        mod.RegisterUI();
                    }
                    mod.IsGameLoaded = true;
                    break;
            }
#if DEBUG
            Log.Info($"{GetType().Name}.OnLevelLoaded complete. id={id}/{instances}");
#endif
        }

        internal void InstallIfPlayerIsInformed() {
#if DEBUG
            Log.Info($"{GetType().Name}.InstallIfPlayerIsInformed called.");
#endif
            string msg = null;
            if (mod.IsCompatibleWithGame) {
                if (mod.IsCompatibleWithOtherMods) {
                    active = true;
                } else if (mod.IsDependencyMet(TransferBrokerMod.DOCUMENTATION_TITLE)) {
                    active = true;
                    msg = $"WARNING: Incompatible mods were found, but thanks to having '{TransferBrokerMod.PLAYER_IS_INFORMED}', you are considered informed. {mod.Name} will now activate.";
                } else {
                    msg = $"WARNING: Incompatible mods were found, but it appears you have not read '{TransferBrokerMod.DOCUMENTATION_TITLE}', and are considered uninformed. {mod.Name} will remain inactive.";
                }
            }
            if (TransferBrokerMod.Installed == null && !mod.installPendingOnHarmonyInstallation) {
                if (msg != null) {
                    Log.Info(msg);
                    Debug.Log($"[{mod.Name}] {msg}");
                }
                if (!mod.IsGameLoaded && active) {
                    /* When called without a game loaded, ie, mod present before loading game, Install immediately on the Main thread
                     * When mod is installed mid game, install in LoadingExtensions.OnCreated, deferred to the Simulation thread
                     */
                    mod.PreInstall();
                    mod.Install();
                }
            }

        }
    }
}
