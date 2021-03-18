namespace TransferBroker.API.Manager {
    public interface ITransferBroker {
        void AddOutgoingOffer(TransferManager.TransferReason material, TransferManager.TransferOffer offer);
        void AddIncomingOffer(TransferManager.TransferReason material, TransferManager.TransferOffer offer);
        void RemoveOffers(TransferManager.TransferReason material, TransferManager.TransferOffer offer);
        void UpdateData(SimulationManager.UpdateMode mode);

#if MAINTAIN_CONNECTIONS
        void ActivateCargoStation(ushort buildingID, ref Building buildingData);
        void DeactivateCargoStation(ushort buildingID, ref Building buildingData);

        void CreateNode(ushort node, ref NetNode data);
        void DeleteNode(ushort node, ref NetNode data);
#endif

        void Pause();
        void Continue();

#if DEBUG
        // void LogOffer(string prefix, ushort id, TransferManager.TransferReason material, TransferManager.TransferOffer offer);
        void LogTransfer(string prefix, TransferManager.TransferReason material, TransferManager.TransferOffer offerOut, TransferManager.TransferOffer offerIn, int delta);
#endif
        // bool IsSupported(TransferManager.TransferReason material);
    }
}
