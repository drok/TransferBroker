namespace TransferBroker.Util {
    using ColossalFramework.Plugins;
    using ColossalFramework.UI;
    using ColossalFramework;
    using CSUtil.Commons;
    using ICities;
    using static ColossalFramework.Plugins.PluginManager;
    using System.Collections.Generic;
    using static System.Array;
    using System.IO;
    using System.Reflection;
    using System;
    using UnityEngine;

    public class ModsCompatibilityChecker {
        // Game always uses ulong.MaxValue to depict local mods
        private const ulong LOCAL_MOD = ulong.MaxValue;

        // parsed contents of incompatible_mods.txt
        private readonly HashSet<string> knownIncompatibleMods = new HashSet<string> {
                "EnhancedHearseAI.Identity", // Enhanced Hearse AI both [ARIS] and non-[ARIS] versions
                "EnhancedGarbageTruckAI.Identity", // [ARIS] Enhanced Garbage Truck AI both [ARIS] and non-[ARIS] versions
                "ServicesOptimizationModule.Identity", // SOM - Services Optimisation Module
                "GSteigertDistricts.GSteigertMod", // Geli-Districts v3.1
                "EnhancedDistrictServices.EnhancedDistrictServicesMod", // Enhanced District Services
        };

        /// <summary>
        /// Iterates installed mods looking for known incompatibilities.
        /// </summary>
        /// <returns>A list of detected incompatible mods.</returns>
        /// <exception cref="ArgumentException">Invalid folder path (contains invalid characters,
        ///     is empty, or contains only white spaces).</exception>
        /// <exception cref="PathTooLongException">Path is too long (longer than the system-defined
        ///     maximum length).</exception>
        public bool PerformModCheck(TransferBrokerMod self) {
            // batch all logging in to a single log message
            string logStr = $"{TransferBrokerMod.PACKAGE_NAME} Incompatible Mod Checker:\n\n";

            // list of installed incompatible mods
            int result = 0;

            // iterate plugins
            foreach (PluginInfo mod in Singleton<PluginManager>.instance.GetPluginsInfo()) {
                if (!mod.isCameraScript) {
                    string strModName = GetModName(mod);
                    ulong workshopID = mod.publishedFileID.AsUInt64;
                    bool isLocal = workshopID == LOCAL_MOD;

                    string strEnabled = mod.isEnabled ? "*" : " ";
                    var modDir = new System.IO.DirectoryInfo(mod.modPath);
                    string strWorkshopId = mod.isBuiltin ? "(built-in)" : isLocal ? $"(local '{modDir.Name}')" : workshopID.ToString();
                    string strIncompatible = " ";

                    //                    Log.Info($"Compare {GetModGuid(mod)} with {selfGuid}");
                    // selfGuid == GetModGuid(mod)
                    if (self == mod.userModInstance) {
                        strIncompatible = "S";
                    } else if (knownIncompatibleMods.Contains(mod.userModInstance.GetType().FullName)) {
                        strIncompatible = "!";
                        if (mod.isEnabled) {
                            Debug.Log($"[{self.Name}] Incompatible mod detected: " + strModName);
                            ++result;
                        }
                    } else if (mod.userModInstance.GetType().FullName == self.GetType().FullName) {
                        string strFolder = Path.GetFileName(mod.modPath);
                        if (IsNewer(mod.userModInstance, self)) {
                            strIncompatible = ">";
                            Debug.Log(
                                $"[{self.Name}] Newer instance detected: {strModName} in {strFolder}{(mod.isEnabled ? string.Empty : " (disabled)")}");
                            result += mod.isEnabled ? 1 : 0;
                        } else {
                            strIncompatible = "o";
                            Debug.Log(
                                $"[{self.Name}] Duplicate or obsolete instance detected: {strModName} in {strFolder}{(mod.isEnabled ? string.Empty : " (disabled)")}");
                        }
                    }

                    logStr +=
                        $"{strIncompatible} {strEnabled} {strWorkshopId.PadRight(28)} {strModName}\n";
                }
            }

            Log.Info(logStr);
            Log.Info("Scan complete: " + result + " incompatible mod(s) found");

            return result == 0;
        }

        private bool IsNewer(object mod, TransferBrokerMod self) {
            return mod.GetType().Assembly.GetName().Version > Assembly.GetExecutingAssembly().GetName().Version ||
                (mod is TransferBrokerMod && (mod as TransferBrokerMod).ImplementationVersion > self.ImplementationVersion);
        }

        private bool IsSame(object mod) {
            return mod.GetType().FullName == Assembly.GetExecutingAssembly().GetName().Name;
        }

        /// <summary>
        /// Gets the name of the specified mod.
        /// It will return the <see cref="IUserMod.Name"/> if found, otherwise it will return
        /// <see cref="PluginInfo.name"/> (assembly name).
        /// </summary>
        /// <param name="plugin">The <see cref="PluginInfo"/> associated with the mod.</param>
        /// <returns>The name of the specified plugin.</returns>
        private string GetModName(PluginInfo plugin) {
            try {
                if (plugin == null) {
                    return "(PluginInfo is null)";
                }

                if (plugin.userModInstance == null) {
                    return string.IsNullOrEmpty(plugin.name)
                        ? "(userModInstance and name are null)"
                        : $"({plugin.name})";
                }

                return ((IUserMod)plugin.userModInstance).Name;
            }
            catch {
                return $"(error retreiving Name)";
            }
        }

        /// <summary>
        /// Gets the <see cref="Guid"/> of a mod.
        /// </summary>
        /// <param name="plugin">The <see cref="PluginInfo"/> associated with the mod.</param>
        /// <returns>The <see cref="Guid"/> of the mod.</returns>
        private Guid GetModGuid(PluginInfo plugin) {
            return plugin.userModInstance.GetType().Assembly.ManifestModule.ModuleVersionId;
        }

    }
}
