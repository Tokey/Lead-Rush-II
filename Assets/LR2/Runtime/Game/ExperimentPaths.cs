using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LR2.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LR2.Game
{
    /// <summary>
    /// Resolves Lead Rush II's external <c>Data/</c> research root beside the launched application
    /// (<c>&lt;project&gt;/Data/</c> in the Editor, <c>&lt;exe dir&gt;/Data/</c> in standalone builds — never buried in AppData),
    /// modeled on the InvasionFramerate study logger: monotonic <c>SessionState.csv</c>, collision-safe
    /// <c>&lt;sessionId&gt;_&lt;runId&gt;</c> session folders, hardware/config audit (<c>SessionInfo_*.csv</c>),
    /// and block/match ledger (<c>SessionLog_*.csv</c>).
    /// </summary>
    public static class ExperimentPaths
    {
        public const string SessionStateFile = "SessionState.csv";
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        private static string _root;

        /// <summary><c>&lt;project root | exe dir&gt;/Data</c> — created on demand.</summary>
        public static string Root
        {
            get
            {
                if (!string.IsNullOrEmpty(_root)) return _root;
                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                string baseDir = parent != null ? parent.FullName : Application.dataPath;
                _root = Path.GetFullPath(Path.Combine(baseDir, "Data"));
                Directory.CreateDirectory(_root);
                return _root;
            }
        }

        public static string LogsRoot
        {
            get
            {
                string d = Path.Combine(Root, "Logs");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string PresetsRoot
        {
            get
            {
                string d = Path.Combine(Root, "Presets");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string ProfilesRoot
        {
            get
            {
                string d = Path.Combine(Root, "Profiles");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string SessionStatePath => Path.Combine(Root, SessionStateFile);

        /// <summary>Reads <c>Data/SessionState.csv</c> without advancing it (defaults to 1). Abandoned runs safely reuse the ID under a fresh <see cref="NewRunId"/>.</summary>
        public static int PeekNextSessionId()
        {
            try
            {
                if (!File.Exists(SessionStatePath))
                {
                    WriteSessionState(1);
                    return 1;
                }
                var lines = File.ReadAllLines(SessionStatePath);
                if (lines.Length >= 2)
                {
                    var parts = lines[1].Split(',');
                    if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id >= 1)
                        return id;
                }
            }
            catch (Exception) { }
            WriteSessionState(1);
            return 1;
        }

        public static void AdvanceSessionId(int completedSessionId) => WriteSessionState(Mathf.Max(1, completedSessionId) + 1);

        private static void WriteSessionState(int nextId)
        {
            try
            {
                Directory.CreateDirectory(Root);
                var sb = new StringBuilder();
                sb.Append("nextSessionId,lastUpdatedIso\n");
                sb.Append(Mathf.Max(1, nextId).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(SessionStatePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LR2] Could not write SessionState.csv: {e.Message}");
            }
        }

        /// <summary>Generates a 4-character non-ambiguous run ID (e.g. <c>Ab21</c>) so retried sessions never collide.</summary>
        public static string NewRunId()
        {
            var chars = new char[4];
            var rng = new System.Random(unchecked((int)(DateTime.UtcNow.Ticks ^ Environment.TickCount)));
            for (int i = 0; i < chars.Length; i++)
                chars[i] = Alphabet[rng.Next(Alphabet.Length)];
            return new string(chars);
        }

        public static string SessionLogDir(int sessionId, string runId)
        {
            string dir = Path.Combine(LogsRoot, $"{sessionId}_{runId}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string AdHocLogDir(string runId)
        {
            string dir = Path.Combine(LogsRoot, $"adhoc_{DateTime.Now:yyyyMMdd_HHmmss}_{runId}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Returns a collision-free file path inside <paramref name="dir"/> (appends <c>_2</c>, <c>_3</c>, ... if taken so existing participant logs are never truncated).</summary>
        public static string UniqueFile(string dir, string prefix, string idTag, string ext = ".csv")
        {
            Directory.CreateDirectory(dir);
            for (int dedup = 0; dedup < 1000; dedup++)
            {
                string name = dedup == 0
                    ? $"{prefix}_{idTag}{ext}"
                    : $"{prefix}_{idTag}_{dedup + 1}{ext}";
                string path = Path.Combine(dir, name);
                if (!File.Exists(path)) return path;
            }
            return Path.Combine(dir, $"{prefix}_{idTag}_{Guid.NewGuid().ToString("N").Substring(0, 4)}{ext}");
        }

        /// <summary>Writes hardware, OS, display, input hardware, LR2 settings, and active experiment config SHA-256 to <c>SessionInfo_&lt;id&gt;_&lt;runId&gt;.csv</c>.</summary>
        public static void WriteSessionInfoCsv(string dir, int sessionId, string runId, string sessionConfigJson = "")
        {
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var rows = new List<KeyValuePair<string, string>>(48);
                void Add(string k, object v) => rows.Add(new KeyValuePair<string, string>(k, Convert.ToString(v, inv) ?? ""));

                Add("session.sessionId", sessionId);
                Add("session.runId", runId);
                Add("session.writtenIso", DateTime.Now.ToString("o", inv));

                Add("app.productName", Application.productName);
                Add("app.version", Application.version);
                Add("app.unityVersion", Application.unityVersion);
                Add("app.platform", Application.platform);
                Add("app.isEditor", Application.isEditor);
                Add("app.buildGuid", Application.buildGUID);

                Add("system.os", SystemInfo.operatingSystem);
                Add("system.cpu", SystemInfo.processorType);
                Add("system.cpuCount", SystemInfo.processorCount);
                Add("system.memoryMB", SystemInfo.systemMemorySize);
                Add("system.gpu", SystemInfo.graphicsDeviceName);
                Add("system.gpuApi", SystemInfo.graphicsDeviceType);
                Add("system.gpuMemoryMB", SystemInfo.graphicsMemorySize);

                Add("display.width", Screen.width);
                Add("display.height", Screen.height);
                Add("display.fullScreenMode", Screen.fullScreenMode);
                Add("display.refreshHz", Screen.currentResolution.refreshRateRatio.value.ToString("0.##", inv));
                Add("display.dpi", Screen.dpi);
                Add("quality.level", QualitySettings.names.Length > 0 ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "");
                Add("quality.vSyncCount", QualitySettings.vSyncCount);

                Mouse mouse = Mouse.current;
                Add("input.mouse", mouse != null ? (mouse.description.product + " / " + mouse.displayName) : "");
                Add("input.keyboard", Keyboard.current != null ? Keyboard.current.displayName : "");

                Add("lr2.playerName", LR2Settings.PlayerName);
                Add("lr2.mouseSensitivity", LR2Settings.MouseSensitivity.ToString("0.###", inv));
                Add("lr2.adsSensitivityMult", LR2Settings.AdsSensitivityMultiplier.ToString("0.###", inv));
                Add("lr2.fov", LR2Settings.FieldOfView.ToString("0.#", inv));
                Add("lr2.upscalerMode", LR2Settings.UpscalerMode);
                Add("lr2.recordTelemetry", LR2Settings.RecordTelemetry);

                if (!string.IsNullOrEmpty(sessionConfigJson))
                {
                    using (var sha = SHA256.Create())
                    {
                        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sessionConfigJson));
                        Add("config.sessionSha256", BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
                    }
                    Add("config.sessionBytes", Encoding.UTF8.GetByteCount(sessionConfigJson));
                }

                string path = UniqueFile(dir, "SessionInfo", $"{sessionId}_{runId}", ".csv");
                var sb = new StringBuilder();
                sb.Append("key,value\n");
                foreach (var kv in rows)
                    sb.Append(CsvEsc(kv.Key)).Append(',').Append(CsvEsc(kv.Value)).Append('\n');
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LR2] Failed to write SessionInfo CSV: {e.Message}");
            }
        }

        public static string EnsureSessionLogFile(string dir, string idTag)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"SessionLog_{idTag}.csv");
            if (!File.Exists(path))
            {
                File.WriteAllText(path,
                    "session_id,run_id,match_index,condition_tag,label,map,mode,blind_mode,duration_s,alpha_score,bravo_score," +
                    "human_kills,human_deaths,human_acc_pct,human_hs_pct,frame_count,avg_frame_ms,p95_frame_ms,p99_frame_ms,slow_frames_33ms,written_iso\n",
                    new UTF8Encoding(false));
            }
            return path;
        }

        public static void AppendSessionLogRow(string sessionLogPath, int sessionId, string runId, int matchIndex,
                                               string conditionTag, string label, string mapName, string modeName,
                                               bool blind, float durationS, int alphaScore, int bravoScore,
                                               int hKills, int hDeaths, float hAccPct, float hHsPct,
                                               int frameCount, float avgFrameMs, float p95FrameMs, float p99FrameMs, int slowFrames)
        {
            if (string.IsNullOrEmpty(sessionLogPath)) return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                string row = string.Format(inv,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8:0.00},{9},{10},{11},{12},{13:0.00},{14:0.00},{15},{16:0.000},{17:0.000},{18:0.000},{19},{20}\n",
                    sessionId, CsvEsc(runId), matchIndex, CsvEsc(conditionTag), CsvEsc(label), CsvEsc(mapName), CsvEsc(modeName),
                    blind ? 1 : 0, durationS, alphaScore, bravoScore, hKills, hDeaths, hAccPct, hHsPct,
                    frameCount, avgFrameMs, p95FrameMs, p99FrameMs, slowFrames, DateTime.Now.ToString("o", inv));
                File.AppendAllText(sessionLogPath, row, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LR2] Failed to append SessionLog row: {e.Message}");
            }
        }

        private static string CsvEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                return "\"" + s.Replace('"', '\'') + "\"";
            return s;
        }
    }
}
