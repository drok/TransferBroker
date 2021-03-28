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
    public class MilestonesExtension : MilestonesExtensionBase {
        private TransferBrokerMod mod;

        /* a lock indicating whether the mod has been activated and not yet deactivated
         * It is entered when app requests activation and released when the app requests release.
         * It is static so only one instance of this assembly is in use at one time, ie,
         * a second copy will block, and freeze the application, rather than corrupt the first.
         */

        /* Is the mod active, as opposed to incompatible and refusing to activate */
        private bool active = false;

//        private DocumentationMilestone informed;

        public MilestonesExtension() {
#if DEBUG
            Log.Info($"{GetType().Name}..ctor() {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            mod = Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly()).userModInstance as TransferBrokerMod;

            Assert.IsTrue(mod != null,
                $"An instance of {mod.GetType().Name} should already exist when {GetType().Name} is instantiated");
        }
#if DEBUG
        ~MilestonesExtension() {
            Log.Info($"{GetType().Name}..~dtor() {Assembly.GetExecutingAssembly().GetName().Version}");
        }
#endif

        [UsedImplicitly]
        public override void OnCreated(IMilestones _milestones) {
            base.OnCreated(_milestones);

#if DEBUG
            Log.Info($"{GetType().Name}.OnCreated({_milestones.GetType().Name}) called. {Assembly.GetExecutingAssembly().GetName().Version} IsGameLoaded={mod.IsGameLoaded}");
#endif

            mod.milestones = _milestones;
            mod.milestonesExtension = this;
            active = true;
        }

        [UsedImplicitly]
        public override void OnReleased() {

#if DEBUG
            Log.Info($"{GetType().Name}.OnReleased() called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            base.OnReleased();

            if (active) {

                mod.milestones = null;
                mod.milestonesExtension = null;
                active = false;
            }

            // mod.milesones = null;

        }

#if false
        internal void OnReady() {
            Log.Info($"{GetType().Name}.OnReady() called - '{Thread.CurrentThread.Name}' thread. {Assembly.GetExecutingAssembly().GetName().Version}");

//            foreach (var i in milestonesManager.EnumerateMilestones()) {
//                var m = MilestoneCollection.FindMilestone(i);
//                var unlocked = false;
//                if (m != null) {
//                    unlocked = Singleton<UnlockManager>.instance.Unlocked(m);
//                }
//                Log.Info($"Milestone - {i} : {m?.name} : {m?.GetDescription()} - unlocked={unlocked}");
//            }

            var informed = MilestoneCollection.FindMilestone(SourcingMod.DOCUMENTATION_READ_MILESTONE) as DocumentationMilestone;

            if (informed == null) {
                informed = new DocumentationMilestone(");
                informed.m_documentName = SourcingMod.DOCUMENTATION_TITLE;
                informed.name = SourcingMod.DOCUMENTATION_READ_MILESTONE;

                MilestoneInfo[] toAdd = new MilestoneInfo[] {
                    informed,
                };
                Log.Info($"Adding my milestones milestones={milestonesManager.EnumerateMilestones().Length}");
                MilestoneCollection.InitializeMilestones(toAdd);
                Singleton<UnlockManager>.instance.RefreshScenarioMilestones();
                Log.Info($"After adding my milestones milestones={milestonesManager.EnumerateMilestones().Length}");

                Assert.IsNotNull(MilestoneCollection.FindMilestone(informed.name),
                    "Adding my milestone should not fail");
            }
            foreach (var i in milestonesManager.EnumerateMilestones()) {
                var m = MilestoneCollection.FindMilestone(i);
                var unlocked = false;
                if (m != null) {
                    unlocked = Singleton<UnlockManager>.instance.Unlocked(m);
                }
                Log.Info($"Milestone - {i} : {m?.name} : {m?.GetDescription()} - unlocked={unlocked}");
            }
        }
#endif

#if DEBUG
        internal void Unlock(string milestone) {

            Log.Info($"{GetType().Name}.Unlock({milestone}) called. {Assembly.GetExecutingAssembly().GetName().Version}");
            // milestonesManager.UnlockMilestone(milestone);
        }
#endif
    }
}
