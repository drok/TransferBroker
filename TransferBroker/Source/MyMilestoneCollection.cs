#if DEBUG
/* Used only to debug threading, as a proxy for MilestoneCollection built in class */
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;
using CSUtil.Commons;

public class MyMilestoneCollection : MonoBehaviour {
    public MilestoneInfo[] m_Milestones;

    private Dictionary<string, MilestoneInfo> m_dict = new Dictionary<string, MilestoneInfo>();

    private void Awake() {
        Log.Info($"{GetType().Name}.Awake() called.");
        InitializeMilestones(m_Milestones);
        Log.Info($"{GetType().Name}.Awake() done.");
    }

    private void OnDestroy() {
        Log.Info($"{GetType().Name}.OnDestroy() called.");
        DestroyMilestones(m_Milestones);
        Log.Info($"{GetType().Name}.OnDestroy() done.");
    }

    public MilestoneInfo FindMilestone(string name) {
        if (m_dict.TryGetValue(name, out var value)) {
            return value;
        }
        CODebugBase<LogChannel>.Warn(LogChannel.Serialization, "Unknown milestone: " + name);
        return null;
    }

    public void InitializeMilestones(MilestoneInfo[] milestones) {
        foreach (MilestoneInfo milestoneInfo in milestones) {
            if (m_dict.ContainsKey(milestoneInfo.name)) {
                CODebugBase<LogChannel>.Error(LogChannel.Core, "Duplicate milestone name: " + milestoneInfo.name);
            } else {
                m_dict.Add(milestoneInfo.name, milestoneInfo);
            }
        }
    }

    public void DestroyMilestones(MilestoneInfo[] milestones) {
        foreach (MilestoneInfo milestoneInfo in milestones) {
            m_dict.Remove(milestoneInfo.name);
        }
    }

    public MilestoneInfo[] GetManualMilestones(ManualMilestone.Type type) {
        List<MilestoneInfo> list = new List<MilestoneInfo>();
        foreach (KeyValuePair<string, MilestoneInfo> item in m_dict) {
            ManualMilestone manualMilestone = item.Value as ManualMilestone;
            if (manualMilestone != null && !manualMilestone.m_hidden && manualMilestone.m_type == type && manualMilestone.m_prefab != null) {
                list.Add(item.Value);
            }
        }
        return list.ToArray();
    }
}
#endif
