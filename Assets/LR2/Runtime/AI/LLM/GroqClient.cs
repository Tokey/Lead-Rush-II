using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace LR2.AI.LLM
{
    /// <summary>
    /// B127 (user 11-20: "implement a system to control the commander thing with LLM ... groq api, free tier only").
    /// Minimal client for Groq's OpenAI-compatible chat completions endpoint, sized for the free tier: one request at a time,
    /// a minimum gap between requests, back-off on HTTP 429 that honours Retry-After, and the rate-limit headers kept for the HUD
    /// and logs. Free-tier limits are per model and per organisation and change over time; the Groq console's limits page is
    /// the authority.
    ///
    /// The API key is read from, in order: the environment variable GROQ_API_KEY, the file given by -lr2GroqKeyFile, or the
    /// first line of &lt;Application.persistentDataPath&gt;/groq_api_key.txt. It is never written, logged or sent anywhere but
    /// the Authorization header to api.groq.com. Keep it out of the repository.
    /// </summary>
    public static class GroqClient
    {
        public const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";
        public const string KeyFileName = "groq_api_key.txt";

        public sealed class Result
        {
            public bool ok;
            public long status;
            public string model;
            public string content;
            public string error;
            public float seconds;
            public int promptTokens, completionTokens;
            public float retryAfter;
            public string remainingRequests, remainingTokens;
            /// <summary>The model name was rejected (unknown or retired): try the next one.</summary>
            public bool modelRejected;
        }

        /// <summary>Smallest gap between two requests from this game (s), across all commanders.</summary>
        public static float MinGapSeconds = 2.5f;
        public static int Requests { get; private set; }
        public static int Failures { get; private set; }
        public static int RateLimited { get; private set; }
        public static string KeySource { get; private set; } = "none";
        public static bool Busy { get; private set; }

        private static string _key;
        private static bool _keyLoaded;
        private static float _blockedUntil;
        private static float _lastRequestAt = -999f;

        public static bool HasKey => !string.IsNullOrEmpty(LoadKey());

        /// <summary>Where the game looks for the key file on this machine (outside the project).</summary>
        public static string DefaultKeyPath => Path.Combine(Application.persistentDataPath, KeyFileName);

        public static string LoadKey()
        {
            if (_keyLoaded) return _key;
            _keyLoaded = true;
            string k = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (!string.IsNullOrWhiteSpace(k)) { _key = k.Trim(); KeySource = "environment GROQ_API_KEY"; return _key; }
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-lr2GroqKeyFile") { _key = ReadFirstLine(args[i + 1]); if (_key != null) { KeySource = "key file (command line)"; return _key; } }
            _key = ReadFirstLine(DefaultKeyPath);
            if (_key != null) { KeySource = "key file in persistentDataPath"; return _key; }
            var fallbacks = new System.Collections.Generic.List<string>
            {
                Path.Combine(Application.persistentDataPath, "groq_api_key..txt")
            };
            var parent = Directory.GetParent(Application.persistentDataPath);
            if (parent != null)
            {
                fallbacks.Add(Path.Combine(parent.FullName, "LeadRush 2", KeyFileName));
                fallbacks.Add(Path.Combine(parent.FullName, "LeadRush 2", "groq_api_key..txt"));
                fallbacks.Add(Path.Combine(parent.FullName, "Lead Rush II", KeyFileName));
                fallbacks.Add(Path.Combine(parent.FullName, "Lead Rush II", "groq_api_key..txt"));
            }
            foreach (var fb in fallbacks)
            {
                _key = ReadFirstLine(fb);
                if (_key != null) { KeySource = "key file in fallback persistentDataPath"; return _key; }
            }
            return _key;
        }

        /// <summary>Forget the cached key so a key file written while the game runs is picked up.</summary>
        public static void ReloadKey() { _keyLoaded = false; _key = null; KeySource = "none"; }

        private static string ReadFirstLine(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                foreach (var line in File.ReadAllLines(path))
                    if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }
            catch (Exception) { }
            return null;
        }

        public static bool CanSendNow
        {
            get
            {
                // A request cannot outlive its 15 s timeout. If Busy is still set long after that, the coroutine that owned it
                // died with its scene (Unity drops a destroyed host's coroutines without running their finally blocks), and
                // without this every later request would wait forever.
                if (Busy && Time.realtimeSinceStartup - _lastRequestAt > RequestTimeoutSeconds + 5f) Busy = false;
                return !Busy && Time.realtimeSinceStartup >= _blockedUntil && Time.realtimeSinceStartup - _lastRequestAt >= MinGapSeconds;
            }
        }

        private const int RequestTimeoutSeconds = 15;

        /// <summary>Play mode without a domain reload keeps statics: start every session idle.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Busy = false;
            _blockedUntil = 0f;
            _lastRequestAt = -999f;
        }

        /// <summary>One chat completion. Calls <paramref name="done"/> exactly once.</summary>
        public static IEnumerator Chat(string model, string system, string user, int maxTokens, float temperature, bool jsonMode,
                                       Action<Result> done)
        {
            var r = new Result { model = model };
            string key = LoadKey();
            if (string.IsNullOrEmpty(key)) { r.error = "no Groq API key (GROQ_API_KEY, -lr2GroqKeyFile or " + KeyFileName + ")"; done(r); yield break; }
            while (!CanSendNow) yield return null;

            var body = new StringBuilder(256 + system.Length + user.Length);
            body.Append("{\"model\":\"").Append(Esc(model)).Append("\",\"temperature\":")
                .Append(temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\"max_tokens\":").Append(maxTokens);
            if (jsonMode) body.Append(",\"response_format\":{\"type\":\"json_object\"}");
            // gpt-oss models reason before answering; at the default effort the reasoning used up max_tokens and the JSON
            // came back cut off ("Failed to validate JSON" on 45 of 92 bench calls, 11-23).
            if (model.Contains("gpt-oss")) body.Append(",\"reasoning_effort\":\"low\"");
            body.Append(",\"messages\":[{\"role\":\"system\",\"content\":\"").Append(Esc(system))
                .Append("\"},{\"role\":\"user\",\"content\":\"").Append(Esc(user)).Append("\"}]}");

            Busy = true;
            _lastRequestAt = Time.realtimeSinceStartup;
            Requests++;
            float t0 = Time.realtimeSinceStartup;
            using (var req = new UnityWebRequest(Endpoint, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString()));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + key);
                req.timeout = RequestTimeoutSeconds;
                yield return req.SendWebRequest();

                r.seconds = Time.realtimeSinceStartup - t0;
                r.status = req.responseCode;
                r.remainingRequests = req.GetResponseHeader("x-ratelimit-remaining-requests");
                r.remainingTokens = req.GetResponseHeader("x-ratelimit-remaining-tokens");
                string text = req.downloadHandler != null ? req.downloadHandler.text : null;

                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var resp = JsonUtility.FromJson<Resp>(text);
                        r.content = resp != null && resp.choices != null && resp.choices.Length > 0 && resp.choices[0].message != null
                            ? resp.choices[0].message.content : null;
                        if (resp != null && resp.usage != null) { r.promptTokens = resp.usage.prompt_tokens; r.completionTokens = resp.usage.completion_tokens; }
                        r.ok = !string.IsNullOrEmpty(r.content);
                        if (!r.ok) r.error = "empty reply";
                    }
                    catch (Exception e) { r.error = "unreadable reply: " + e.Message; }
                }
                else
                {
                    string msg = null;
                    try { var err = JsonUtility.FromJson<Resp>(text); msg = err != null && err.error != null ? err.error.message : null; } catch (Exception) { }
                    r.error = $"HTTP {req.responseCode} {(msg ?? req.error)}";
                    if (req.responseCode == 429)
                    {
                        RateLimited++;
                        float ra = 0f;
                        float.TryParse(req.GetResponseHeader("retry-after"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ra);
                        r.retryAfter = Mathf.Clamp(ra > 0f ? ra : 20f, 2f, 120f);
                        _blockedUntil = Time.realtimeSinceStartup + r.retryAfter;
                    }
                    else if (req.responseCode >= 500 || req.responseCode == 0)
                        _blockedUntil = Time.realtimeSinceStartup + 6f;
                    string lower = (msg ?? "").ToLowerInvariant();
                    r.modelRejected = (req.responseCode == 404 || req.responseCode == 400) && lower.Contains("model");
                }
            }
            Busy = false;
            if (!r.ok) Failures++;
            done(r);
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

#pragma warning disable 0649 // filled by JsonUtility
        [Serializable] private class Resp { public Choice[] choices; public Usage usage; public Err error; }
        [Serializable] private class Choice { public Msg message; }
        [Serializable] private class Msg { public string content; }
        [Serializable] private class Usage { public int prompt_tokens; public int completion_tokens; }
        [Serializable] private class Err { public string message; public string type; public string code; }
#pragma warning restore 0649
    }
}
