namespace TransferBroker.Manager.Impl {
    using System;
    using System.Reflection;
    using HarmonyLib;
    using ColossalFramework;
    using ColossalFramework.IO;
    using ColossalFramework.Math;
    using global::TransferBroker.API.Manager;
    using CSUtil.Commons;
    using Priority_Queue;
    using UnityEngine;
    using System.Threading;
    using System.Collections;
    using System.Collections.Generic;

    internal class MatchMaker {
        public enum MatchMode {
            SatisfyDemand = 1,
            SoakSupply,
            Rebalance,
            Imports,
            Exports,
        }

        public enum Algo {
            ANN = 0,
            Legacy = 1,
        }

        private Thread thread;
        private TransferBroker.Coordination c;
        private int myID;
#if DEBUG
        bool _debug;
#endif

        public const int ANN_PQ_SIZE = 8000;
        public const int NODE_SEGMENTS = 8;
        public const int EXPORT_THRESHOLD = 1;
        public const int IMPORT_THRESHOLD = 2;
        public const float CONNECTION_USE_COST = 1000f;

        /* FIXME: Make vj an OfferStruct to save a dereferrence in the inner loop */
        private readonly Offer vj = new Offer(0, 0);
        private readonly NearestObject[] nodeState = new NearestObject[NetManager.MAX_NODE_COUNT];
        private bool[] isHelicopterService = new bool[TransferManager.TRANSFER_OFFER_COUNT * TransferManager.TRANSFER_PRIORITY_COUNT];

        /* Cache of service nodes for current queries. Do not scan, it is not cleared */
        private ushort[] queryServiceNode = new ushort[TransferManager.TRANSFER_OFFER_COUNT * TransferManager.TRANSFER_PRIORITY_COUNT];

        private TransferBroker.MatchWork work = new TransferBroker.MatchWork { material = TransferManager.TransferReason.None };
        private struct PathConstraints {
            public NetInfo.LaneType laneTypes;
            public bool canUsePublicTransport;
//            public ItemClass.Service service1;
//            public ItemClass.Service service2;
            public int minQueryPrioritySatisfy;
            public int minQueryPrioritySoak;
            public int minObjPrioritySatisfy;
            public int minObjPrioritySoak;
        }
        private PathConstraints constraints = default(PathConstraints);

        /* Challenge mode randomness.
         * There are 2 parameters to the amount of chaos introduced in challenge mode:
         * 1. m_param1 is the probability between [0,1] that a particular segment will be affected.
         * 2. The magnitude of the effect is a pseudo-random amount between [0,m_param2 * CHAOS_FACTOR - 1]
         *
         * m_randomness is m_param1 normalized to NetManager.MAX_MAP_SEGMENTS. Storing it as a pre-computed
         * variable saves one float to int conversion and an integer multiplication inside the inner loop,
         * the segment Cost function
         *
         * m_chaosRange is the precomputed value (c.m_para2 * CHAOS_FACTOR)
         */
        private int m_randomness;
        private uint m_chaosRange;
        private const byte CHAOS_FACTOR = 10;

        /* FIXME: Space in the queue cannot theoretically be MAX_NODE_COUNT * numobjects
         * but more likely limited to num of nodes within a radius since each segment has a max len
         * thus the range of costs is limited
         * maybe num_cost_quanta * numObjects
         * Or better, quantum = size of PQ / numObjects *
         *
         * (max_cost / min_cost) * num_segments_at_node * numOffers = PQsize;
         * min_cost = PQSIze / (num_segments_at_node * numOffers * max_cost)
         *
         * min_cost = cost quantum
         */
        private readonly FastPriorityQueue<Offer> ANNPQ = new FastPriorityQueue<Offer>(ANN_PQ_SIZE);

        public class Offer : FastPriorityQueueNode {
            public ushort nnid;
            public ushort nodeID;

            public Offer(ushort oid, ushort n) {
                nnid = oid;
                nodeID = n;
            }
        }

        public struct NearestObject {
#if MAINTAIN_CONNECTIONS
            public bool hasConnections;
#endif
            public ushort nnid;
            public float nndistance;
            // public void NearestObject() { nndistance = float.PositiveInfinity; nnid = 0; }
        }

        public MatchMaker(TransferBroker.Coordination coordination, int i) {

            // owner = broker;
            c = coordination;
            myID = i + 1;
            thread = new Thread(Worker);
            thread.Name = "Matchmaker #" + myID;
            thread.Priority = SimulationManager.SIMULATION_PRIORITY;
            thread.Start();
            if (!thread.IsAlive) {
                CODebugBase<LogChannel>.Error(LogChannel.Core, $"Matchmaker thread #{myID} failed to start!");
            } else {
                Log.InfoFormat("Started Thread '{0}' {1}", thread.Name, Assembly.GetExecutingAssembly().GetName().Version);
            }

        }

        public void Join() {
#if DEBUG
            Log.InfoFormat("Matchmaker #{0}: Joining {1} state={2}", myID, Thread.CurrentThread.Name, thread.ThreadState);
#endif
            thread.Join();
#if DEBUG
            Log.InfoFormat("Matchmaker #{0}: Joined state={1}", myID, thread.ThreadState);
#endif
        }

        private void Worker() {
            try {

                while (true) {
                    lock (c.workQ) {
                        /* mark previous task as completed */
                        if (work.material != TransferManager.TransferReason.None) {
#if DEBUG || LABS || EXPERIMENTAL
                            if (c.missedOfferReports < TransferBroker.Coordination.MAX_MISSED_REPORTS && c.offerQueueFull[(int)work.material]) {
                                Log.InfoFormat("[WARNING] Accepting {0} offers again (missed {1}). Report {2}/{3}.",
                                    work.material,
                                    c.missedOFfers[(int)work.material],
                                    c.missedOfferReports,
                                    TransferBroker.Coordination.MAX_MISSED_REPORTS);
                                ++c.missedOfferReports;
                            }
                            c.missedOFfers[(int)work.material] = 0;
#endif
                            c.offerQueueFull[(int)work.material] = false;
                            c.m_workInProgress[(int)work.material] = TransferBroker.Coordination.WorkStatus.Idle;
                            lock (c.goingIdle_lock) {
                                if (--c.numBusyMaterials.value == 0) {
                                    if (c.workQ.Count == 0) {
                                        c.stopwatch.Stop();
                                        c.runTime = c.stopwatch.Elapsed;
                                        // Log.InfoFormat("MatchMaker #{0} going idle. runtime = {1} ms ({2} ticks) c.numBusyMaterials={3} {4}", myID, c.runTime.TotalMilliseconds, c.runTime.Ticks, c.numBusyMaterials.value, work.material);
                                        Monitor.PulseAll(c.goingIdle_lock);
                                    }
                                }
                            }
                        }

                        while (c.workQ.Count == 0) Monitor.Wait(c.workQ);
                        work = c.workQ.Dequeue();
                        if (work.material != TransferManager.TransferReason.None) {
                            lock (c.goingIdle_lock) {
                                ++c.numBusyMaterials.value;
                                // Log.InfoFormat("MatchMaker #{0} starting work c.numBusyMaterials={1} {2}", myID, c.numBusyMaterials.value, work.material);
                            }
                            c.m_workInProgress[(int)work.material] = TransferBroker.Coordination.WorkStatus.Busy;
                        }
                        if (work.material == c.config.cacheReason) {
                            CacheOffers();
                        }
                    }

#if DEBUG
                    Log.InfoFormat("MatchMaker #{0} got work: {1}", myID, work.material);
//                    Thread.Sleep(500);
#endif
                    if (work.material == TransferManager.TransferReason.None) {
                        break; // Quit the worker thread
                    } else {

                        MatchOffers();
#if DEBUG
                        Log.InfoFormat("MatchMaker #{0} finished work: {1}", myID, work.material);
#endif
                        TransferBrokerMod.Installed.broker.OnMatchmakerFinished(work.material);
                    }
                }
            }
            catch (Exception ex) {
                Log.Info($"Exception {ex.GetType().FullName} in Matchmaker #{myID} ({ex.Message})\n{ex.StackTrace}");
            }

            Log.InfoFormat("Quitting MatchMaker #{0} {1}", myID, Assembly.GetExecutingAssembly().GetName().Version);
        }

