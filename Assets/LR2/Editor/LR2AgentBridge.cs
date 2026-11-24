using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LR2.EditorTools
{
    /// <summary>
    /// Lets AI agents (Claude / Gemini) drive the user's OPEN editor instead of fighting over the project lock with
    /// batch Unity: drop a one-line command into <c>Temp/LR2_AgentCommand.txt</c> and the editor runs it visibly
    /// (the user can watch). Result lines are appended to <c>Temp/LR2_AgentResult.txt</c>.
    /// Commands:
    ///   rebuild-all                       full content + all maps (LR2 > Build Everything)
    ///   rebuild-chars                     FX, audio, weapons, soldiers only (fast)
    ///   play [scenePath] [seconds]        robot playtest in the editor, screenshots + Logs/LR2_SmokeTest.txt
    ///   stop                              leave play mode
    ///   refresh                           AssetDatabase.Refresh + script compile (no user focus needed)
    /// </summary>
    [InitializeOnLoad]
    public static class LR2AgentBridge
    {
        public const string CommandPath = "Temp/LR2_AgentCommand.txt";
        public const string ResultPath = "Temp/LR2_AgentResult.txt";
        public const string RobotFlag = "Temp/LR2_RobotRequest.flag";
        private static double _next;

        static LR2AgentBridge()
        {
            if (Application.isBatchMode) return;
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 1.0;
            if (!File.Exists(CommandPath) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode && !File.ReadAllText(CommandPath).Trim().StartsWith("stop")) return;
            string cmd;
            try
            {
                cmd = File.ReadAllText(CommandPath).Trim();
                File.Delete(CommandPath);
            }
            catch (IOException) { return; }
            Run(cmd);
        }

        private static void Result(string line)
        {
            File.AppendAllText(ResultPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
            Debug.Log("[LR2 AgentBridge] " + line);
        }

        private static void Run(string cmd)
        {
            var parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            Result("received: " + cmd);
            try
            {
                switch (parts[0])
                {
                    case "rebuild-all":
                        LR2BuildPipeline.BuildEverything();
                        Result("rebuild-all done");
                        break;
                    case "rebuild-chars":
                        bool ok = true;
                        ok &= Step(LR2ContentBuilder.BuildFx);
                        ok &= Step(LR2ContentBuilder.BuildAudio);
                        ok &= Step(LR2ContentBuilder.BuildWeapons);
                        ok &= Step(LR2ContentBuilder.BuildSoldiers);
                        AssetDatabase.SaveAssets();
                        Result("rebuild-chars " + (ok ? "OK" : "FAILED"));
                        break;
                    case "build-weapons":
                        LR2ContentBuilder.BuildWeapons();
                        AssetDatabase.SaveAssets();
                        Result("build-weapons OK");
                        break;
                    case "build-player":
                        bool pOk = LR2BuildPipeline.BuildWindowsPlayer();
                        Result("build-player " + (pOk ? "SUCCESS -> H:/Unity Projects/Builds/Lead Rush II/Lead Rush II.exe" : "FAILED"));
                        break;
                    case "play":
                        string scene = parts.Length > 1 ? parts[1] : LR2MapBuilder.ScenePath;
                        float seconds = parts.Length > 2 && float.TryParse(parts[2], out float s) ? s : 45f;
                        File.WriteAllText(RobotFlag, "1");
                        LR2SmokeTest.RunFromBridge(scene, seconds);
                        Result($"play started: {scene} for {seconds:0}s (report -> Logs/LR2_SmokeTest.txt)");
                        break;
                    case "refresh":
                        // pick up script/asset edits without the user having to click into the editor
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                        UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                        Result("refresh requested (compiling if scripts changed)");
                        break;
                    case "stop":
                        EditorApplication.isPlaying = false;
                        Result("stopped");
                        break;
                    default:
                        Result("unknown command");
                        break;
                }
            }
            catch (Exception e)
            {
                Result("FAILED: " + e.Message);
                Debug.LogException(e);
            }
        }

        private static bool Step(Action a)
        {
            try { a(); return true; }
            catch (Exception e) { Debug.LogException(e); return false; }
        }
    }
}
