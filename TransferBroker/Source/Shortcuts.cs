namespace TransferBroker.Util {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Diagnostics;
    using ColossalFramework;
    using CSUtil.Commons;
    using TransferBroker.API.Manager;
    using TransferBroker.Manager.Impl;
    using UnityEngine;

    internal static class Shortcuts {
        internal static bool InSimulationThread() =>
            System.Threading.Thread.CurrentThread == SimulationManager.instance.m_simulationThread;

        /// <summary>
        /// returns a new calling Clone() on all items.
        /// </summary>
        /// <typeparam name="T">item time must be IClonable</typeparam>
        internal static IList<T> Clone<T>(this IList<T> listToClone)
            where T : ICloneable =>
            listToClone.Select(item => (T)item.Clone()).ToList();

        internal static void Swap<T>(ref T a, ref T b) {
            T temp = a;
            a = b;
            b = temp;
        }

        internal static void Swap<T>(this T[] array, int index1, int index2) {
            T temp = array[index1];
            array[index1] = array[index2];
            array[index2] = temp;
        }

        internal static void Swap<T>(this List<T> list, int index1, int index2) {
            T temp = list[index1];
            list[index1] = list[index2];
            list[index2] = temp;
        }

        private static Building[] _buildingBuffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

        internal static ref Building ToBuilding(this ushort buildingId) => ref _buildingBuffer[buildingId];

        internal static Func<bool, int> Int = (bool b) => b ? 1 : 0;

#if DEBUG
        private static int[] _frameCounts = new int[100];
        internal static void LogAndWait(string m, int waitFrames, ushort ID) {
            int frameCount = Time.frameCount;
            int diff = frameCount - _frameCounts[ID];
            if (diff < 0 || diff > waitFrames) {
                Log._Debug(m);
                _frameCounts[ID] = frameCount;
            }
        }
#else
        internal static void LogAndWait(string m, ushort ID) {
            
        }
#endif

        /// <summary>
        /// useful for easily debuggin inline functions
        /// to be used like this example:
        /// TYPE inlinefunctionname(...) => expression
        /// TYPE inlinefunctionname(...) => expression.LogRet("messege");
        /// </summary>
        internal static T LogRet<T>(this T a, string m) {
            Log._Debug(m + a);
            return a;
        }

        internal static string CenterString(this string stringToCenter, int totalLength) {
            int leftPadding = ((totalLength - stringToCenter.Length) / 2) + stringToCenter.Length;
            return stringToCenter.PadLeft(leftPadding).PadRight(totalLength);
        }

        /// <summary>
        /// Creates and string of all items with enumerable inpute as {item1, item2, item3}
        /// null argument returns "Null".
        /// </summary>
        internal static string ToSTR<T>(this IEnumerable<T> enumerable) {
            if (enumerable == null)
                return "Null";
            string ret = "{ ";
            foreach (T item in enumerable) {
                ret += $"{item}, ";
            }
            ret.Remove(ret.Length - 2, 2);
            ret += " }";
            return ret;
        }

        internal static void AssertEq<T>(T a, T b, string m = "")
            where T : IComparable {
            if (a.CompareTo(b) != 0) {
                Log.Error($"Assertion failed. Expected {a} == {b} | " + m);
            }
            System.Diagnostics.Debug.Assert(a.CompareTo(b) == 0, m);
        }

        internal static void AssertNEq<T>(T a, T b, string m = "")
            where T : IComparable {
            if (a.CompareTo(b) == 0) {
                Log.Error($"Assertion failed. Expected {a} != {b} | " + m);
            }
            System.Diagnostics.Debug.Assert(a.CompareTo(b) != 0, m);
        }

        internal static void AssertNotNull(object obj, string m = "") {
            if (obj == null) {
                Log.Error("Assertion failed. Expected not null: " + m);
            }
            System.Diagnostics.Debug.Assert(obj != null, m);
        }

        internal static void Assert(bool con, string m = "") {
            if (!con) {
                Log.Error("Assertion failed: " + m);
            }
            System.Diagnostics.Debug.Assert(con, m);
        }

        internal static bool ShiftIsPressed => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        internal static bool ControlIsPressed => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        internal static bool AltIsPressed => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

    }
}
