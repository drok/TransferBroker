#if !(DEBUG || LABS || EXPERIMENTAL)
namespace TransferBroker {
    public static class Versioning {
        public const string MyFileVersion = "0.4.0";
        public const uint MyFileVersionNum = 0x00040000;

    }
}
#endif
