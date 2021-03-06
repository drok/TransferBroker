/*
The MIT License (MIT)

Copyright (c) 2015 Svetlozar

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
 */

namespace CSUtil.Commons {
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using ColossalFramework.IO;

#if !DEBUG
    #if TRACE
        #error TRACE is defined outside of a DEBUG build, please remove
    #endif
#endif

    /// <summary>
    /// Log.Trace, Log.Debug, Log.Info, Log.Warning, Log.Error -- these format the message in place,
    ///     ✔ Cheap if there is a const string or a very simple format call with a few args.
    ///     ✔ Cheap if wrapped in an if (booleanValue) { ... }
    ///     Log.Debug and Log.Trace are optimized away if not in Debug mode
    ///     ⚠ Expensive if multiple $"string {interpolations}" are used (like breaking into multiple lines)
    ///
    /// Log.DebugFormat, Log.InfoFormat, ... - these format message later, when logging. Good for
    /// very-very long format strings with multiple complex arguments.
    ///     ✔ As they use format string literal, it can be split multiple lines without perf penalty
    ///     💲 The cost incurred: bulding args array (with pointers)
    ///     Prevents multiple calls to string.Format as opposed to multiline $"string {interpolations}"
    ///     Log.DebugFormat is optimized away, others are not, so is a good idea to wrap in if (boolValue)
    ///     ⚠ Expensive if not wrapped in a if () condition
    ///
    /// Log.DebugIf, Log.WarningIf, ... - these first check a condition, and then call a lambda,
    /// which provides a formatted string.
    ///     ✔ Lambda building is just as cheap as format args building
    ///     💲 The cost incurred: each captured value (pointer) is copied into lambda
    ///     ✔ Actual string is formatted ONLY if the condition holds true
    ///     Log.DebugIf is optimized away if not in Debug mode
    ///     ⚠ Cannot capture out and ref values
    ///
    /// Log.NotImpl logs an error if something is not implemented and only in debug mode
    /// </summary>
    internal static class Log {
        private static readonly object LogLock = new object();

        // TODO refactor log filename to configuration
        private static readonly string LogFilename
            = Path.Combine(DataLocation.localApplicationData, $"{typeof(TransferBroker.TransferBrokerMod).Name}.log");

        private enum LogLevel {
            Trace,
            Debug,
            Info,
            Warning,
            Error,
        }

        private static Stopwatch _sw = Stopwatch.StartNew();

        static Log() {
#if false
            try {
            if (File.Exists(LogFilename)) {
                   File.Delete(LogFilename);
               }
           }
           catch (Exception) {
           }
#endif
        }

        /// <summary>
        /// Will log only if debug mode
        /// </summary>
        /// <param name="s">The text</param>
        [Conditional("DEBUG")]
        public static void _Debug(string s) {
            LogToFile(s, LogLevel.Debug);
        }

        /// <summary>
        /// Will log only if debug mode, the string is prepared using string.Format
        /// </summary>
        /// <param name="format">The text</param>
        [Conditional("DEBUG")]
        public static void _DebugFormat(string format, params object[] args) {
            LogToFile(string.Format(format, args), LogLevel.Debug);
        }

        /// <summary>
        /// Will log only if debug mode is enabled and the condition is true
        /// NOTE: If a lambda contains values from `out` and `ref` scope args,
        /// then you can not use a lambda, instead use `if (cond) { Log._Debug }`
        /// </summary>
        /// <param name="cond">The condition</param>
        /// <param name="s">The function which returns text to log</param>
        // TODO: Add log thread and replace formatted strings with lists to perform late formatting in that thread
        [Conditional("DEBUG")]
        public static void _DebugIf(bool cond, Func<string> s) {
            if (cond) {
                LogToFile(s(), LogLevel.Debug);
            }
        }

        [Conditional("TRACE")]
        public static void _Trace(string s) {
            LogToFile(s, LogLevel.Trace);
        }

        public static void Info(string s) {
            LogToFile(s, LogLevel.Info);
        }

        public static void InfoFormat(string format, params object[] args) {
            LogToFile(string.Format(format, args), LogLevel.Info);
        }

