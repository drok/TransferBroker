namespace TransferBroker {
    using ColossalFramework;
    using ColossalFramework.UI;
    using CSUtil.Commons;
    using HarmonyLib;
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using TransferBroker.Util;
    using UnityEngine.Assertions;

    public class Patcher {
        public static Patcher Instance { get; private set; }

        public static Patcher Create() => Instance = new Patcher();

        private const string HARMONY_ID = "org.ohmi.sourcing";

        private bool initialized_ = false;

        public bool Install() {

            Assert.IsFalse(initialized_,
                "Should not call Patcher.Install() more than once");

#if DEBUG
            Log.Info("Init detours");
#endif

            try {
#if DEBUG
                Harmony.DEBUG = true;
#endif
#if DEBUG || LABS || EXPERIMENTAL
                Log.Info("Performing Harmony attribute-driven patching");
#endif
                var harmony = new Harmony(HARMONY_ID);
                Shortcuts.Assert(harmony != null, "HarmonyInst!=null");
                harmony.PatchAll();
#if DEBUG || LABS || EXPERIMENTAL
                Log.Info("Harmony attribute-driven patching successful!");
#endif
                initialized_ = true;
            }
            catch (Exception e) {
                Log.Error("Could not apply Harmony patches because the following exception occured:\n " +
                    e.Message + "\n" + e.StackTrace +
                    "\n   -- End of inner exception stack trace -- ");
            }
            // catch {
            //     Log.Warning("Could not apply Harmony patches because a general exception.");
            // }

            return initialized_;
        }

        public void Uninstall() {
            if (!initialized_) {
                return;
            }

            var harmony = new Harmony(HARMONY_ID);
            Shortcuts.Assert(harmony != null, "HarmonyInst!=null");
            harmony.UnpatchAll(HARMONY_ID);

            initialized_ = false;
            Log.Info("Reverting detours finished.");
        }

        internal static void SetupUsefulMethods(TransferBrokerMod mod) {
            mod.PathVisualizer_AddInstance = AccessTools.Method(typeof(PathVisualizer), "AddInstance", new System.Type[] { typeof(InstanceID), });

        }

    }
}
