namespace TransferBroker.Manager.Impl {
    using System.Collections;
    using ColossalFramework;
    using ColossalFramework.Plugins;
    using UnityEngine;
    using UnityEngine.Assertions;
    using CSUtil.Commons;
    using System.Reflection;


    [ExecuteInEditMode]
    public class BrokerProperties : MonoBehaviour {

        private TransferBrokerMod mod;

        public BrokerProperties() {
            mod = Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly()).userModInstance as TransferBrokerMod;

            Assert.IsTrue(mod != null,
                $"An instance of {mod.GetType().Name} should already exist when {GetType().Name} is instantiated");
        }

        private void Awake() {
            Log._Debug($"{GetType().Name}.Awake()");

            if (Application.isPlaying) {
                Singleton<LoadingManager>.instance.QueueLoadingAction(InitializeProperties());
            }
        }

        private IEnumerator InitializeProperties() {
            Log._Debug($"{GetType().Name}.InitializeProperties()");
            Singleton<LoadingManager>.instance.m_loadingProfilerMain.BeginLoading("BrokerProperties");
            mod.broker.InitializeProperties(this);
            Singleton<LoadingManager>.instance.m_loadingProfilerMain.EndLoading();
            yield return 0;
        }

        private void OnDestroy() {
            Log._Debug($"{GetType().Name}.OnDestroy()");
            if (Application.isPlaying) {
                Singleton<LoadingManager>.instance.m_loadingProfilerMain.BeginLoading("BrokerProperties");
                mod.broker.DestroyProperties(this);
                Singleton<LoadingManager>.instance.m_loadingProfilerMain.EndLoading();
            }
        }
    }
}