        /// <summary>
        /// Will log a warning only if debug mode
        /// </summary>
        /// <param name="s">The text</param>
        [Conditional("DEBUG")]
        public static void _DebugOnlyWarning(string s) {
            LogToFile(s, LogLevel.Warning);
        }

        /// <summary>
        /// Log a warning only in debug mode if cond is true
        /// NOTE: If a lambda contains values from `out` and `ref` scope args,
        /// then you can not use a lambda, instead use `if (cond) { Log._DebugOnlyWarning() }`
        /// </summary>
        /// <param name="cond">The condition</param>
        /// <param name="s">The function which returns text to log</param>
        [Conditional("DEBUG")]
        public static void _DebugOnlyWarningIf(bool cond, Func<string> s) {
            if (cond) {
                LogToFile(s(), LogLevel.Warning);
            }
        }

        public static void Warning(string s) {
            LogToFile(s, LogLevel.Warning);
        }

        public static void WarningFormat(string format, params object[] args) {
            LogToFile(string.Format(format, args), LogLevel.Warning);
        }

        /// <summary>
        /// Log a warning only if cond is true
        /// NOTE: If a lambda contains values from `out` and `ref` scope args,
        /// then you can not use a lambda, instead use `if (cond) { Log.Warning() }`
        /// </summary>
        /// <param name="cond">The condition</param>
        /// <param name="s">The function which returns text to log</param>
        public static void WarningIf(bool cond, Func<string> s) {
            if (cond) {
                LogToFile(s(), LogLevel.Warning);
            }
        }

        public static void Error(string s) {
            LogToFile(s, LogLevel.Error);
        }

        public static void ErrorFormat(string format, params object[] args) {
            LogToFile(string.Format(format, args), LogLevel.Error);
        }

        /// <summary>
        /// Log an error only if cond is true
        /// NOTE: If a lambda contains values from `out` and `ref` scope args,
        /// then you can not use a lambda, instead use `if (cond) { Log.Error() }`
        /// </summary>
        /// <param name="cond">The condition</param>
        /// <param name="s">The function which returns text to log</param>
        public static void ErrorIf(bool cond, Func<string> s) {
            if (cond) {
                LogToFile(s(), LogLevel.Error);
            }
        }

        /// <summary>
        /// Log error only in debug mode
        /// </summary>
        /// <param name="s">The text</param>FAILED
        [Conditional("DEBUG")]
        public static void _DebugOnlyError(string s) {
            LogToFile(s, LogLevel.Error);
        }

        /// <summary>
        /// Writes an Error message about something not implemented. Debug only.
        /// </summary>
        /// <param name="what">The hint about what is not implemented</param>
        [Conditional("DEBUG")]
        public static void NotImpl(string what) {
            LogToFile("Not implemented: " + what, LogLevel.Error);
        }

        private static void LogToFile(string log, LogLevel level) {
            try {
                Monitor.Enter(LogLock);

                using (StreamWriter w = File.AppendText(LogFilename)) {
                    long secs = _sw.ElapsedTicks / Stopwatch.Frequency;
                    long fraction = _sw.ElapsedTicks % Stopwatch.Frequency;
                    w.WriteLine(
                        $"{level.ToString(),7}.{Thread.CurrentThread.Name,-13} " +
                        $"{secs:n0}.{fraction:D7}: " +
                        $"{log}");

                    if (level == LogLevel.Warning || level == LogLevel.Error) {
                        w.WriteLine((new System.Diagnostics.StackTrace(2, true)).ToString());
                        w.WriteLine();
                    }
                }
            }
#pragma warning disable CS0168 // Variable is declared but never used
            catch (IOException ex) {
#pragma warning restore CS0168 // Variable is declared but never used
                              // do nothing, allow another attempt.
                              // (ex.Data)
                /* FIXME: If this keeps failing, eg disk full, it can't stop the mod from working */
                //UnityEngine.Debug.Log($"[FAILED to log to {LogFilename} ({ex.Message})] : {log}");
            }
            finally {
                Monitor.Exit(LogLock);
            }
        }
    }
}
