namespace TransferBroker.Manager.Impl {
    using System;
    using System.Reflection;
    using HarmonyLib;
    using ColossalFramework;
    using ColossalFramework.IO;
    using ColossalFramework.UI;
    using ColossalFramework.Threading;
    using ColossalFramework.Plugins;
    using ColossalFramework.Globalization;
    using global::TransferBroker.API.Manager;
    using CSUtil.Commons;
    using Priority_Queue;
    using UnityEngine;
    using UnityEngine.Assertions;
    using System.Threading;
    using System.Collections;
    using System.Diagnostics;
    using System.Collections.Generic;

    internal class TransferBroker
            : SimulationManagerBase<TransferBroker, BrokerProperties>, ISimulationManager, ICustomManager,
            // : AbstractCustomManager,
            ITransferBroker {

//        private SourcingMod mod;

        private InstanceID m_lastRemovedInstance; /* keep track to avoid repeated stalling of workers */

#if MAINTAIN_CONNECTIONS
        /* Store a list of cargo connections for Road network nodes that have such connections */
        public Dictionary<ushort, List<ushort>> cargoConnections;
#endif

        public const TransferManager.TransferReason LAST_VALID_REASON = TransferManager.TransferReason.IntercityBus;
        public const string ACTIVATION_BUILDING = "Transport Tower";
        public const float MINIMUM_ACTIVATION_PARAM = 1f;
        public const float MAXIMUM_ACTIVATION_PARAM = 4f;

        /* Constraints for detecting a building can be a traffic camera building */
        public const ItemClass.Service ACTIVATION_TRAFFIC_SERVICE = ItemClass.Service.PoliceDepartment;
        public const ItemClass.Level ACTIVATION_TRAFFIC_LEVEL = ItemClass.Level.Level1;

        public const string ACTIVATION_TRAFFIC_NAME = "Traffic Operations Center";

        public const string CHEAT_PREFIX = "Filthy Cheat";

        public const int EXPENSE_FACTOR = 10;

        /* Input to matchmaker (jobs) */
        public struct MatchWork {
            public TransferManager.TransferReason material;
            public ushort[] incomingEndAt;
            public ushort[] outgoingEndAt;
            public Configuration options;
        }
        public struct Configuration {
            //            public bool UseLegacyAlgo;

            /* Inefficiency - request an inefficient match.
             *
             * 0        = shortest path
             * 1..254   = 1/256 * 4 to 254/256 * 4 longer path.
             * 255      = use Legacy Algorithm
             */
            // public byte Inefficiency;
            public MatchMaker.Algo algo;
        }

        /* Output from matchmaker (material transfers) */
        public struct TransferWork {
            public TransferManager.TransferReason material;
            public TransferManager.TransferOffer offerOut;
            public TransferManager.TransferOffer offerIn;
            public int amount;
        }

        public class OneUshort {
            public ushort value;
        }

        public struct Status {
            public float[] LongestMatch;
        }

        public struct Coordination {
            public enum WorkStatus : byte {
                Idle = 0,
                Pending,
                Busy,
            }

            /* input */
            public Queue<MatchWork> workQ;
            public TransferManager.TransferOffer[] m_outgoingOffers;
            public TransferManager.TransferOffer[] m_incomingOffers;
            public ushort[] m_outgoingReadAt;
            public ushort[] m_incomingReadAt;

            /* Flag whether the matchmaker is falling behind.
             * When it does, no new offers will be accepted, except inorganic ones
             */
            public bool[] offerQueueFull;
#if DEBUG || LABS || EXPERIMENTAL
            public uint[] missedOFfers;
            public uint missedOfferReports;
            public const uint MAX_MISSED_REPORTS = 200;
#endif

            /* Cached variables */
            public BuildingManager buildings;
            public VehicleManager vehicles;
            public CitizenManager citizens;
            public NetManager nets;
            public SimulationManager sim;

            /* State, keep track of which materials are currently being matched, in order to detect idle */
            /* FIXME, m_workInProgress will see cache misses because diff threads write to it, same as m_*ReadAt */
            /* FIXME: Replace workInProgress with semaphore. more granularity is not needed */
            public WorkStatus[] m_workInProgress;
            public OneUshort numBusyMaterials;
            public object goingIdle_lock;
            public Stopwatch stopwatch;
            public TimeSpan runTime;

            public class MatchmakingConfig {
                public float m_rndFrequency; /* algo param0 - percentage of routes affected by RNG */
                public float m_rndAmplitude; /* algo param1 - artifical cost factor modifier */
                public bool m_hasCameras; /* True if traffic congestion should be monitored */

                /* Output match cache for coloring purposes */
                public TransferManager.TransferReason cacheReason;

                public override string ToString() {
                    return $"rndFrequency={m_rndFrequency} rndAmplitude={m_rndAmplitude} cameras={m_hasCameras}";
                }
            }

            /* FIXME: Writes happen on the master thread, and reads on Matchmaker threads.
             * config needs synchronization
             */
            public MatchmakingConfig config;

            /* output */
            // Also ref Queue<TransferWork> transferQ which is passed as parameter
            public TransferBroker owner;
            public object transferQ_lock;
            public Status status;

            /* Output match cache for coloring purposes */
            public Dictionary<InstanceID, TransferWork> resultsCache;
        }

        private struct Backup {
            public TransferManager transferManager;

            public string[] m_uneducatedFemaleJobs;
            public string[] m_educatedFemaleJobs;
            public string[] m_wellEducatedFemaleJobs;
            public string[] m_highlyEducatedFemaleJobs;
            public string[] m_uneducatedMaleJobs;
            public string[] m_educatedMaleJobs;
            public string[] m_wellEducatedMaleJobs;
            public string[] m_highlyEducatedMaleJobs;

            public int m_maintenanceCost;

            public Locale LocaleOverride;
        }

        private Backup m_backup;

        /* Options */
        public ushort m_activator; /* buildingID of the activation building */
        public ushort m_activator_traffic; /* building ID controlling cameras/congestion awareness */
        public float m_activator_param; /* Parameter given on activator building */

        public bool m_activator_is_cheat; /* true if activator is not ACTIVATION_BUILDING */
        private bool m_activated; /* Is the Activation Building Present? */
        private bool m_enabled; /* Is The Activation Building Absent or Enabled ? */

        Coordination c;
        public Queue<TransferWork> transferQ;
        private Dictionary<InstanceID, TransferManager.TransferOffer>[] m_queuedIncomingOffers;
        private Dictionary<InstanceID, TransferManager.TransferOffer>[] m_queuedOutgoingOffers;
        private HashSet<InstanceID>[] removedOffers;

        private MatchMaker[] matchmakers;
        public DocumentationMilestone m_documentation;
        private MilestoneCollection m_milestones;

        public ushort[] m_outgoingWriteAt = new ushort[((int)LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT];
        public ushort[] m_incomingWriteAt = new ushort[((int)LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT];

        object startupLock;

        // internal Dictionary<InstanceID, TransferWork> coloringData => c.resultsCache;

#if DEBUG
        public TransferBroker() {
            Log._Debug($"{GetType().Name}..ctor()");
        // SaneTransferManager.Instance = new SaneTransferManager();

        // mod = Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly()).userModInstance as SourcingMod;

#if false
            OriginalStartTransfer = typeof(TransferManager).GetMethod("StartTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic,
                Type.DefaultBinder,
                new Type[] { typeof(TransferManager.TransferReason),
                             typeof(TransferManager.TransferOffer),
                             typeof(TransferManager.TransferOffer),
                             typeof(int), },
                null) ?? throw new Exception("TransferManager.StartTransfer() not found");
#endif
    }

        ~TransferBroker() {
            Log._Debug($"{GetType().Name}..~dtor()");
        }
#endif
        public void AddIncomingOffer(
                        TransferManager.TransferReason material,
                        TransferManager.TransferOffer offer) {

            m_lastRemovedInstance = default(InstanceID);

            if (!m_enabled) return;

            var organic = IsOrganic(material, offer);
            try {

                lock (c.workQ) {
                    if (!c.offerQueueFull[(int)material] || !organic) {
                        if (c.m_workInProgress[(int)material] != Coordination.WorkStatus.Busy || !organic) {
                            for (int priority = offer.Priority; priority >= 0; priority--) {
                                int tableNum = ((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                                ushort nextWrite = (ushort)((m_incomingWriteAt[tableNum] + 1) % TransferManager.TRANSFER_OFFER_COUNT);
                                if (nextWrite != c.m_incomingReadAt[tableNum]) {
                                    c.m_incomingOffers[(tableNum * TransferManager.TRANSFER_OFFER_COUNT) + m_incomingWriteAt[tableNum]] = offer;
                                    m_incomingWriteAt[tableNum] = nextWrite;
                                    break;
                                }
                            }
                            return;
                        }
#if DEBUG || LABS || EXPERIMENTAL
                    } else {
                        ++c.missedOFfers[(int)material];
#endif
                    }
                }
                /* When receiving offers for a material which is currently being matched,
                    * assuming the offer is organic, ie, backed by a buffer as opposed to artificial, like an outside connection,
                    * the offer is queued until the material matching is finished.
                    * Then, the enqueued transfered amounts are subtracted from this new queued offer,
                    * so that offers for the same buffer space are not duplicated
                    */
                EnqueueOffer(material, offer, m_queuedIncomingOffers[((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + offer.Priority]);
            }
            catch (Exception ex) {
                Log.Info($"Failed to add Offer ({ex.Message})\n{ex.StackTrace}");
            }
        }

        public void AddOutgoingOffer(
                        TransferManager.TransferReason material,
                        TransferManager.TransferOffer offer) {

            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
                $"{GetType().Name}.AddOutgoingOffer() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

            m_lastRemovedInstance = default(InstanceID);

            if (!m_enabled) return;

            var organic = IsOrganic(material, offer);
            if (!c.offerQueueFull[(int)material] || !organic) {
                try {

                    lock (c.workQ) {
                        if (c.m_workInProgress[(int)material] != Coordination.WorkStatus.Busy || !organic) {
                            for (int priority = offer.Priority; priority >= 0; priority--) {
                                int tableNum = ((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                                ushort nextWrite = (ushort)((m_outgoingWriteAt[tableNum] + 1) % TransferManager.TRANSFER_OFFER_COUNT);
                                if (nextWrite != c.m_outgoingReadAt[tableNum]) {
                                    c.m_outgoingOffers[(tableNum * TransferManager.TRANSFER_OFFER_COUNT) + m_outgoingWriteAt[tableNum]] = offer;
                                    m_outgoingWriteAt[tableNum] = nextWrite;
                                    break;
                                }
                            }
                            return;
                        }
                    }
                    EnqueueOffer(material, offer, m_queuedOutgoingOffers[((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + offer.Priority]);
                }
                catch (Exception ex) {
                    Log.Info($"Failed to add Offer ({ex.Message})\n{ex.StackTrace}");
                }
            }

        }

        private bool IsOrganic(TransferManager.TransferReason material, TransferManager.TransferOffer offer) {
            if (offer.m_object.Type == InstanceType.Building) {
                return (c.buildings.m_buildings.m_buffer[offer.Building].m_flags & Building.Flags.IncomingOutgoing) == 0;
            }
            return true;
        }

        // private bool IsIdle(TransferManager.TransferReason material) {
        //     lock (c.workQ) {
        //         return c.m_workInProgress[(int)material] == Coordination.WorkStatus.Idle;
        //     }
        // }

        private void EnqueueOffer(TransferManager.TransferReason material, TransferManager.TransferOffer offer, Dictionary<InstanceID, TransferManager.TransferOffer> queue) {
            if (queue.ContainsKey(offer.m_object)) {
                var oldOffer = queue[offer.m_object];
                offer.Amount += oldOffer.Amount;
                queue[offer.m_object] = offer;
            } else {
                queue.Add(offer.m_object, offer);
            }
        }

        public void RemoveOffers(
                        TransferManager.TransferReason material,
                        TransferManager.TransferOffer offer) {

            /* removals come in clusters, when a building is deactivated or
             * a vehicle needs to despawn.
             * Lock up the sim thread only once per deactivated building/vehicle,
             * but remove all offers from that building/vehicle.
             */

            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
                $"{GetType().Name}.RemoveOffers() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

            if (!m_enabled) return;

            if (m_lastRemovedInstance != offer.m_object) {
                FilterTransferQueue(offer.m_object);
                m_lastRemovedInstance = offer.m_object;
                //                lock(m_incomingWriteAt) {

                lock (c.workQ) {
                    if (c.m_workInProgress[(int)material] == Coordination.WorkStatus.Busy) {
                        removedOffers[(int)material].Add(offer.m_object);
                    } else {
                        for (int mat = 0; mat < TransferManager.TRANSFER_REASON_COUNT; ++mat) {
                            for (int priority = 0; priority < TransferManager.TRANSFER_PRIORITY_COUNT; ++priority) {
                                int tableOffset = (int)((mat * TransferManager.TRANSFER_PRIORITY_COUNT) + priority) * TransferManager.TRANSFER_OFFER_COUNT;

                                for (ushort k = c.m_incomingReadAt[((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority];
                                    k != m_incomingWriteAt[((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority];
                                    k = (ushort)((k + 1) % TransferManager.TRANSFER_OFFER_COUNT)) {
                                    if (c.m_incomingOffers[tableOffset + k].m_object == offer.m_object) {
                                        c.m_incomingOffers[tableOffset + k] = default(TransferManager.TransferOffer);
                                    }
                                }

                                for (ushort k = c.m_outgoingReadAt[((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority];
                                    k != m_outgoingWriteAt[((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority];
                                    k = (ushort)((k + 1) % TransferManager.TRANSFER_OFFER_COUNT)) {
                                    if (c.m_outgoingOffers[tableOffset + k].m_object == offer.m_object) {
                                        c.m_outgoingOffers[tableOffset + k] = default(TransferManager.TransferOffer);
                                    }
                                }
                            }
                        }
                    }
                }
//                }
            }
        }
#if false
        public bool IsSupported(TransferManager.TransferReason material) {

            // From WarehouseAI.GetTransferVehicleService()
            switch (material) {
                case TransferManager.TransferReason.Ore:
                case TransferManager.TransferReason.Coal:
                case TransferManager.TransferReason.Glass:
                case TransferManager.TransferReason.Metals:
                case TransferManager.TransferReason.Logs:
                case TransferManager.TransferReason.Lumber:
                case TransferManager.TransferReason.Paper:
                case TransferManager.TransferReason.PlanedTimber:
                case TransferManager.TransferReason.Oil:
                case TransferManager.TransferReason.Petrol:
                case TransferManager.TransferReason.Petroleum:
                case TransferManager.TransferReason.Plastics:
                case TransferManager.TransferReason.Grain:
                case TransferManager.TransferReason.Food:
                case TransferManager.TransferReason.Flours:
                case TransferManager.TransferReason.AnimalProducts:

                case TransferManager.TransferReason.Goods:
                case TransferManager.TransferReason.LuxuryProducts:
                    return true;
            }
            return false;
        }
#endif

        private void IncludeQueuedOffers(TransferManager.TransferReason material) {

            for (int p = 0; p < TransferManager.TRANSFER_PRIORITY_COUNT; ++p) {
                var dict = m_queuedIncomingOffers[(int)material * TransferManager.TRANSFER_PRIORITY_COUNT + p];
                foreach (var i in dict) {
                    AddIncomingOffer(material, i.Value);
                }
                dict.Clear();

                dict = m_queuedOutgoingOffers[(int)material * TransferManager.TRANSFER_PRIORITY_COUNT + p];

                foreach (var i in dict) {
                    AddOutgoingOffer(material, i.Value);
                }
                dict.Clear();
            }
        }

        /* FIXME: If the Transport Tower is on fire, it will be inactive,
         * and unable to match fire figthers. It will burn to the ground
         * and there will be no matching until buldozed.
         * Fire services must still be matched when this happens
         */
        private void GetConfiguration() {

            if (m_activator != 0) {
#if DEBUG || LABS || EXPERIMENTAL
                bool wasEnabled = m_enabled;
                bool hadCameras = c.config.m_hasCameras;
                bool wasActivated = m_activated;
#endif

                /* If an activator building is present, activate the matchmaking implementation (activated=false, enabled=true),
                 * if not present, activate only the legacy implementation (activated=false, enabled=true)
                 *
                 * if the activator is present but deactivated, disable all matchmaking. (activated=false, enabled=false).
                 * however, if it's deactivated because it's burning, allow the emergency bunker to operate (activated=false, enabled=true).
                 * The scheduler will check if the activator is on fire in order to limit services to Fire only, using the _legacy_ algo.
                 *
                 * If the activator is a cheat building, then there is no emergency bunker, so _all_ matchmaking stops. Let's see them
                 * cheat this challenge, LOL.
                 */
                m_activated = (c.buildings.m_buildings.m_buffer[m_activator].m_flags & Building.Flags.Active) != 0;
                m_enabled = m_activated || (!m_activator_is_cheat && c.buildings.m_buildings.m_buffer[m_activator].m_fireIntensity != 0);

                c.config.m_hasCameras = (c.buildings.m_buildings.m_buffer[m_activator_traffic].m_flags & Building.Flags.Active) != 0;

#if DEBUG
                if (m_enabled && !wasEnabled) {
                    for (ushort p = 0; p < c.buildings.m_buildings.m_buffer[m_activator].Info.m_props.Length; ++p) {
                        if (c.buildings.m_buildings.m_buffer[m_activator].Info.m_props[p].m_prop) {
                            Log.InfoFormat("prop {0} = {1}",
                                p,
                                c.buildings.m_buildings.m_buffer[m_activator].Info.m_props[p].m_prop?.GetGeneratedTitle());
                        } else break;
                    }
                }
#endif
#if DEBUG || LABS || EXPERIMENTAL
                if (m_enabled != wasEnabled || c.config.m_hasCameras != hadCameras || m_activated != wasActivated) {
                    Log.InfoFormat("{0}.GetConfiguration() - #{1}({2})/#{3}({4}), flags={5} active={6} enabled={7} {8}",
                        GetType().Name,
                        m_activator,
                        m_activator != 0 ? c.buildings.GetBuildingName(m_activator, InstanceID.Empty) : "n/a",
                        m_activator_traffic,
                        m_activator_traffic != 0 ? c.buildings.GetBuildingName(m_activator_traffic, InstanceID.Empty) : "n/a",
                        m_activator != 0 ? c.buildings.m_buildings.m_buffer[m_activator].m_flags : Building.Flags.None,
                        m_activated,
                        m_enabled,
                        c.config);
                }
#endif
            }
#if DEBUG
            else {
            Log.InfoFormat("{0}.GetConfiguration() - #{1}({2})/#{3}({4}), flags={5} active={6} enabled={7} {8}",
                    GetType().Name,
                    m_activator,
                    m_activator != 0 ? c.buildings.GetBuildingName(m_activator, InstanceID.Empty) : "n/a",
                    m_activator_traffic,
                    m_activator_traffic != 0 ? c.buildings.GetBuildingName(m_activator_traffic, InstanceID.Empty) : "n/a",
                    m_activator != 0 ? c.buildings.m_buildings.m_buffer[m_activator].m_flags : Building.Flags.None,
                    m_activated,
                    m_enabled,
                    c.config);
            }
#endif
        }

        protected override void SimulationStepImpl(int subStep) {

            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
                $"{GetType().Name}.SimulationStepImpl() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

            /* WARNING: This function assumes there are at most 128 materials,
             * and it skips the mapping done in vanilla GetFrameReason
             * It matches materials up to TransferManager.TransferReason.IntercityBus
             */
            if (subStep != 0) {
                int frameIndex = (int)(c.sim.m_currentFrameIndex & 0xFF);
//                Log.InfoFormat("TB: Simulate frameIndex {0} substep {1}", frameIndex, subStep);
                if (frameIndex == 0) {
                    bool wasEnabled = m_enabled;

                    GetConfiguration();

                    Assert.IsNotNull(c.m_incomingOffers, "Offers table should not be null");

                    lock (c.workQ) {

                        if (m_enabled) {
                            MatchWork work;
                            /* FIXME: This option can be moved into c.config */
                            work.options = new Configuration { algo = m_activated ? MatchMaker.Algo.ANN : MatchMaker.Algo.Legacy };

                            c.stopwatch.Reset();

#if DEBUG
                            Log._DebugFormat("TB: Enqueue matchmaking work for {0} queue={1}", Assembly.GetExecutingAssembly().GetName().Version, c.workQ.Count);
#endif

                            var isActivatorBurning = m_activated && c.buildings.m_buildings.m_buffer[m_activator].m_fireIntensity != 0;
                            TransferManager.TransferReason minReason = isActivatorBurning ? TransferManager.TransferReason.Fire : (TransferManager.TransferReason)0;
                            TransferManager.TransferReason maxReason = isActivatorBurning ? TransferManager.TransferReason.Fire : LAST_VALID_REASON;

                            /* FIXME: Enqueue in order of longest running to shortest task */
                            for (work.material = minReason; work.material <= maxReason; ++work.material) {
                                if (c.m_workInProgress[(int)work.material] == Coordination.WorkStatus.Idle) {

                                    work.incomingEndAt = new ushort[TransferManager.TRANSFER_PRIORITY_COUNT];
                                    work.outgoingEndAt = new ushort[TransferManager.TRANSFER_PRIORITY_COUNT];

                                    lock (c.transferQ_lock) {
                                        removedOffers[(int)work.material].Clear();
                                    }

                                    c.m_workInProgress[(int)work.material] = Coordination.WorkStatus.Pending;

                                    IncludeQueuedOffers(work.material);

                                    for (int p = 0; p < TransferManager.TRANSFER_PRIORITY_COUNT; ++p) {
                                        work.incomingEndAt[p] = m_incomingWriteAt[((int)work.material) * TransferManager.TRANSFER_PRIORITY_COUNT + p];
                                        work.outgoingEndAt[p] = m_outgoingWriteAt[((int)work.material) * TransferManager.TRANSFER_PRIORITY_COUNT + p];
                                    }
                                    c.workQ.Enqueue(work);

                                } else {
                                    c.offerQueueFull[(int)work.material] = true;
#if DEBUG || LABS || EXPERIMENTAL
                                    c.missedOFfers[(int)work.material] = 0;
                                    if (c.missedOfferReports < TransferBroker.Coordination.MAX_MISSED_REPORTS && c.offerQueueFull[(int)work.material]) {
                                        string msg = $"WARNING: Stoped accepting {work.material} offers, matchmaker is falling behind.";
                                        Log.InfoFormat(msg);

                                        UnityEngine.Debug.Log($"[{TransferBrokerMod.Installed.Name}] {msg}");
                                        if (c.missedOfferReports != 0) {
                                            UnityEngine.Debug.Log($"[{TransferBrokerMod.Installed.Name}] Matchmakers ({matchmakers.Length}) falling behind is not a bug, but it is unexpected.\n" +
                                                "It may be avoidable. Please report it to the mod author.");
                                        }
                                        ++c.missedOfferReports;
                                    }
#endif
                                }
                            }

                            Monitor.PulseAll(c.workQ);
                            c.stopwatch.Start();
                        } else if (wasEnabled) {

                            /* TODO: Also clear active queues, but this must use an interlock with the MatchMakers
                             * And should not wait for idle (don't stall simulation when matching is disabled)
                             */
                            WaitForIdle();
                            m_incomingWriteAt.CopyTo(c.m_incomingReadAt, 0);
                            m_outgoingWriteAt.CopyTo(c.m_outgoingReadAt, 0);

                            for (int i = 0; i < ((int)(LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT); ++i) {
                                m_queuedOutgoingOffers[i].Clear();
                                m_queuedIncomingOffers[i].Clear();
                            }
                        }
                    }
                }
            }
        }

        private void WaitForIdle() {
            /* FIXME: Implement pausing matchmaker while the netgrid is updated */
            lock (c.goingIdle_lock) {
                while (c.numBusyMaterials.value != 0) Monitor.Wait(c.goingIdle_lock);
            }
        }

        public void Pause() {
            WaitForIdle();
        }

        public void Continue() {
            /* FIXME: Implement pausing matchmaker while the netgrid is updated */
            /* Also when NetNode.CreateSegment() / DeleteSegment() are called */
            /* or NetTool.CreateNode() */
            /* or NetManager.Data.Deserialize() */
        }

        private void Quit() {
            MatchWork work = new MatchWork { material = TransferManager.TransferReason.None };

            lock (c.workQ) {
                for (int i = 0; i < matchmakers.Length; ++i) {
                    c.workQ.Enqueue(work);
                }
                Monitor.PulseAll(c.workQ);
            }

            for (int i = 0; i < matchmakers.Length; ++i) {
#if DEBUG
                Log._DebugFormat("TB: Joining matchmaker[{0}]", i);
#endif
                matchmakers[i].Join();
#if DEBUG
                Log._DebugFormat("TB: Joined matchmaker[{0}]", i);
#endif
            }
#if DEBUG
            Log._DebugFormat("TB: Joined {0} matchmakers", matchmakers.Length);
#endif
        }

        private void FilterTransferQueue(InstanceID instance) {
            lock (c.transferQ_lock) {
                /* Wait for all workers to finish, then delete matching transfers */
                /* Also delete queued offers */
                Queue<TransferWork> newQ = new Queue<TransferWork>();
                foreach (var transfer in transferQ) {
                    if (transfer.offerOut.m_object == instance) {
                        TransferManager.TransferOffer newoffer = transfer.offerIn;
                        newoffer.Amount = transfer.amount;
                        AddIncomingOffer(transfer.material, newoffer);
                    } else if (transfer.offerIn.m_object == instance) {
                        TransferManager.TransferOffer newoffer = transfer.offerOut;
                        newoffer.Amount = transfer.amount;
                        AddOutgoingOffer(transfer.material, newoffer);
                    } else {
                        newQ.Enqueue(transfer);
                    }
                }
                transferQ = newQ;
            }
        }

        public void StartTansfers() {
            lock(c.transferQ_lock) {
                TransferWork transfer;
                while (transferQ.Count != 0) {
                    transfer = transferQ.Dequeue();

                    if (removedOffers[(int)transfer.material].Contains(transfer.offerIn.m_object)) {
                        continue;
                    }

#if ONE_TRUCK_PER_STARTTRANSFER_BUGFIX
                    StartTransfer(transfer.material, transfer.offerOut, transfer.offerIn, transfer.amount);
#else
                    /* As of Game version v1.13.1-f1, only one cargo truck is sent regardless of amount argument
                     * This workaround allows multiple trucks sent.
                     */
                    for (int i = transfer.amount; i > 0; --i) {
                        StartTransfer(transfer.material, transfer.offerOut, transfer.offerIn, 1);
                    }
#endif
                    /* For offers that arrived while the MatchMaker was working,
                     * subtract amounts already matched;
                     * remove offers that were completely matched.
                     */
                    var q = m_queuedIncomingOffers[(int)transfer.material * TransferManager.TRANSFER_PRIORITY_COUNT + transfer.offerIn.Priority];
                    if (q.ContainsKey(transfer.offerIn.m_object)) {
                        var queuedOffer = q[transfer.offerIn.m_object];
                        queuedOffer.Amount = queuedOffer.Amount - transfer.offerIn.Amount;
                        if (queuedOffer.Amount == 0) {
                            q.Remove(transfer.offerIn.m_object);
                        }
                    }

                    q = m_queuedOutgoingOffers[(int)transfer.material * TransferManager.TRANSFER_PRIORITY_COUNT + transfer.offerOut.Priority];
                    if (q.ContainsKey(transfer.offerOut.m_object)) {
                        var queuedOffer = q[transfer.offerOut.m_object];
                        queuedOffer.Amount = queuedOffer.Amount - transfer.offerOut.Amount;
                        if (queuedOffer.Amount == 0) {
                            q.Remove(transfer.offerOut.m_object);
                        }
                    }
                }
            }
        }

        public bool GetColoringData(InstanceID id, out TransferWork work) {
            lock (c.transferQ_lock) {
                return c.resultsCache.TryGetValue(id, out work);
            }
            // work = default(TransferWork);
            // return false;
        }

        internal void SetInfoViewTarget(InstanceID id) {
            TransferManager.TransferReason newReason = TransferManager.TransferReason.None;

            switch (id.Type) {
                case InstanceType.Building:
                    var data = c.buildings.m_buildings.m_buffer[id.Building];
                    var ai = data.Info.GetAI();

                    if (ai is LandfillSiteAI) {
                        newReason = TransferManager.TransferReason.Garbage;
                    } else if (ai is WarehouseAI) {
                        newReason = (ai as WarehouseAI).GetActualTransferReason(id.Building, ref data);
                    } else if (ai is PoliceStationAI) {
                        newReason = TransferManager.TransferReason.Crime;
                    } else if (ai is HelicopterDepotAI) {
                        newReason = (ai as BuildingAI).m_info.m_class.m_service switch {
                            ItemClass.Service.HealthCare => TransferManager.TransferReason.Sick2,
                            ItemClass.Service.PoliceDepartment => TransferManager.TransferReason.Crime,
                            /* NOTE: FireDepartment has a 2nd reason, Fire2, which can be show only,
                             * when a Fire2 helicopter is selected. */
                            ItemClass.Service.FireDepartment => TransferManager.TransferReason.ForestFire,
                            _ => TransferManager.TransferReason.None,
                        };
                    } else if (ai is HospitalAI) {
                        newReason = TransferManager.TransferReason.Sick;
                    } else if (ai is FireStationAI) {
                        newReason = TransferManager.TransferReason.Fire;
                    } else if (ai is CommercialBuildingAI) {
                        newReason = TransferManager.TransferReason.Goods;
                    } else if (ai is IndustrialBuildingAI) {
                        newReason = (ai as IndustrialBuildingAI).m_info.m_class.m_subService switch {
                            ItemClass.SubService.IndustrialForestry => TransferManager.TransferReason.Lumber,
                            ItemClass.SubService.IndustrialFarming => TransferManager.TransferReason.Food,
                            ItemClass.SubService.IndustrialOil => TransferManager.TransferReason.Petrol,
                            ItemClass.SubService.IndustrialOre => TransferManager.TransferReason.Coal,
                            _ => TransferManager.TransferReason.Goods,
                        };
                    } else if (ai is ExtractingFacilityAI) {
                        newReason = (ai as ExtractingFacilityAI).m_outputResource;
                    } else if (ai as ProcessingFacilityAI) {
                        newReason = (ai as ProcessingFacilityAI).m_outputResource;
                    }
                    break;
                case InstanceType.Vehicle:
                    newReason = (TransferManager.TransferReason)c.vehicles.m_vehicles.m_buffer[id.Vehicle].m_transferType;
                    break;
                case InstanceType.Citizen:
                    // var instance = c.citizens.m_citizens.m_buffer[id.Citizen].m_instance;
                    // var vehicle = c.citizens.m_citizens.m_buffer[id.Citizen].m_vehicle;
                    // if (vehicle != 0) {
                    //     newReason = (TransferManager.TransferReason)c.vehicles.m_vehicles.m_buffer[vehicle].m_transferType;
                    // } else {
                    //     newReason = c.citizens.m_instances.m_buffer[instance].m
                    // }
                    newReason = TransferManager.TransferReason.None;
                    break;
            }

            if (newReason != c.config.cacheReason) {
#if DEBUG || EXPERIMENTAL
                Log.Info($"Setting new material for Offer Book display: {newReason}");
#endif
                lock (c.transferQ_lock) {
                    lock (c.workQ) {
                        c.config.cacheReason = newReason;
                        c.resultsCache.Clear();
                    }
                }
#if DEBUG || EXPERIMENTAL
                Log.Info($"Will display Offer Book: {newReason}");
#endif
                if (Singleton<InfoManager>.instance.CurrentMode == InfoManager.InfoMode.TrafficRoutes) {
                    c.buildings.UpdateBuildingColors();
                }

            }
        }

        public void OnMatchmakerFinished(TransferManager.TransferReason material) {
#if DEBUG
            Log.Info($"Matchmaker finished {material} watching {c.config.cacheReason} Size = {c.resultsCache.Count} traffic = {Singleton<InfoManager>.instance.CurrentMode == InfoManager.InfoMode.TrafficRoutes}");
#endif
            if (c.config.cacheReason == material && Singleton<InfoManager>.instance.CurrentMode == InfoManager.InfoMode.TrafficRoutes) {
                /* FIXME : Only the affected buildings */
                c.buildings.UpdateBuildingColors();
            }
        }
        /* UpdateMyData() hooks the data from the TransferManager (not-threadsafe), and
         * converts it to TransferBroker (thread-safe).
         *
         * It is called when TransferManager.UpdateData is called (before Simulation runs, so thread safe)
         * also when OnModLoaded() is called on the Simulation Thread
         */
        private void UpdateMyData() {
#if DEBUG
            Log._Debug($"{GetType().Name}.UpdateMyData()");
#endif

            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
                $"{GetType().Name}.UpdateMyData() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

            var inst = Traverse.Create(Singleton<TransferManager>.instance);
            ushort[] m_outgoingCount = inst.Field("m_outgoingCount").GetValue<ushort[]>();
            ushort[] m_incomingCount = inst.Field("m_incomingCount").GetValue<ushort[]>();

            lock (c.goingIdle_lock) {
#if DEBUG
                Log._Debug($"{GetType().Name}.UpdateMyData() goingIdle_lock aquired c.numBusyMaterials.value={c.numBusyMaterials.value}");
#endif
                while (c.numBusyMaterials.value != 0) {
                    Monitor.Wait(c.goingIdle_lock);
                }
                for (int reason = 0; reason <= (int)LAST_VALID_REASON; ++reason) {

                    for (int priority = 0; priority < TransferManager.TRANSFER_PRIORITY_COUNT; ++priority) {

                        int tableNum = reason * TransferManager.TRANSFER_PRIORITY_COUNT + priority;

                        /* I maintain a ring buffer backed by a 256-element array and I want to use
                            * 8-bit pointers (byte), so that incrementing the read/write pointers does not need
                            * arithmetic to detect fullness.
                            * Instead, one of the 256-elements is a guard, not used to store data. This way
                            * to iterate the ring buffer I can use the simple (write pointer == read pointer)
                            * termination condition.
                            * For this to work, I am discarding the last offer from tables that contain 256 offers.
                            * It's a choice, to gain a tiny bit of performance, and a lot cleaner ring-buffer code
                            */
                        m_incomingWriteAt[tableNum] = (ushort)Mathf.Min(m_incomingCount[tableNum], 255);
                        m_outgoingWriteAt[tableNum] = (ushort)Mathf.Min(m_outgoingCount[tableNum], 255);
                        c.m_incomingReadAt[tableNum] = 0;
                        c.m_outgoingReadAt[tableNum] = 0;
                    }
                }
            }

        }

        /* UpdateTransferManagerData() converts the thread-safe data stored by TransferBroker back
         * into the vanilla TransferManager structures, so it can be serialized, and also
         * when the mod is unsubscribed live, so execution can continue with the TransferManager.
         */
        private void UpdateTransferManagerData() {
            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
                $"{GetType().Name}.UpdateTransferManagerData() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

#if DEBUG
            Log._Debug($"{GetType().Name}.UpdateTransferManagerData()");
#endif
            var inst = Traverse.Create(Singleton<TransferManager>.instance);
            ushort[] m_outgoingCount = inst.Field("m_outgoingCount").GetValue<ushort[]>();
            ushort[] m_incomingCount = inst.Field("m_incomingCount").GetValue<ushort[]>();

            int[] m_outgoingAmount = inst.Field("m_outgoingAmount").GetValue<int[]>();
            int[] m_incomingAmount = inst.Field("m_incomingAmount").GetValue<int[]>();

            // Log._DebugFormat("m_outgoingCount={0}", m_outgoingCount);
            // Log._DebugFormat("m_outgoingCount.Length={0}", m_outgoingCount.Length);
            // Log._DebugFormat("m_outgoingAmount{0}", m_outgoingAmount);
            // Log._DebugFormat("m_outgoingAmount.Length={0}", m_outgoingAmount.Length);
            lock (c.goingIdle_lock) {
                while (c.numBusyMaterials.value != 0) {
                    Monitor.Wait(c.goingIdle_lock);
                }
                TransferManager.TransferOffer[] temp = new TransferManager.TransferOffer[TransferManager.TRANSFER_OFFER_COUNT - 1];
                int j;
                int amount;
                for (int reason = 0; reason <= (int)LAST_VALID_REASON; ++reason) {
                    for (int priority = 0; priority < TransferManager.TRANSFER_PRIORITY_COUNT; ++priority) {
                        int tableNum = reason * TransferManager.TRANSFER_PRIORITY_COUNT + priority;
                        int tableOffset = tableNum * TransferManager.TRANSFER_OFFER_COUNT;

                        m_incomingCount[tableNum] = (ushort)((ushort)(m_incomingWriteAt[tableNum] - c.m_incomingReadAt[tableNum]) % TransferManager.TRANSFER_OFFER_COUNT);
                        m_outgoingCount[tableNum] = (ushort)((ushort)(m_outgoingWriteAt[tableNum] - c.m_outgoingReadAt[tableNum]) % TransferManager.TRANSFER_OFFER_COUNT);
//                        Log._DebugFormat("{0}: m_outgoingCount = {1}    m_incomingCount = {2}", tableNum, m_outgoingCount[tableNum], m_incomingCount[tableNum]);

                        j = c.m_incomingReadAt[tableNum];
//                        Log._DebugFormat("{0}: m_outgoingReadAt = {1}    m_incomingReadAt = {2}", tableNum, m_outgoingReadAt[tableNum], m_incomingReadAt[tableNum]);
                        amount = 0;
                        for (int k = 0; k < m_incomingCount[tableNum]; ++k) {
                            try {

                                temp[k] = c.m_incomingOffers[tableOffset + j];
                                amount += c.m_incomingOffers[tableOffset + j].Amount;
                                j = (j + 1) % TransferManager.TRANSFER_OFFER_COUNT;
                            }
                            catch (Exception e) {
                                Log.InfoFormat("Exception {0} at {1}", e.Message, e.StackTrace);
                                Log.Info($"k={0} j={j} tableNum={tableNum} m_incomingWriteAt[tableNum]={m_incomingWriteAt[tableNum]} c.m_incomingReadAt[tableNum]={c.m_incomingReadAt[tableNum]} m_incomingCount[tableNum]={m_incomingCount[tableNum]}");
                            }

                        }
                        temp.CopyTo(c.m_incomingOffers, tableOffset);
                        m_incomingAmount[reason] = amount;

                        j = c.m_outgoingReadAt[tableNum];
                        amount = 0;
                        for (int k = 0; k < m_outgoingCount[tableNum]; ++k) {
                            try {
                                temp[k] = c.m_outgoingOffers[tableOffset + j];
                                amount += c.m_outgoingOffers[tableOffset + j].Amount;
                                j = (j + 1) % TransferManager.TRANSFER_OFFER_COUNT;
                            }
                            catch (Exception e) {
                                Log.InfoFormat("Exception {0} at {1}", e.Message, e.StackTrace);
                                Log.Info($"k={0} j={j} tableNum={tableNum} m_outgoingWriteAt[tableNum]={m_outgoingWriteAt[tableNum]} c.m_outgoingReadAt[tableNum]={c.m_outgoingReadAt[tableNum]} m_outgoingCount[tableNum]={m_outgoingCount[tableNum]}");
                            }
                        }
                        temp.CopyTo(c.m_outgoingOffers, tableOffset);
                        m_outgoingAmount[reason] = amount;

                        /* Reset my pointers */
                        c.m_incomingReadAt[tableNum] = 0;
                        c.m_outgoingReadAt[tableNum] = 0;
                        m_incomingWriteAt[tableNum] = m_incomingCount[tableNum];
                        m_outgoingWriteAt[tableNum] = m_outgoingCount[tableNum];
                    }
                }
            }
        }

        public void OnModLoaded() {
#if DEBUG || LABS || EXPERIMENTAL
            Log.Info($"{GetType().Name}.OnModLoaded() {Assembly.GetExecutingAssembly().GetName().Version} IsGameLoaded={TransferBrokerMod.Installed.IsGameLoaded}");
#endif
            if (TransferBrokerMod.Installed.IsGameLoaded) {

                UpdateMyData();

#if DEBUG
                /* FIXME: Check if traffic police building is enabled */
                Log.Info($"Locale Override= {Locale.LocaleOverride != null} append={Locale.LocaleOverride?.appendOverride}");
#endif
                SetupMilestones();
                TransferBrokerMod.Installed.CheckDependencies();

                if (SetupActivator()) {
                    ThreadHelper.dispatcher.Dispatch(GameMainToolbar.instance.RefreshPanel);
                }
#if DEBUG && false
                ListSpecialBuildings();
#endif
                CheckActivation();

                var openPanelTarget = WorldInfoPanel.GetCurrentInstanceID();
                if (!openPanelTarget.IsEmpty) {
                    SetInfoViewTarget(openPanelTarget);
                }

            }
#if false
            if (SourcingMod.Installed.IsGameLoaded) {
                var inst = Traverse.Create(Singleton<TransferManager>.instance);
                c.m_outgoingOffers = inst.Field("m_outgoingOffers").GetValue<TransferManager.TransferOffer[]>();
                c.m_incomingOffers = inst.Field("m_incomingOffers").GetValue<TransferManager.TransferOffer[]>();

                Log._DebugFormat("{0}.Start() c.m_outgoingOffers={1}", GetType().Name, c.m_outgoingOffers);

#if MAINTAIN_CONNECTIONS
                    if (LoadingExtension.IsGameLoaded) {
                        RecordPublicTransportation();
                    }
#endif
            }
#endif

            //            Start();
        }

        private void HookTransferManagerData() {
#if DEBUG
            Thread.Sleep(1000);
#endif

            //            if (SourcingMod.Installed.IsGameLoaded) {
            var inst = Traverse.Create(Singleton<TransferManager>.instance);
            c.m_outgoingOffers = inst.Field("m_outgoingOffers").GetValue<TransferManager.TransferOffer[]>();
            c.m_incomingOffers = inst.Field("m_incomingOffers").GetValue<TransferManager.TransferOffer[]>();

#if DEBUG
            Log._DebugFormat("{0}.HookTransferManagerData() c.m_outgoingOffers={1}", GetType().Name, c.m_outgoingOffers);
#endif

#if MAINTAIN_CONNECTIONS
                    if (LoadingExtension.IsGameLoaded) {
                        RecordPublicTransportation();
                    }
#endif
            //            }

        }

        public void OnActivated(ushort id, bool isCheat) {
#if DEBUG || LABS || EXPERIMENTAL
            Log.Info($"{GetType().Name}.OnActivated({id}, isCheat={isCheat})");
#endif
            m_activator = id;
            m_activator_is_cheat = isCheat;
            GetActivatorParams(c.buildings.GetBuildingName(id, InstanceID.Empty));
            InstallJobTitles(m_activator_param, m_activator_traffic != 0);
        }

        public void OnBuildingRemoved(ushort id) {
#if DEBUG
            Log.Info($"{GetType().Name}.OnDeactivated({id})");
#endif
            if (id == m_activator) {
#if DEBUG || LABS || EXPERIMENTAL
                Log.Info($"{GetType().Name}.OnBuildingRemoved({id})");
#endif
                /* Reset activation and check if another activator is present */
                ResetActivation();
                CheckActivation();
            } else if (id == m_activator_traffic) {
                OnActivatedTraffic(id, false);
                // CheckActivation();
            }
        }
        public void OnActivatedTraffic(ushort buildingID, bool IsTOC) {
#if DEBUG || LABS || EXPERIMENTAL
            Log.Info($"{GetType().Name}.OnActivatedTraffic({buildingID}, isTrafficOperationsCenter={IsTOC})");
#endif
            if (IsTOC) {
                m_activator_traffic = buildingID;
                if (m_activator != 0) {
                    InstallJobTitles(m_activator_param, true);
                }
            } else {
                m_activator_traffic = 0;
                /* Check if any other police station has the needed name */
                CheckActivation();
                if (m_activator != 0 && m_activator_traffic == 0) {
                    InstallJobTitles(m_activator_param, false);
                }
            }
        }

        private void InstallJobTitles(float param, bool hasTrafficPolice) {
#if DEBUG
            Log.Info($"{GetType().Name}.InstallJobTitles({param}, hasTrafficPolice={hasTrafficPolice})");
#endif

            var activator = PrefabCollection<BuildingInfo>.FindLoaded(ACTIVATION_BUILDING);

            Assert.IsTrue(activator != null,
                $"{GetType().Name}.InstallJobTitles() could not find Prefab '{ACTIVATION_BUILDING}'");

#if DEBUG
            Log.Info($"{GetType().Name}.InstallJobTitles({param}) activator={activator != null}");
#endif

            if (activator == null) {
                return;
            }

            // TODO: Chirper
            // Singleton<MessageManager>.instance.TryCreateMessage(string firstID, string repeatID, string key, uint senderID, string tag);

            var ai = activator.GetAI() as MonumentAI;
            Assert.IsTrue(ai != null,
                $"{GetType().Name}.InstallJobTitles() could not find Prefab '{ACTIVATION_BUILDING}' of type MonumentAI");

#if DEBUG
            Log.Info($"{GetType().Name}.InstallJobTitles({param}) ai={ai != null}");
#endif
            if (ai == null) {
                return;
            }

            if (param == float.MinValue) {
                /* Restore to defaults */
                ai.m_maintenanceCost = m_backup.m_maintenanceCost;
#if DEBUG
                Log.Info($"{GetType().Name}.InstallJobTitles({param} as minValue) new m_maintenanceCost={ai.m_maintenanceCost}");
#endif

                ColossalFramework.Globalization.Locale.SetOverriddenLocalizedStrings("BUILDING_SHORT_DESC", ACTIVATION_BUILDING, new string[] { });

                ai.m_uneducatedFemaleJobs = m_backup.m_uneducatedFemaleJobs;
                ai.m_educatedFemaleJobs = m_backup.m_educatedFemaleJobs;
                ai.m_wellEducatedFemaleJobs = m_backup.m_wellEducatedFemaleJobs;
                ai.m_highlyEducatedFemaleJobs = m_backup.m_highlyEducatedFemaleJobs;

                ai.m_uneducatedMaleJobs = m_backup.m_uneducatedMaleJobs;
                ai.m_educatedMaleJobs = m_backup.m_educatedMaleJobs;
                ai.m_wellEducatedMaleJobs = m_backup.m_wellEducatedMaleJobs;
                ai.m_highlyEducatedMaleJobs = m_backup.m_highlyEducatedMaleJobs;

            } else if (float.IsNaN(param)) {
                ai.m_maintenanceCost = m_backup.m_maintenanceCost * EXPENSE_FACTOR;
#if DEBUG
                Log.Info($"{GetType().Name}.InstallJobTitles({param} as nan) new m_maintenanceCost={ai.m_maintenanceCost}");
#endif

                if (Singleton<UnlockManager>.instance.Unlocked(m_documentation)) {
                    ColossalFramework.Globalization.Locale.SetOverriddenLocalizedStrings("BUILDING_SHORT_DESC", ACTIVATION_BUILDING, new string[] {
                        hasTrafficPolice ?
                            "Transfer Brokerage offices and data center with traffic camera feeds." :
                            "Transfer Brokerage offices and data center.", });

                    ai.m_uneducatedFemaleJobs = m_backup.m_uneducatedFemaleJobs;
                    ai.m_educatedFemaleJobs = m_backup.m_educatedFemaleJobs;
                    ai.m_wellEducatedFemaleJobs = m_backup.m_wellEducatedFemaleJobs;
                    ai.m_highlyEducatedFemaleJobs = new string[] {
                       // ColossalFramework.Globalization.Locale.Get("CITIZEN_OCCUPATION_PROFESSION_HIGHLYEDUCATED_FEMALE", SourcingMod.PACKAGE_NAME),
                        "Chief of Transportation",
                    };

                    ai.m_uneducatedMaleJobs = m_backup.m_uneducatedMaleJobs;
                    ai.m_educatedMaleJobs = m_backup.m_educatedMaleJobs;
                    ai.m_wellEducatedMaleJobs = m_backup.m_wellEducatedMaleJobs;
                    ai.m_highlyEducatedMaleJobs = new string[] {
                        // ColossalFramework.Globalization.Locale.Get("CITIZEN_OCCUPATION_PROFESSION_HIGHLYEDUCATED", SourcingMod.PACKAGE_NAME),
                        "Chief of Transportation",
                    };
                } else {
                    ColossalFramework.Globalization.Locale.SetOverriddenLocalizedStrings("BUILDING_SHORT_DESC", ACTIVATION_BUILDING, new string[] {
                        hasTrafficPolice ?
                            "Transfer Brokerage.\nRun by a politician with a traffic camera budget." :
                            "Transfer Brokerage.\nRun by a politician.", });

                    ai.m_uneducatedFemaleJobs = new string[] { "Minister's Mistress", };
                    ai.m_educatedFemaleJobs = new string[] { "The Granddaughter", };
                    ai.m_wellEducatedFemaleJobs = new string[] { "Daughter #1", };
                    ai.m_highlyEducatedFemaleJobs = new string[] { "Minister of Transportation", };

                    ai.m_uneducatedMaleJobs = new string[] { "Minister's Friend", };
                    ai.m_educatedMaleJobs = new string[] { "The Grandson", };
                    ai.m_wellEducatedMaleJobs = new string[] { "Son #1", };
                    ai.m_highlyEducatedMaleJobs = new string[] { "Minister of Transportation", };
                }
            } else if (Mathf.Abs((float)(param - Math.PI)) <= 0.005f) {
                /* Enjoy your pie, nerd! */

                ai.m_maintenanceCost = 625; // 100$
#if DEBUG
                Log.Info($"{GetType().Name}.InstallJobTitles({param} as pi) new m_maintenanceCost={ai.m_maintenanceCost}");
#endif

                ColossalFramework.Globalization.Locale.SetOverriddenLocalizedStrings("BUILDING_SHORT_DESC", ACTIVATION_BUILDING, new string[] {
                        hasTrafficPolice ?
                            "Transfer Brokerage Singularity with traffic camera feed." :
                            "Transfer Brokerage Singularity.", });

                ai.m_uneducatedFemaleJobs = new string[] {
                    "Circulation Expert",
                };
                ai.m_educatedFemaleJobs = new string[] {
                    "Chaos Management Consultant",
                };
                ai.m_wellEducatedFemaleJobs = new string[] {
                    "Mayhem Coordinator",
                };
                ai.m_highlyEducatedFemaleJobs = new string[] {
                    "Genius",
                };

                ai.m_uneducatedMaleJobs = new string[] {
                    "Tan Gent",
                };
                ai.m_educatedMaleJobs = new string[] {
                    "Chaos Management Consultant",
                };
                ai.m_wellEducatedMaleJobs = new string[] {
                    "Mayhem Coordinator",
                };
                ai.m_highlyEducatedMaleJobs = new string[] {
                    "Genius",
                };

            } else if (param >= MINIMUM_ACTIVATION_PARAM && param <= MAXIMUM_ACTIVATION_PARAM) {
                ai.m_maintenanceCost = 1250 + (int)((1f - c.config.m_rndAmplitude) * ((float)m_backup.m_maintenanceCost * (float)EXPENSE_FACTOR - 1250f)); // >= $200
#if DEBUG
                Log.Info($"{GetType().Name}.InstallJobTitles({param} in range) new m_maintenanceCost={ai.m_maintenanceCost} rndFrequency={c.config.m_rndFrequency} hasTrafficPolice={hasTrafficPolice}");
#endif

                try {

                    ColossalFramework.Globalization.Locale.SetOverriddenLocalizedStrings("BUILDING_SHORT_DESC", ACTIVATION_BUILDING, new string[] {
                            hasTrafficPolice ?
                                "Transfer Breakerage office building.\nCameras, Schmameras." :
                                "Transfer Breakerage office building.", });
#if DEBUG

                    Log.Info($"{GetType().Name}.InstallJobTitles({param} in range) hasTrafficPolice={hasTrafficPolice}");
#endif
                }
                catch (Exception ex) {
                    Log.Info($"Failed to SetOverriddenLocalizedStrings ({ex.Message})\n{ex.StackTrace}");
                }

                ai.m_uneducatedFemaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY
                    "Personal Chef to YourNameCouldBeHere",
#endif
                };
                ai.m_educatedFemaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY
                    "Personal Assistant to YourNameCouldBeHere",
#endif
                };
                ai.m_wellEducatedFemaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY
                    "Advisor to YourNameCouldBeHere",
#endif
                };
                ai.m_highlyEducatedFemaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY_EXTRAORDINARY
                    "Visionary",
#endif
                    "Chief of Transport",
                };

                ai.m_uneducatedMaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY
                    "Personal Chef to YourNameCouldBeHere",
#endif
                };
                ai.m_educatedMaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY
                    "Personal Assistant to YourNameCouldBeHere",
#endif
                };
                ai.m_wellEducatedMaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY
                    "Advisor to YourNameCouldBeHere",
#endif
                };
                ai.m_highlyEducatedMaleJobs = new string[] {
#if CUSTOM_JOB_TITLES_AS_BUG_BOUNTY_EXTRAORDINARY
                    "Visionary",
#endif
                    "Chief of Transport",
                };

            }
        }

        private void ResetParameters() {
            m_activator_param = float.NaN;
            c.config.m_rndFrequency = 0f;
            c.config.m_rndAmplitude = 1f;
        }

        private void GetActivatorParams(string name) {
#if DEBUG
            Log._DebugFormat("{0}.GetActivatorParams({1})", GetType().Name, name);
#endif

            float param;

            if (name == null) {
                ResetParameters();
                param = float.NaN;
            } else {

                string lastword = name.Substring(name.LastIndexOf(' ') + 1);

                try {
                    param = float.Parse(lastword, System.Globalization.NumberStyles.Float);
                }
#pragma warning disable CS0168 // Variable is declared but never used
                catch (FormatException ex) {
#pragma warning restore CS0168 // Variable is declared but never used
                    param = float.NaN;
                }

                if (param >= MINIMUM_ACTIVATION_PARAM && param <= MAXIMUM_ACTIVATION_PARAM) {
                    c.config.m_rndFrequency = Mathf.Cos(param) / 2f + 0.5f;
                    c.config.m_rndAmplitude = Mathf.Sin(param) / 2f + 0.5f;
                } else {
                    ResetParameters();
                }
            }
            m_activator_param = param;
#if DEBUG || LABS || EXPERIMENTAL
            Log.InfoFormat("{0}.GetActivatorParams({1}) {2}", GetType().Name, name, c.config);
#endif
        }

        /* OnInstanceSetName
         * In the case of cheats, there's a bug. If there is more than one cheat buildings,
         * the first one to changed to a non-cheat name will deactivate the cheat,
         * but on reloading the save, the remaining cheat buildings will cause
         * the mod to be activated again.
         *
         * I don't think it's worth coding a more fool-proof algo at this time.
         */
        public void OnSetInstanceName(InstanceID id, string newName) {
#if DEBUG
            Log._DebugFormat("{0}.OnSetInstanceName({1}, {2})", GetType().Name, InstanceName(id), newName);
#endif
            if (m_backup.transferManager == null) {
                return;
            }

            if (id.Type == InstanceType.Building) {
                if (m_activator == id.Building) {
                    if (m_activator_is_cheat && !newName.StartsWith(CHEAT_PREFIX)) {
                        OnBuildingRemoved(id.Building);
                        return;
                    }
                    GetActivatorParams(newName);
                    if (!m_activator_is_cheat) {
                        InstallJobTitles(m_activator_param, c.config.m_hasCameras);
                    }
                    /* FIXME: Test what happens with custom buildings, do they have a prefabDataIndex, or -1 ? */
                } else if (IsTrafficCameraBuilding(id.Building)) {
                    OnActivatedTraffic(id.Building, IsTrafficCameraBuilding(id.Building) && newName.StartsWith(ACTIVATION_TRAFFIC_NAME));
                } else if (m_activator == 0) {
                    if (newName.StartsWith(CHEAT_PREFIX)) {
                        OnActivated(id.Building, true);
                    }
                }
            }
        }

        protected override void Awake() {
#if DEBUG
            Log._Debug($"{GetType().Name}.Awake() ");
#endif

            base.Awake();

            startupLock = new object();

            c.sim = Singleton<SimulationManager>.instance;
            c.buildings = Singleton<BuildingManager>.instance;
            c.vehicles = Singleton<VehicleManager>.instance;
            c.citizens = Singleton<CitizenManager>.instance;
            c.nets = Singleton<NetManager>.instance;

            ResetActivation();

            c.workQ = new Queue<MatchWork>();
            c.m_workInProgress = new Coordination.WorkStatus[(int)LAST_VALID_REASON + 1];
            c.numBusyMaterials = new OneUshort();
            c.numBusyMaterials.value = 0;
            c.stopwatch = new Stopwatch();
            c.goingIdle_lock = new object();
            c.owner = this;
            transferQ = new Queue<TransferWork>();
            c.transferQ_lock = new object();

            c.m_incomingReadAt = new ushort[((int)LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT];
            c.m_outgoingReadAt = new ushort[((int)LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT];
            c.offerQueueFull = new bool[((int)LAST_VALID_REASON + 1)];
#if DEBUG || LABS || EXPERIMENTAL
            c.missedOFfers = new uint[((int)LAST_VALID_REASON + 1)];
#endif
            m_queuedIncomingOffers = new Dictionary<InstanceID, TransferManager.TransferOffer>[((int)LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT];
            for (int i = 0; i < m_queuedIncomingOffers.Length; ++i) m_queuedIncomingOffers[i] = new Dictionary<InstanceID, TransferManager.TransferOffer>();

            m_queuedOutgoingOffers = new Dictionary<InstanceID, TransferManager.TransferOffer>[((int)LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT];
            for (int i = 0; i < m_queuedOutgoingOffers.Length; ++i) m_queuedOutgoingOffers[i] = new Dictionary<InstanceID, TransferManager.TransferOffer>();

            removedOffers = new HashSet<InstanceID>[(int)LAST_VALID_REASON + 1];
            for (int i = 0; i < removedOffers.Length; ++i) removedOffers[i] = new HashSet<InstanceID>();

#if MAINTAIN_CONNECTIONS
            cargoConnections = new Dictionary<ushort, List<ushort>>();
#endif
            c.config = new Coordination.MatchmakingConfig();
            ResetParameters();
            c.config.m_hasCameras = false;

            c.config.cacheReason = TransferManager.TransferReason.None;
            c.resultsCache = new Dictionary<InstanceID, TransferWork>(2 * ((int)LAST_VALID_REASON + 1) * TransferManager.TRANSFER_PRIORITY_COUNT);

            HookTransferManagerData();

            StartMatchmakers();

            Assert.IsNotNull(c.m_incomingOffers, "Offers table should not be null");
        }

        public void Start() {
#if DEBUG
            Log._Debug($"{GetType().Name}.Start() isGameLoaded={TransferBrokerMod.Installed.IsGameLoaded}");
#endif

            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.Start() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            try {

                var m_managers = Traverse.Create(typeof(SimulationManager)).Field("m_managers").GetValue<FastList<ISimulationManager>>();
                var numManagers = m_managers.m_size;

                m_managers.Remove(Singleton<TransferManager>.instance);
                if (m_managers.m_size != numManagers) {
                    // Backup the reference if it existed, so it can be restored only if needed
                    m_backup.transferManager = Singleton<TransferManager>.instance;

                    /* Replace vanilla TransferManager */
                    SimulationManager.RegisterSimulationManager(this as ISimulationManager);

                    //                    if (SourcingMod.Installed.IsGameLoaded) {
                    //                        if (m_backup.transferManager != null) {
                    //                            m_activator = 0;
                    //                            m_enabled = true;
                    //                        }
                    //                    }

#if STEAMSTUFF
#if DEBUG
                    ColossalFramework.PlatformServices.PlatformService.SetRichPresence($"Developing the {TransferBrokerMod.PACKAGE_NAME} mod");
#elif LABS || EXPERIMENTAL
                    ColossalFramework.PlatformServices.PlatformService.SetRichPresence($"Testing the {TransferBrokerMod.PACKAGE_NAME} mod");
#endif
#if DEBUG || LABS || EXPERIMENTAL
                    ColossalFramework.PlatformServices.PlatformService.SetRichPresenceVisibility(true);
                    var state = ColossalFramework.PlatformServices.PlatformService.personaState;
                    var b = ColossalFramework.PlatformServices.PlatformService.platformType;
                    var c = ColossalFramework.PlatformServices.PlatformService.userID;
                    Log.Info($"Playing as personaName={ColossalFramework.PlatformServices.PlatformService.personaName} ; {state} ; {b} {c}");
#endif
#endif

                } else {
                    Log.Warning($"Vanilla TransferManager instance not found in the Simulators list of Managers. {Versioning.PACKAGE_NAME} will be inactive.");
                }
            }
            catch (Exception ex) {
                Log.Info($"Failed to {GetType().Name}.Start() ({ex.Message})\n{ex.StackTrace}");
            }
#if DEBUG
            Log._Debug($"{GetType().Name}.Start() DONE");
#endif

        }

        public void StartMatchmakers() {

#if SINGLETHREAD
                    int numthreads = 1;
#else
            /* Reserve 2 threads for Simulation and Rendering */
            int numthreads = Mathf.Clamp(SystemInfo.processorCount - 2, 1, 2);
#endif

            matchmakers = new MatchMaker[numthreads];
            for (int i = 0; i < matchmakers.Length; ++i) {
                if (c.workQ == null) throw new Exception("workQ is null when starting matchmaker");
                matchmakers[i] = new MatchMaker(c, i);
            }
        }

        public void OnDestroy() {
#if DEBUG
            Log._Debug($"{GetType().Name}.OnDestroy()");
#endif
            //            if (matchmakers == null) {
            //                Log._Debug($"{GetType().Name}.OnDestroy() could have been skipped");
            //                /* This can be skipped if Start() was skipped */
            //            }
            if (m_milestones != null) {
                Destroy(m_milestones);
#if DEBUG
                Log._Debug($"{GetType().Name}.OnDestroy() - destroying milestones");
#endif
            }
            if (m_documentation != null) {
                ScriptableObject.Destroy(m_documentation);
#if DEBUG
                Log._Debug($"{GetType().Name}.OnDestroy() - destroying documentation");
#endif
            }
            if (DestroyActivator()) {
                GameMainToolbar.instance.RefreshPanel();
            }

            if (Singleton<InfoManager>.instance.CurrentMode == InfoManager.InfoMode.TrafficRoutes) {
                lock (c.workQ) {
                    c.resultsCache.Clear();
                }
                c.buildings.UpdateBuildingColors();
            }

        }

        public override void InitializeProperties(BrokerProperties properties) {
#if DEBUG
            Log.Info($"{GetType().Name}.InitializeProperties()");
#endif
            base.InitializeProperties(properties);
            // SetupActivator();
        }

        public override void DestroyProperties(BrokerProperties properties) {
#if DEBUG
            Log.Info($"{GetType().Name}.DestroyProperties()");
#endif
            base.DestroyProperties(properties);
            // SetupActivator();
        }

        private bool SetupActivator() {
#if DEBUG
            Log.Info($"{GetType().Name}.SetupActivator()");
#endif

            bool success = false;

#if false
            Task<BuildingInfo> task = ThreadHelper.dispatcher.Dispatch(() => PrefabCollection<BuildingInfo>.FindLoaded(ACTIVATION_BUILDING));
            task.Wait();
            BuildingInfo activator = task.result;
#else
/*
 *              Assert.IsTrue(
                    (TransferBrokerMod.Installed.IsGameLoaded && Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread) ||
                    (!TransferBrokerMod.Installed.IsGameLoaded && Dispatcher.currentSafe == ThreadHelper.dispatcher),
                    $"{GetType().Name}.SetupActivator() should be called on Simulation Thread when Game is in progress, otherwise on Main Thread. (not '{Thread.CurrentThread.Name}')");
*/

/*
 * Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.SetupActivator() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");
*/
            var activator = PrefabCollection<BuildingInfo>.FindLoaded(ACTIVATION_BUILDING);
#endif
#if DEBUG
            Log.Info($"{GetType().Name}.SetupActivator() continues");
#endif

            Assert.IsTrue(activator != null,
                $"{GetType().Name}.SetupActivator() could not find Prefab '{ACTIVATION_BUILDING}'");

            if (activator != null) {
                /* TODO: Test with theme installed; it provides its own override */
                m_backup.LocaleOverride = Locale.LocaleOverride;
                if (Locale.LocaleOverride == null) {
#if DEBUG
                    Log.Info($"{GetType().Name}.SetupActivator() setting up new LocaleOverride");
#endif

                    Locale.LocaleOverride = new Locale();
                    Locale.LocaleOverride.appendOverride = false;
                    Locale.LocaleOverride.AddLocalizedString(new Locale.Key {
                        m_Identifier = "BUILDING_SHORT_DESC",
                        m_Key = ACTIVATION_BUILDING,
                        m_Index = 0,
                    },
                    "An Enormous Office Building");
                }

                var ai = activator.GetAI() as MonumentAI;
                if (ai != null) {

                    m_backup.m_uneducatedFemaleJobs = ai.m_uneducatedFemaleJobs;
                    m_backup.m_educatedFemaleJobs = ai.m_educatedFemaleJobs;
                    m_backup.m_wellEducatedFemaleJobs = ai.m_wellEducatedFemaleJobs;
                    m_backup.m_highlyEducatedFemaleJobs = ai.m_highlyEducatedFemaleJobs;
                    m_backup.m_uneducatedMaleJobs = ai.m_uneducatedMaleJobs;
                    m_backup.m_educatedMaleJobs = ai.m_educatedMaleJobs;
                    m_backup.m_wellEducatedMaleJobs = ai.m_wellEducatedMaleJobs;
                    m_backup.m_highlyEducatedMaleJobs = ai.m_highlyEducatedMaleJobs;

                    m_backup.m_maintenanceCost = ai.m_maintenanceCost;
#if DEBUG
                    Log.Info($"{GetType().Name}.SetupActivator() backup original m_maintenanceCost={ai.m_maintenanceCost}");
#endif

                    ai.m_constructionCost *= EXPENSE_FACTOR;
                    ++ai.m_workPlaceCount3; /* The Chief's position */

                    // InstallJobTitles(float.NaN, c.config.m_hasCameras);

                    success = true;

#if DEBUG
                    Log.Info($"{GetType().Name}.SetupActivator() new constructionCost={ai.m_constructionCost}");
#endif
                } else {
                    Log.Warning($"{GetType().Name}.SetupActivator() - activator building is not MonumentAI, but {ai.GetType().AssemblyQualifiedName}");
                }
            } else {
                Log.Warning($"{GetType().Name}.SetupActivator() - activator building '{ACTIVATION_BUILDING}' not found");
            }
            return success;
        }

        private bool DestroyActivator()
        {
            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.Start() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

#if DEBUG
            Log.Info($"{GetType().Name}.DestroyActivator()");
#endif

            bool success = false;

            var activator = PrefabCollection<BuildingInfo>.FindLoaded(ACTIVATION_BUILDING);

            if (activator != null)
            {

                Locale.LocaleOverride = m_backup.LocaleOverride;

                var ai = activator.GetAI() as MonumentAI;
                if (ai != null)
                {
                    ai.m_constructionCost /= EXPENSE_FACTOR;
                    ai.m_maintenanceCost = m_backup.m_maintenanceCost;
#if DEBUG
                    Log.Info($"{GetType().Name}.DestroyActivator() new m_maintenanceCost={ai.m_maintenanceCost}");
#endif
                    --ai.m_workPlaceCount3; /* The Chief's position */

                    InstallJobTitles(float.MinValue, false);
                    success = true;

#if DEBUG
                    Log.Info($"{GetType().Name}.DestroyActivator() new constructionCost={ai.m_constructionCost}");
#endif
                }
            }

            return success;
        }
#if DEBUG
        public void OnEnable() {

            Log.Info($"{GetType().Name}.OnEnable() IsGameLoaded={TransferBrokerMod.Installed.IsGameLoaded}");

            if (!TransferBrokerMod.Installed.IsGameLoaded) {
                /* FIXME:
                 * Where is the best place to do stuff after Prefabs are loaded?
                 */
                // SourcingMod.Instance.threading.QueueSimulationThread(SetupActivator);
                // Singleton<LoadingManager>.instance.QueueLoadingAction(SetupActivatorEnum());
            }
        }
#endif

#if DEBUG
        public void OnDisable() {
            Log.Info($"{GetType().Name}.OnDisable()");
        //            if (DestroyActivator()) {
        //                GameMainToolbar.instance.RefreshPanel();
        //            }
    }
#endif

        public void OnModOutdated() {
#if DEBUG || LABS || EXPERIMENTAL
            Log.InfoFormat("{0}.OnModOutdated() {1}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, Thread.CurrentThread.Name);
#endif

            //            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
            //                $"{GetType().Name}.OnModOutdated() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

            if (m_backup.transferManager != null) {
                var m_managers = Traverse.Create(typeof(SimulationManager)).Field("m_managers").GetValue<FastList<ISimulationManager>>();
#if DEBUG
                Log._DebugFormat("{0}.OnModOutdated() m_managers={1} m_size={2} Length={3}",
                    GetType().Name,
                    m_managers,
                    m_managers?.m_size,
                    m_managers?.m_buffer?.Length);
#endif
                m_managers.Remove(this as ISimulationManager);
#if DEBUG
                Log._DebugFormat("{0}.OnModOutdated() after remove m_managers={1} m_size={2} Length={3}",
                    GetType().Name,
                    m_managers,
                    m_managers?.m_size,
                    m_managers?.m_buffer?.Length);
#endif

                /* Restore vanilla TransferManager */
                SimulationManager.RegisterSimulationManager(m_backup.transferManager);

                /* Needs to run after start on the same thread */
                /* Join on Main thread to avoid OnModOutdated arriving before awake. and thread start */
                var task = ThreadHelper.dispatcher.Dispatch(Quit);
                task.Wait();
                // Quit();

                if (TransferBrokerMod.Installed.IsGameLoaded) {
                    /* needs to run on simulation thread */
                    UpdateTransferManagerData();
                }
            }

#if DEBUG
            Log._Debug($"{GetType().Name}.OnModOutdated() DONE");
#endif
        }

        public override void EarlyUpdateData()
        {
            WaitForIdle();
        }

        // XXX this should be override
        public override void UpdateData(SimulationManager.UpdateMode mode) {
#if DEBUG
            Log._DebugFormat("{0}.UpdateData({1}) - setting up arrays", GetType().Name, mode);
#endif
            /* TransferManager cleans up the data of missing buildings, etc before it's ready for use */
            Singleton<TransferManager>.instance.UpdateData(mode);
            UpdateMyData();
        }

        public void ResetActivation() {
#if DEBUG || LABS || EXPERIMENTAL
            if (m_activator != 0) {
                Log.Info($"{GetType().Name}.ResetActivation() - will use legacy algo");
            }
#endif
            m_activator = 0;
            m_activator_traffic = 0;
            m_enabled = true;
            m_activated = false;
        }

        private void CheckActivation() {
#if DEBUG
            Log._DebugFormat("{0}.CheckActivation() m_activator={1} buildings.Length={2}", GetType().Name, m_activator, c.buildings.m_buildings.m_buffer.Length);
#endif
            ushort foundActivation = 0;
            ushort foundActivationTraffic = 0;
            bool isCheat = false;
            for (ushort b = 1; b < c.buildings.m_buildings.m_buffer.Length; ++b) {
                if (c.buildings.m_buildings.m_buffer[b].m_flags != 0 && c.buildings.m_buildings.m_buffer[b].Info != null) {
                    if (IsActivatorBuilding(b)) {
                        foundActivation = b;
                    } else if (foundActivationTraffic == 0 && IsTrafficCameraBuilding(b) && c.buildings.GetBuildingName(b, InstanceID.Empty).StartsWith(ACTIVATION_TRAFFIC_NAME)) {
                        foundActivationTraffic = b;
                    } else if (foundActivation == 0 && c.buildings.GetBuildingName(b, InstanceID.Empty).StartsWith(CHEAT_PREFIX))
                    {
                        foundActivation = b;
                        isCheat = true;
#if DEBUG
                        Log._DebugFormat("{0}.CheckActivation() found cheat #{1} ({2})", GetType().Name, b, c.buildings.GetBuildingName(b, InstanceID.Empty));
#endif

                        /* don't break. If there is a ACTIVATION_BUILDING as well as cheats, must find the ACTIVATION_BUILDING,
                         * so keep searching.
                         */
                    }
                }
            }

            if (foundActivationTraffic != 0) {
                OnActivatedTraffic(foundActivationTraffic, true);
            }
            if (m_activator == 0 && foundActivation != 0) {
                OnActivated(foundActivation, isCheat);
            }

        }

        public override void LateUpdateData(SimulationManager.UpdateMode mode) {
#if DEBUG
            Log._DebugFormat("{0}.LateUpdateData({1}) - scanning other managers", GetType().Name, mode);
#endif

            Assert.IsTrue(Thread.CurrentThread == Singleton<SimulationManager>.instance.m_simulationThread,
                $"{GetType().Name}.LateUpdateData() should only be called on Simulation Thread (not '{Thread.CurrentThread.Name}')");

            SetupMilestones();
            TransferBrokerMod.Installed.CheckDependencies();
            if (m_backup.transferManager != null) {
                var task = ThreadHelper.dispatcher.Dispatch(SetupActivator);
                task.Wait();
                ResetActivation();
#if DEBUG && false
                ListSpecialBuildings();
#endif
                CheckActivation();
            }

            base.LateUpdateData(mode);

            // CheckActivation();
        }
#if MAINTAIN_CONNECTIONS
        /* FIXME: Lock around cargoConnections from Matchmaker */
                        private void RecordPublicTransportation() {
            ushort roadnode;
            ushort connection;

            Log._DebugFormat("in RecordPublicTransportation cargoConnections={0}", cargoConnections);
            cargoConnections.Clear();

            Log._DebugFormat("in RecordPublicTransportation c.buildings.m_buildings={0}", c.buildings.m_buildings == null ? "null" : "obj");
            Log._DebugFormat("in RecordPublicTransportation c.buildings.m_buildings.m_buffer={0}", c.buildings.m_buildings.m_buffer == null ? "null" : "obj");
            Log._DebugFormat("in RecordPublicTransportation cargoConnections={0}", cargoConnections == null ? "null" : "obj");
            Log._DebugFormat("in RecordPublicTransportation matchmakers={0}", matchmakers == null ? "null" : "obj");

            for (ushort i = 1; i < BuildingManager.MAX_BUILDING_COUNT; i++) {
                try {
                    // if (buildings.m_buildings.m_buffer[i].Info.m_class.m_service == ItemClass.Service.PublicTransport)
                    var building = c.buildings.m_buildings.m_buffer[i];
                    if (true || building.Info != null) {
                        if ((building.m_flags & Building.Flags.Active) != 0 && building.Info.m_buildingAI is CargoStationAI) {
                            ActivateCargoStation(i, ref building);
                            //                    roadnode = FindNearestServiceNode(i, ItemClass.Service.Road);
                            //                    connection = FindNearestServiceNode(i, ItemClass.Service.PublicTransport);
                            //
#if DEBUG
                            roadnode = FindNearestServiceNode(i, ItemClass.Service.Road);
                            connection = FindNearestServiceNode(i, ItemClass.Service.PublicTransport);

                            Log._DebugFormat(
                                "REC: Found building #{0} of subservice={1} level={2} ai={3} roadnode={4} connection={5}",
                                i,
                                building.Info.m_class.m_subService,
                                building.Info.m_class.m_level,
                                building.Info.m_buildingAI,
                                roadnode,
                                connection);
#endif
                            //                    if (!cargoConnections.ContainsKey(roadnode)) {
                            //                        cargoConnections.Add(roadnode, new List<ushort>());
                            //                    }
                            //
                            //                    cargoConnections[roadnode].Add(connection);
                            //
                            //                    if (!cargoConnections.ContainsKey(connection)) {
                            //                        cargoConnections.Add(connection, new List<ushort>());
                            //                    }
                            //
                            //                    cargoConnections[connection].Add(roadnode);
                        }
#if DEBUG
                        else if ((building.m_flags & Building.Flags.Active) == 0 && building.Info != null && building.Info.m_buildingAI is CargoStationAI) {
                            roadnode = FindNearestServiceNode(i, ItemClass.Service.Road);
                            connection = FindNearestServiceNode(i, ItemClass.Service.PublicTransport);

                            Log._DebugFormat(
                                "REC: Found INACTIVE building #{0} of subservice={1} level={2} ai={3} roadnode={4} connection={5}",
                                i,
                                building.Info.m_class.m_subService,
                                building.Info.m_class.m_level,
                                building.Info.m_buildingAI,
                                roadnode,
                                connection);
                        }
#endif

                    } else {
                        Log.InfoFormat("Building #{0} has no info", i);
                    }
                }
                catch (Exception e) {
                    Log.InfoFormat("Exception {0} at i={1} in RecordPublicTransportation. Trace:\n{2}", e, i, e.StackTrace);
                }
            }
        }

        public void DeactivateCargoStation(ushort buildingID, ref Building buildingData)
        {
            var roadnode = FindNearestServiceNode(buildingID, ItemClass.Service.Road);
            var connection = FindNearestServiceNode(buildingID, ItemClass.Service.PublicTransport);

            WaitForIdle();

            if (cargoConnections.ContainsKey(roadnode)) {
                cargoConnections[roadnode].Remove(connection);
                if (cargoConnections[roadnode].Count == 0) {
                    cargoConnections.Remove(roadnode);
                }
            }

            if (cargoConnections.ContainsKey(connection)) {
                cargoConnections[connection].Remove(roadnode);
                if (cargoConnections[connection].Count == 0) {
                    cargoConnections.Remove(connection);
                }
            }

        }

        public void ActivateCargoStation(ushort buildingID, ref Building buildingData) {
            var roadnode = FindNearestServiceNode(buildingID, ItemClass.Service.Road);
            var connection = FindNearestServiceNode(buildingID, ItemClass.Service.PublicTransport);

            WaitForIdle();

            if (!cargoConnections.ContainsKey(roadnode)) {
                cargoConnections.Add(roadnode, new List<ushort>());
            }

            cargoConnections[roadnode].Add(connection);

            if (!cargoConnections.ContainsKey(connection)) {
                cargoConnections.Add(connection, new List<ushort>());
            }

            cargoConnections[connection].Add(roadnode);
        }

        public void DeleteNode(ushort node, ref NetNode data) {


            if (cargoConnections.ContainsKey(node)) {
                List<ushort> cleanupList = new List<ushort>();

                WaitForIdle();

                foreach (ushort n in cargoConnections[node]) {
                    if (data.Info.m_class.m_service == ItemClass.Service.Road) {
                        // Remove Public transport connections
                        cargoConnections.Remove(n);
                    } else {
                        // Remove self from road's connection list
                        var roadConnection = cargoConnections[n];

                        roadConnection.Remove(node);

                        if (roadConnection.Count == 0) {
                            cleanupList.Add(n);
                        }
                    }
                }
                foreach (ushort n in cleanupList) {
                    cargoConnections.Remove(n);
                }
                cargoConnections.Remove(node);
            }
        }

        public void CreateNode(ushort node, ref NetNode data) {
            // FIXME: If 
            // Use Singleton<BuildingManager>.instance.UpdateNotifications to be notified to road connections
            if (data.Info.m_class.m_service == ItemClass.Service.Road) {
                // Fixme see if it is next to a Cargo Station, if yes connect it to cargoConnections
            }
        }
#endif

#if DEBUG
        internal static string OfferDescription(TransferManager.TransferOffer offer) {
            return offer.m_object.Type != 0 ? $"Qty {offer.Amount} by {InstanceName(offer.m_object)} ({offer.m_object.Type}#{offer.m_object.Index}) prio {offer.Priority}" :
                "removedOffer";
        }

        public static string InstanceName(InstanceID instanceId) {
            string name;

            if (instanceId.IsEmpty) return "none";

            switch (instanceId.Type) {
                case InstanceType.Building:
                    name = Singleton<BuildingManager>.instance.GetBuildingName(instanceId.Building, InstanceID.Empty) +
                        (Singleton<BuildingManager>.instance.m_buildings.m_buffer[instanceId.Building].Info.m_class.m_service == ItemClass.Service.PublicTransport ? ("/" + Singleton<BuildingManager>.instance.m_buildings.m_buffer[instanceId.Building].Info.m_class.m_subService) : string.Empty);
                    break;
                case InstanceType.Vehicle:
                    name = Singleton<VehicleManager>.instance.GetVehicleName(instanceId.Vehicle);
                    break;
                case InstanceType.Citizen:
                    name = Singleton<CitizenManager>.instance.GetCitizenName(instanceId.Citizen);
                    break;
                case InstanceType.NetSegment:
                    name = Singleton<NetManager>.instance.GetSegmentName(instanceId.NetSegment);
                    break;
                default:
                    name = $"Unknown of type {instanceId.Type}";
                    break;
            }
            return name;
        }

        public static void LogOffer(string prefix, ushort id, TransferManager.TransferReason material, TransferManager.TransferOffer offer) {

            Log._DebugFormat("{0} {1}: {2}: {3} @{4}",
                prefix,
                material,
                id,
                OfferDescription(offer),
                TransferBrokerMod.Installed.threading.simulationFrame & 0x3ff);
        }
        public void LogTransfer(string prefix, TransferManager.TransferReason material, TransferManager.TransferOffer offerOut, TransferManager.TransferOffer offerIn, int delta) {

            Log._DebugFormat("{0} {1}: {2} -> {3} (qty {4})",
                prefix,
                material,
                OfferDescription(offerOut),
                OfferDescription(offerIn),
                delta);
        }

#endif

        public void OnLevelUnloading() {
#if DEBUG
            Log._Debug($"{GetType().Name}.OnLevelUnloading()");
#endif
            if (m_backup.transferManager == null) {
                return;
            }
            // m_outgoingOffers = outgoingOffers;
            // MatchOffers(TransferManager.TransferReason.Paper);

            DestroyActivator();
        }

        public void OnLevelLoaded() {
#if DEBUG
            Log._Debug($"{GetType().Name}.OnLevelLoaded()");
#endif

            if (m_backup.transferManager == null) {
                return;
            }

            // m_outgoingOffers = outgoingOffers;
            // MatchOffers(TransferManager.TransferReason.Paper);
#if MAINTAIN_CONNECTIONS
            RecordPublicTransportation();
#endif
            /* FIXME:
             * Where is the best place to do stuff after Prefabs are loaded?
             */
            //            SetupActivator();
            //            CheckActivation();

            // Singleton<LoadingManager>.instance.QueueLoadingAction(SetupActivatorEnum());

        }

#if DEBUG
        public void PrintDebugInfo() {
            Log._Debug($"=== {GetType().Name}.PrintDebugInfo() *START* ===");
            
            // DebugPrintServiceRadii();
            // MatchMaker.InternalPrintDebugInfo();
            Log._Debug($"=== {GetType().Name}.PrintDebugInfo() *END* ===");
        }

        private void DebugPrintServiceRadii() {
            Log._Debug("___ Basic Service Radius ___");
            for (int reason = 0; reason <= (int)LAST_VALID_REASON; ++reason) {
                float distanceMultiplier = MatchMaker.GetDistanceMultiplier((TransferManager.TransferReason)reason);
                float serviceRadius = ((distanceMultiplier == 0f) ? 0f : (0.01f / distanceMultiplier));
                Log._DebugFormat("{0} - {1} - ({2}u)",
                    (TransferManager.TransferReason)reason,
                    serviceRadius,
                    Mathf.Sqrt(serviceRadius));
            }
        }
#endif

        /* Copied AS-IS from Assembly-CSharp */
        private void StartTransfer(TransferManager.TransferReason material, TransferManager.TransferOffer offerOut, TransferManager.TransferOffer offerIn, int delta) {
            bool activeIn = offerIn.Active;
            bool activeOut = offerOut.Active;
#if DEBUG
            Log._DebugFormat("{0}.StartTransfer({1} {2} -> {3} (delta={4})",
                    GetType().Name,
                    material,
                    OfferDescription(offerOut),
                    OfferDescription(offerIn),
                    delta);
#endif
            if (activeIn && offerIn.Vehicle != 0) {
                ushort vehicle = offerIn.Vehicle;
                VehicleInfo info = c.vehicles.m_vehicles.m_buffer[vehicle].Info;
                offerOut.Amount = delta;
                info.m_vehicleAI.StartTransfer(vehicle, ref c.vehicles.m_vehicles.m_buffer[vehicle], material, offerOut);
            } else if (activeOut && offerOut.Vehicle != 0) {
                ushort vehicle2 = offerOut.Vehicle;
                VehicleInfo info2 = c.vehicles.m_vehicles.m_buffer[vehicle2].Info;
                offerIn.Amount = delta;
                info2.m_vehicleAI.StartTransfer(vehicle2, ref c.vehicles.m_vehicles.m_buffer[vehicle2], material, offerIn);
            } else if (activeIn && offerIn.Citizen != 0) {
                uint citizen = offerIn.Citizen;
                CitizenInfo citizenInfo = c.citizens.m_citizens.m_buffer[citizen].GetCitizenInfo(citizen);
                if ((object)citizenInfo != null) {
                    offerOut.Amount = delta;
                    citizenInfo.m_citizenAI.StartTransfer(citizen, ref c.citizens.m_citizens.m_buffer[citizen], material, offerOut);
                }
            } else if (activeOut && offerOut.Citizen != 0) {
                uint citizen2 = offerOut.Citizen;
                CitizenInfo citizenInfo2 = c.citizens.m_citizens.m_buffer[citizen2].GetCitizenInfo(citizen2);
                if ((object)citizenInfo2 != null) {
                    offerIn.Amount = delta;
                    citizenInfo2.m_citizenAI.StartTransfer(citizen2, ref c.citizens.m_citizens.m_buffer[citizen2], material, offerIn);
                }
            } else if (activeOut && offerOut.Building != 0) {
                ushort building = offerOut.Building;
                BuildingInfo info3 = c.buildings.m_buildings.m_buffer[building].Info;
                offerIn.Amount = delta;
                info3.m_buildingAI.StartTransfer(building, ref c.buildings.m_buildings.m_buffer[building], material, offerIn);
            } else if (activeIn && offerIn.Building != 0) {
                ushort building2 = offerIn.Building;
                BuildingInfo info4 = c.buildings.m_buildings.m_buffer[building2].Info;
                offerOut.Amount = delta;
                info4.m_buildingAI.StartTransfer(building2, ref c.buildings.m_buildings.m_buffer[building2], material, offerOut);
            }
        }

        private ushort FindNearestServiceNode(ushort buildingID, ItemClass.Service service) {
#if USE_QUICK_BUILDING_POSITION
            /* This works well enough for many buildings, and is fast, but it fails when building has roads on the long sides
             * but entrance on the short side (eg, Medium Warehouse)
             */
            return FindNearestServiceNode(buildings.m_buildings.m_buffer[buildingID].m_position, service, buildingID);
#else
            if (c.buildings.m_buildings.m_buffer[buildingID].Info.m_buildingAI is OutsideConnectionAI) {
                return FindNearestServiceNode(c.buildings.m_buildings.m_buffer[buildingID].m_position, service, buildingID);
            }
            return FindNearestServiceNode(c.buildings.m_buildings.m_buffer[buildingID].CalculateSidewalkPosition(0f, 2f), service, buildingID);
#endif

        }

        private ushort FindNearestServiceNode(Vector3 pos, ItemClass.Service service1, ushort buildingID) {
            /* This clamping should not be needed,
             * but somewhere the outside cities are at Z=-30105 which works out to TransferManager.REASON_CELL_SIZE_LARGE (270)
             * Find where the 30105 maximum is set. It should be -8640..8576
             * This clamping is done in the inner loop of MatchAll so it's a major performance drag.
             * Probably in OutsideConnectionAI (CreateBuilding, BuildingLoaded)
             */
            int x = Mathf.Clamp((int)((pos.x / 64) + (TransferManager.REASON_CELL_SIZE_LARGE / 2)), 0, (int)TransferManager.REASON_CELL_SIZE_LARGE - 1);
            int z = Mathf.Clamp((int)((pos.z / 64f) + (TransferManager.REASON_CELL_SIZE_LARGE / 2)), 0, (int)TransferManager.REASON_CELL_SIZE_LARGE - 1);

            // #if DEBUG
            //             if (x >= TransferManager.REASON_CELL_SIZE_LARGE  || x < 0) {
            //                 Log._DebugFormat("X({0}) out of range", x);
            //                 return 0;
            //             }
            //             if (z >= TransferManager.REASON_CELL_SIZE_LARGE || z < 0) {
            //                 Log._DebugFormat("z({0}) out of range", z);
            //                 return 0;
            //             }
            // #endif

            float nearestDistance = float.PositiveInfinity;
            float distMetric;
            ushort nearestClassNode = 0;

            ushort node = c.nets.m_nodeGrid[(z * (int)TransferManager.REASON_CELL_SIZE_LARGE) + x];
            while (node != 0) {
                //                if (buildingID == 11691)
                //                {
                //                    Log._DebugFormat("Considering node #{0} of type {1} at dist=0", node, nets.m_nodes.m_buffer[node].Info.m_class.m_service);
                //                }

                if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == service1) {
                    distMetric = Vector3.SqrMagnitude(pos - c.nets.m_nodes.m_buffer[node].m_position);
                    if (distMetric < nearestDistance) {
                        nearestDistance = distMetric;
                        nearestClassNode = node;
                    }
                }
                node = c.nets.m_nodes.m_buffer[node].m_nextGridNode;
            }

            uint maxdist = 4;

            x = Math.Max(x, (int)maxdist);
            x = Math.Min(x, (int)TransferManager.REASON_CELL_SIZE_LARGE - 1 - (int)maxdist);
            z = Math.Max(z, (int)maxdist);
            z = Math.Min(z, (int)TransferManager.REASON_CELL_SIZE_LARGE - 1 - (int)maxdist);

            for (int dist = 1; dist <= maxdist && nearestClassNode == 0; ++dist) {
                for (int n = -dist; n <= dist; ++n) {
                    node = c.nets.m_nodeGrid[((z + dist) * (int)TransferManager.REASON_CELL_SIZE_LARGE) + x + n];

                    while (node != 0) {
                        //                        if (buildingID == 11691)
                        //                        {
                        //                            Log._DebugFormat("Considering node #{0} of type {1} at dist={2}", node, nets.m_nodes.m_buffer[node].Info.m_class.m_service, dist);
                        //                        }
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == service1) {
                            distMetric = Vector3.SqrMagnitude(pos - c.nets.m_nodes.m_buffer[node].m_position);
                            if (distMetric < nearestDistance) {
                                nearestDistance = distMetric;
                                nearestClassNode = node;
                            }
                        }

                        node = c.nets.m_nodes.m_buffer[node].m_nextGridNode;
                    }

                    node = c.nets.m_nodeGrid[((z - dist) * (int)TransferManager.REASON_CELL_SIZE_LARGE) + x + n];
                    while (node != 0) {
                        //                        if (buildingID == 11691)
                        //                        {
                        //                            Log._DebugFormat("Considering node #{0} of type {1} at dist={2}", node, nets.m_nodes.m_buffer[node].Info.m_class.m_service, dist);
                        //                        }
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == service1) {
                            distMetric = Vector3.SqrMagnitude(pos - c.nets.m_nodes.m_buffer[node].m_position);
                            if (distMetric < nearestDistance) {
                                nearestDistance = distMetric;
                                nearestClassNode = node;
                            }
                        }

                        node = c.nets.m_nodes.m_buffer[node].m_nextGridNode;
                    }

                    node = c.nets.m_nodeGrid[((z + n) * (int)TransferManager.REASON_CELL_SIZE_LARGE) + x - dist];
                    while (node != 0) {
                        //                        if (buildingID == 11691)
                        //                        {
                        //                            Log._DebugFormat("Considering node #{0} of type {1} at dist={2}", node, nets.m_nodes.m_buffer[node].Info.m_class.m_service, dist);
                        //                        }
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == service1) {
                            distMetric = Vector3.SqrMagnitude(pos - c.nets.m_nodes.m_buffer[node].m_position);
                            if (distMetric < nearestDistance) {
                                nearestDistance = distMetric;
                                nearestClassNode = node;
                            }
                        }

                        node = c.nets.m_nodes.m_buffer[node].m_nextGridNode;
                    }

                    node = c.nets.m_nodeGrid[((z + n) * (int)TransferManager.REASON_CELL_SIZE_LARGE) + x + dist];
                    while (node != 0) {
                        //                        if (buildingID == 11691)
                        //                        {
                        //                            Log._DebugFormat("Considering node #{0} of type {1} at dist={2}", node, nets.m_nodes.m_buffer[node].Info.m_class.m_service, dist);
                        //                        }
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == service1) {
                            distMetric = Vector3.SqrMagnitude(pos - c.nets.m_nodes.m_buffer[node].m_position);
                            if (distMetric < nearestDistance) {
                                nearestDistance = distMetric;
                                nearestClassNode = node;
                            }
                        }

                        node = c.nets.m_nodes.m_buffer[node].m_nextGridNode;
                    }
                }
            }
            return nearestClassNode;
        }

        public override void GetData(FastList<IDataContainer> data) {
            UpdateTransferManagerData();
            base.GetData(data);
            data.Add(new TransferManager.Data());
        }
        internal void SetupMilestones() {
            /* FIXME: Move milestone registration out of Start() and into the Simulation thread
             */
            try {
                m_documentation = MilestoneCollection.FindMilestone(TransferBrokerMod.DOCUMENTATION_READ_MILESTONE) as DocumentationMilestone;
                if (m_documentation == null) {

#if DEBUG
                    Log.Info($"{GetType().Name}.SetupMilestones() before SetActive(false)");
#endif
                    gameObject.SetActive(false);
#if DEBUG
                    Log.Info($"{GetType().Name}.SetupMilestones() after SetActive(false)");

                    var ztest = gameObject.AddComponent<MyMilestoneCollection>();
#endif
                    m_milestones = gameObject.AddComponent<MilestoneCollection>();
                    m_documentation = ScriptableObject.CreateInstance<DocumentationMilestone>();
                    m_documentation.SetValues(TransferBrokerMod.DOCUMENTATION_READ_MILESTONE, TransferBrokerMod.DOCUMENTATION_TITLE);
                    // m_documentation.SetParams(SourcingMod.DOCUMENTATION_READ_MILESTONE, SourcingMod.DOCUMENTATION_TITLE);
                    m_milestones.m_Milestones = new MilestoneInfo[] { m_documentation, };
#if DEBUG
                    ztest.m_Milestones = new MilestoneInfo[] { m_documentation, };
                    Log.Info($"{GetType().Name}.SetupMilestones() before SetActive(true) => name={m_documentation.name}, m_name={m_documentation.m_name}");
#endif
                    var data = m_documentation.GetData();
                    m_documentation.m_openUnlockPanel = true;

                    gameObject.SetActive(true);
#if DEBUG
                    Log.Info($"{GetType().Name}.SetupMilestones() after SetActive(true) dictsize={Singleton<UnlockManager>.instance.m_allMilestones.Count}");
                    // #endif
                    /* FIXME: Need to wait until MilestoneCollection() runs awake() before CheckDependencies() */
                    // Thread.Sleep(100);

                    if (data != null) {
                        Log.Info($"{GetType().Name}.SetupMilestones() check doc {data.m_progress} {data.m_passedCount} {data.m_isAssigned} {data}");
                    }
#endif
                    Assert.IsNotNull(MilestoneCollection.FindMilestone(TransferBrokerMod.DOCUMENTATION_READ_MILESTONE),
                        "Milestone should have been added");
                } else {
                    m_documentation.Load();
                }
            } 
            catch (Exception ex) {
                Log.Info($"Failed to Setup Milestones ({ex.Message})\n{ex.StackTrace}");
            }
        }

        internal static bool IsActivatorBuilding(ushort buildingID) {
            var prefabName = PrefabCollection<BuildingInfo>.PrefabName((uint)Singleton<BuildingManager>.instance.m_buildings.m_buffer[buildingID].Info.m_prefabDataIndex);
            var retval = prefabName == TransferBroker.ACTIVATION_BUILDING;
#if DEBUG
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (buildings[buildingID].Info.m_class.m_service == ItemClass.Service.Monument) {
                Log.Info($"Building #{buildingID} ('{prefabName}' as '{Singleton<BuildingManager>.instance.GetBuildingName(buildingID, InstanceID.Empty)}') size={buildings[buildingID].Info.m_size} constructionCost={buildings[buildingID].Info.GetConstructionCost()} upkeep={buildings[buildingID].Info.GetMaintenanceCost()} isActivatorBuilding={retval}");
            }
#endif
            return retval;
        }
        private bool IsTrafficCameraBuilding(ushort buildingID) {
            var retval = c.buildings.m_buildings.m_buffer[buildingID].Info.m_class.m_service == ACTIVATION_TRAFFIC_SERVICE &&
                c.buildings.m_buildings.m_buffer[buildingID].Info.m_class.m_level == ACTIVATION_TRAFFIC_LEVEL;
#if DEBUG
            if (c.buildings.m_buildings.m_buffer[buildingID].Info.m_class.m_service == ItemClass.Service.PoliceDepartment) {
                var IsOperationalTrafficCameraBuilding = c.buildings.GetBuildingName(buildingID, InstanceID.Empty) == ACTIVATION_TRAFFIC_NAME;
                var prefabName = PrefabCollection<BuildingInfo>.PrefabName((uint)c.buildings.m_buildings.m_buffer[buildingID].Info.m_prefabDataIndex);
                Log.Info($"Building #{buildingID} ('{prefabName}' as '{c.buildings.GetBuildingName(buildingID, InstanceID.Empty)}') is {c.buildings.m_buildings.m_buffer[buildingID].Info.m_class.m_service} {c.buildings.m_buildings.m_buffer[buildingID].Info.m_class.m_level} canOperateAsTrafficBuilding={retval} isOperationalTrafficBuilding={IsOperationalTrafficCameraBuilding}");
            }
#endif
            // was prefabName == ACTIVATION_TRAFFIC_PREFAB
            return retval;
        }
#if DEBUG && false
        private void ListSpecialBuildings() {
            int numPrefabs = PrefabCollection<BuildingInfo>.LoadedCount();
            string prefabName;
            BuildingInfo prefab;
            bool canBeCamera;

            Log.Info($"              Prefab Name              |    ServiceClass    | Can Be Activator | Can Be TOC");
            for (uint i = 0; i < numPrefabs; ++i) {
                prefab = PrefabCollection<BuildingInfo>.GetLoaded(i);
                if (prefab.m_class.m_service == ItemClass.Service.Monument || prefab.m_class.m_service == ItemClass.Service.PoliceDepartment | prefab.m_isCustomContent || true) {

                    prefabName = PrefabCollection<BuildingInfo>.PrefabName(i);
                    canBeCamera = prefab.m_class.m_service == ACTIVATION_TRAFFIC_SERVICE && prefab.m_class.m_level == ACTIVATION_TRAFFIC_LEVEL;
                    Log.Info($"{PrefabCollection<BuildingInfo>.PrefabName(i),-39}| {prefab.m_class.m_service,-18} | {prefabName == TransferBroker.ACTIVATION_BUILDING,-15} | {canBeCamera,-10} | {prefab.GetService()}");
                }
            }
        }
#endif
#if false
        public class Data : IDataContainer {

            TransferManager.Data upstreamData = new TransferManager.Data();
            TransferBroker broker;

            public Data(TransferBroker b) {
                Log._DebugFormat("TransferBroker.Data..ctor() upstream={0}", upstreamData == null ? "null" : "obj");
                broker = b;
            }

            private Data() {

            }

            ~Data() {
                Log._DebugFormat("TransferBroker.Data..dtor() upstream={0}", upstreamData == null ? "null" : "obj");
            }

            public void Serialize(DataSerializer s) {
                Log._DebugFormat("TransferBroker.Data.Serialize() upstream={0}", upstreamData == null ? "null" : "obj");
                broker.UpdateTransferManagerData();
                upstreamData.Serialize(s);
            }
            public void Deserialize(DataSerializer s) {
                Log._DebugFormat("TransferBroker.Data.Deserialize() upstream={0}", upstreamData == null ? "null" : "obj");
                upstreamData.Deserialize(s);
            }
            public void AfterDeserialize(DataSerializer s) {
                Log._DebugFormat("TransferBroker.Data.AfterDeserialize() upstream={0}", upstreamData == null ? "null" : "obj");
                upstreamData.AfterDeserialize(s);
            }
        }
#endif
    }
}