        private static bool IsWarehousedGood(TransferManager.TransferReason material) {
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

        public Func<ushort, NetInfo.Direction, float> SelectCostFn(TransferBroker.Coordination.MatchmakingConfig config) {
            return config.m_rndFrequency == 0 ?
                        (config.m_hasCameras ? CongestedTravelTime : UncongestedTravelTime) :
                        WarpedUncongestedTravelTime;
        }
#if DEBUG
        private void CheckOffers(TransferManager.TransferReason material,
                ref TransferManager.TransferOffer[] offers,
                ref ushort[] startAt,
                ref ushort[] endAt,
                bool outgoing) {
            HashSet<TransferManager.TransferOffer> set = new HashSet<TransferManager.TransferOffer>();
            Log._DebugFormat("{0}.CheckOffers({1} {2})", GetType().Name, material, outgoing ? "out" : "in");
            for (int priority = TransferManager.TRANSFER_PRIORITY_COUNT - 1; priority >= 0; --priority) {
                int tableNum = ((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                int tableOffset = tableNum * TransferManager.TRANSFER_OFFER_COUNT;
                //                Log._DebugFormat("endAt[priority]={0}", endAt[priority]);
                //                Log._DebugFormat("startAt[tableNum]={0}", startAt[tableNum]);
                for (int i = startAt[tableNum]; i != endAt[priority]; i = (i + 1) % TransferManager.TRANSFER_OFFER_COUNT) {
                    if (offers[tableOffset + i].m_object.Type == (InstanceType)0) {
                        Log.Info($"Invalid offer type: #{oid((ushort)priority, (ushort) i)} : {TransferBroker.OfferDescription(offers[tableOffset + i])}");
                    } else if (!set.Add(offers[tableOffset + i])) {
                        Log.Info($"Dupe Offer: {TransferBroker.OfferDescription(offers[tableOffset + i])}");
                    }
                }
            }
        }
#endif
        private void MatchOffers() {

#if DEBUG
            if (work.material == c.config.cacheReason) {
                _debug = true;
            } else {
                _debug = false;
            }

            try {
                ListOffers(work.material, ref c.m_outgoingOffers, ref c.m_outgoingReadAt, ref work.outgoingEndAt, true);
                ListOffers(work.material, ref c.m_incomingOffers, ref c.m_incomingReadAt, ref work.incomingEndAt, false);

            }
            catch (Exception ex) {
                Log.Info($"Failed to ListOffers ({ex.Message})\n{ex.StackTrace}");
            }

            CheckOffers(work.material, ref c.m_outgoingOffers, ref c.m_outgoingReadAt, ref work.outgoingEndAt, true);
            CheckOffers(work.material, ref c.m_incomingOffers, ref c.m_incomingReadAt, ref work.incomingEndAt, false);
#endif

            try {

            //            if (IsWarehousedGood(material)) {
                if (work.options.algo != Algo.Legacy) {
                    constraints = GetConstraints();
                    if (constraints.laneTypes == NetInfo.LaneType.None) {

                        /* Legacy match-making, but multi-threaded */
#if DEBUG
                        Log._DebugFormat("{0}.LegacyMatchOffers({1}) @{2}", GetType().Name, work.material.ToString(), TransferBrokerMod.Installed.threading.simulationFrame & 0x3ff);
#endif
                        MatchOffersLegacy();
                    } else {
                        Func<ushort, NetInfo.Direction, float> Cost = SelectCostFn(c.config);
#if DEBUG
                        Log._DebugFormat("{0}.MatchOffers({1}, {1})", GetType().Name, work.material.ToString(), Cost.Method.Name);
#endif

                        /* Precompute randomness and chaos amount */
                        m_randomness = (int)(c.config.m_rndFrequency * NetManager.MAX_MAP_SEGMENTS);
                        m_chaosRange = (uint)(c.config.m_rndAmplitude * CHAOS_FACTOR);

                        do {
                        } while (MatchAll(
                                c.m_outgoingOffers, c.m_outgoingReadAt, work.outgoingEndAt,
                                c.m_incomingOffers, c.m_incomingReadAt, work.incomingEndAt, MatchMode.SatisfyDemand, true, Cost));
                        //            }

                        do {
                            // #if DEBUG
                            //                 ListOffers(material, ref m_outgoingOffers, ref m_outgoingCount, true);
                            //                 ListOffers(material, ref m_incomingOffers, ref m_incomingCount, false);
                            // #endif
                        } while (MatchAll(
                        c.m_incomingOffers, c.m_incomingReadAt, work.incomingEndAt,
                        c.m_outgoingOffers, c.m_outgoingReadAt, work.outgoingEndAt, MatchMode.SoakSupply, false, Cost));

                        if (IsWarehousedGood(work.material)) {
                            do {
                                // #if DEBUG
                                //                         ListOffers(material, ref m_outgoingOffers, ref m_outgoingCount, true);
                                //                         ListOffers(material, ref m_incomingOffers, ref m_incomingCount, false);
                                // #endif
                            } while (MatchAll(
                            c.m_outgoingOffers, c.m_outgoingReadAt, work.outgoingEndAt,
                            c.m_incomingOffers, c.m_incomingReadAt, work.incomingEndAt, MatchMode.Rebalance, true, Cost));

                            do {
#if false
                            ListOffers(material, ref m_outgoingOffers, ref m_outgoingCount, true);
                            ListOffers(material, ref m_incomingOffers, ref m_incomingCount, false);
#endif
                            } while (MatchAll(
                            c.m_incomingOffers, c.m_incomingReadAt, work.incomingEndAt,
                            c.m_outgoingOffers, c.m_outgoingReadAt, work.outgoingEndAt, MatchMode.Imports, false, Cost));

                            /* TODO:
                             * Call OutsideConnectionsAI.AddConnectionOffers only only if excess/deficit goods exists.
                             */
                            //                    do {
                            //#if DEBUG
                            //                        ListOffers(material, ref m_outgoingOffers, ref m_outgoingCount, true);
                            //                        ListOffers(material, ref m_incomingOffers, ref m_incomingCount, false);
                            //#endif
                            //                    } while (MatchAll(material,
                            //                    ref m_outgoingOffers, ref m_outgoingCount,
                            //                    ref m_incomingOffers, ref m_incomingCount, MatchMode.Exports, true));
                        }
                    }
                } else {
                    MatchOffersLegacy();
                }
            }
            catch (Exception ex) {
                Log.Info($"Exception caught while matchmaking ({ex.GetType().Name}: {ex.Message})\n{ex.StackTrace}");
            }
            finally {
                work.incomingEndAt.CopyTo(c.m_incomingReadAt, ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT));
                work.outgoingEndAt.CopyTo(c.m_outgoingReadAt, ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT));
            }

            //for (int k = 0; k < TransferManager.TRANSFER_PRIORITY_COUNT; k++) {
            //    int material_prio_slot = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + k;
            //    c.m_incomingReadAt[material_prio_slot] = work.incomingEndAt[k];
            //    c.m_outgoingReadAt[material_prio_slot] = work.outgoingEndAt[k];
            //}

#if DEBUG
            Log._DebugFormat("{0}.MatchOffers({1}) - DONE", GetType().Name, work.material);
#endif
        }

        /* Copied from base game, AS-IS */
        public static float GetDistanceMultiplier(TransferManager.TransferReason material) {
            return material switch {
                TransferManager.TransferReason.Garbage => 5E-07f,
                TransferManager.TransferReason.Crime => 1E-05f,
                TransferManager.TransferReason.Sick => 1E-06f,
                TransferManager.TransferReason.Dead => 1E-05f,
                TransferManager.TransferReason.Worker0 => 1E-07f,
                TransferManager.TransferReason.Worker1 => 1E-07f,
                TransferManager.TransferReason.Worker2 => 1E-07f,
                TransferManager.TransferReason.Worker3 => 1E-07f,
                TransferManager.TransferReason.Student1 => 2E-07f,
                TransferManager.TransferReason.Student2 => 2E-07f,
                TransferManager.TransferReason.Student3 => 2E-07f,
                TransferManager.TransferReason.Fire => 1E-05f,
                TransferManager.TransferReason.Bus => 1E-05f,
                TransferManager.TransferReason.Oil => 1E-07f,
                TransferManager.TransferReason.Ore => 1E-07f,
                TransferManager.TransferReason.Logs => 1E-07f,
                TransferManager.TransferReason.Grain => 1E-07f,
                TransferManager.TransferReason.Goods => 1E-07f,
                TransferManager.TransferReason.PassengerTrain => 1E-05f,
                TransferManager.TransferReason.Coal => 1E-07f,
                TransferManager.TransferReason.Family0 => 1E-08f,
                TransferManager.TransferReason.Family1 => 1E-08f,
                TransferManager.TransferReason.Family2 => 1E-08f,
                TransferManager.TransferReason.Family3 => 1E-08f,
                TransferManager.TransferReason.Single0 => 1E-08f,
                TransferManager.TransferReason.Single1 => 1E-08f,
                TransferManager.TransferReason.Single2 => 1E-08f,
                TransferManager.TransferReason.Single3 => 1E-08f,
                TransferManager.TransferReason.PartnerYoung => 1E-08f,
                TransferManager.TransferReason.PartnerAdult => 1E-08f,
                TransferManager.TransferReason.Shopping => 2E-07f,
                TransferManager.TransferReason.Petrol => 1E-07f,
                TransferManager.TransferReason.Food => 1E-07f,
                TransferManager.TransferReason.LeaveCity0 => 1E-08f,
                TransferManager.TransferReason.LeaveCity1 => 1E-08f,
                TransferManager.TransferReason.LeaveCity2 => 1E-08f,
                TransferManager.TransferReason.Entertainment => 2E-07f,
                TransferManager.TransferReason.Lumber => 1E-07f,
                TransferManager.TransferReason.GarbageMove => 5E-07f,
                TransferManager.TransferReason.MetroTrain => 1E-05f,
                TransferManager.TransferReason.PassengerPlane => 1E-05f,
                TransferManager.TransferReason.PassengerShip => 1E-05f,
                TransferManager.TransferReason.DeadMove => 5E-07f,
                TransferManager.TransferReason.DummyCar => -1E-08f,
                TransferManager.TransferReason.DummyTrain => -1E-08f,
                TransferManager.TransferReason.DummyShip => -1E-08f,
                TransferManager.TransferReason.DummyPlane => -1E-08f,
                TransferManager.TransferReason.Single0B => 1E-08f,
                TransferManager.TransferReason.Single1B => 1E-08f,
                TransferManager.TransferReason.Single2B => 1E-08f,
                TransferManager.TransferReason.Single3B => 1E-08f,
                TransferManager.TransferReason.ShoppingB => 2E-07f,
                TransferManager.TransferReason.ShoppingC => 2E-07f,
                TransferManager.TransferReason.ShoppingD => 2E-07f,
                TransferManager.TransferReason.ShoppingE => 2E-07f,
                TransferManager.TransferReason.ShoppingF => 2E-07f,
                TransferManager.TransferReason.ShoppingG => 2E-07f,
                TransferManager.TransferReason.ShoppingH => 2E-07f,
                TransferManager.TransferReason.EntertainmentB => 2E-07f,
                TransferManager.TransferReason.EntertainmentC => 2E-07f,
                TransferManager.TransferReason.EntertainmentD => 2E-07f,
                TransferManager.TransferReason.Taxi => 1E-05f,
                TransferManager.TransferReason.CriminalMove => 5E-07f,
                TransferManager.TransferReason.Tram => 1E-05f,
                TransferManager.TransferReason.Snow => 5E-07f,
                TransferManager.TransferReason.SnowMove => 5E-07f,
                TransferManager.TransferReason.RoadMaintenance => 5E-07f,
                TransferManager.TransferReason.SickMove => 1E-07f,
                TransferManager.TransferReason.ForestFire => 1E-05f,
                TransferManager.TransferReason.Collapsed => 1E-05f,
                TransferManager.TransferReason.Collapsed2 => 1E-05f,
                TransferManager.TransferReason.Fire2 => 1E-05f,
                TransferManager.TransferReason.Sick2 => 1E-06f,
                TransferManager.TransferReason.FloodWater => 5E-07f,
                TransferManager.TransferReason.EvacuateA => 1E-05f,
                TransferManager.TransferReason.EvacuateB => 1E-05f,
                TransferManager.TransferReason.EvacuateC => 1E-05f,
                TransferManager.TransferReason.EvacuateD => 1E-05f,
                TransferManager.TransferReason.EvacuateVipA => 1E-05f,
                TransferManager.TransferReason.EvacuateVipB => 1E-05f,
                TransferManager.TransferReason.EvacuateVipC => 1E-05f,
                TransferManager.TransferReason.EvacuateVipD => 1E-05f,
                TransferManager.TransferReason.Ferry => 1E-05f,
                TransferManager.TransferReason.CableCar => 1E-05f,
                TransferManager.TransferReason.Blimp => 1E-05f,
                TransferManager.TransferReason.Monorail => 1E-05f,
                TransferManager.TransferReason.TouristBus => 1E-05f,
                TransferManager.TransferReason.ParkMaintenance => 5E-07f,
                TransferManager.TransferReason.TouristA => 2E-07f,
                TransferManager.TransferReason.TouristB => 2E-07f,
                TransferManager.TransferReason.TouristC => 2E-07f,
                TransferManager.TransferReason.TouristD => 2E-07f,
                TransferManager.TransferReason.Mail => 1E-05f,
                TransferManager.TransferReason.UnsortedMail => 5E-07f,
                TransferManager.TransferReason.SortedMail => 5E-07f,
                TransferManager.TransferReason.OutgoingMail => 5E-07f,
                TransferManager.TransferReason.IncomingMail => 5E-07f,
                TransferManager.TransferReason.AnimalProducts => 1E-07f,
                TransferManager.TransferReason.Flours => 1E-07f,
                TransferManager.TransferReason.Paper => 1E-07f,
                TransferManager.TransferReason.PlanedTimber => 1E-07f,
                TransferManager.TransferReason.Petroleum => 1E-07f,
                TransferManager.TransferReason.Plastics => 1E-07f,
                TransferManager.TransferReason.Glass => 1E-07f,
                TransferManager.TransferReason.Metals => 1E-07f,
                TransferManager.TransferReason.LuxuryProducts => 1E-07f,
                TransferManager.TransferReason.GarbageTransfer => 5E-07f,
                TransferManager.TransferReason.PassengerHelicopter => 1E-05f,
                TransferManager.TransferReason.Trolleybus => 1E-05f,
                TransferManager.TransferReason.Fish => 1E-05f,
                TransferManager.TransferReason.ElderCare => 1E-06f,
                TransferManager.TransferReason.ChildCare => 1E-06f,
                _ => 1E-07f,
            };
        }

        /* Copy of Vanilla TransferManager.MatchOffers() adapted for multi-threading
         */
        private void MatchOffersLegacy() {

            float distanceMultiplier = GetDistanceMultiplier(work.material);
            float serviceRadius = ((distanceMultiplier == 0f) ? 0f : (0.01f / distanceMultiplier));

            for (int priority = TransferManager.TRANSFER_PRIORITY_COUNT - 1; priority >= 0; priority--) {
#if DEBUG
                if (_debug) {
                    Log._DebugFormat("Legacy MatchMaker {0} prio={1}",
                        work.material,
                        priority);
                }
#endif
                // int prio_material = (int)material * 8 + priority;
                // int numInOffers = m_incomingCount[prio_material];
                // int numOutOffers = m_outgoingCount[prio_material];
                int tableNum = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                int tableOffset = (((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority) * TransferManager.TRANSFER_OFFER_COUNT;
                byte inOffer_i = (byte)c.m_incomingReadAt[tableNum];
                byte outOffer_i = (byte)c.m_outgoingReadAt[tableNum];
                byte inOffer_e = (byte)work.incomingEndAt[priority];
                byte outOffer_e = (byte)work.outgoingEndAt[priority];
                while (inOffer_i != inOffer_e || outOffer_i != outOffer_e) {
                    if (inOffer_i != inOffer_e) {
                        TransferManager.TransferOffer inOffer = c.m_incomingOffers[tableOffset + inOffer_i];
                        if (!inOffer.m_object.IsEmpty) {

                            Vector3 position = inOffer.Position;
                            int inAmount = inOffer.Amount;
                            do // While inOfferAmount lasts
                            {
                                int num9 = Mathf.Max(0, 2 - priority);
                                int num10 = ((!inOffer.Exclude) ? num9 : Mathf.Max(0, 3 - priority));
                                int num11 = -1;
                                int matching_outOffer_slot = -1;
                                float nearest = -1f;
                                float offerEuclidianDistance = -1f;
                                // byte num14 = outOffer_i;
                                for (int prio_cursor_o = priority; prio_cursor_o >= num9; prio_cursor_o--) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Legacy MatchMaker {0} prio_cursor_o={1}",
                                            work.material,
                                            prio_cursor_o);
                                    }
#endif
                                    int num16 = ((int)work.material * 8) + prio_cursor_o;
                                    int outOffer_match_i = work.outgoingEndAt[prio_cursor_o];
                                    float serviceArea = (float)prio_cursor_o + 0.1f;
                                    if (nearest >= serviceArea) {
                                        break;
                                    }
                                    for (byte i = (byte)c.m_outgoingReadAt[((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + prio_cursor_o]; i != outOffer_match_i; i++) {
                                        TransferManager.TransferOffer outOffer3 = c.m_outgoingOffers[(num16 * TransferManager.TRANSFER_OFFER_COUNT) + i];
#if DEBUG
                                        if (_debug) {
                                            Log._DebugFormat("Legacy MatchMaker {0} prio_cursor_o={1} out={2}",
                                                work.material,
                                                prio_cursor_o,
                                                oid((ushort)prio_cursor_o, i));
                                        }
#endif
                                        if (outOffer3.m_object.IsEmpty) {
                                            continue;
                                        }
                                        // Exlude> in0 = nogo, in1 out=0 ok, in2 out=0,1 ok; in3 out=0,1,2 ok; in3+ ok
                                        if (!(inOffer.m_object != outOffer3.m_object) || (outOffer3.Exclude && prio_cursor_o < num10)) {
                                            continue;
                                        }
                                        offerEuclidianDistance = Vector3.SqrMagnitude(outOffer3.Position - position);
                                        float proximity = (!(distanceMultiplier < 0f)) ?
                                            (serviceArea / (1f + (offerEuclidianDistance * distanceMultiplier))) :
                                            (serviceArea - (serviceArea / (1f - (offerEuclidianDistance * distanceMultiplier))));
#if DEBUG
                                        if (_debug) {
                                        Log._DebugFormat("Legacy MatchMaker {0} in={1} out={2} proximity={3} nearest={4} radius={5}",
                                            work.material,
                                            oid((ushort)priority, inOffer_i),
                                            oid((ushort)prio_cursor_o, i),
                                            proximity,
                                            nearest,
                                            serviceRadius);
                                        }
#endif

                                        if (proximity > nearest) {
                                            num11 = prio_cursor_o;
                                            matching_outOffer_slot = i;
                                            nearest = proximity;
                                            if (offerEuclidianDistance < serviceRadius) {
                                                break;
                                            }
                                        }
                                    }
                                    // num14 = 0;
                                }
                                if (num11 == -1) {
                                    break;
                                }
                                int out_material_prio = (int)work.material * 8 + num11;
                                TransferManager.TransferOffer outMatchedOffer = c.m_outgoingOffers[(out_material_prio * TransferManager.TRANSFER_OFFER_COUNT) + matching_outOffer_slot];
                                int outAmount = outMatchedOffer.Amount;
                                int transferAmount = Mathf.Min(inAmount, outAmount);
                                if (transferAmount != 0) {
                                    StartTransfer(oid((ushort)num11, (ushort)matching_outOffer_slot), oid((ushort)priority, (ushort)inOffer_i), outMatchedOffer, inOffer, transferAmount, Mathf.Sqrt(offerEuclidianDistance) / 1000);
                                }

                                inAmount -= transferAmount;
                                outAmount -= transferAmount;

                                if (outAmount == 0) {
                                    c.m_outgoingOffers[(out_material_prio * TransferManager.TRANSFER_OFFER_COUNT) + matching_outOffer_slot] = default(TransferManager.TransferOffer);
                                } else {
                                    c.m_outgoingOffers[(out_material_prio * TransferManager.TRANSFER_OFFER_COUNT) + matching_outOffer_slot].Amount = outAmount;
                                }

                                inOffer.Amount = inAmount;
                            }

                            while (inAmount != 0);
                            if (inAmount == 0) {
                                // Remove depleted incomingOffer
                                c.m_incomingOffers[tableOffset + inOffer_i] = default(TransferManager.TransferOffer);
                            } else { // Adjust inOffer with remaining unused amount
                                c.m_incomingOffers[tableOffset + inOffer_i].Amount = inAmount;
                            }
                        }
                        inOffer_i++;
                    }
                    if (outOffer_i == outOffer_e) { // When no outOffers remain at this priority, go to next inOffer
                        continue;
                    }

                    TransferManager.TransferOffer outOffer2 = c.m_outgoingOffers[tableOffset + outOffer_i];
                    if (!outOffer2.m_object.IsEmpty) {
                        Vector3 position2 = outOffer2.Position;
                        int outAmount2 = outOffer2.Amount;
                        // Fill remaining outOffers with lower priority inOffers
                        do {
                            int num25 = Mathf.Max(0, 2 - priority);
                            int num26 = ((!outOffer2.Exclude) ? num25 : Mathf.Max(0, 3 - priority));
                            int matchPrio = -1;
                            int num28 = -1;
                            float nearest = -1f;
                            // int num30 = inOffer_i;
                            byte scan_start = inOffer_i;
                            float offerEuclidianDistance = -1f;
                            for (int prio_cursor_i = priority; prio_cursor_i >= num25; prio_cursor_i--) {
#if DEBUG
                                if (_debug) {
                                    Log._DebugFormat("Legacy MatchMaker {0} prio_cursor_i={1}",
                                        work.material,
                                        prio_cursor_i);
                                }
#endif
                                int num32 = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + prio_cursor_i;
                                // byte scan_start = inOffer_i;
                                //int num33 = m_incomingCount[num32];
                                float serviceArea = (float)prio_cursor_i + 0.1f;
                                if (nearest >= serviceArea) {
                                    break;
                                }
                                //for (byte i = scan_start; i != ; i++) {
                                for (byte i = scan_start; i != work.incomingEndAt[prio_cursor_i]; ++i) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Legacy MatchMaker {0} prio_cursor_i={1} in={2}",
                                            work.material,
                                            prio_cursor_i,
                                            oid((ushort)prio_cursor_i, i));
                                    }
#endif
                                    TransferManager.TransferOffer inOffer2 = c.m_incomingOffers[(num32 * TransferManager.TRANSFER_OFFER_COUNT) + i];
                                    if (inOffer2.m_object.IsEmpty) {
                                        continue;
                                    }
                                    if (!(outOffer2.m_object != inOffer2.m_object) || (inOffer2.Exclude && prio_cursor_i < num26)) {
                                        continue;
                                    }
                                    offerEuclidianDistance = Vector3.SqrMagnitude(inOffer2.Position - position2);
                                    float proximity = (!(distanceMultiplier < 0f)) ?
                                        (serviceArea / (1f + (offerEuclidianDistance * distanceMultiplier))) :
                                        (serviceArea - (serviceArea / (1f - (offerEuclidianDistance * distanceMultiplier))));
#if DEBUG
                                    if (_debug) {
                                            Log._DebugFormat("Legacy MatchMaker {0} out={1} in={2} proximity={3} nearest={4} radius={5}",
                                            work.material,
                                            oid((ushort)priority, outOffer_i),
                                            oid((ushort)prio_cursor_i, i),
                                            proximity,
                                            nearest,
                                            serviceRadius);
                                    }
#endif

                                    if (proximity > nearest) {
                                        matchPrio = prio_cursor_i;
                                        num28 = i;
                                        nearest = proximity;
                                        if (offerEuclidianDistance < serviceRadius) {
                                            break;
                                        }
                                    }
                                }
                                if (prio_cursor_i != num25) {
                                    scan_start = (byte)c.m_incomingReadAt[((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + prio_cursor_i - 1];
                                }
                            }

                            if (matchPrio == -1) {
                                break;
                            }
                            int num37 = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + matchPrio;
                            TransferManager.TransferOffer inOffer = c.m_incomingOffers[(num37 * TransferManager.TRANSFER_OFFER_COUNT) + num28];
                            int inAmount = inOffer.Amount;
                            int xferAmount = Mathf.Min(outAmount2, inAmount);
                            if (xferAmount != 0) {
#if DEBUG
                                if (_debug) {
                                    Log._DebugFormat("Legacy MatchMaker {0} TRANSFER in={1} out={2}, amount={3}",
                                        work.material,
                                        oid((ushort)matchPrio, (ushort)num28),
                                        oid((ushort)priority, outOffer_i),
                                        xferAmount);
                                }
#endif
                                StartTransfer(oid((ushort)priority, outOffer_i), oid((ushort)matchPrio, (ushort)num28), outOffer2, inOffer, xferAmount, Mathf.Sqrt(offerEuclidianDistance) / 1000);
                            }
#if DEBUG
                            if (_debug) {
                                if (inAmount == 0) {
                                    Log._DebugFormat("Legacy MatchMaker {0} SKIP in={1} because amount={2}",
                                        work.material,
                                        oid((ushort)matchPrio, (ushort)num28),
                                        inAmount);
                                }
                                if (outAmount2 == 0) {
                                    Log._DebugFormat("Legacy MatchMaker {0} SKIP out={1} because amount={2}",
                                        work.material,
                                        oid((ushort)priority, (ushort)outOffer_i),
                                        inAmount);
                                }
                            }
#endif
                            outAmount2 -= xferAmount;
                            inAmount -= xferAmount;
                            // Remove depleted inOffer or adjust it
                            if (inAmount == 0) // Remove or Adjust inOffer
                            { // Remove inOffer
                                c.m_incomingOffers[(num37 * TransferManager.TRANSFER_OFFER_COUNT) + num28] = default(TransferManager.TransferOffer);
                            } else {
                                c.m_incomingOffers[(num37 * TransferManager.TRANSFER_OFFER_COUNT) + num28].Amount = inAmount;
                            }
                            outOffer2.Amount = outAmount2;
                        }
                        while (outAmount2 != 0);
                        if (outAmount2 == 0) {
                            c.m_outgoingOffers[tableOffset + outOffer_i] = default(TransferManager.TransferOffer);
                        } else {
                            c.m_outgoingOffers[tableOffset + outOffer_i].Amount = outAmount2;
                        }
                    }
                    outOffer_i++;
                }
            }
        }

#if DEBUG
        private void ListOffers(TransferManager.TransferReason material,
            ref TransferManager.TransferOffer[] offers,
            ref ushort[] startAt,
            ref ushort[] endAt,
            bool outgoing) {
            Log._DebugFormat("{0}.ListOffers({1} {2})", GetType().Name, material, outgoing ? "out" : "in");
            for (int priority = TransferManager.TRANSFER_PRIORITY_COUNT - 1; priority >= 0; --priority) {
                int tableNum = ((int)material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                int tableOffset = tableNum * TransferManager.TRANSFER_OFFER_COUNT;
                // Log._DebugFormat("Entries in prio {0} table: {1}..{2} tableNum={3}", priority, startAt[tableNum], endAt[priority], tableNum);
                for (int i = startAt[tableNum]; i != endAt[priority]; i = (i + 1) % TransferManager.TRANSFER_OFFER_COUNT) {

                    // Log._DebugFormat("{0} #{1} type {2}", outgoing ? "Out" : "In", oid((ushort)priority, (ushort)i), offers[tableOffset + i].m_object.Type);
                    TransferBroker.LogOffer(outgoing ? "Out" : "In", oid((ushort)priority, (ushort)i), material, offers[tableOffset + i]);
                }
            }
        }
#endif
        private void CacheOffers() {
            TransferBroker.TransferWork entry = new TransferBroker.TransferWork();
            entry.material = c.config.cacheReason;

            c.resultsCache.Clear();

            for (int priority = 0; priority < TransferManager.TRANSFER_PRIORITY_COUNT; ++priority) {
                int tableNum = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                int tableOffset = tableNum * TransferManager.TRANSFER_OFFER_COUNT;

                for (int i = c.m_outgoingReadAt[tableNum]; i != work.outgoingEndAt[priority]; i = (i + 1) % TransferManager.TRANSFER_OFFER_COUNT) {
                    entry.offerOut = c.m_outgoingOffers[tableOffset + i];
                    c.resultsCache[entry.offerOut.m_object] = entry;
//                    Log.Info($"{i}/{priority} Cache 1 from {entry.offerOut.m_object.Type} {entry.offerOut.m_object.Building}");
                }

            }
            entry.offerOut = default(TransferManager.TransferOffer);

//            foreach (var key in c.resultsCache.Keys) {
//                Log.Info($"A Cache db: {key.Building} = {c.resultsCache[key].offerIn.Building} {c.resultsCache[key].offerOut.Building}");
//            }

            for (int priority = 0; priority < TransferManager.TRANSFER_PRIORITY_COUNT; ++priority) {
                int tableNum = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                int tableOffset = tableNum * TransferManager.TRANSFER_OFFER_COUNT;

                for (int i = c.m_incomingReadAt[tableNum]; i != work.incomingEndAt[priority]; i = (i + 1) % TransferManager.TRANSFER_OFFER_COUNT) {
                    if (c.resultsCache.TryGetValue(c.m_incomingOffers[tableOffset + i].m_object, out var existingEntry)) {
                        if (existingEntry.offerIn.m_object.IsEmpty) {
                            existingEntry.offerIn = c.m_incomingOffers[tableOffset + i];
                            c.resultsCache[existingEntry.offerOut.m_object] = existingEntry;

//                            Log.Info($"{i}/{priority} Cache 3 to {existingEntry.offerIn.m_object.Type} {existingEntry.offerIn.m_object.Building} from {existingEntry.offerOut.m_object.Type} {existingEntry.offerOut.m_object.Building}");
                        }
                    } else {
                        entry.offerIn = c.m_incomingOffers[tableOffset + i];
                        c.resultsCache[entry.offerIn.m_object] = entry;

//                        Log.Info($"{i}/{priority} Cache 2 to {entry.offerIn.m_object.Type} {entry.offerIn.m_object.Building}");
                    }
                }
            }

//            foreach(var key in c.resultsCache.Keys) {
//                Log.Info($"B Cache db: {key.Building} = {c.resultsCache[key].offerIn.Building} {c.resultsCache[key].offerOut.Building}");
//            }
        }

        private float UncongestedTravelTime(ushort segmentID, NetInfo.Direction direction) {
            var segment = c.nets.m_segments.m_buffer[segmentID];
            for (int i = segment.Info.m_lanes.Length - 1; i >= 0; --i) {
                if ((segment.Info.m_lanes[i].m_laneType & constraints.laneTypes) != 0 && (segment.Info.m_lanes[i].m_finalDirection & direction) == direction) {
                    return segment.m_averageLength / segment.Info.m_lanes[i].m_speedLimit;
                }
            }
            return 0f;
        }

        /* The Congested Travel Time is the travel time along a segment, weighted by
         * the current congestion on that segment.
         * The m_trafficDensity metric is used, de-emphasized by a CONGESTION_SENSITIVITY
         * constant, picked arbitrarily.
         * FIXME: Tune CONGESTION_SENSITIVITY based on some reaason
         */
        private float CongestedTravelTime(ushort segmentID, NetInfo.Direction direction) {
            var segment = c.nets.m_segments.m_buffer[segmentID];
            const int CONGESTION_SENSITIVITY = 10;
            for (int i = segment.Info.m_lanes.Length - 1; i >= 0; --i) {
                if ((segment.Info.m_lanes[i].m_laneType & constraints.laneTypes) != 0 && (segment.Info.m_lanes[i].m_finalDirection & direction) == direction) {
                    return (segment.m_averageLength + segment.m_trafficDensity + CONGESTION_SENSITIVITY) / segment.Info.m_lanes[i].m_speedLimit;
                }
            }

            return 0f;
        }

        private float WarpedUncongestedTravelTime(ushort segmentID, NetInfo.Direction direction) {
            /* The Warped Cost function is used when the player opts for the extra
             * challenge mode, by specifying a Theta to their activator building. That
             * challenge is implemented in this function.
             *
             * Add some chaos to the path cost calculation, to force matchmaking to pick
             * unexpected pairs. There are two variables that affect the chaos, given
             * by the cartesian coordinates on a unit circle, where the player controls
             * the Theta angle.
             *
             * The x coordinate is m_param1, and y is m_param2.
             *
             * The set of affected segments should change completely when x changes.
             * The size of the set is a proportion of the total set of segments, given by
             * the magnitude of x.
             * The amount of chaos inflicted on the affected nodes varies pseudorandomly
             * in the range [0,y).
             *
             * The player is rewarded with low upkeep cost when maximizing y, but, at y=1
             * the set of affected segments is 50% of all segments. Any change in Theta
             * will cause the chaos to be completely redistributed (a different set of ~50%
             * will be be affected when x changes). So, only the most resilient city designs
             * will be able to cope with small Theta changes around the minimum upkeep value.
             * Newly placed segments may be very affected by the large chaos factor at the
             * min upkeep point. It should be utter mayhem.
             *
             * On the other hand, observant players will have figured out how to have their
             * pi and eat it too, so I need not explain it here.
             */
            Randomizer randomizer = new Randomizer(m_randomness * segmentID);
            bool warpEnable = randomizer.UInt32(NetManager.MAX_MAP_SEGMENTS) < m_randomness;

            var segment = c.nets.m_segments.m_buffer[segmentID];
            for (int i = segment.Info.m_lanes.Length - 1; i >= 0; --i) {
                if ((segment.Info.m_lanes[i].m_laneType & constraints.laneTypes) != 0 && (segment.Info.m_lanes[i].m_finalDirection & direction) == direction) {
                    return (warpEnable ? randomizer.UInt32(1, m_chaosRange) : 1) * segment.m_averageLength / segment.Info.m_lanes[i].m_speedLimit;
                }
            }

            return 0f;
        }

        private ushort oid(ushort priority, ushort slot) {
            return (ushort)(slot + (priority * TransferManager.TRANSFER_OFFER_COUNT) + 1);
        }
        private uint oid2index(ushort o) {
            return ((uint)work.material * (TransferManager.TRANSFER_PRIORITY_COUNT * TransferManager.TRANSFER_OFFER_COUNT)) + o - 1;
        }

        private ushort CountOffers(ref ushort[] startAt, ref ushort[] endAt, int minPriority, int maxPriority) {
            ushort count = 0;

            for (int priority = maxPriority; priority >= minPriority; --priority) {
                int prio_material = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                count += (ushort)((endAt[priority] - startAt[prio_material]) % TransferManager.TRANSFER_OFFER_COUNT);
            }
            return count;
        }
        private int StartTransfer(ushort outOid, ushort inOid, TransferManager.TransferOffer outOffer, TransferManager.TransferOffer inOffer, int amount, float cost) {
            bool haveDeferals = false;
            return StartTransfer(outOid, inOid, cost, MatchMode.SatisfyDemand, ref haveDeferals);
            }

        private int StartTransfer(ushort outOid, ushort inOid, float cost, MatchMode mode, ref bool haveDeferals) {
            uint i, o;

            o = oid2index(outOid);
            i = oid2index(inOid);

            if (!c.m_outgoingOffers[o].m_object.IsEmpty && !c.m_incomingOffers[i].m_object.IsEmpty) {
                var amount = Mathf.Min(c.m_outgoingOffers[o].Amount, c.m_incomingOffers[i].Amount);
#if DEBUG
                if (_debug) {
                    Log._DebugFormat("StartTransfer({0}, {1}/{2}/{3} -> {4}/{5}/{6} ie, {7}->{8}) - started; cost={9:0.0}{10} amount={11}",
                    work.material,
                    outOid,
                    c.m_outgoingOffers[o].Active,
                    c.m_outgoingOffers[o].Exclude,
                    inOid,
                    c.m_incomingOffers[i].Active,
                    c.m_incomingOffers[i].Exclude,
                    TransferBroker.InstanceName(c.m_outgoingOffers[o].m_object),
                    TransferBroker.InstanceName(c.m_incomingOffers[i].m_object),
                    cost,
                    constraints.laneTypes == NetInfo.LaneType.None ? "km" : "d",
                    amount);
                }
#endif
                lock (c.transferQ_lock) {
                    /* FIXME: Implement one transferQ per matchmaker so the transferQ does not need to get locked per transfer
                     * and dispatch Start Transfer when all the work is done (less locking/unlocking)
                     */
                    if (c.owner.transferQ.Count == 0) {
                       c.sim.m_ThreadingWrapper.QueueSimulationThread(new Action(c.owner.StartTansfers));
                    }

                    TransferBroker.TransferWork job = new TransferBroker.TransferWork {
                        material = work.material,
                        offerOut = c.m_outgoingOffers[o],
                        offerIn = c.m_incomingOffers[i],
                        amount = amount, };
                    c.owner.transferQ.Enqueue(job);

                    if (c.config.cacheReason == work.material) {
                        /* Avoid overwriting, to preserve warehouses, which
                         * have both in and out offers pointing to themselves. This
                         * is used to color them specially.
                         * 
                         * Non-warehouses get their second slot filled in.
                         * 
                         * This logic also only costs 1 or 2 dict searches per
                         * job, not more
                         */
#if false
                        if (c.resultsCache.TryGetValue(job.offerIn.m_object, out var existingEntry)) {
                            Log.Info($"Overwrite 1 Cache {job.offerIn.m_object.Type} {job.offerIn.m_object.Building}");
                            if (existingEntry.offerOut.m_object == InstanceID.Empty) {
                                c.resultsCache[job.offerIn.m_object] = job;
                            } else if (existingEntry.offerIn.m_object == InstanceID.Empty) {
                                c.resultsCache[job.offerIn.m_object] = job;
                            }
                        } else {
                            Log.Info($"Overwrite 2 Cache {job.offerIn.m_object.Type} {job.offerIn.m_object.Building}");
                            c.resultsCache[job.offerIn.m_object] = job;
                        }
#endif
                        c.resultsCache[job.offerIn.m_object] = job;
                        c.resultsCache[job.offerOut.m_object] = job;

                    }

                }

                if (amount == c.m_outgoingOffers[o].Amount) {
                    c.m_outgoingOffers[o] = default(TransferManager.TransferOffer);
                } else {
                    c.m_outgoingOffers[o].Amount -= amount;
                }
                if (amount == c.m_incomingOffers[i].Amount) {
                    c.m_incomingOffers[i] = default(TransferManager.TransferOffer);
                } else {
                    c.m_incomingOffers[i].Amount -= amount;
                }

                return amount;
            } else {
                haveDeferals = true;
#if DEBUG
                if (_debug) {
                    Log._DebugFormat("StartTransfer skipped ({0}, {1}/empty={2} -> {3}/empty={4} cost={5}",
                        work.material,
                        outOid,
                        c.m_outgoingOffers[o].m_object.IsEmpty,
                        inOid,
                        c.m_incomingOffers[i].m_object.IsEmpty,
                        cost);
                }
#endif
            }

            return 0;
        }

        /* FIXME: This looks up m_nodeGrid from the Simulation thread - need locking ?*/
        /* FIXME: This looks up m_nodeGrid from the Simulation thread - need locking ?*/
        /* FIXME: This looks up m_nodeGrid from the Simulation thread - need locking ?*/
        /* FIXME: This looks up m_nodeGrid from the Simulation thread - need locking ?*/
        /* FIXME: This looks up m_nodeGrid from the Simulation thread - need locking ?*/
        /* FIXME: This looks up m_nodeGrid from the Simulation thread - need locking ?*/
        /* FIXME: This looks up m_nodeGrid from the Simulation thread - need locking ?*/
        public ushort FindNearestServiceNode(Vector3 pos, ItemClass itemClass = null) {
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

            ItemClass.Service wantService;
            ItemClass.SubService wantSubService;
            ushort node = c.nets.m_nodeGrid[(z * (int)TransferManager.REASON_CELL_SIZE_LARGE) + x];

            if (itemClass?.m_service == ItemClass.Service.PublicTransport) {
                wantService = ItemClass.Service.PublicTransport;
                wantSubService = itemClass.m_subService;
            } else {
                wantService = ItemClass.Service.Road;
                wantSubService = ItemClass.SubService.None;
            }
            while (node != 0) {
                //                if (buildingID == 11691)
                //                {
                //                    Log._DebugFormat("Considering node #{0} of type {1} at dist=0", node, nets.m_nodes.m_buffer[node].Info.m_class.m_service);
                //                }

                if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == wantService &&
                    c.nets.m_nodes.m_buffer[node].Info.m_class.m_subService == wantSubService) {
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
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == wantService &&
                            c.nets.m_nodes.m_buffer[node].Info.m_class.m_subService == wantSubService) {
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
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == wantService &&
                            c.nets.m_nodes.m_buffer[node].Info.m_class.m_subService == wantSubService) {
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
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == wantService &&
                            c.nets.m_nodes.m_buffer[node].Info.m_class.m_subService == wantSubService) {
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
                        if (c.nets.m_nodes.m_buffer[node].Info.m_class.m_service == wantService &&
                            c.nets.m_nodes.m_buffer[node].Info.m_class.m_subService == wantSubService) {
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
        public ushort FindNearestServiceNode(Vector3 pos, ItemClass.Service service1, NetInfo.LaneType laneType, VehicleInfo.VehicleType vehicleType) {
            return 0;
        }


        private Vector3 PositionOf(TransferManager.TransferOffer offer) {
            UnityEngine.Vector3 startPos;
            switch (offer.m_object.Type) {
                case InstanceType.Building:
                    startPos = c.buildings.m_buildings.m_buffer[offer.Building].m_position;
                    break;
                case InstanceType.Vehicle:
#if VEHICLES_SCHEDULED_FROM_HOMEBASE
                    InstanceID ownerID = c.vehicles.m_vehicles.m_buffer[offer.Vehicle].Info.m_vehicleAI.GetOwnerID(offer.Vehicle, ref Singleton<VehicleManager>.instance.m_vehicles.m_buffer[offer.Vehicle]);
                    switch (ownerID.Type) {
                        case InstanceType.Building:
                            startPos = c.buildings.m_buildings.m_buffer[ownerID.Building].m_position;
                            break;
                        default:
                            startPos = c.vehicles.m_vehicles.m_buffer[offer.Vehicle].GetLastFramePosition();
                            break;
                    }
#else
                    startPos = c.vehicles.m_vehicles.m_buffer[offer.Vehicle].GetLastFramePosition();
#endif
                    break;
                //case InstanceType.Citizen:
                //    startPos = citizens.m_citizens.m_buffer[offer.Citizen].CurrentLocation();
                default:
                    startPos = offer.Position;
                    break;
            }
            return startPos;
        }

        /* Return true if more work remains */
        private bool MatchAll(
            TransferManager.TransferOffer[] objects,
            ushort[] objectsStartAt,
            ushort[] objectsEndAt,

            TransferManager.TransferOffer[] queries,
            ushort[] queriesStartAt,
            ushort[] queriesEndAt,
            MatchMode mode,
            bool objectIsOutgoing,
            Func<ushort, NetInfo.Direction, float> Cost
            ) {
            // int numNodes = Singleton<NetManager>.instance.m_nodeCount;

            /*
             * In SatisfyDemand mode, Warehouses are suppliers (queries)
             * In SoakSupply mode, Warehouses are demand (objects)
             */

            // min_distance should be the quantum. It serves to tell the difference between
            // an adjacent object and a query node
            const float MIN_DISTANCE = 0.125f;

            ref var nodes = ref c.nets.m_nodes.m_buffer;
            ref var segments = ref c.nets.m_segments.m_buffer;
#if DEBUG
            if (_debug) {
                Log._DebugFormat("Match offers .................... {0} {1} ....................", mode, work.material);
            }
#endif

            int minQueryPriority;
            int maxQueryPriority;
            switch (mode) {
                case MatchMode.Rebalance:
                    minQueryPriority = 0;
                    maxQueryPriority = 1; // Only send excess to nearly empty warehouses (from nearly full), willing takers and export
                    break;
                case MatchMode.SatisfyDemand:
                    minQueryPriority = constraints.minQueryPrioritySatisfy;
                    maxQueryPriority = TransferManager.TRANSFER_PRIORITY_COUNT - 1;
                    break;
                case MatchMode.SoakSupply:
                    minQueryPriority = constraints.minQueryPrioritySoak;
                    maxQueryPriority = TransferManager.TRANSFER_PRIORITY_COUNT - 1;
                    break;
                case MatchMode.Imports:
                    minQueryPriority = 0;
                    maxQueryPriority = 0;
                    break;
                default:
                    minQueryPriority = 0;
                    maxQueryPriority = TransferManager.TRANSFER_PRIORITY_COUNT - 1;
                    break;
            }

            /* Each object other than nearest has an incremental cost, decreases as better paths are found */
            int totalQueries = CountOffers(ref queriesStartAt, ref queriesEndAt, minQueryPriority, maxQueryPriority);
#if DEBUG
            if (_debug) {
                Log._DebugFormat("Match offers ... {0} numQueries={1} prio={2}..{3}", mode, totalQueries, minQueryPriority, maxQueryPriority);
            }
#endif

            if (totalQueries == 0) return false;

            // minObjPriority is efficient as fewer tables are scanned, but it assumes
            // All importers are priority 0. It should be "importer.priority + 2" but
            // the ANN algo does not know importer.priority until startTransfer.
            // If importers can ever have prio > 0, either implement the condition,
            // or set minObjPriority=0 unconditionally, and compare priorities in
            // startTransfer.
            int minObjPriority;
            switch (mode) {
                case MatchMode.Rebalance:
                    minObjPriority = 2; // From nearly full warehouses
                    break;
                case MatchMode.Exports:
                    minObjPriority = EXPORT_THRESHOLD + 1; // From  "While inOfferAmount lasts", modified to 1
                    break;
                case MatchMode.SatisfyDemand:
                    minObjPriority = constraints.minObjPrioritySatisfy;
                    break;
                case MatchMode.Imports:
                    minObjPriority = IMPORT_THRESHOLD;
                    break;
                case MatchMode.SoakSupply:
                    minObjPriority = constraints.minObjPrioritySoak;
                    break;
                default:
                    minObjPriority = 0;
                    break;
            }

            int totalObjects = CountOffers(ref objectsStartAt, ref objectsEndAt, minObjPriority, TransferManager.TRANSFER_PRIORITY_COUNT - 1);
#if DEBUG
            if (_debug) {
                Log._DebugFormat("Match offers ... {0} numObjects={1} prio={2}+", mode, totalObjects, minObjPriority);
            }
#endif

            if (totalObjects == 0) return false;

            // http://renata.borovica-gajic.com/data/DASFAA2018_ANN.pdf
            // Step 7. Initialize an array N with size |V|
            // Each vertex has a PQ for all the possible offers
            // NearestObject[] N = InitN<NearestObject>(NetManager.MAX_NODE_COUNT);
            // var N = new NearestObject[NetManager.MAX_NODE_COUNT];
            //            for (int i = 0; i < NetManager.MAX_NODE_COUNT; i++) {
            //                if (N[i].nnid != 0) {
            //                    throw new Exception($"NNID is not zero N{i}.nnid={N[i].nnid}");
            //                }
            //                if (N[i].nndistance != 0f) {
            //                    throw new Exception($"NNDISTANCE is not zero N{i}.nndistance={N[i].nndistance}");
            //                }
            //            }

            bool haveDeferals = false;
            int objAmount = 0;
            try {
                for (int priority = maxQueryPriority; totalQueries != 0 && priority >= minQueryPriority; --priority) {
                    int prio_material = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                    int tableOffset = prio_material * TransferManager.TRANSFER_OFFER_COUNT;

                    // Step x (Generalized for ANN queries without pre-computation (3.3)
                    for (int i = queriesStartAt[prio_material]; i != queriesEndAt[priority]; i = (i + 1) % TransferManager.TRANSFER_OFFER_COUNT) {
                        if (queries[tableOffset + i].m_object.IsEmpty) {
#if DEBUG
                            if (_debug) {
                                Log._DebugFormat("Skip Empty prio={0}, query={1} building={2} @({3}/{4})",
                                priority,
                                oid((ushort)priority, (ushort)i),
                                queries[tableOffset + i].Building,
                                queries[tableOffset + i].Position,
                                c.buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty));
                            }

#endif
                            --totalQueries;
                            continue;
                        }
                        switch (mode) {
                            case MatchMode.Imports:
                                if ((c.buildings.m_buildings.m_buffer[queries[tableOffset + i].Building].m_flags & Building.Flags.Outgoing) == 0) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Skip Non-Exporter prio={0}, query={1} building={2} @({3}/{4})",
                                        priority,
                                        oid((ushort)priority, (ushort)i),
                                        queries[tableOffset + i].Building,
                                        queries[tableOffset + i].Position,
                                        c.buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty));
                                    }
#endif
                                    --totalQueries;
                                    continue; // SKip "Excluded" exporters with priority less than 3 (from "While inOfferAmount lasts")
                                }
                                break;
                            case MatchMode.Exports:
                                if ((c.buildings.m_buildings.m_buffer[queries[tableOffset + i].Building].m_flags & Building.Flags.Outgoing) != 0) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Skip Exporter prio={0}, query={1} building={2} @({3}/{4})",
                                        priority,
                                        oid((ushort)priority, (ushort)i),
                                        queries[tableOffset + i].Building,
                                        queries[tableOffset + i].Position,
                                        c.buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty));
                                    }

#endif
                                    --totalQueries;
                                    continue;
                                }
                                break;
                            case MatchMode.SoakSupply:
                                if (queries[tableOffset + i].Exclude/* && priority < 2*/) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Skip Warehouse prio={0}, query={1} building={2} ({3})",
                                        priority,
                                        oid((ushort)priority, (ushort)i),
                                        queries[tableOffset + i].Building,
                                        c.buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty));
                                    }
