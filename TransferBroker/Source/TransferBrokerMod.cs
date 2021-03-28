namespace TransferBroker {
    using ColossalFramework;
    using ColossalFramework.UI;
    using ColossalFramework.Plugins;
    using ColossalFramework.Threading;
    using CSUtil.Commons;
    using ICities;
    using JetBrains.Annotations;
    using System.Reflection;
    using System;
    using System.Threading;
    using TransferBroker.Util;
    using UnityEngine.SceneManagement;
    using System.Collections.Generic;
    using TransferBroker.API.Manager;
    using TransferBroker.Manager.Impl;
    using Harmony;
    using UnityEngine;
    using UnityEngine.Assertions;

    using static ColossalFramework.Plugins.PluginManager;

    public class TransferBrokerMod : IUserMod {
#if BENCHMARK
        public SourcingMod() {
            Benchmark.BenchmarkManager.Setup();
        }
#endif
#if LABS
        public const string BRANCH = "BETA";
#elif DEBUG
        public const string BRANCH = "DEBUG";
#elif EXPERIMENTAL
        public const string BRANCH = "EXPERIMENTAL";
#else
        public const string BRANCH = "STABLE";
#endif

        // public const TransferManager.TransferReason myreason = (TransferManager.TransferReason)254; // .None;
        //        public const TransferManager.TransferReason myreason = TransferManager.TransferReason.Goods;
        /* Issues:
         * 
         * Garbage - incinerator sends out all available trucks unneccesarily, they return nearly empty
         */

        // These values from `BuildConfig` class (`APPLICATION_VERSION` constants) in game file `Managed/Assembly-CSharp.dll` (use ILSpy to inspect them)
        public const uint GAME_VERSION = BuildConfig.APPLICATION_VERSION;
        public const uint GAME_VERSION_A = BuildConfig.APPLICATION_VERSION_A;
        public const uint GAME_VERSION_B = BuildConfig.APPLICATION_VERSION_B;
        public const uint GAME_VERSION_C = BuildConfig.APPLICATION_VERSION_C;
        public const uint GAME_VERSION_BUILD = BuildConfig.APPLICATION_BUILD_NUMBER;

        public const string PLAYER_IS_INFORMED = "Ability to Read";
        public const string PLAYER_IS_INFORMED_PACKAGE = "1145223801";
        public const string DOCUMENTATION = "https://steamcommunity.com/sharedfiles/filedetails/?id=2389228470";
        public const string DOCUMENTATION_TITLE = Versioning.PACKAGE_NAME + " workshop page on Steam";
        public const string DOCUMENTATION_READ_MILESTONE = "Road to Enlightenment";

        // Use SharedAssemblyInfo.cs to modify Sane Sourcing version
        // External mods (eg. CSUR Toolbox) reference the versioning for compatibility purposes
        // public Version ModVersion => typeof(SourcingMod).Assembly.GetName().Version;

        // used for in-game display
        // private string VersionString => typeof(TransferBrokerMod).Assembly.GetName().File.ToString(3);
#if LABS || DEBUG || EXPERIMENTAL
        private string VersionString => Assembly.GetExecutingAssembly().GetName().Version.ToString();
        public uint ImplementationVersion = 0;
#else
        private string VersionString => Versioning.MyFileVersion;
        public uint ImplementationVersion = Versioning.MyFileVersionNum;
#endif

        internal static string ModName = Versioning.PACKAGE_NAME + " " +
#if LABS || DEBUG || EXPERIMENTAL
                Assembly.GetExecutingAssembly().GetName().Version.ToString() + " " + BRANCH
#else
                Versioning.MyFileVersion
#endif
                ;

        public string Name => ModName;

        public string Description => "Supply chain matchmaking";

        internal bool IsCompatibleWithGame { get; private set; }
        internal bool IsEnabled { get; private set; }

        /* Indicate willingness to activate to other instances of
         * this mod (eg, offline and workshop enabled at the same time)
         * Can be used to later indicate something wrong with this instance
         * and that it will not activate. Then, another instance will
         * activate instead (simple consensus arbitration)
         */
        public bool willActivate { get { return true; } }

        /// <summary>
        /// determines whether Game data is loaded.
        /// </summary>
        internal bool IsGameLoaded { get; set; }

        public bool HaveNaturalDisastersDLC { get; private set; }
        internal bool updatingPathMesh { get; set; }
        /* Has the player read the documentation ? */
        // internal bool IsPlayerInformed { get; private set; }
        internal bool isInformed;

        /* Broker Install Pending on Harmony installation */
        internal bool installPendingOnHarmonyInstallation;

#if CITIESHARMONY_ISSUE_13_WORKAROUND
        List<Action> _harmonyReadyActions = new List<Action>();
#endif

        internal static TransferBrokerMod Installed = null;
        //         internal static SourcingMod Instance = null;

        internal TransferBroker broker;

        // private BrokerProperties brokerProperties;
        internal BrokerStatusPanel serviceVehicleUI;
        internal BrokerStatusPanel citizenVehicleUI;
        internal BrokerStatusPanel citizenUI;
        internal BrokerStatusPanel touristUI;

        internal static int WarehouseAI_maxLoadSize { get; private set; }

        internal ThreadingExtension threading { get; set; }

        /* FIXME: loading can be removed */
        internal ILoading loading { get; set; }
        internal LoadingExtension loadingExtension { get; set; }

        internal IMilestones milestones { get; set; }
        internal MilestonesExtension milestonesExtension { get; set; }

        internal IAssetData assetData { get; set; }
        internal AssetDataExtension assetDataExtension { get; set; }

        internal MethodInfo PathVisualizer_AddInstance;
        // private ITransferBroker broker = null; broker = SimulationManager.game base.gameObject.AddComponent<PathFind>();

        // internal bool InGameHotReload { get; set; } = false;

        /// <summary>
        /// determines if simulation is inside game/editor. useful to detect hot-reload.
        /// </summary>
        //        internal static bool InGame() =>
        //            SceneManager.GetActiveScene().name != "IntroScreen" &&
        //            SceneManager.GetActiveScene().name != "Startup";

        internal enum Notification {
            LevelLoaded,
            LevelUnloading,
            ModOutdated,
            ModLoaded,   /* When the mod is updated mid game */
            Activated,   /* When the activator building is created */
            Deactivated, /* When the activator building is deleted */
        }

        public TransferBrokerMod() {
            Assert.raiseExceptions = true;
#if DEBUG
            Log._Debug($"{GetType().Name}..ctor() called.");
#endif
        }

        [UsedImplicitly]
        public void OnEnabled() {
            string helloMessage =
                Name + $", Interface {Assembly.GetExecutingAssembly().GetName().Version} enabled for game version" +
                $" {GAME_VERSION_A}.{GAME_VERSION_B}.{GAME_VERSION_C}-f{GAME_VERSION_BUILD}";
            UnityEngine.Debug.LogWarning(helloMessage);
            Log.InfoFormat(helloMessage);
            // Shortcuts.Assert(false, $"{GetType().Name}.OnEnabled() test shortcut assertion FAIL on Main Thread (not '{Thread.CurrentThread.Name}')");
            // Assert.IsTrue(false, $"{GetType().Name}.OnEnabled() test unity assertion FAIL on Main Thread (not '{Thread.CurrentThread.Name}')");
            // Log.Info("Yet I continue");
            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.OnEnabled() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            IsCompatibleWithGame = CheckAppVersion();
            /* While debugging. Released versions may be become incompatible when the game updates. */
            Assert.IsTrue(IsCompatibleWithGame,
                $"{GetType().Name}.OnEnable() the mod should be compatible with app version {GAME_VERSION}");

            if (IsCompatibleWithGame) {
                // HarmonyHelper.EnsureHarmonyInstalled();
                HaveNaturalDisastersDLC = SteamHelper.IsDLCOwned(SteamHelper.DLC.NaturalDisastersDLC);
            }

            IsEnabled = Harmony.SetIncompatibleMods(new HashSet<IncompatibleMod>(){
                new IncompatibleMod(){ assemblyName = "EnhancedDistrictServices", },
                new IncompatibleMod(){ assemblyName = "EnhancedHearseAI",},
                new IncompatibleMod(){ assemblyName = "EnhancedGarbageTruckAI",},
                new IncompatibleMod(){ assemblyName = "ServicesOptimizationModule",},
                new IncompatibleMod(){ assemblyName = "GSteigertDistricts",},
            });

            if (!IsEnabled) {
                Log.Warning($"{Versioning.PACKAGE_NAME} is disabled due to conflicting mods. See Harmony Report for details");
            }

            if (SceneManager.GetActiveScene().name == "Game") {
                IsGameLoaded = SceneManager.GetActiveScene().isLoaded;
            }

#if TEST_EXCEPTION_HANDLING
            Assert.IsTrue(false, "Forced Harmony Debug Assertion in TB.Mod.OnEnabled()");
            throw new Exception("Forced Harmony Debug Exception in TB.Mod.OnEnabled()");
#endif
        }

        internal bool ShouldActivate { get {

                if (!IsEnabled) {
                    return false;
                }

                if (!IsCompatibleWithGame) {
                    return false;
                }

#if false
                // Log Mono version
                Type monoRt = Type.GetType("Mono.Runtime");
                if (monoRt != null) {
                    MethodInfo displayName = monoRt.GetMethod(
                        "GetDisplayName",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (displayName != null) {
                        Log.InfoFormat("Mono version: {0}", displayName.Invoke(null, null));
                    }
                }

                Log._Debug($"Scene is {SceneManager.GetActiveScene().name} .. valid={SceneManager.GetActiveScene().IsValid()}, isloaded={SceneManager.GetActiveScene().isLoaded}, isDirty={SceneManager.GetActiveScene().isDirty}");
#endif

                return true;
            }
        }

        internal void CacheVariables()
        {
#if DEBUG
            Log.Info($"{GetType().Name}.CacheVariables() {Assembly.GetExecutingAssembly().GetName().Version} called.");
#endif
            /* Setup cached variables */
            /* FIXME: Is this valid here, if other mods that change these variables have not loaded yet? */
            var WarehouseAI_GetMaxLoadSize = typeof(WarehouseAI).GetMethod("GetMaxLoadSize",
                BindingFlags.Instance | BindingFlags.NonPublic,
                Type.DefaultBinder,
                new Type[] { },
                null);

            if (WarehouseAI_GetMaxLoadSize != null)
            {
                var ai = new WarehouseAI();
                WarehouseAI_maxLoadSize = (int)WarehouseAI_GetMaxLoadSize.Invoke(ai, null);
            }
            else
            {
                WarehouseAI_maxLoadSize = 8000;
            }

        }

#if CITIESHARMONY_ISSUE_13_WORKAROUND
        private void DoWhenHarmonyReady(Action action) {
#if DEBUG
            Log.Info($"{GetType().Name}.DoWhenHarmonyReady({action.GetType().Name}) {Assembly.GetExecutingAssembly().GetName().Version} called.");
#endif
            try {
                if (Harmony.IsHarmonyInstalled) {
                    action();
                } else {
                    _harmonyReadyActions.Add(action);
                    Harmony.DoOnHarmonyReady(OnHarmonyReady);
                }
            }
            catch (Exception ex) {
                Log.Info($"Failed to schedule Harmony Ready ({ex.Message})\n{ex.StackTrace}");
            }
//            catch {
//                Log.Error($"Failed to access CitiesHarmony.API ... is the DLL present?");
//            }
        }
        private void OnHarmonyReady() {
            foreach(var action in _harmonyReadyActions) {
                try {
                    action();
                }
                catch (Exception ex) {
                    Log.Info($"Uncaught Exception while invoking action: '{ex.Message}'\n{ex.StackTrace}");
                }
            }
            _harmonyReadyActions.Clear();
        }
#endif
        internal void PreInstall() {
#if DEBUG
            Log.Info($"{GetType().Name}.PreInstall() {Assembly.GetExecutingAssembly().GetName().Version} called.");
#endif

            try {
#if CITIESHARMONY_ISSUE_13_WORKAROUND
                DoWhenHarmonyReady(() => DoNowOrInLiveGame(SetupUsefulMethods));
#else
                HarmonyHelper.DoOnHarmonyReady(() => OnReadyToInstall(DoInstall));
#endif
            }
            catch {
                Log.Error($"Failed to access CitiesHarmony.API ... is the DLL present?");
            }
        }

        internal void Install() {
#if DEBUG
            Log.Info($"{GetType().Name}.Install() {Assembly.GetExecutingAssembly().GetName().Version} called.");
#endif

            installPendingOnHarmonyInstallation = true;
            try {
#if CITIESHARMONY_ISSUE_13_WORKAROUND
                DoWhenHarmonyReady(() => DoNowOrInLiveGame(DoInstall));
#else
                HarmonyHelper.DoOnHarmonyReady(() => OnReadyToInstall(DoInstall));
#endif
            }
            catch {
                Log.Error($"Failed to access CitiesHarmony.API ... is the DLL present?");
            }
        }

        private void DoNowOrInLiveGame(Action action) {
            /* This will be called sometime after OnCreate, and after Harmony is downloaded;
             * by then the game may have loaded
             */
            try {
                if (!IsGameLoaded) {
                    action();
                } else {
                    threading.QueueSimulationThread(action);
                }
            }
            catch (Exception ex) {
                Log.Info($"Caught Exception during (scheduling) (Un)Installation: {ex.Message}\n{ex.StackTrace}");
            }
        }

        internal void SetupUsefulMethods() {
            try {
                Patcher.SetupUsefulMethods(this);

                Installed = this;
            }
            catch (Exception ex) {
                Log.Info($"Harmony Failure; cannot continue running: {ex.Message}\n{ex.StackTrace}");
                IsEnabled = false;
            }
        }

        /* Install() hooks the Simulation Data and installs detours/patches.
         * It runs on the Simulation thread
         */
        private void DoInstall() {
            Log.Info($"{GetType().Name}.DoInstall() {Assembly.GetExecutingAssembly().GetName().Version} called.");

            Assert.IsTrue(
                    (IsGameLoaded && Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread) ||
                    (!IsGameLoaded && Dispatcher.currentSafe == ThreadHelper.dispatcher),
                    $"{GetType().Name}.DoInstall() should be called on Simulation Thread when Game is in progress, otherwise on Main Thread. (not '{Thread.CurrentThread.Name}')");

            /* FIXME: Version arbitration still allows multiple versions to install conflicting coloring patches */
            if (IsEnabled && Installed != null) {

                RegisterCustomManagers();

                if (Patcher.Create().Install()) {

                    /* FIXME: ModLoaded needs to go after Start() */
                    NotifyManagers(Notification.ModLoaded);
                } else {
                    UnRegisterCustomManagers();
                    Installed = null;
                }
                installPendingOnHarmonyInstallation = false;
            }
        }

        /* Uninstall() schedules uninstallation if mid-game, or does it immediately if at main menu
         * or loading another mode (eg, AssetEditor)
         */
        internal void Uninstall() {
#if DEBUG

            Log.Warning($"{GetType().Name}.Uninstall() {Assembly.GetExecutingAssembly().GetName().Version} called. threading={threading != null}");
#endif
            /* This will be called sometime after OnCreate, and after Harmony is downloaded;
             * by then the game may have loaded
             */
            DoNowOrInLiveGame(DoUninstall);
        }

        /* DoUninstall() unhooks the Simulation Data and uninstalls detours/patches.
         * It runs on the Simulation thread.
         * 
         * It can be called even if Installation failed, eg, if mod is subbed mid-game, but Harmony is absent and cannot be fetched.
         */

        /* FIXME: Switching to Asset Editor
         *    Info.              1,387.4953608: BuildingExtension..~dtor() 0.3.1.30570
   Info.Main          1,440.347711: LoadingExtension..ctor() 0.3.1.30570 id=1/2
   Info.Main          1,440.357573: LoadingExtension.OnCreated(AssetEditor) called. 0.3.1.30570 id=1/2 loadingComplete=False IsGameLoaded=False
Warning.Main          1,440.359354: TransferBrokerMod.Uninstall() 0.3.1.30570 called. threading=True
   at TransferBroker.TransferBrokerMod.Uninstall() in U:\proj\skylines\SaneSourcing\sourcing\sourcing\TransferBrokerMod.cs:line 366
   at TransferBroker.LoadingExtension.OnCreated(ILoading loading) in U:\proj\skylines\SaneSourcing\sourcing\sourcing\LoadingExtension.cs:line 88
   at LoadingWrapper.OnLoadingExtensionsCreated()
   at LoadingWrapper.GetImplementations()
   at LoadingWrapper..ctor(.LoadingManager loadingManager)
   at LoadingManager.CreateRelay()
   at LoadingManager.MetaDataLoaded()
   at LoadingManager+<LoadLevelCoroutine>c__Iterator1.MoveNext()
   at UnityEngine.SetupCoroutine.InvokeMoveNext(IEnumerator enumerator, IntPtr returnValueAddress) in C:\buildslave\unity\build\Runtime\Export\Coroutines.cs:line 17

   Info.Main          1,440.362750: TransferBrokerMod.DoUninstall() 0.3.1.30570 called.
   Info.Main          1,440.365041: Exception unloading mod (Already in the same thread. Call directly)
  at ColossalFramework.Threading.Dispatcher.CheckAccessLimitation () [0x00000] in <filename unknown>:0
  at ColossalFramework.Threading.DispatcherBase.Dispatch (System.Action action, Boolean safe) [0x00000] in <filename unknown>:0
  at ColossalFramework.Threading.DispatcherBase.Dispatch (System.Action action) [0x00000] in <filename unknown>:0
  at TransferBroker.TransferBrokerMod.DoUninstall () [0x000a7] in U:\proj\skylines\SaneSourcing\sourcing\sourcing\TransferBrokerMod.cs:392

        */
        internal void DoUninstall() {
            Log.Info($"{GetType().Name}.DoUninstall() {Assembly.GetExecutingAssembly().GetName().Version} called.");

            Assert.IsTrue(
                    (IsGameLoaded && Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread) ||
                    (!IsGameLoaded && Dispatcher.currentSafe == ThreadHelper.dispatcher),
                    $"{GetType().Name}.DoUninstall() should Called on Simulation Thread when Game is in progress, otherwise on Main Thread. (not '{Thread.CurrentThread.Name}')");

            if (Installed != null) {
                 try {
                    /* FIXME: Should block dispatch thread while patching the UpdateBindings methods */
        Patcher.Instance?.Uninstall();

                    ThreadHelper.dispatcher.Dispatch(UnRegisterUI);

                    NotifyManagers(Notification.ModOutdated);

                    UnRegisterCustomManagers();

                    Installed = null;

                }
                catch (Exception e) {
                    Log.Info($"Exception unloading mod ({e.Message})\n{e.StackTrace}");
                }
            } else if (installPendingOnHarmonyInstallation) {
                /* FIXME: Need a way to cancel the HarmonyHelper.DoOnHarmonyReady() call */
                /* I could implement a queue of Actions here, and give CitiesHarmony a delegate to handle that queue
                 * then it would not matter if it calls it multiple time, each action would only run once, and it can
                 * be cleared in OnDisable()
                 */

#if CITIESHARMONY_ISSUE_13_WORKAROUND
                _harmonyReadyActions.Clear();
#else
                Assert.IsTrue(false,
                    "Clearing HarmonyHelper.DoOnHarmonyReady() queue on Uninstall should be implemented. See https://github.com/boformer/CitiesHarmony/issues/13");
#endif

                installPendingOnHarmonyInstallation = false;
            }
        }

        //        public void Remove() {
        //            Uninstall();
        //        }

        [UsedImplicitly]
        public void OnDisabled() {
            /* FIXME:
             *
             * AssertionException: Assertion failed. Value was False
             * Expected: True
             * TransferBrokerMod.OnDisabled() should only be called on Main Thread (not 'Main')
             *   at UnityEngine.Assertions.Assert.Fail (System.String message, System.String userMessage) [0x0001f] in C:\buildslave\unity\build\Runtime\Export\Assertions\Assert\AssertBase.cs:25
             *   at UnityEngine.Assertions.Assert.IsTrue (Boolean condition, System.String message) [0x0000e] in C:\buildslave\unity\build\Runtime\Export\Assertions\Assert\AssertBool.cs:19
             *   at TransferBroker.TransferBrokerMod.OnDisabled () [0x00017] in U:\proj\skylines\SaneSourcing\sourcing\sourcing\TransferBrokerMod.cs:512
             *   at (wrapper managed-to-native) System.Reflection.MonoMethod:InternalInvoke (object,object[],System.Exception&)
             *   at System.Reflection.MonoMethod.Invoke (System.Object obj, BindingFlags invokeAttr, System.Reflection.Binder binder, System.Object[] parameters, System.Globalization.CultureInfo culture) [0x00000] in <filename unknown>:0
             * Rethrow as TargetInvocationException: Exception has been thrown by the target of an invocation.
             *   at System.Reflection.MonoMethod.Invoke (System.Object obj, BindingFlags invokeAttr, System.Reflection.Binder binder, System.Object[] parameters, System.Globalization.CultureInfo culture) [0x00000] in <filename unknown>:0
             *   at System.Reflection.MethodBase.Invoke (System.Object obj, System.Object[] parameters) [0x00000] in <filename unknown>:0
             *   at ColossalFramework.Plugins.PluginManager+PluginInfo.Unload () [0x00000] in <filename unknown>:0
             * UnityEngine.DebugLogHandler:Internal_LogException(Exception, Object)
             * UnityEngine.DebugLogHandler:LogException(Exception, Object)
             * UnityEngine.Logger:LogException(Exception, Object)
             * UnityEngine.Debug:LogException(Exception)
             * ColossalFramework.Plugins.PluginInfo:Unload()
             * ColossalFramework.Plugins.PluginManager:OnDestroy()
             *
             */
#if DEBUG || LABS || EXPERIMENTAL
            Log.Info($"{Name} disabled.");
#endif
            // Log.Info($"Sane Sourcing disabled at {threading.simulationTime}.");

            //            if (InGame() && LoadingExtension.Instance != null) {
            //                //Hot reload Unloading
            //                LoadingExtension.Instance.OnLevelUnloading();
            //                LoadingExtension.Instance.OnReleased();
            //            }
            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.OnDisabled() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            /* FIXME: This is not quite correct. Race condition is if the mod is deleted after ThreadingExtension.OnCreated() but before
             * the Simulation thread activates and sets Installed
             */
            IsEnabled = false;
#if false
            if (Installed != null) {
                if (threading != null) {
                    threading.Uninstall();
                }
            }
            else if (installPendingOnHarmonyInstallation) {
                /* Eg, this mod deleted while waiting for Harmony to be subscribed and downloaded,
                 * or after LoadingExtension.OnCreated(Game) but before Simulation thread first runs
                 */
#if CITIESHARMONY_ISSUE_13_WORKAROUND
                _harmonyReadyActions.Clear();
#else
                Assert.IsTrue(false,
                    "Clearing HarmonyHelper.DoOnHarmonyReady() queue on Disabled should be implemented. See https://github.com/boformer/CitiesHarmony/issues/13");
#endif
            }
#endif

#if CITIESHARMONY_ISSUE_13_WORKAROUND
            Assert.IsTrue(_harmonyReadyActions.Count == 0,
                "There should be no remaining enqueued actions pending Harmony Install after Disable");
#else
            /* Appropriate HarmonyHelper check for empty queue goes HERE, when implemented in CitiesHarmony */
            Assert.IsTrue(false,
                "There should be no remaining enqueued actions pending Harmony Install after Disable");
#endif
        }

#if DEBUG
        ~TransferBrokerMod() {
            Log.Warning($"Destroying Mod {Assembly.GetExecutingAssembly().GetName().Version}");
        }
#endif

        private bool CheckAppVersion() {
            if (BuildConfig.applicationVersion != BuildConfig.VersionToString(
                    TransferBrokerMod.GAME_VERSION,
                    false)) {
                string[] majorVersionElms = BuildConfig.applicationVersion.Split('-');
                string[] versionElms = majorVersionElms[0].Split('.');
                uint versionA = Convert.ToUInt32(versionElms[0]);
                uint versionB = Convert.ToUInt32(versionElms[1]);
                uint versionC = Convert.ToUInt32(versionElms[2]);

#if DEBUG
                Log.Info($"Detected application version v{BuildConfig.applicationVersion}");
#endif

                bool isModTooOld = TransferBrokerMod.GAME_VERSION_A < versionA ||
                                   (TransferBrokerMod.GAME_VERSION_A == versionA &&
                                    TransferBrokerMod.GAME_VERSION_B < versionB);

                if (isModTooOld) {
                    string msg = string.Format(
                        Versioning.PACKAGE_NAME + " detected that you are running " +
                        "a newer game version ({0}) than it was built for ({1}). " +
                        "Please be aware that Sane Sourcing has not been updated for the newest game " +
                        "version yet and thus it is disabled.",
                        BuildConfig.applicationVersion,
                        BuildConfig.VersionToString(TransferBrokerMod.GAME_VERSION, false));

                    Log.Error(msg);
                    Singleton<SimulationManager>.instance.m_ThreadingWrapper.QueueMainThread(
                            () => {
                                UIView.library
                                      .ShowModal<ExceptionPanel>("ExceptionPanel")
                                      .SetMessage(
                                          Versioning.PACKAGE_NAME + " has not been updated yet",
                                          msg,
                                          false);
                            });
                }
                return !isModTooOld;
            }
            return true;
        }

        internal void NotifyManagers(Notification ev) {

            // Log.Info("Fixing non-created nodes with problems...");
            // FixNonCreatedNodeProblems();
#if DEBUG
            Log.InfoFormat("Notifying managers... {0}", ev.ToString());
#endif
            if (broker == null) {
//                Log.Error(Name + " failed to notify, the broker is not yet started (benign message if this TB is incompatible)");
                return;
            }

            switch (ev) {
                case Notification.LevelLoaded:
                    broker.OnLevelLoaded();
                    break;
                case Notification.LevelUnloading:
                    broker.OnLevelUnloading();
                    break;
                case Notification.ModOutdated:
                    broker.OnModOutdated();
                    break;
                case Notification.ModLoaded:
                    broker.OnModLoaded();
                    break;
            }
#if DEBUG
            broker.PrintDebugInfo();
#endif
        }

        internal void NotifyManagers(Notification ev, ushort id) {

            // Log.Info("Fixing non-created nodes with problems...");
            // FixNonCreatedNodeProblems();
#if DEBUG
            Log.InfoFormat("Notifying managers... {0}", ev.ToString());
#endif
            if (broker == null) {
//                Log.Error("Failed to notify, the broker is not yet started");
                return;
            }

            switch (ev) {
                case Notification.Activated:
                    broker.OnActivated(id, false);
                    break;
                case Notification.Deactivated:
                    broker.OnBuildingRemoved(id);
                    break;
            }
#if DEBUG
            broker.PrintDebugInfo();
#endif
        }

        internal void RegisterUI() {
#if DEBUG
            Log.Info($"{GetType().Name}.RegisterUI() {Assembly.GetExecutingAssembly().GetName().Version}");
#endif

            try {
#if CITIESHARMONY_ISSUE_13_WORKAROUND
                DoWhenHarmonyReady(() => DoNowOrInLiveGame(DoRegisterUI));
#else
                HarmonyHelper.DoOnHarmonyReady(() => OnReadyToInstall(DoRegisterUI));
#endif
            }
            catch {
                Log.Error($"Failed to access CitiesHarmony.API ... is the DLL present?");
            }
#if DEBUG
            Log.Info($"{GetType().Name}.RegisterUI() {Assembly.GetExecutingAssembly().GetName().Version} DONE");
#endif
        }

        private void DoRegisterUI() {
#if DEBUG
            Log.Info($"{GetType().Name}.DoRegisterUI() {Assembly.GetExecutingAssembly().GetName().Version} Installed={Installed != null} '{Thread.CurrentThread.Name}' thread. tasks={ThreadHelper.dispatcher.taskCount}");
#endif

            Assert.IsNull(serviceVehicleUI,
                $"{GetType().Name}.DoRegisterUI() should only be called once");
            // ThreadHelper.dispatcher.ProcessTasks();
            if (IsCompatibleWithGame) {

                try {
                    //                    var c = UIView.library.Get<UIPanel>("CityServiceVehicleWorldInfoPanel")?.components[1];
                    // c.Start();

                    //                    Log.Info($"{GetType().Name}.DoRegisterUI()CityServiceVehicleWorldInfoPanel vis={c.isVisible}");
                    //                    c.Show();
                    serviceVehicleUI = UIView.library.Get<UIPanel>("CityServiceVehicleWorldInfoPanel")?.components[1].AddUIComponent<BrokerStatusPanel>();
                    Assert.IsNotNull(serviceVehicleUI,
                        $"{GetType().Name}.DoRegisterUI() serviceVehicleUI should not be null");

                    citizenVehicleUI = UIView.library.Get<UIPanel>("CitizenVehicleWorldInfoPanel")?.components[1].AddUIComponent<BrokerStatusPanel>();
                    citizenUI = UIView.library.Get<UIPanel>("CitizenWorldInfoPanel")?.components[1].AddUIComponent<BrokerStatusPanel>();
                    touristUI = UIView.library.Get<UIPanel>("TouristWorldInfoPanel")?.components[1].AddUIComponent<BrokerStatusPanel>();
                }
                catch (Exception ex) {
                    Log.Info($"Failed to register UI ({ex.GetType().Name}: {ex.Message})\n{ex.StackTrace}");
                }
            }
#if DEBUG
            Log.Info($"{GetType().Name}.DoRegisterUI() {Assembly.GetExecutingAssembly().GetName().Version} aDONE. Dispatch tasks={ThreadHelper.dispatcher.taskCount} serviceVehicleUI={serviceVehicleUI != null}");
#endif
//            ThreadHelper.dispatcher.ProcessTasks();
#if DEBUG
            Log.Info($"{GetType().Name}.DoRegisterUI() {Assembly.GetExecutingAssembly().GetName().Version} DONE. Dispatch tasks={ThreadHelper.dispatcher.taskCount}");
#endif
        }

        internal void UnRegisterUI() {
#if DEBUG
            Log.Info($"{GetType().Name}.UnRegisterUI() {Assembly.GetExecutingAssembly().GetName().Version} tasks={ThreadHelper.dispatcher.taskCount}");
#endif

            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.OnEnabled() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            /* If the GameMode is not running, the UI was automatically destroyed
             * but if the mod is unloaded while the GameMode is running, remove
             * the info panels manually.
             * */
            if (serviceVehicleUI != null) {
                Assert.IsNotNull(serviceVehicleUI,
                    $"{GetType().Name}.UnRegisterUI() should only be called after RegisterUI");
//                ThreadHelper.dispatcher.ProcessTasks();

                //            serviceVehicleUI.Deactivate();
                //UIView.library.Get<UIPanel>("CityServiceVehicleWorldInfoPanel")?.components[1].RemoveUIComponent(serviceVehicleUI);
                if (serviceVehicleUI != null) {
#if DEBUG_UI_TEARDOWN
                    for (int attempt = 1; attempt <= 100 && serviceVehicleUI != null; ++attempt) {
                        try {
                            var parent = serviceVehicleUI.parent;
                            Log.Info($"{GetType().Name}.UnRegisterUI() attempt={attempt} hasParent={serviceVehicleUI.parent != null} contains1={parent?.components?.Contains(serviceVehicleUI)}");
                            // serviceVehicleUI.Hide();
                            serviceVehicleUI.DebugMessage("1");
                            Log.Info($"{GetType().Name}.UnRegisterUI() attempt={attempt} hasParent={serviceVehicleUI.parent != null} contains2={parent?.components?.Contains(serviceVehicleUI)}");
                            serviceVehicleUI.parent.RemoveUIComponent(serviceVehicleUI);
                            serviceVehicleUI.DebugMessage("2");
                            Log.Info($"{GetType().Name}.UnRegisterUI() attempt={attempt} hasParent={serviceVehicleUI.parent != null} contains3={parent?.components?.Contains(serviceVehicleUI)}");
                            serviceVehicleUI.DebugMessage("3");
                            parent.ResetLayout();
                            serviceVehicleUI.DebugMessage("4");
                            Log.Info($"{GetType().Name}.UnRegisterUI() attempt={attempt} hasParent={serviceVehicleUI.parent != null} contains4={parent?.components?.Contains(serviceVehicleUI)}");
#endif
                            UnityEngine.Object.Destroy(serviceVehicleUI);
                            serviceVehicleUI = null;
#if DEBUG_UI_TEARDOWN
                            Log.Info($"{GetType().Name}.UnRegisterUI() attempt={attempt} serviceVehicleUI={serviceVehicleUI != null} contains5={parent?.components?.Contains(serviceVehicleUI)}");
                            // GC.Collect(3, GCCollectionMode.Forced);
                            //UnityEngine.Object.Destroy(null);
                        }
                        catch (Exception ex) {
                            Log.Info($"When Removing UI component ({ex.GetType().Name}: {ex.Message})\n{ex.StackTrace}");
                            Thread.Sleep(50);
                        }
                        catch {
                            Log.Info($"When Removing UI component - generic exception.");
                        }
                        finally {
                        }
                    }
#endif
                }
                UnityEngine.Object.Destroy(citizenVehicleUI);
                citizenVehicleUI = null;
                UnityEngine.Object.Destroy(citizenUI);
                citizenUI = null;
                UnityEngine.Object.Destroy(touristUI);
                touristUI = null;
                /* just synchronize, allow the engine to clean up the objects flagged for destroying */
                // ThreadHelper.dispatcher.Dispatch(() => touristUI = null);

            }
//            Log.Info($"{GetType().Name}.UnRegisterUI() {Assembly.GetExecutingAssembly().GetName().Version} aDONE. Dispatch tasks={ThreadHelper.dispatcher.taskCount}");
//            ThreadHelper.dispatcher.ProcessTasks();
#if DEBUG
            Log.Info($"{GetType().Name}.UnRegisterUI() {Assembly.GetExecutingAssembly().GetName().Version} DONE. Dispatch tasks={ThreadHelper.dispatcher.taskCount}");
#endif
        }

        public void RegisterCustomManagers() {
            /* FIXME, I think this must happen on the main thread */
#if DEBUG
            Log.Info($"{GetType().Name}.RegisterCustomManagers() {Assembly.GetExecutingAssembly().GetName().Version}");
#endif

            GameObject gameObject = new GameObject(typeof(TransferBroker).Name);
            broker = gameObject.AddComponent<TransferBroker>();
            BrokerProperties properties = new BrokerProperties();
            // broker.InitializeProperties(brokerProperties);
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
        }

        public void UnRegisterCustomManagers() {
#if DEBUG
            Log.Info($"{GetType().Name}.UnRegisterCustomManagers() {Assembly.GetExecutingAssembly().GetName().Version}");
#endif

            // Game object is destroyed in broker.OnModOutdated()
            // broker.DestroyProperties(brokerProperties)
            UnityEngine.Object.Destroy(broker);

            broker = null;
        }

        internal void CheckDependencies() {
#if DEBUG
            Log.Info($"{GetType().Name}.CheckDependencies() {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            /* Simulation thread requirement is because of CheckMilestone() call */
            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
                $"{GetType().Name}.CheckDependencies() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

            /* If the Required package is installed after the mod is started,
             * the mod will not notice. I don't think this behaviour needs to
             * be different.
             */
#if false
            var wasread = PrefabCollection<BuildingInfo>.FindLoaded(DOCUMENTATION_READ_MILESTONE);
            isInformed = wasread != null;
            if (isInformed) {
                if (!broker.m_documentation.m_unlocked.exists) {
                    broker.m_documentation.m_unlocked.value = isInformed;
                }

            } else {
                var msg = $"[{Versioning.PACKAGE_NAME}] WARNING: Required '{PLAYER_IS_INFORMED}' was not found. Suggested tech support level: minimal; refer to {DOCUMENTATION}";
                Debug.LogWarning(msg);
                Log.Info(msg);
            }
            return;
#endif
            try {
            // milestonesExtension.OnReady();
            /* Fixme : use broker.m_documentation */
                var informedMilestone = MilestoneCollection.FindMilestone(DOCUMENTATION_READ_MILESTONE) as DocumentationMilestone;

                if (informedMilestone != null) {
#if DEBUG
                    Log.Info($"{GetType().Name}.CheckDependencies() isInformed={isInformed} unlocked={Singleton<UnlockManager>.instance.Unlocked(informedMilestone)} hasAsset={PrefabCollection<BuildingInfo>.LoadedExists(PLAYER_IS_INFORMED_PACKAGE + "." + PLAYER_IS_INFORMED + "_Data")}");
#endif
                    isInformed = Singleton<UnlockManager>.instance.Unlocked(informedMilestone) ||
                        PrefabCollection<BuildingInfo>.LoadedExists(PLAYER_IS_INFORMED_PACKAGE + "." + PLAYER_IS_INFORMED + "_Data");
                    // isInformed = true;

                    /* If this is the first time the docs were read when playing with the mod,
                     * show fireworks.
                     * If it's the first time the mod runs for this user, and the docs were read,
                     * skip the fireworks.
                     */
                    informedMilestone.m_openUnlockPanel = !informedMilestone.m_unlocked.value && informedMilestone.m_unlocked.exists;

#if !UNLOCKING_MILESTONE_PANEL_MAKES_SENSE
                    informedMilestone.m_openUnlockPanel = false;
#endif

                    if (!informedMilestone.m_unlocked.exists) {
                        informedMilestone.m_unlocked.value = isInformed;
                    }
#if DEBUG
                    Log.Info($"{GetType().Name}.CheckDependencies() isInformed={isInformed} isUnlocked={informedMilestone.m_unlocked} unlockPanel={informedMilestone.m_openUnlockPanel}");
#endif

                    if (!isInformed) {
                        var msg = $"[{Versioning.PACKAGE_NAME}] WARNING: Required '{PLAYER_IS_INFORMED}' was not found. Suggested tech support level: minimal; refer to {DOCUMENTATION}";
                        Debug.LogWarning(msg);
                        Log.Info(msg);
                    } else if (!informedMilestone.m_unlocked.value) {
                        Singleton<UnlockManager>.instance.CheckMilestone(informedMilestone, false, false);
                    }

                    /* FIXME: figure out why this cast fails, even though
                     * informedMilestone is of type DocumentationMilestone
                     * Is it because they MilestoneInfo and DocumentationMilestone are
                     * defined in different Assemblies?
                     */
#if false
                    for (var i = 0; i < PrefabCollection<BuildingInfo>.LoadedCount(); ++i) {
                        Log.Info($"Prefab {i} = {PrefabCollection<BuildingInfo>.GetLoaded((uint)i).name}");
                    }
#endif
#if true
                    var doc = informedMilestone as DocumentationMilestone;
                    Assert.IsTrue(informedMilestone != null, $"informedMilestone should not be null; type={informedMilestone.GetType().Name}");
                    Assert.IsTrue(doc != null, $"doc should not be null; type={informedMilestone.GetType().AssemblyQualifiedName} not {typeof(DocumentationMilestone).AssemblyQualifiedName}");
#endif
                } else {
                    var found = MilestoneCollection.FindMilestone(DOCUMENTATION_READ_MILESTONE);
                    Log.Error($"Documentation Milestone not found (found {found.GetType().AssemblyQualifiedName})");
                }
            }
            catch (Exception ex) {
                Log.Info($"Failed to Check Dependencies ({ex.Message})\n{ex.StackTrace}");
            }

            // ListSettings();
        }
        internal void OnPlayerInformed(string clue) {
#if DEBUG || EXPERIMENTAL
            Log.Info($"{GetType().Name}.OnPlayerInformed({clue}) {Assembly.GetExecutingAssembly().GetName().Version}");
#endif

            if (clue == PLAYER_IS_INFORMED) {
                // milestonesExtension.Unlock(DOCUMENTATION_READ_MILESTONE);

                if (!isInformed) {
                    isInformed = true;

                    if (Installed != null) {

                        var informedMilestone = MilestoneCollection.FindMilestone(DOCUMENTATION_READ_MILESTONE) as DocumentationMilestone;

                        if (informedMilestone != null) {
                            /* If this is the first time the docs were read when playing with the mod,
                             * show fireworks.
                             * If it's the first time the mod runs for this user, and the docs were read,
                             * skip the fireworks.
                             */
                            informedMilestone.m_openUnlockPanel = informedMilestone.m_unlocked.exists;
#if DEBUG
                            Log.Info($"{GetType().Name}.OnPlayerInformed({clue}) unlocked exists = {informedMilestone.m_unlocked.exists} value={informedMilestone.m_unlocked.value}");
#endif
                            /* Milestones can only be checked from the Simulation Thread because the UnlockManager
                             * calls the fireworks panel on the Main thread, and it asserts if the dispatch is called
                             * from the same thread
                             */
                            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                                $"{GetType().Name}.OnPlayerInformed() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

                            threading.QueueSimulationThread(() => Singleton<UnlockManager>.instance.CheckMilestone(informedMilestone, false, false));
                        }
                    } else {
                        loadingExtension.InstallIfPlayerIsInformed();
                    }
                }
            }
        }

        internal bool IsDependencyMet(string document) {
#if DEBUG
            Log.Info($"{GetType().Name}.IsDependencyMet({document}) => {isInformed} {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            if (!isInformed && Installed == null) {
                /* If called after TransferBroker starts, don't lookup the SavedBool.
                 * CheckDependency() will do that based on the milestone.
                 * If called before TransferBroker starts (eg, called by LoadingExtension to
                 * determine if incompatible mods are safe to ignore), lookup the bool
                 * directly, because the Milestone is not yet set up.
                 */
                /* FIXME: Also check for prefab in case live loading with incompatible mods */

                var wasInformedPreviously = new SavedBool(name: $"Unlocked[{DOCUMENTATION_READ_MILESTONE}]", fileName: Settings.userGameState, def: false, autoUpdate: true) ||
                    PrefabCollection<BuildingInfo>.LoadedExists(PLAYER_IS_INFORMED_PACKAGE + "." + PLAYER_IS_INFORMED + "_Data");
                return wasInformedPreviously;
            }
            return isInformed;
        }

#if DEBUG
        private void ListSettings() {
            SettingsFile settingsFile = GameSettings.FindSettingsFileByName(Settings.userGameState);
            if (settingsFile != null) {
                string[] array = settingsFile.ListKeys();
                foreach (string text in array) {
                    Log.Info($"Setting {text} =>");
                }
            }
        }
#endif

#if false
        [UsedImplicitly]
        private bool Check3rdPartyModLoaded(string namespaceStr, bool printAll = false) {
            bool thirdPartyModLoaded = false;

            FieldInfo loadingWrapperLoadingExtensionsField = typeof(LoadingWrapper).GetField(
                "m_LoadingExtensions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            List<ILoadingExtension> loadingExtensions = null;

            if (loadingWrapperLoadingExtensionsField != null) {
                loadingExtensions =
                    (List<ILoadingExtension>)loadingWrapperLoadingExtensionsField.GetValue(
                        Singleton<LoadingManager>.instance.m_LoadingWrapper);
            } else {
                Log.Warning("Could not get loading extensions field");
            }

            if (loadingExtensions != null) {
                foreach (ILoadingExtension extension in loadingExtensions) {
                    if (printAll) {
                        Log.Info($"Detected extension: {extension.GetType().Name} in " +
                                 $"namespace {extension.GetType().Namespace}");
                    }

                    if (extension.GetType().Namespace == null) {
                        continue;
                    }

                    string nsStr = extension.GetType().Namespace;

                    if (namespaceStr.Equals(nsStr)) {
                        Log.Info($"The mod '{namespaceStr}' has been detected.");
                        thirdPartyModLoaded = true;
                        break;
                    }
                }
            } else {
                Log._Debug("Could not get loading extensions");
            }

            return thirdPartyModLoaded;
        }
#endif
    }
}
