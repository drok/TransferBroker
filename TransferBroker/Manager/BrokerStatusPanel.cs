namespace TransferBroker.Manager.Impl {
    using System;
    using System.Reflection;
    using HarmonyLib;
    using ColossalFramework;
    using ColossalFramework.IO;
    using ColossalFramework.UI;
    using ColossalFramework.Threading;
    using global::TransferBroker.API.Manager;
    using CSUtil.Commons;
    using Priority_Queue;
    using UnityEngine;
    using UnityEngine.Assertions;
    using System.Threading;
    using System.Collections;
    using System.Diagnostics;
    using System.Collections.Generic;

    public class BrokerStatusPanel : UIPanel {
        //CityServiceWorldInfoPanel m_ActivatorPanel;
        //CityServiceVehicleWorldInfoPanel m_VehicleP;
        //// CityServiceVehicleWorldInfoPanel
        //private UIPanel m_VehiclePanel;
        // private UILabel TravelDistanceValue;
        private UILabel TravelDistanceLabel;
        private UILabel TravelDistanceValue;

        // private bool layoutDone;
        private WorldInfoPanel infoPanel;
        private UIPanel parentPanel;

#if LEAKTEST
        private byte[] MemoryLeakTest = new byte[100000000];
#endif
#if DEBUG
        private void DumpPanel(UIComponent c, string indent) {

            foreach (var i in c.components) {
                var label = i as UILabel;
                var panel = i as UIPanel;
                Log._DebugFormat("Panel child {0} {1} ({2}) children={3} pos={4} autosize={5} anchor={6} size={9} color={11} zOrder={12} relPos={14} text='{15}' rawText='{16}', vis={17} maxSize={18}",
                    indent,
                    i.name,
                    i.GetType(),
                    i.childCount,
                    i.position,
                    i.autoSize,
                    i.anchor,
                    i.area,
                    i.absolutePosition,
                    i.size,
                    label?.font,
                    i.color,
                    i.zOrder,
                    panel?.verticalSpacing,
                    i.relativePosition,
                    label?.text,
                    label?.rawText,
                    i.IsVisibleInParent(),
                    panel?.maximumSize
                    );
                if (i != c && i != c && i.childCount != 0 && indent.Length <= 4) {
                    DumpPanel(i, indent + "  ");
                }
            }
        }
#endif

        public override void Start() {
#if DEBUG
            Log._DebugFormat("{0}.Start() {1} parent.size={2} vis={3}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, parent.size, parent.isVisible);
#endif

            UILabel sampleLabel;

            autoFitChildrenVertically = true;
            autoLayout = true;
            wrapLayout = true;
            size = parent.components[1].size;
            anchor = UIAnchorStyle.Top | UIAnchorStyle.Right | UIAnchorStyle.Left;

            infoPanel = parent.parent.GetComponent<WorldInfoPanel>();
            parentPanel = parent as UIPanel;
            // parentPanel.backgroundSprite = "TextFieldPanelHovered";
            // parentPanel.backgroundSprite = null;
            parentPanel.autoFitChildrenVertically = true;
            // parentPanel.autoLayout = true;

            // (infoPanel.component as UIPanel).autoFitChildrenVertically = true;

            if (infoPanel as CityServiceVehicleWorldInfoPanel != null) {
                zOrder = 3;
                sampleLabel = parent.components[0].components[0] as UILabel;
            } else if (infoPanel as CitizenWorldInfoPanel != null) {
                zOrder = 4;
                sampleLabel = parent.components[2].components[0] as UILabel;
            } else if (infoPanel as CitizenVehicleWorldInfoPanel) {
                zOrder = 2;
                sampleLabel = parent.components[0].components[0] as UILabel;
            } else { /* TouristWorldInfoPanel */
                zOrder = 4;
                sampleLabel = parent.components[0] as UILabel;
            }

#if false
            EnableTransfer = AddUIComponent<UICheckBox>();
            EnableTransfer.name = "EnableTransfer";
            EnableTransfer.isChecked = true;
            EnableTransfer.autoSize = true;
            EnableTransfer.anchor = UIAnchorStyle.Left;
            EnableTransfer.label = AddUIComponent<UILabel>();
            if (sampleLabel != null) {
                EnableTransfer.label.text = "Enable Transfer";
                EnableTransfer.label.textScale = sampleLabel.textScale;
                EnableTransfer.label.color = sampleLabel.color;
            }
            EnableTransfer.label.anchor = UIAnchorStyle.Left;
#endif

            TravelDistanceLabel = AddUIComponent<UILabel>();
            if (sampleLabel != null) {
                TravelDistanceLabel.font = sampleLabel.font;
                TravelDistanceLabel.textScale = sampleLabel.textScale;
                TravelDistanceLabel.textColor = sampleLabel.textColor;
            }
            TravelDistanceLabel.name = "TravelDistanceLabel";
            TravelDistanceLabel.text = "Travel: ";
            TravelDistanceLabel.position = default(Vector3);
            TravelDistanceLabel.textAlignment = UIHorizontalAlignment.Left;
            TravelDistanceLabel.autoSize = true;

            TravelDistanceValue = AddUIComponent<UILabel>();
            if (sampleLabel != null) {
                TravelDistanceValue.font = sampleLabel.font;
                TravelDistanceValue.textScale = sampleLabel.textScale;
                TravelDistanceValue.color = sampleLabel.color;
            }
            TravelDistanceValue.name = "TravelDistanceValue";
            TravelDistanceValue.text = "3.14 km";
            TravelDistanceValue.position = default(Vector3);
            TravelDistanceValue.textAlignment = UIHorizontalAlignment.Left;
            TravelDistanceValue.autoSize = true;
            TravelDistanceValue.position = new Vector3(TravelDistanceLabel.size.x, 0, 0);

            var oldSize = (infoPanel.component as UIPanel).size;
            (infoPanel.component as UIPanel).size = new Vector2(oldSize.x, oldSize.y + size.y);
            (infoPanel.component as UIPanel).autoFitChildrenVertically = true;

            // if (infoPanel as TouristWorldInfoPanel != null) {
            //     Log._Debug("------------------------");
            //     DumpPanel(parent.parent, "");
            // }

            base.Start();
#if DEBUG
            Log._DebugFormat("{0}.Start() {1} END parent.size={2}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, parent.size);
#endif
        }
        public override void OnDestroy() {
#if DEBUG
            Log._DebugFormat("{0}.OnDestroy() {1} parent={2} vis={3}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, parent != null, isVisible);
#endif
#if LEAKTEST
            MemoryLeakTest = null;
#endif
#if DEBUG
            Log._DebugFormat("{0}.OnDestroy() {1} parent={2} vis={3}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, parent != null, isVisible);
#endif
            Destroy(TravelDistanceLabel);
            TravelDistanceLabel = null;
            Destroy(TravelDistanceValue);
            TravelDistanceValue = null;
            // Destroy(EnableTransfer);

            base.OnDestroy();
        }

#if DEBUG_UI_TEARDOWN
        public void DebugMessage(string val) {
            Log._DebugFormat("{0}.DebugMessage() {1} parent={2} ({4}) vis={3} inv={5} leak={6}B", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, parent != null, isVisible, infoPanel.GetType().Name, m_IsComponentInvalidated, MemoryLeakTest?.Length);
            if (TravelDistanceValue != null) {
                // TravelDistanceValue.text = $"{ pathUnit.m_length / 1000:F2} km";
                TravelDistanceValue.text = $"{Assembly.GetExecutingAssembly().GetName().Version} - {val}";
            }
        }
#endif

        public override void OnDisable() {
#if DEBUG
            Log._DebugFormat("{0}.OnDisable() {1} parent={2} ({4}) vis={3}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, parent != null, isVisible, infoPanel.GetType().Name);
#endif
            var oldSize = (infoPanel.component as UIPanel).size;
            // (infoPanel.component as UIPanel).autoSize = true;
            (infoPanel.component as UIPanel).size = new Vector2(oldSize.x, oldSize.y - size.y);

            base.OnDisable();
        }

#if DEBUG
        BrokerStatusPanel() {
            Log._DebugFormat("{0}..ctor() {1}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version);
        }

        ~BrokerStatusPanel() {
#if LEAKTEST
        Log._DebugFormat("{0}..dtor() {1} Leak={2} bytes", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version, MemoryLeakTest?.Length);
#else
            Log._DebugFormat("{0}..dtor() {1}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version);
#endif

        }
#endif

        public void UpdateBindings() {

            Assert.IsTrue(Dispatcher.currentSafe == ThreadHelper.dispatcher,
                $"{GetType().Name}.OnEnabled() should only be called on Main Thread (not '{Thread.CurrentThread.Name}')");

            InstanceID id = WorldInfoPanel.GetCurrentInstanceID();
            uint path;

            switch (id.Type) {
                case InstanceType.Vehicle:
                    /* FIXME: Copied this hack from CitizenWorldInfoPanel ... see if it can be removed */
                    if (!Singleton<VehicleManager>.exists) {
                        return;
                    }

                    var firstVehicle = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[id.Vehicle].GetFirstVehicle(id.Vehicle);
                    path = Singleton<VehicleManager>.instance.m_vehicles.m_buffer[firstVehicle].m_path;
                    break;
                case InstanceType.Citizen:
                    if (!Singleton<CitizenManager>.exists) {
                        return;
                    }
                    var instance = Singleton<CitizenManager>.instance.m_citizens.m_buffer[id.Citizen].m_instance;
                    path = Singleton<CitizenManager>.instance.m_instances.m_buffer[instance].m_path;
                    break;
                case InstanceType.CitizenInstance:
                    path = Singleton<CitizenManager>.instance.m_instances.m_buffer[id.CitizenInstance].m_path;
                    break;
                default:
                    path = 0;
                    break;
            }

            if (path != 0) {
                var pathUnit = Singleton<PathManager>.instance.m_pathUnits.m_buffer[path];

                /* Check if null because when live loading and a WorldInfo is open, Start() may be called after UpdateBindings */
                if (TravelDistanceValue != null) {
#if DEBUG_UI_TEARDOWN
                    TravelDistanceValue.text = $"{Assembly.GetExecutingAssembly().GetName().Version}";
#else
                    TravelDistanceValue.text = $"{ pathUnit.m_length / 1000:F2} km";
#endif
                }

                if (!isVisible) {
                    isVisible = true;
                }
            } else {
                // Used to hide it for parked vehicles
                if (isVisible) {
                    isVisible = false;
                }
            }
        }

        public override void Update() {
            // Log._DebugFormat("{0}.Update() {1}", GetType().Name, Assembly.GetExecutingAssembly().GetName().Version);
            base.Update();
        }

    }
}
