namespace TransferBroker.API.Manager {

    public interface ICustomManager {
        // TODO documentation
#if false
        IServiceFactory Services { get; }
        void OnBeforeLoadData();
        void OnAfterLoadData();
        void OnBeforeSaveData();
        void OnAfterSaveData();
#endif
        void OnLevelLoaded();
        void OnLevelUnloading();
        void OnModLoaded();

        void OnModOutdated();
#if false
        void PrintDebugInfo();
#endif
    }
}