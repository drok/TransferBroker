namespace TransferBroker {
    using ColossalFramework.UI;
    using ColossalFramework.Plugins;
    using ColossalFramework;
    using CSUtil.Commons;
    using ICities;
    using JetBrains.Annotations;
    using System;
    using System.Threading;
    using System.Collections.Generic;
    using System.Reflection;
    using TransferBroker.API.Manager;
    using ColossalFramework.Threading;
    using CitiesHarmony.API;
    using UnityEngine;
    using UnityEngine.Assertions;

    [UsedImplicitly]
    public sealed class ThreadingExtension : ThreadingExtensionBase {

        private TransferBrokerMod mod;

        // internal static ThreadingExtension Instance = null;
        private IThreading threading;
        //        private bool installed;
#if DEBUG
        private static int instances = 0;
        private int id;
#endif

        internal uint simulationFrame => threading.simulationFrame;

        public ThreadingExtension() {

#if DEBUG
            id = instances++;
            Log.Info($"{GetType().Name}..ctor() {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
#endif
            mod = Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly()).userModInstance as TransferBrokerMod;

            Assert.IsTrue(mod != null,
                $"An instance of {mod.GetType().Name} should already exist when {GetType().Name} is instantiated");
        }

#if DEBUG
        ~ThreadingExtension() {

            Log.Info($"{GetType().Name}..~dtor() {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
            --instances;
        }
#endif
        /* Called every time (this or another) mod is changed */
        public override void OnCreated(IThreading _threading) {
            /* ThreadingExtensions are OnCreated() on the Main Thread, when a GameMode starts or a mod is updated midgame
             * IUserMod.OnEnabled() runs on the Main thread, when enabled/disabled, or *this* mod updated midgame.
             * Harmony patches must be done on the Simulation thread to be synchronous with hooking TransferManager's data
             *
             * Compatibility checks have to be done after all mods are enumerated, not before the GameMode starts.
             * Enabling/Disabling this mod as a consequence of incompatibility must follow the OnCreated() to access the full
             * list of loaded mods, and on the Main thread to be thread-safe wrt. PluginManager's data.
             *
             * These constraints lead to the following load sequence:
             *
             * OnEnable() does almost nothing; merely checks that the application is compatible by version number.
             * OnCreated() checks compatibility with other mods and schedules Patching on the Simulation thread
             *
             * The unload sequence is:
             *
             * OnReleased() schedules Uninstall on the Simulation thread
             * Uninstall() unpatches and removes data hooks, and removes reference to self, allowing DLL to be unloaded
             *
             * When the mod is live removed, OnDisabled() is called on the Main thread, but not OnRelased():
             * OnDisabled() schedules Uninstall() on the Simulation thread via the Threading extension
             *          iff a Game mode is already running
             *          if a Game mode is not running, Uninstall() is not scheduled, the DLL is un-referenced to allow unloading
             * Uninstall() unpatches and removes data hooks, and unreferences the IUserMod to allow DLL unloading
             *
             * When the mode is live added, if already enabled by configuration, the Loading sequence is:
             * OnEnabled() checks version compatibility with the app, and nothing else.
             * OnCreated() schedules Install() on the Simulation Thread.
             * Install() patches and hooks the data synchronous with Simulation
             */
            base.OnCreated(_threading);

#if DEBUG
            Log.Info($"{GetType().Name}.OnCreated() called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
#endif

            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.OnCreated() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

#if DEBUG
            if (Thread.CurrentThread.Name is null) {
                Thread.CurrentThread.Name = "Main";
            }
#endif

            //            Assert.IsTrue(mod.IsGameLoaded,
            //                $"{GetType().Name}.OnCreated() should only be called when IsGameLoaded)");

            // Instance = this;
            threading = _threading;
            //ticksSinceLastMinuteUpdate = 0;
            //Singleton<SimulationManager>.instance.m_ThreadingWrapper.
            //            if (!installed) {
            mod.threading = this;
            //                installed = true;
            //            }

            if (/* mod.loading.currentMode == AppMode.Game && */
                mod.IsGameLoaded &&
                mod.loadingExtension.active &&
                !mod.installPendingOnHarmonyInstallation &&
                TransferBrokerMod.Installed == null) {

                // mod.CheckDependencies();
                if (mod.ShouldActivate) {
                    /* The UI can be installed on a separate condition, even
                     * if the Broker Manager itself is not compatible, hence the duplicate
                     * ShouldActivate condition.
                     */
                    mod.PreInstall();
                    mod.RegisterUI();
                }

                if (mod.ShouldActivate) {

                    /* TODO:
                     * Need to be able to compile against an older CitiesHarmony.API package than the current,
                     * and have the mod automatically link to the latest installed Harmony.
                     * Otherwise, next time the CitiesHarmony mod is updated, runtime linking will fail:
                     *
                     * Execution error: Could not load type 'HarmonyLib.Harmony' from assembly 'Sourcing'.
                     *  at Sourcing.SourcingMod.Install () [0x00006] in U:\proj\skylines\SaneSourcing\sourcing\sourcing\SourcingMod.cs:192
                     *  at AsyncAction.Execute () [0x00000] in <filename unknown>:0   [Core]
                     *
                     * TypeLoadException: Could not load type 'HarmonyLib.Traverse' from assembly 'Sourcing'.
                     *
                     * Assembly '0Harmony, Version=2.0.0.9, Culture=neutral, PublicKeyToken=null' resolved to ''  [Serialization]
                     */

                    mod.Install();
                }

            }

            base.OnCreated(threading);
        }

#if false
        public void Uninstall() {
        Log.Info($"{GetType().Name}.Uninstall() called - '{Thread.CurrentThread.Name}' thread. id={id}/{instances}");
                /* This is a guess. Before the game is loaded, the t variable is valid.
                 * When the game loads, the m_ThreadingWrapper is created, and the t variable becomes null
                 * This logic allows orderly uninstall if the mod is deleted while the game waits at the main menu
                 * ie, the uninstall request comes before the install request completes.
                 */

            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.Uninstall() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            mod.UnRegisterUI();
            mod.Uninstall();
            //            if (installed) {
            //                if (threading != null) {
            //            threading.QueueSimulationThread(new Action(mod.DoUninstall));
            //                } else {
            //                    SimulationManager.instance.m_ThreadingWrapper.QueueSimulationThread(new Action(SourcingMod.Instance.Uninstall));
            //                    // threading = null;
            //                }
            //                installed = false;
            //            mod.threading = null;
            //            }
        }
#endif
        //        public void Remove() {
        //            Log.Info($"{GetType().Name}.Remove() called - '{Thread.CurrentThread.Name}' thread. id={id}/{instances}");
        //            threading.QueueSimulationThread(new Action(SourcingMod.Instance.Remove));
        //        }

        public void QueueSimulationThread(Action action) {
#if DEBUG
            Log.Info($"{GetType().Name}.QueueSimulationThread({action.GetType().FullName}) called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
#endif
            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.QueueSimulationThread() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            threading.QueueSimulationThread(action);
        }

        public override void OnReleased() {
#if DEBUG
            Log.Info($"{GetType().Name}.OnReleased() called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version} id={id}/{instances}");
#endif

            /* On hot mod load, Mod is OnDisable() first, then ThreadingExtension is OnReleased().
             * On game reload, ThreadingExtension is OnReleased() and then a new one OnCreate()
             * At Quit to Menu, it is called after OnLevelUnloaded (ie, with !mod.IsGameLoaded)
             */
            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.OnReleased() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            /* FIXME: This is not true. and test.
             * If the mod is removed before the game finishes loading, OnReleased is called without IsGameLoaded.
             */
//            Assert.IsTrue(mod.IsGameLoaded,
//                $"{GetType().Name}.OnReleased() should only be called when IsGameLoaded)");
//
            if (mod.IsGameLoaded && (TransferBrokerMod.Installed != null || mod.installPendingOnHarmonyInstallation) && !mod.IsEnabled) {
                mod.Uninstall();
            }
            base.OnReleased();
        }

    } // end class
}