#endif
                                    --totalQueries;
                                    continue;
                                }
                                break;
                            case MatchMode.SatisfyDemand:
                                if (queries[tableOffset + i].Exclude && priority < 2) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Skip Warehouse prio={0}, query={1} building={2} ({3})",
                                        priority,
                                        oid((ushort)priority, (ushort)i),
                                        queries[tableOffset + i].Building,
                                        c.buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty));
                                    }
#endif
                                    --totalQueries;
                                    continue;
                                }
                                break;
#if true
                            case MatchMode.Rebalance:
                                if (queries[tableOffset + i].Exclude && priority < 1) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Skip Warehouse prio={0}, query={1} building={2} ({3})",
                                        priority,
                                        oid((ushort)priority, (ushort)i),
                                        queries[tableOffset + i].Building,
                                        c.buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty));
                                    }
#endif
                                    --totalQueries;
                                    continue;
                                }
                                break;
#endif
                            default:
                                //                            if ((buildings.m_buildings.m_buffer[queries[tableOffset + i].Building].m_flags & Building.Flags.IncomingOutgoing) != 0) {
                                //#if DEBUG
                                //                                Log._DebugFormat("Skip {0} prio={1}, query={2} building={3} ({4})",
                                //                                    ((buildings.m_buildings.m_buffer[queries[tableOffset + i].Building].m_flags & Building.Flags.Outgoing) != 0) ? "Exporter" : "Importer",
                                //                                    priority,
                                //                                    oid((ushort)priority, (ushort)i),
                                //                                    queries[tableOffset + i].Building,
                                //                                    buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty));
                                //#endif
                                //                                --totalQueries;
                                //                                continue;
                                //                            }
                                break;
                        }



                        if (TransferBrokerMod.Installed.HaveNaturalDisastersDLC) {
                            /* Handle helicopter ojectives, only in SoakSupply, ie they will
                             * handle leftover demand that ground units could not manage.
                             * This includes overflow offers in-network (eg, more criminals than police cars)
                             * and off-network offers (forrest fires, or medical service to isolated outposts
                             */
                            // Log.Info("Eval Air query service");
                            var id = queries[tableOffset + i].m_object;
                            switch (id.Type) {
                                case InstanceType.Building:
                                    var buildingAI = c.buildings.m_buildings.m_buffer[id.Building].Info.GetAI();
                                    if (buildingAI is HelicopterDepotAI) {
#if DEBUG
                                        Log.Info($"query {oid((ushort)priority, (ushort)i)} is air service - skip {TransferBroker.InstanceName(id)}");
#endif
                                        --totalQueries;
                                        continue;
                                    } else if (work.material == TransferManager.TransferReason.Collapsed2 && buildingAI is DisasterResponseBuildingAI) {
                                        /* Only Collapsed 2 is transported by Heli, Collapsed is transported on the road */
                                        --totalQueries;
                                        continue;
                                    }
                                    break;
                                case InstanceType.Vehicle:
                                    if (c.vehicles.m_vehicles.m_buffer[id.Vehicle].Info.GetAI() is HelicopterAI) {
#if DEBUG
                                        Log.Info($"query {oid((ushort)priority, (ushort)i)} is helicopter - skip {TransferBroker.InstanceName(id)}");
#endif
                                        --totalQueries;
                                        continue;
                                    }
                                    break;
                            }
                        }



                        ushort node = FindNearestServiceNode(queries[tableOffset + i]);
                        try {
                            queryServiceNode[oid((ushort)priority, (ushort)i) - 1] = node;
                        }
                        catch (Exception ex) {
                            Log.Info($"Exception caught while caching serviceNode priority={priority} i={i} oid={oid((ushort)priority, (ushort)i)} ({ex.GetType().Name}: {ex.Message})\n{ex.StackTrace}");
                        }
                        finally {

                        }


                        if (node != 0) {
#if DEBUG
                            if (_debug) {
                                Log._DebugFormat("Found service node prio={0}, query={1} node={2}",
                                priority,
                                oid((ushort)priority, (ushort)i),
                                node);
                            }
#endif
                            if (nodeState[node].nnid != 0) {
                                if (nodeState[node].nndistance == 0) {
#if DEBUG
                                    if (_debug) {
                                        Log._DebugFormat("Duplicated query nodenum=={0} prio={1}, query={2} building={3}", node, priority, i, queries[tableOffset + i].Building);
                                    }
#endif
                                    // XXX add queries as a linked list to one node?
                                    --totalQueries;
                                    continue;
                                }
                            }
                            nodeState[node].nndistance = 0;
                            nodeState[node].nnid = oid((ushort)priority, (ushort)i);
                        } else {
#if DEBUG
                            if (_debug) {
                                Log._DebugFormat("No service node found: prio={0}, query={1} building={2} ({3}) pos={4} ParentNode={5} type={6}",
                                priority,
                                oid((ushort)priority, (ushort)i),
                                queries[tableOffset + i].Building,
                                c.buildings.GetBuildingName(queries[tableOffset + i].Building, InstanceID.Empty),
                                queries[tableOffset + i].Position,
                                c.buildings.m_buildings.m_buffer[queries[tableOffset + i].Building].FindParentNode(queries[tableOffset + i].Building),
                                c.buildings.m_buildings.m_buffer[queries[tableOffset + i].Building].Info.m_buildingAI);
                            }
#endif
                            if (--totalQueries == 0) break;
                            continue;
                        }
                    }
                }
                if (totalQueries == 0) return false;

                ANNPQ.Clear();
                try {

                    for (int priority = TransferManager.TRANSFER_PRIORITY_COUNT - 1; priority >= minObjPriority; --priority) {
                        int prio_material = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + priority;
                        int tableOffset = prio_material * TransferManager.TRANSFER_OFFER_COUNT;

                        // Step 8-10 init objectives oi
                        // Step 12. Setup the objectives
                        // Step 13. PQ.insert(v*)
                        for (ushort i = objectsStartAt[prio_material]; i != objectsEndAt[priority]; i = (ushort)((i + 1) % TransferManager.TRANSFER_OFFER_COUNT)) {
                            //                    if (!objects[tableOffset + i].Active) {
                            //                        --totalObjects;
                            //                        continue;
                            //                    }
                            // Log._DebugFormat("Will NearestTransportSegment: pos={0}", objects[tableOffset + i].Position);
                            /* FIXME: replace objects[tableOffset + i].m_object with objectID to avoid many dereferences */
                            if (objects[tableOffset + i].m_object.IsEmpty) {
#if DEBUG
                                if (_debug) {
                                    Log._DebugFormat("Skip Empty prio={0}, object={1} building={2} ({3})",
                                    priority,
                                    oid((ushort)priority, (ushort)i),
                                    objects[tableOffset + i].Building,
                                    c.buildings.GetBuildingName(objects[tableOffset + i].Building, InstanceID.Empty));
                                }
#endif
                                --totalObjects;
                                continue;
                            }

                            switch (mode) {
                                case MatchMode.Imports:
                                    if ((c.buildings.m_buildings.m_buffer[objects[tableOffset + i].Building].m_flags & Building.Flags.Incoming) != 0) {
#if DEBUG
                                        if (_debug) {
                                            Log._DebugFormat("Skip Importers prio={0}, object={1} building={2} ({3})",
                                            priority,
                                            oid((ushort)priority, (ushort)i),
                                            objects[tableOffset + i].Building,
                                            c.buildings.GetBuildingName(objects[tableOffset + i].Building, InstanceID.Empty));
                                        }
#endif
                                        --totalObjects;
                                        continue; // SKip "Excluded" exporters with priority less than 3 (from "While inOfferAmount lasts")
                                    }
                                    break;
                                case MatchMode.Exports:
                                    if (objects[tableOffset + i].Exclude && priority < (EXPORT_THRESHOLD + 1)) {
#if DEBUG
                                        if (_debug) {
                                            Log._DebugFormat("Skip Excluded Exporter prio={0}, object={1} building={2} ({3})",
                                            priority,
                                            oid((ushort)priority, (ushort)i),
                                            objects[tableOffset + i].Building,
                                            c.buildings.GetBuildingName(objects[tableOffset + i].Building, InstanceID.Empty));
                                        }
#endif
                                        --totalObjects;
                                        continue; // SKip "Excluded" exporters with priority less than 3 (from "While inOfferAmount lasts")
                                    }
                                    if ((c.buildings.m_buildings.m_buffer[objects[tableOffset + i].Building].m_flags & Building.Flags.Outgoing) != 0) {
#if DEBUG
                                        if (_debug) {
                                            Log._DebugFormat("Skip Exporter prio={0}, object={1} building={2} ({3})",
                                            priority,
                                            oid((ushort)priority, (ushort)i),
                                            objects[tableOffset + i].Building,
                                            c.buildings.GetBuildingName(objects[tableOffset + i].Building, InstanceID.Empty));
                                        }
#endif
                                        --totalObjects;
                                        continue; // SKip "Excluded" exporters with priority less than 3 (from "While inOfferAmount lasts")
                                    }
                                    break;
#if true
                                case MatchMode.Rebalance:
                                    if (objects[tableOffset + i].Exclude & priority < 2) {
#if DEBUG
                                        if (_debug) {
                                            Log._DebugFormat("Skip Warehouse prio={0}, object={1} building={2} ({3})",
                                            priority,
                                            oid((ushort)priority, (ushort)i),
                                            objects[tableOffset + i].Building,
                                            c.buildings.GetBuildingName(objects[tableOffset + i].Building, InstanceID.Empty));
                                        }
#endif
                                        --totalObjects;
                                        continue;
                                    }
                                    break;
#endif
                                default:
                                    /* TODO: Remove this check when Export/Import offers are added only when needed.
                                     */
                            //                            if ((buildings.m_buildings.m_buffer[objects[tableOffset + i].Building].m_flags & Building.Flags.IncomingOutgoing) != 0) {
                            //
                            //#if DEBUG
                            //                                Log._DebugFormat("Skip {0} prio={1}, object={2} building={3} ({4})",
                            //                                    ((buildings.m_buildings.m_buffer[objects[tableOffset + i].Building].m_flags & Building.Flags.Outgoing) != 0) ? "Exporter" : "Importer",
                            //                                    priority,
                            //                                    oid((ushort)priority, (ushort)i),
                            //                                    objects[tableOffset + i].Building,
                            //                                    buildings.GetBuildingName(objects[tableOffset + i].Building, InstanceID.Empty));
                            //#endif
                            //                                --totalObjects;
                            //                                continue;
                            //                            }
                            break;
                            }








                            if (TransferBrokerMod.Installed.HaveNaturalDisastersDLC) {
                                /* Handle helicopter ojectives, only in SoakSupply, ie they will
                                 * handle leftover demand that ground units could not manage.
                                 * This includes overflow offers in-network (eg, more criminals than police cars)
                                 * and off-network offers (forrest fires, or medical service to isolated outposts
                                 */
//                                        Log.Info("Eval Air service object");
                                var id = objects[tableOffset + i].m_object;
                                if (id.Building != 0) {
                                    var ai = c.buildings.m_buildings.m_buffer[objects[tableOffset + i].m_object.Building].Info.GetAI();
                                    ushort air_nnid = oid((ushort)priority, (ushort)i);
                                    isHelicopterService[air_nnid-1] = ai is HelicopterDepotAI || ai is DisasterResponseBuildingAI;
#if DEBUG
                                    if (isHelicopterService[air_nnid-1])
                                        Log.Info($"object {air_nnid} is air service - {TransferBroker.InstanceName(id)}");
#endif
                                } else if (objects[tableOffset + i].m_object.Vehicle != 0) {
                                    var ai = c.vehicles.m_vehicles.m_buffer[objects[tableOffset + i].m_object.Vehicle].Info.GetAI();
                                    ushort air_nnid = oid((ushort)priority, (ushort)i);
                                    isHelicopterService[air_nnid-1] = ai is HelicopterAI;
#if DEBUG
                                    if (isHelicopterService[air_nnid-1])
                                        Log.Info($"object {air_nnid} is helicopter - {TransferBroker.InstanceName(id)}");
#endif
                                } else {
                                    ushort air_nnid = oid((ushort)priority, (ushort)i);
                                    isHelicopterService[air_nnid-1] = false;
                                }

                                if (isHelicopterService[oid((ushort)priority, (ushort)i)-1]) {
                                    /* FIXME: only Crime needs to be done in SoakSupply
                                     * FIXME: Multiple heli depots override each other, need to be the min cost.
                                     */
                                    if (work.material != TransferManager.TransferReason.Crime || mode == MatchMode.SoakSupply) {

                                        for (int queryPriority = maxQueryPriority; totalQueries != 0 && queryPriority >= minQueryPriority; --queryPriority) {
                                            int query_prio_material = ((int)work.material * TransferManager.TRANSFER_PRIORITY_COUNT) + queryPriority;
                                            int queryTableOffset = query_prio_material * TransferManager.TRANSFER_OFFER_COUNT;

                                            // Step x (Generalized for ANN queries without pre-computation (3.3)
                                            for (int j = queriesStartAt[query_prio_material]; j != queriesEndAt[queryPriority]; j = ((j + 1) % TransferManager.TRANSFER_OFFER_COUNT)) {
                                                if (!queries[queryTableOffset + j].m_object.IsEmpty) {
                                                    var nnid = oid((ushort)queryPriority, (ushort)j);
                                                    var airServiceCost = OfferAirCost(PositionOf(objects[tableOffset + i]), PositionOf(queries[queryTableOffset + j]));
#if DEBUG
                                                    if (_debug) {
                                                        Log._DebugFormat("Found air service from object={0} to query={1} @ node #{2} airServiceCost={3}",
                                                        oid((ushort)priority, (ushort)i),
                                                        nnid,
                                                        queryServiceNode[nnid-1],
                                                        airServiceCost);
                                                    }
#endif
                                                    ushort air_nnid = oid((ushort)priority, (ushort)i);
                                                    ANNPQ.Enqueue(new Offer(air_nnid, queryServiceNode[nnid-1]), airServiceCost);
                                                }
                                            }
                                        }
                                    }
                                    objAmount += objects[tableOffset + i].Amount;
                                    --totalObjects;
                                    continue;
                                } else {

                                }
                            }




                            ushort node = FindNearestServiceNode(objects[tableOffset + i]);
                            if (node != 0) {
#if DEBUG
                                if (_debug) {
                                    Log._DebugFormat("Found service node prio={0}, object={1} node={2}",
                                    priority,
                                    oid((ushort)priority, (ushort)i),
                                    node);
                                }
#endif
                                if (nodeState[node].nnid != 0) {
                                    if (nodeState[node].nndistance != 0) {
#if DEBUG
                                        if (_debug) {
                                            Log._DebugFormat("Duplicated object nodenum=={0} prio={1}, object={2} building={3}",
                                                node,
                                                priority,
                                                i,
                                                objects[tableOffset + i].Building);
                                        }
#endif
                                        --totalObjects;
                                        objAmount += objects[tableOffset + i].Amount;
                                        continue;
                                        //                            } else if (mode == MatchMode.SoakSupply) {
                                        //                                Log._DebugFormat("Warehouse? used as supply, will skip query={0} at node={1}", N[node].nnid, node);
                                        //                                //--totalQueries;
                                        //                                //// continue;
                                        //
                                        //                                --totalObjects;
                                        //                                continue;
                                        //                            } else if (mode == MatchMode.Rebalance) {
                                        //#if DEBUG
                                        //                                Log._DebugFormat("Match Mode: {0} {1}, object/demand={3} overrides query/supply={2}", mode, material, N[node].nnid, oid((ushort)priority, (ushort)i));
                                        //#endif
                                        //                                if (--totalQueries == 0) break;
                                    } else {
                                        uint queryIndex = oid2index(nodeState[node].nnid);
                                        if (objects[tableOffset + i].m_object == queries[queryIndex].m_object) {
#if false
    if (mode != MatchMode.Rebalance) {
#endif

#if DEBUG
                                            if (_debug) {
                                                Log._DebugFormat("Match Mode: {0} {1}, object={2} overrides query={3}",
                                                    mode,
                                                    work.material,
                                                    oid((ushort)priority, (ushort)i),
                                                    nodeState[node].nnid);
                                            }
#endif
                                            if (--totalQueries == 0) break;
#if false
                                        } else {
#if DEBUG
                                            Log._DebugFormat("Match Mode: {0} {1}, query={3} overrides object={2}", mode, material, oid((ushort)priority, (ushort)i), N[node].nnid);
#endif
                                            --totalObjects;
                                            continue;
                                        }
#endif

                                        } else {
                                            /* Immediate delivery at this node */
#if DEBUG
                                            if (_debug) {
                                                Log._DebugFormat("In-Place Transfer: {0} {1}, object={2} query={3}",
                                                    mode,
                                                    work.material,
                                                    oid((ushort)priority, (ushort)i),
                                                    nodeState[node].nnid);
                                            }
#endif
                                            objAmount += objects[tableOffset + i].Amount;
                                            if (objectIsOutgoing) {
                                                objAmount -= StartTransfer(oid((ushort)priority, (ushort)i), nodeState[node].nnid, 0, mode, ref haveDeferals);
                                            } else {
                                                objAmount -= StartTransfer(nodeState[node].nnid, oid((ushort)priority, (ushort)i), 0, mode, ref haveDeferals);
                                            }
                                            if (queries[queryIndex].m_object.IsEmpty) {
                                                if (--totalQueries == 0) break;
                                                if (objects[tableOffset + i].m_object.IsEmpty) {
                                                    --totalObjects;
                                                    continue;
                                                }
                                            } else {
                                                --totalObjects;
                                                continue;
                                            }
                                        }
                                        // continue;
                                    }
                                }
                                nodeState[node].nndistance = MIN_DISTANCE;
                                nodeState[node].nnid = oid((ushort)priority, i);
                                ANNPQ.Enqueue(new Offer(nodeState[node].nnid, node), nodeState[node].nndistance);
                                objAmount += objects[tableOffset + i].Amount;
                            } else {
#if DEBUG
                                if (_debug) {
                                    Log._DebugFormat("No service node found: prio={0}, object={1} building={2} ({3}) pos={4} ParentNode={5}",
                                    priority,
                                    oid((ushort)priority, (ushort)i),
                                    objects[tableOffset + i].Building,
                                    c.buildings.GetBuildingName(objects[tableOffset + i].Building, InstanceID.Empty),
                                    objects[tableOffset + i].Position,
                                    c.buildings.m_buildings.m_buffer[objects[tableOffset + i].Building].FindParentNode(objects[tableOffset + i].Building));
                                }
#endif
                                --totalObjects;
                                continue;
                            }
                        }
                    }
                }
                finally {
                    if (mode == MatchMode.SoakSupply && TransferBrokerMod.Installed.HaveNaturalDisastersDLC) {
                        Array.Clear(isHelicopterService, 0, isHelicopterService.Length);
                    }
                }
