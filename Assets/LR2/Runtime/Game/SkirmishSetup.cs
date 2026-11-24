using System;
using System.Collections.Generic;
using LR2.AI;
using LR2.Combat;
using UnityEngine;

namespace LR2.Game
{
    /// <summary>One bot slot in the skirmish lobby.</summary>
    [Serializable]
    public class SkirmishBot
    {
        public BotArchetype archetype = BotArchetype.HumanLike;
        [Tooltip("0 Recruit, 1 Regular, 2 Hardened, 3 Veteran, 4 Adaptive.")]
        public int difficulty = 1;
        public TeamId team = TeamId.Bravo;
    }

    /// <summary>
    /// Skirmish lobby state (user 11-19: "just have play, then in that have all the settings ... 3 way, 4 way, so i can select
    /// how many bots and which team they can be in ... maybe max 20"). Saved in PlayerPrefs; MatchManager spawns from it while <see cref="Active"/>.
    /// </summary>
    public static class SkirmishSetup
    {
        public const int MaxBots = 20;
        public static readonly string[] DifficultyNames = { "RECRUIT", "REGULAR", "HARDENED", "VETERAN", "ADAPTIVE" };
        public static readonly string[] ArchetypeNames = { "HUMAN", "DUMB", "LLM" };
        public static readonly TeamId[] TeamChoices = { TeamId.None, TeamId.Alpha, TeamId.Bravo, TeamId.Charlie, TeamId.Delta };
        public static readonly GameModeType[] Modes =
            { GameModeType.TeamDeathmatch, GameModeType.FreeForAll, GameModeType.CaptureTheFlag, GameModeType.Duel };
        public static readonly string[] ModeNames = { "TEAM DEATHMATCH", "FREE FOR ALL", "CAPTURE THE FLAG", "1V1 DUEL" };
        public static readonly string[] Presets = { "1V1", "2 TEAMS", "3-WAY", "4-WAY", "FFA" };

        /// <summary>True from the lobby's START until the menu loads again: MatchManager spawns from these slots.</summary>
        public static bool Active;
        /// <summary>AI vs AI: no player soldier; a spectator camera follows the bots (used in Experiments / CLI bench).</summary>
        public static bool PlayerSpectates;
        /// <summary>B153 Blind Turing Test mode: suppresses archetype & difficulty tags on bot display names.</summary>
        public static bool BlindMode;
        public static TeamId PlayerTeam = TeamId.Alpha;
        public static GameModeType Mode = GameModeType.TeamDeathmatch;
        public static readonly List<SkirmishBot> Bots = new List<SkirmishBot>();

        private const string Key = "LR2.v1.Skirmish";

        [Serializable]
        private class Saved
        {
            public bool spectate;
            public TeamId playerTeam = TeamId.Alpha;
            public GameModeType mode = GameModeType.TeamDeathmatch;
            public List<SkirmishBot> bots = new List<SkirmishBot>();
        }

        /// <summary>Teams only count in team modes, and only when someone is on one.</summary>
        public static bool IsTeamGame
        {
            get
            {
                if (Mode == GameModeType.FreeForAll || Mode == GameModeType.Duel || Mode == GameModeType.FreeForAllCaptureTheFlag) return false;
                if (!PlayerSpectates && PlayerTeam != TeamId.None) return true;
                foreach (var b in Bots) if (b.team != TeamId.None) return true;
                return false;
            }
        }

        /// <summary>Distinct teams in use (player included unless spectating).</summary>
        public static int TeamCount
        {
            get
            {
                var set = new HashSet<TeamId>();
                if (!PlayerSpectates && PlayerTeam != TeamId.None) set.Add(PlayerTeam);
                foreach (var b in Bots) if (b.team != TeamId.None) set.Add(b.team);
                return set.Count;
            }
        }

        public static void Load()
        {
            Bots.Clear();
            string json = PlayerPrefs.GetString(Key, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var s = JsonUtility.FromJson<Saved>(json);
                    if (s != null)
                    {
                        PlayerSpectates = s.spectate && LR2.World.MediaDirector.IsBenchRun;
                        PlayerTeam = s.playerTeam == TeamId.None ? TeamId.None : TeamId.Alpha; // B116: your team is always Alpha
                        Mode = s.mode;
                        if (s.bots != null)
                            foreach (var b in s.bots)
                                if (Bots.Count < MaxBots && b != null)
                                    Bots.Add(new SkirmishBot
                                    {
                                        archetype = b.archetype == BotArchetype.HumanFitted
                                            ? BotArchetype.HumanLike
                                            : (BotArchetype)Mathf.Clamp((int)b.archetype, 0, ArchetypeNames.Length - 1),
                                        difficulty = Mathf.Clamp(b.difficulty, 0, 4),
                                        team = b.team
                                    });
                    }
                }
                catch (Exception) { Bots.Clear(); }
            }
            if (Bots.Count == 0) ApplyPreset("2 TEAMS");
        }

        public static void Save()
        {
            var s = new Saved { spectate = PlayerSpectates, playerTeam = PlayerTeam, mode = Mode, bots = new List<SkirmishBot>(Bots) };
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(s));
            PlayerPrefs.Save();
        }

        public static void ApplyPreset(string preset)
        {
            Bots.Clear();
            PlayerSpectates = false;
            void Add(TeamId t, int d, int n) { for (int i = 0; i < n && Bots.Count < MaxBots; i++) Bots.Add(new SkirmishBot { team = t, difficulty = d }); }
            switch (preset)
            {
                case "1V1":
                    Mode = GameModeType.Duel; PlayerTeam = TeamId.None; Add(TeamId.None, 2, 1); break;
                case "3-WAY":
                    Mode = GameModeType.TeamDeathmatch; PlayerTeam = TeamId.Alpha;
                    Add(TeamId.Alpha, 1, 2); Add(TeamId.Bravo, 1, 3); Add(TeamId.Charlie, 1, 3); break;
                case "4-WAY":
                    Mode = GameModeType.TeamDeathmatch; PlayerTeam = TeamId.Alpha;
                    Add(TeamId.Alpha, 1, 2); Add(TeamId.Bravo, 1, 3); Add(TeamId.Charlie, 1, 3); Add(TeamId.Delta, 1, 3); break;
                case "FFA":
                    Mode = GameModeType.FreeForAll; PlayerTeam = TeamId.None; Add(TeamId.None, 1, 7); break;
                case "AI VS AI":
                    // difficulty test bench: four Veterans against four Regulars, watched from the spectator camera
                    Mode = GameModeType.TeamDeathmatch; PlayerSpectates = true; PlayerTeam = TeamId.None;
                    Add(TeamId.Alpha, 3, 4); Add(TeamId.Bravo, 1, 4); break;
                default: // "2 TEAMS"
                    Mode = GameModeType.TeamDeathmatch; PlayerTeam = TeamId.Alpha;
                    Add(TeamId.Alpha, 1, 3); Add(TeamId.Bravo, 1, 4); break;
            }
        }

        public static TeamId NextTeam(TeamId t, int dir)
        {
            int i = Array.IndexOf(TeamChoices, t);
            if (i < 0) i = 0;
            return TeamChoices[(i + dir + TeamChoices.Length) % TeamChoices.Length];
        }
    }
}
