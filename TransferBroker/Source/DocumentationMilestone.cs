namespace TransferBroker {
    using ColossalFramework;
    using ColossalFramework.Globalization;
    using ColossalFramework.IO;
    using UnityEngine;
    using System;
    using System.Threading;
    using System.Reflection;
    using CSUtil.Commons;

    internal class DocumentationMilestone : MilestoneInfo {

        /* FIXME: hardcoded to document  "SourcingMod.DOCUMENTATION_TITLE"
         * need the fix to support multiple documentations
         * FIXME: m_documentName is superfluous, use m_name
         */
        public string m_documentName;

        [NonSerialized]
        public SavedBool m_unlocked;

        public class DocumentationData : Data {
            public bool m_read;

            public override void Serialize(DataSerializer s) {
                base.Serialize(s);
                s.WriteBool(m_read);
            }

            public override void Deserialize(DataSerializer s) {
                base.Deserialize(s);
                m_read = s.ReadBool();
            }

            public override void AfterDeserialize(DataSerializer s) {
                base.AfterDeserialize(s);
            }
        }

#if DEBUG
        public DocumentationMilestone(string _name, string desc) {
            Log.Info($"{GetType().Name}..ctor() {Assembly.GetExecutingAssembly().GetName().Version}");
            Thread.Sleep(100);
            Log.Info($"{GetType().Name}..ctor() done {Assembly.GetExecutingAssembly().GetName().Version} name={name} m_documentName={m_documentName} unlocked exists = {m_unlocked.exists}, unlocked={m_unlocked.value} version={m_unlocked.version}");
        }
#endif

        public void SetValues(string _name, string desc) {
#if DEBUG
            Log.Info($"{GetType().Name}.SetValues({_name}, {desc}) {Assembly.GetExecutingAssembly().GetName().Version}");
#endif
            //Thread.Sleep(100);
            name = _name;
            m_name = _name;
            m_documentName = desc;
            Load();
#if DEBUG
            Log.Info($"{GetType().Name}.SetValues({_name}, {desc}) done {Assembly.GetExecutingAssembly().GetName().Version} name={name} m_documentName={m_documentName} unlocked exists = {m_unlocked.exists}, unlocked={m_unlocked.value} version={m_unlocked.version}");
#endif
        }
 
        /* this causes the SavedBool to be re-loaded from the config file,
         * even if a new game is loaded (this makes the .exists flag to become true
         * if this is the 2nd game played since the documentation milestone was created.
         */
        public void Load() {
#if DEBUG
            Log.Info($"{GetType().Name}.Load() {Assembly.GetExecutingAssembly().GetName().Version}");
            Thread.Sleep(100);
#endif
            try {
                string key = $"Unlocked[{name}]";
#if DEBUG
                Log.Info($"{GetType().Name}.Load() 0 {Assembly.GetExecutingAssembly().GetName().Version} wasLoaded={m_unlocked != null}");
                Thread.Sleep(100);
#endif
                /* FIXME: Loading this SavedBool does not work, it always returns exists=false
                 */
                m_unlocked = new SavedBool(name: key, fileName: Settings.userGameState, def: false, autoUpdate: true);
                GetData().m_passedCount = m_unlocked ? 1 : 0;
                // Thread.Sleep(1000);
#if DEBUG
//                Thread.Sleep(100);
//                m_unlocked.value = false;
//                Thread.Sleep(100);
                Log.Info($"{GetType().Name}.Load() 1 {Assembly.GetExecutingAssembly().GetName().Version} val={m_unlocked.value} Exists={m_unlocked.exists}");
//                m_unlocked = new SavedBool(name: key, fileName: Settings.userGameState, def: false, autoUpdate: true);
//                Thread.Sleep(100);
//                Log.Warning($"{GetType().Name}.Load() 2 {Assembly.GetExecutingAssembly().GetName().Version} val={m_unlocked} Exists={m_unlocked.exists}");
//                Thread.Sleep(100);
//                m_unlocked = new SavedBool(name: key, fileName: Settings.userGameState, def: false, autoUpdate: true);
//                Thread.Sleep(100);
//                Log.Warning($"{GetType().Name}.Load() 3 {Assembly.GetExecutingAssembly().GetName().Version} val={m_unlocked} Exists={m_unlocked.exists}");
#endif
            }
            catch (Exception ex) {
                Log.Info($"Failed to Load m_unlocked ({ex.Message})\n{ex.StackTrace}");
            }
        }

#if DEBUG
        DocumentationMilestone() {
            Log.Info($"{GetType().Name}..ctor() {Assembly.GetExecutingAssembly().GetName().Version}");
        }

        static DocumentationMilestone() {
            Log.Info($"DocumentationMilestone..sctor() {Assembly.GetExecutingAssembly().GetName().Version}");
            Thread.Sleep(100);
            Log.Info($"DocumentationMilestone..sctor() done {Assembly.GetExecutingAssembly().GetName().Version}");
        }

        ~DocumentationMilestone() {
            Log.Info($"{GetType().Name}..dtor() {Assembly.GetExecutingAssembly().GetName().Version}");
        }
#endif
        public void Awake() {
#if DEBUG
            Log.Info($"{GetType().Name}.Awake() {Assembly.GetExecutingAssembly().GetName().Version} name={name}");
            Thread.Sleep(100);
            Log.Info($"{GetType().Name}.Awake() done {Assembly.GetExecutingAssembly().GetName().Version} name={name}");
#endif
            m_directReference = true;
            // Thread.Sleep(1000);

        }

#if DEBUG
        public void Start() {
            Log.Info($"{GetType().Name}.Start() {Assembly.GetExecutingAssembly().GetName().Version} name={name}");
            Thread.Sleep(1000);
            Log.Info($"{GetType().Name}.Start() done {Assembly.GetExecutingAssembly().GetName().Version} name={name}");
        }
#endif

#if DEBUG
        public void OnEnable() {
            Log.Info($"{GetType().Name}.OnEnable()) {Assembly.GetExecutingAssembly().GetName().Version} name={name}");
            // MilestoneInfo found = null;
            // m_name = m_documentName;
            //            try {
            //                // Reset(true);
            //                found = MilestoneCollection.FindMilestone(m_documentName);
            //            }
            //            catch (Exception ex) {
            //                Log.Info($"Failed to Add milestone {m_documentName} ({ex.GetType().Name}: {ex.Message})\n{ex.StackTrace}");
            //            }

            Thread.Sleep(100);
            Log.Info($"{GetType().Name}.OnEnable()) done {Assembly.GetExecutingAssembly().GetName().Version} name={name}");
        }
#endif
        public override void GetLocalizedProgressImpl(int targetCount, ref ProgressInfo totalProgress, FastList<ProgressInfo> subProgress) {
            if (totalProgress.m_description == null) {
                GetProgressInfo(ref totalProgress);
                return;
            }
            subProgress.EnsureCapacity(subProgress.m_size + 1);
            GetProgressInfo(ref subProgress.m_buffer[subProgress.m_size]);
            subProgress.m_size++;
        }

        public override GeneratedString GetDescriptionImpl(int targetCount) {
            var targetDocument = m_documentName;
            return new GeneratedString.Format(new GeneratedString.Locale("Read {0}"), new GeneratedString.String(targetDocument));
        }

        private void GetProgressInfo(ref ProgressInfo info) {
            Data data = m_data;
            int target = 1;
            info.m_min = 0f;
            info.m_max = target;
            if (data != null) {
                info.m_passed = data.m_passedCount != 0;
                info.m_current = data.m_progress;
            } else {
                info.m_passed = false;
                info.m_current = 0f;
            }
            info.m_description = StringUtils.SafeFormat(ColossalFramework.Globalization.Locale.Get("Read {0}"), m_documentName);
            info.m_progress = StringUtils.SafeFormat(ColossalFramework.Globalization.Locale.Get("MILESTONE_MANUAL_NUM_PROG"), data.m_progress, target);
            if (info.m_prefabs != null) {
                info.m_prefabs.Clear();
            }
        }
#if DEBUG
        public void OnDestroy() {
            Log.Info($"{GetType().Name}.OnDestroy() name={name} {Assembly.GetExecutingAssembly().GetName().Version}");
        }
#endif
        public override string ToString() {
            return "Read " + m_documentName + " milestone";
            // return StringUtils.SafeFormat(ColossalFramework.Globalization.Locale.Get("Read {0}"), m_documentName);
        }
        public override int GetComparisonValue() {
            return 1;
        }

        public override Data GetData() {
            if (m_data == null) {
                DocumentationData readCompletion = new DocumentationData();
                readCompletion.m_read = false;
                m_data = readCompletion;
                m_data.m_isAssigned = true;
            }
            return m_data;
        }

        public override void SetData(Data data) {
            if (data == null || data.m_isAssigned) {
                return;
            }
            data.m_isAssigned = true;
            DocumentationData readCompletion = data as DocumentationData;
            if (readCompletion != null) {
                if (m_data == null) {
                    m_data = readCompletion;
                } else {
                    m_data.m_passedCount = readCompletion.m_passedCount;
                    m_data.m_progress = readCompletion.m_progress;
                    DocumentationData docData2 = m_data as DocumentationData;
                    if (docData2 != null) {
                        docData2.m_read = readCompletion.m_read;
                    }
                }
                m_written = true;
            } else if (data != null) {
                Data data2 = GetData();
                data2.m_passedCount = data.m_passedCount;
                data2.m_progress = data.m_progress;
                m_written = true;
            }
        }

        public override Data CheckPassed(bool forceUnlock, bool forceRelock) {
#if DEBUG
            Log._Debug($"{GetType().Name}.CheckPassed({forceUnlock}, {forceRelock})");
#endif
            Data data = GetData();
            if (data.m_passedCount != 0 && !m_canRelock) {
                return data;
            }
            int target = 1;
            if (forceUnlock) {
                data.m_progress = target;
                data.m_passedCount = Mathf.Max(1, data.m_passedCount);
                DocumentationData docData = data as DocumentationData;
                if (docData != null) {
                    docData.m_read = false;
                }
            } else if (forceRelock) {
                data.m_progress = 0L;
                data.m_passedCount = 0;
                DocumentationData timeData2 = data as DocumentationData;
                if (timeData2 != null) {
                    timeData2.m_read = false;
                    m_unlocked.value = false;
                }
            } else {
                data.m_progress = TransferBrokerMod.Installed.IsDependencyMet(m_documentName) ? 1 : 0;
                data.m_passedCount = (data.m_progress == target) ? 1 : 0;
                if (data.m_progress == target) {
                    m_unlocked.value = true;
                }
            }
            return data;
        }

        public override void Serialize(DataSerializer s) {
            base.Serialize(s);
        }

        public override void Deserialize(DataSerializer s) {
            base.Deserialize(s);
        }

        public override float GetProgress() {
            Data data = m_data;
            if (data == null) {
                return 0f;
            }
            if (data.m_passedCount != 0) {
                return 1f;
            }
            int target = 1;
            return Mathf.Min(target, data.m_progress);
        }
    }
}