#if MAINTAIN_CONNECTIONS
                if (totalQueries != 0 && ANNPQ.Count != 0) {
                    /* Add connections to other modes of transport */
                    foreach (ushort n in c.owner.cargoConnections.Keys) {
                        nodeState[n].hasConnections = true;
                    }
                }
#endif
                Offer vi;
                float cost;

#if DEBUG
                if (_debug) {
                    Log._DebugFormat("Match offers ... {0} numObjects={1} numQueries={2} ANNPQ.Size={3}", mode, totalObjects, totalQueries, ANNPQ.Count);
                }
#endif
                /* FIXME: When totalQueries == 1, no need to search... Match it with object vi */
                /* FIXME: Track when objects fall out of ANNPQ, and when objects are depleted of goods. When numObjects able to serve = 0, stop the search */


                if (ANNPQ.Count != 0) {

                    /* Shortcut, assumes all objects are either Active or Inactive */
                    var index = oid2index(ANNPQ.First.nnid);

                    /* What is forward, what is reverse? */
                    NetInfo.Direction fwd = objects[index].Active ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;
                    NetInfo.Direction rev = objects[index].Active ? NetInfo.Direction.Backward : NetInfo.Direction.Forward;

                    while (totalQueries != 0 && ANNPQ.Count != 0) {
                        vi = ANNPQ.Dequeue();
#if DEBUG && true
                        if (_debug) {
                            Log._Debug($"Visiting node {vi.nodeID} for object={vi.nnid}");
                        }
#endif
                        if (nodeState[vi.nodeID].nnid != 0) {
                            if (nodeState[vi.nodeID].nndistance == 0) { /* is a query, first visit */

                                if (objectIsOutgoing) {
                                    objAmount -= StartTransfer(vi.nnid, nodeState[vi.nodeID].nnid, vi.Priority, mode, ref haveDeferals);
                                } else {
                                    objAmount -= StartTransfer(nodeState[vi.nodeID].nnid, vi.nnid, vi.Priority, mode, ref haveDeferals);
                                }
                                /* Also stop searching if there is only 1 request remaining, match it
                                    * with the next ANN dequeue that is still active (cheapest obj)
                                    */
                                if (objAmount == 0) break;
#if false
                                /* Why is this needed ?? */
                                if (!queries[oid2index(material, nodeState[vi.nodeID].nnid)].m_object.IsEmpty) {
                                    /* IE, the query at this node is not fully satisfied by the objective */
                                    Log._DebugFormat("Marking query #{0} as having deferals.", nodeState[vi.nodeID].nnid);
                                    haveDeferals = true;
                                }
#endif
                                if (--totalQueries == 0) break;
                            } else if (vi.Priority != MIN_DISTANCE) {
                                continue; // not a query, but already visited
                            }
                        }
                        /* vi has not yet been visited */
#if MAINTAIN_CONNECTIONS
                        for (int i = 0; i < NODE_SEGMENTS; ++i) {
                            var seg_i = nodes[vi.nodeID].GetSegment(i);
#else
                        int numSegments = NODE_SEGMENTS + ((nodes[vi.nodeID].m_lane != 0) ? 1 : 0);
                        for (int i = 0; i < numSegments; ++i) {
                            var seg_i = i != NODE_SEGMENTS ? nodes[vi.nodeID].GetSegment(i) : c.nets.m_lanes.m_buffer[nodes[vi.nodeID].m_lane].m_segment;
#endif

#if false
#if DEBUG
                            if (_debug && i == NODE_SEGMENTS) {
                                Log._DebugFormat("Found {0} obj={1} lane={2} at node={3} tosegment={4}",
                                work.material,
                                vi.nnid,
                                nodes[vi.nodeID].m_lane,
                                vi.nodeID,
                                seg_i);
                            }
#endif
#endif
                            if (seg_i != 0) {
                                var segment = segments[seg_i];

                                NetInfo.Direction direction;

                                if (vi.nodeID == segment.m_startNode) {
                                    direction = (segment.m_flags & NetSegment.Flags.Invert) == 0 ? fwd : rev;
                                    vj.nodeID = segment.m_endNode;
                                } else {
                                    direction = (segment.m_flags & NetSegment.Flags.Invert) == 0 ? rev : fwd;
                                    vj.nodeID = segment.m_startNode;
                                }

                                if (nodeState[vj.nodeID].nndistance == 0) { /* not visited */
                                    cost = Cost(seg_i, direction);
#if true
#if DEBUG && SINGLETHREAD
                                    if (_debug) {
                                        Log._DebugFormat("Found {0} obj={1} at node={2} segment={3} tonode={4} cost={5:0.0}",
                                        work.material,
                                        vi.nnid,
                                        vi.nodeID,
                                        seg_i,
                                        vj.nodeID,
                                        cost + vi.Priority);
                                    }
#endif
#endif

                                    if (cost != 0f) {
                                        ANNPQ.Enqueue(new Offer(vi.nnid, vj.nodeID), cost + vi.Priority);

                                        var lane = segment.m_lanes;
                                        while (lane != 0) {
                                            var node = c.nets.m_lanes.m_buffer[lane].m_nodes;
                                            // if (c.nets.m_nodes.m_buffer[node].m_building)
                                            while (node != 0) {
                                                if (nodeState[node].nndistance == 0 && (nodes[node].Info.m_laneTypes & constraints.laneTypes) != 0) {
#if true
#if DEBUG && SINGLETHREAD
                                                    if (_debug) {
                                                        Log._DebugFormat("Found {0} obj={1} at segment={3} lane={4} tonode={2} laneTypes={5}",
                                                        work.material,
                                                        vi.nnid,
                                                        node,
                                                        seg_i,
                                                        lane,
                                                        nodes[node].Info.m_laneTypes);
                                                    }
#endif
#endif
                                                    ANNPQ.Enqueue(new Offer(vi.nnid, node), (cost * 2) + vi.Priority);
                                                }

                                                node = c.nets.m_nodes.m_buffer[node].m_nextLaneNode;
                                            }
                                            lane = c.nets.m_lanes.m_buffer[lane].m_nextLane;
                                        }
                                    }
                                }
                            }
                        }
#if MAINTAIN_CONNECTIONS
                        if (nodeState[vi.nodeID].hasConnections) {
                            foreach (ushort n in c.owner.cargoConnections[vi.nodeID]) {
                                if (nodeState[n].nndistance == 0) {
#if true
                                    Log._DebugFormat("Visiting cargo connection {0} ({1}.{2}) from {3} ({4}.{5})",
                                        n,
                                        c.nets.m_nodes.m_buffer[n].Info.m_class.m_service,
                                        c.nets.m_nodes.m_buffer[n].Info.m_class.m_subService,
                                        vi.nodeID,
                                        c.nets.m_nodes.m_buffer[vi.nodeID].Info.m_class.m_service,
                                        c.nets.m_nodes.m_buffer[vi.nodeID].Info.m_class.m_subService);
#endif
                                    cost = vi.Priority + CONNECTION_USE_COST; // Fixed cost of switching modes.
                                    ANNPQ.Enqueue(new Offer(vi.nnid, n), cost);
                                }
                            }
                        }
#endif
                        nodeState[vi.nodeID].nnid = vi.nnid; /* mark as visited */
                        nodeState[vi.nodeID].nndistance = vi.Priority;
                    }
                }
            }
            finally {
                Array.Clear(nodeState, 0, nodeState.Length);
            }
            return haveDeferals && objAmount != 0;

        }

#if false

        private bool IsSupportedByNewAlgo(TransferManager.TransferReason material) {
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
                case TransferManager.TransferReason.Crime:
                case TransferManager.TransferReason.Garbage:
                    return true;
            }
            return false;
        }
#endif

        PathConstraints GetConstraints() {
            switch (work.material) {
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
                case TransferManager.TransferReason.Fish:

                    /* These are materials shippable by PublicTransport (cargo truck, train, ship or plane) */
                    return new PathConstraints {
                        laneTypes = NetInfo.LaneType.Vehicle | NetInfo.LaneType.CargoVehicle,
                        /* FIXME MAYBE:
                         * Highway outside connections should be matched because they are PublicTransport class,
                         * not because they have a Road nearby
                         */
                        canUsePublicTransport = true,
                        // service1 = ItemClass.Service.PublicTransport,
                        // service2 = ItemClass.Service.Road,
                        minQueryPrioritySatisfy = 1,
                        minQueryPrioritySoak = 1,
                        minObjPrioritySoak = 0,
                        minObjPrioritySatisfy = 0,
                    };

                /* Materials which are served with capacitated vehicles (garbage trucks, hearses)
                 * priority = 2 before dispatching vehicles
                 */
                case TransferManager.TransferReason.Garbage:
                case TransferManager.TransferReason.Crime:
                case TransferManager.TransferReason.Dead:
                    return new PathConstraints {
                        laneTypes = NetInfo.LaneType.Vehicle,
                        /* FIXME:
                         * Police Depot should always match, no matter network connection
                         */
                        canUsePublicTransport = false,
                        // service1 = ItemClass.Service.Road,
                        // service2 = ItemClass.Service.None,
                        minQueryPrioritySatisfy = 0, /* Incinerators send trucks as long as they are not full */
                        minQueryPrioritySoak = 1, /* rolling trucks pick up meaningful amounts of garbage (prio 2+) */
                        minObjPrioritySoak = 2, /* ie, rolling stock and empty buildings will soak */
                        minObjPrioritySatisfy = 2, /* let garbage, etc, accumulate for a bit, don't send truck at first candy wrapper */
                    };

                /* Helicopter services
                 * These can also be handled by the Legacy Matchmaker,
                 * they are matched in a straight line (flight path)
                 * distance. The only reason I'm using the ANN algo
                 * is to exercise/test it more, as it supports Crime
                 * which is serviced by mixed land/helicopter.
                 */
                case TransferManager.TransferReason.ForestFire:
                case TransferManager.TransferReason.Collapsed2:
                case TransferManager.TransferReason.Fire2:
                case TransferManager.TransferReason.Sick2:
                    return new PathConstraints {
                        laneTypes = NetInfo.LaneType.None,
                        canUsePublicTransport = false,
                        // service1 = ItemClass.Service.Road,
                        // service2 = ItemClass.Service.None,
                        minQueryPrioritySatisfy = 0, /*  */
                        minQueryPrioritySoak = 1, /*  */
                        minObjPrioritySoak = 2,
                        minObjPrioritySatisfy = 2, /*  */
                    };

                /* Garbage move happens as long as any garbage is available, ie prio 0+, but incinerators at prio 1+ */
                case TransferManager.TransferReason.GarbageMove:
                case TransferManager.TransferReason.DeadMove:
                case TransferManager.TransferReason.CriminalMove:
                case TransferManager.TransferReason.SnowMove:
                case TransferManager.TransferReason.SickMove:
                    return new PathConstraints {
                        laneTypes = NetInfo.LaneType.Vehicle,
                        canUsePublicTransport = false,
                        // service1 = ItemClass.Service.Road,
                        // service2 = ItemClass.Service.None,
                        minQueryPrioritySatisfy = 1, /* Move destinations accept as long as they are not almost full */
                        minQueryPrioritySoak = 0, /* pick up any amount (prio 0+) */
                        minObjPrioritySoak = 0,
                        minObjPrioritySatisfy = 0, /* No accumulation, pick everything up */
                    };

                /* Materials which have a binary behaviour are visited ASAP
                 * Ie, there is no buffer for dead/sick, and no accumulation
                 * 
                 * Ideally, hearses are sent from crematorium when request is prio>=1 (satisfy demand)
                 * but hearses on route should match with prio>=0, (soak supply)
                 * (similar behaviour to garbage pickup by proximity)
                 * But 
                 */
                case TransferManager.TransferReason.Sick:
                case TransferManager.TransferReason.Fire:
                    return new PathConstraints {
                        laneTypes = NetInfo.LaneType.Vehicle,
                        canUsePublicTransport = false,
                        // service1 = ItemClass.Service.Road,
                        // service2 = ItemClass.Service.None,
                        minQueryPrioritySatisfy = 0, /* Hospitals, etc send ambulances as long as they are not full */
                        minQueryPrioritySoak = 0, /* rolling ambulances pick up any dead */
                        minObjPrioritySoak = 0,
                        minObjPrioritySatisfy = 0, /* no accumulation at homes */
                    };

                case TransferManager.TransferReason.Collapsed:
                case TransferManager.TransferReason.Worker0:
                case TransferManager.TransferReason.Worker1:
                case TransferManager.TransferReason.Worker2:
                case TransferManager.TransferReason.Worker3:
                case TransferManager.TransferReason.Student1:
                case TransferManager.TransferReason.Student2:
                case TransferManager.TransferReason.Student3:
                case TransferManager.TransferReason.Bus:
                case TransferManager.TransferReason.PassengerTrain:

                case TransferManager.TransferReason.Family0:
                case TransferManager.TransferReason.Family1:
                case TransferManager.TransferReason.Family2:
                case TransferManager.TransferReason.Family3:
                case TransferManager.TransferReason.Single0:
                case TransferManager.TransferReason.Single1:
                case TransferManager.TransferReason.Single2:
                case TransferManager.TransferReason.Single3:

                case TransferManager.TransferReason.PartnerYoung:
                case TransferManager.TransferReason.PartnerAdult:
                case TransferManager.TransferReason.Shopping:
                case TransferManager.TransferReason.LeaveCity0:
                case TransferManager.TransferReason.LeaveCity1:
                case TransferManager.TransferReason.LeaveCity2:
                case TransferManager.TransferReason.Entertainment:
                case TransferManager.TransferReason.MetroTrain:
                case TransferManager.TransferReason.PassengerPlane:
                case TransferManager.TransferReason.PassengerShip:
                case TransferManager.TransferReason.DummyCar:
                case TransferManager.TransferReason.DummyTrain:
                case TransferManager.TransferReason.DummyShip:
                case TransferManager.TransferReason.DummyPlane:
                case TransferManager.TransferReason.Single0B:
                case TransferManager.TransferReason.Single1B:
                case TransferManager.TransferReason.Single2B:
                case TransferManager.TransferReason.Single3B:
                case TransferManager.TransferReason.ShoppingB:
                case TransferManager.TransferReason.ShoppingC:
                case TransferManager.TransferReason.ShoppingD:
                case TransferManager.TransferReason.ShoppingE:
                case TransferManager.TransferReason.ShoppingF:
                case TransferManager.TransferReason.ShoppingG:
                case TransferManager.TransferReason.ShoppingH:
                case TransferManager.TransferReason.EntertainmentB:
                case TransferManager.TransferReason.EntertainmentC:
                case TransferManager.TransferReason.EntertainmentD:
                case TransferManager.TransferReason.Taxi:
                case TransferManager.TransferReason.Tram:
                case TransferManager.TransferReason.Snow:
                case TransferManager.TransferReason.RoadMaintenance:
                case TransferManager.TransferReason.FloodWater:
                case TransferManager.TransferReason.EvacuateA:
                case TransferManager.TransferReason.EvacuateB:
                case TransferManager.TransferReason.EvacuateC:
                case TransferManager.TransferReason.EvacuateD:
                case TransferManager.TransferReason.EvacuateVipA:
                case TransferManager.TransferReason.EvacuateVipB:
                case TransferManager.TransferReason.EvacuateVipC:
                case TransferManager.TransferReason.EvacuateVipD:
                case TransferManager.TransferReason.Ferry:
                case TransferManager.TransferReason.CableCar:
                case TransferManager.TransferReason.Blimp:
                case TransferManager.TransferReason.Monorail:
                case TransferManager.TransferReason.TouristBus:
                case TransferManager.TransferReason.ParkMaintenance:
                case TransferManager.TransferReason.TouristA:
                case TransferManager.TransferReason.TouristB:
                case TransferManager.TransferReason.TouristC:
                case TransferManager.TransferReason.TouristD:
                case TransferManager.TransferReason.Mail:
                case TransferManager.TransferReason.UnsortedMail:
                case TransferManager.TransferReason.SortedMail:
                case TransferManager.TransferReason.OutgoingMail:
                case TransferManager.TransferReason.IncomingMail:
                case TransferManager.TransferReason.GarbageTransfer:
                case TransferManager.TransferReason.PassengerHelicopter:
                case TransferManager.TransferReason.Trolleybus:
                case TransferManager.TransferReason.ElderCare:
                case TransferManager.TransferReason.ChildCare:
                case TransferManager.TransferReason.IntercityBus:
                    return default(PathConstraints);
            }
            return default(PathConstraints);
        }

        /* FIXME: Find why Found building=0 for Police Helicopter
         *       Debug.Matchmaker #1 439.4705938: In Crime: 1812: Qty 1 by Police Helicopter (Vehicle#9918) prio 7
         *       Debug.Matchmaker #1 439.4707658: In Crime: 554: Qty 1 by Police Station (Building#23260) prio 2
         *       Debug.Matchmaker #1 439.4709228: MatchMaker#1.MatchOffers(Crime, UncongestedTravelTime)
         *       Debug.Matchmaker #1 439.4710798: Match offers #1 .................... SatisfyDemand Crime ....................
         *       Debug.Matchmaker #1 439.4712685: Match offers #1 ... SatisfyDemand numQueries=2 7>=prio>=0
         *       Debug.Matchmaker #1 439.4714260: Match offers #1 ... SatisfyDemand numObjects=2 prio>=
         * >>>>> Debug.Matchmaker #1 439.4716232: Found service node prio=7, query=1812 building=0 @((506.3, 0.0, 393.8)/)  ((0.0, 0.0, 0.0)) node=31075
         */
        private ushort FindNearestServiceNode(TransferManager.TransferOffer offer) {
            switch (offer.m_object.Type) {
                case InstanceType.Citizen:
                    return FindNearestServiceNode(PositionOf(offer), c.buildings.m_buildings.m_buffer[offer.m_object.Building].Info.m_class);
                case InstanceType.Building:
                    if (constraints.canUsePublicTransport) {
                        var buildingClass = c.buildings.m_buildings.m_buffer[offer.m_object.Building].Info.m_class;
                        if (buildingClass.m_service == ItemClass.Service.PublicTransport) {
                            return FindNearestServiceNode(PositionOf(offer), buildingClass);
                        } else {
                            return FindNearestServiceNode(PositionOf(offer));
                        }
                    } else {
                        return FindNearestServiceNode(PositionOf(offer));
                    }
                case InstanceType.Vehicle:
                    return FindNearestServiceNode(PositionOf(offer), c.vehicles.m_vehicles.m_buffer[offer.m_object.Vehicle].Info.m_class);
            }

            return 0;
        }

        private float OfferAirCost(Vector3 from, Vector3 to) {
            /* FIXME: Assume helicopter speed is 2x
             * The correct way could be PrefabCollection<VehicleInfo>.FindPrefab("Helicopter").m_maxSpeed
             * but a mod could select different heli prefabs, with different max speed,
             * so instead I'd have to try getting a vehicle for the offer to see what speed
             * to use. There is some performance cost to finding this speed per search object:
             * m_info is Building's info
             * VehicleInfo randomVehicleInfo2 = Singleton<VehicleManager>.instance.GetRandomVehicleInfo(ref Singleton<SimulationManager>.instance.m_randomizer, m_info.m_class.m_service, m_info.m_class.m_subService, m_info.m_class.m_level, VehicleInfo.VehicleType.Car)
             * 
             * This info needs to be correctish because heli serice
             * competes on cost with ground service, so comparison
             * should be possible.
             * Hardcode to 20x for now (NaturalDisaster Helicopter maxspeed), revise later if needed
             */

            return Vector3.Magnitude(to - from) * 0.05f; /* same as divide by 20, for  */
        }
    }
}
