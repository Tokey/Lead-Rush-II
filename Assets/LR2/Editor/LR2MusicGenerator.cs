using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LR2.EditorTools
{
    /// <summary>
    /// Procedural adaptive music composer for Lead Rush II (B136o revamped).
    /// Generates Vorbis stereo WAVs under <c>Assets/LR2/Resources/LR2/Music</c>:
    /// - <c>Music_Menu.wav</c>: distinct dark military-industrial dossier theme
    /// - Per-map adaptive 3-stem loops (<c>Saltworks</c>, <c>Deadsector</c>, <c>Foundry</c>, <c>TestRange</c>):
    ///   distinct keys/melodic motifs/timbre per level + legacy fallback <c>Music_{Calm,Tension,Combat}</c>
    /// - <c>Music_Overdrive.wav</c>: intense endgame climax stem (double-kick, 32nd snare/tom fills, rising octave riff)
    /// - <c>Music_MatchWin.wav</c> &amp; <c>Music_MatchLose.wav</c>: non-looping 9s end-of-match fanfare / dirge.
    /// </summary>
    public static class LR2MusicGenerator
    {
        public const string OutputDir = "Assets/LR2/Resources/LR2/Music";
        private const int SampleRate = 44100;
        private const float Bpm = 120f;
        private const int Bars = 8;
        private const float DurationSec = Bars * (60f / Bpm) * 4f; // 16.0 s exact @ 120 BPM
        private static readonly int TotalSamples = SampleRate * 16;

        private readonly struct MapThemeSpec
        {
            public readonly string KeyName;
            public readonly float SemitoneShift;
            public readonly float Brightness;
            public readonly float AnvilLevel;
            public readonly float SynthPulse;
            public readonly int Seed;

            public MapThemeSpec(string keyName, float semitoneShift, float brightness, float anvilLevel, float synthPulse, int seed)
            {
                KeyName = keyName;
                SemitoneShift = semitoneShift;
                Brightness = brightness;
                AnvilLevel = anvilLevel;
                SynthPulse = synthPulse;
                Seed = seed;
            }
        }

        private static readonly MapThemeSpec[] MapThemes =
        {
            new MapThemeSpec("Saltworks", 0f, 1.00f, 0.14f, 0.25f, 101),  // D minor: coastal industrial tension
            new MapThemeSpec("Deadsector", -2f, 0.78f, 0.08f, 0.35f, 202), // C minor: darker dystopian urban dread
            new MapThemeSpec("Foundry", 2f, 1.15f, 0.42f, 0.15f, 303),     // E minor: heavy metallic refinery assault
            new MapThemeSpec("TestRange", -5f, 1.08f, 0.04f, 0.55f, 404),  // A minor: crisp tactical electronic precision
        };

        [MenuItem("LR2/Audio/Regenerate Procedural Music Stems", priority = 35)]
        public static void EnsureGeneratedMenu() => EnsureGenerated(true);

        public static void EnsureGenerated(bool force = false)
        {
            Directory.CreateDirectory(OutputDir);

            var allPaths = new List<string>();
            bool anyWritten = false;

            string menuPath = OutputDir + "/Music_Menu.wav";
            allPaths.Add(menuPath);
            if (force || !File.Exists(menuPath)) { WriteMenuStem(menuPath); anyWritten = true; }

            // Legacy default stems (Saltworks D minor)
            string calmPath = OutputDir + "/Music_Calm.wav";
            string tensionPath = OutputDir + "/Music_Tension.wav";
            string combatPath = OutputDir + "/Music_Combat.wav";
            allPaths.Add(calmPath);
            allPaths.Add(tensionPath);
            allPaths.Add(combatPath);
            if (force || !File.Exists(calmPath)) { WriteCalmStem(calmPath, MapThemes[0]); anyWritten = true; }
            if (force || !File.Exists(tensionPath)) { WriteTensionStem(tensionPath, MapThemes[0]); anyWritten = true; }
            if (force || !File.Exists(combatPath)) { WriteCombatStem(combatPath, MapThemes[0]); anyWritten = true; }

            // Distinct per-map stems
            foreach (var theme in MapThemes)
            {
                string cP = $"{OutputDir}/Music_{theme.KeyName}_Calm.wav";
                string tP = $"{OutputDir}/Music_{theme.KeyName}_Tension.wav";
                string xP = $"{OutputDir}/Music_{theme.KeyName}_Combat.wav";
                allPaths.Add(cP);
                allPaths.Add(tP);
                allPaths.Add(xP);
                if (force || !File.Exists(cP)) { WriteCalmStem(cP, theme); anyWritten = true; }
                if (force || !File.Exists(tP)) { WriteTensionStem(tP, theme); anyWritten = true; }
                if (force || !File.Exists(xP)) { WriteCombatStem(xP, theme); anyWritten = true; }
            }

            // Insane climax overdrive layer + road-to-win/loss mid-match momentum layers + revamped match outcome stingers
            string overdrivePath = OutputDir + "/Music_Overdrive.wav";
            string winMomPath = OutputDir + "/Music_Momentum_Winning.wav";
            string loseMomPath = OutputDir + "/Music_Momentum_Losing.wav";
            string winPath = OutputDir + "/Music_MatchWin.wav";
            string losePath = OutputDir + "/Music_MatchLose.wav";
            allPaths.Add(overdrivePath);
            allPaths.Add(winMomPath);
            allPaths.Add(loseMomPath);
            allPaths.Add(winPath);
            allPaths.Add(losePath);
            if (force || !File.Exists(overdrivePath)) { WriteOverdriveStem(overdrivePath); anyWritten = true; }
            if (force || !File.Exists(winMomPath)) { WriteMomentumStem(winMomPath, true); anyWritten = true; }
            if (force || !File.Exists(loseMomPath)) { WriteMomentumStem(loseMomPath, false); anyWritten = true; }
            if (force || !File.Exists(winPath)) { WriteOutcomeStinger(winPath, true); anyWritten = true; }
            if (force || !File.Exists(losePath)) { WriteOutcomeStinger(losePath, false); anyWritten = true; }

            if (anyWritten)
            {
                AssetDatabase.Refresh();
                foreach (var p in allPaths)
                    ConfigureMusicImporter(p);
                AssetDatabase.SaveAssets();
                Debug.Log($"[LR2] Generated {allPaths.Count} revamped progressive & outcome music WAVs -> {OutputDir}");
            }
        }

        private static void ConfigureMusicImporter(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as AudioImporter;
            if (imp == null) return;
            imp.forceToMono = false;
            imp.loadInBackground = false;
            var s = imp.defaultSampleSettings;
            s.loadType = AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = AudioCompressionFormat.Vorbis;
            s.quality = 0.70f;
            imp.defaultSampleSettings = s;
            imp.SaveAndReimport();
        }

        private static readonly float[] BassMidi = { 38f, 34f, 31f, 33f, 38f, 36f, 34f, 33f };
        private static readonly float[][] PadChords =
        {
            new[] { 50f, 57f, 60f, 64f },
            new[] { 46f, 53f, 57f, 62f },
            new[] { 43f, 50f, 55f, 58f },
            new[] { 45f, 52f, 57f, 61f },
            new[] { 50f, 57f, 60f, 65f },
            new[] { 48f, 55f, 60f, 64f },
            new[] { 46f, 53f, 58f, 62f },
            new[] { 45f, 52f, 57f, 62f },
        };
        private static readonly float[][] ArpNotes =
        {
            new[] { 62f, 65f, 69f, 76f, 69f, 65f, 64f, 69f },
            new[] { 58f, 62f, 65f, 69f, 65f, 62f, 65f, 74f },
            new[] { 55f, 58f, 62f, 69f, 62f, 58f, 62f, 67f },
            new[] { 57f, 64f, 67f, 73f, 69f, 64f, 61f, 64f },
            new[] { 62f, 65f, 69f, 74f, 69f, 65f, 64f, 62f },
            new[] { 60f, 64f, 67f, 72f, 67f, 64f, 60f, 67f },
            new[] { 58f, 62f, 65f, 70f, 65f, 62f, 58f, 65f },
            new[] { 57f, 62f, 64f, 69f, 64f, 62f, 61f, 64f },
        };

        private static float MidiToHz(float midi) => 440f * Mathf.Pow(2f, (midi - 69f) / 12f);

        private static float Tri(float phase)
        {
            float f = phase - Mathf.Floor(phase);
            return 4f * Mathf.Abs(f - 0.5f) - 1f;
        }

        private static float Saw(float phase)
        {
            float f = phase - Mathf.Floor(phase);
            return 2f * f - 1f;
        }

        private static float SoftLowpass(ref float state, float x, float cutoffHz)
        {
            float a = Mathf.Clamp01(2f * Mathf.PI * cutoffHz / SampleRate);
            state += a * (x - state);
            return state;
        }

        private static float LoopLockedPhaseHz(float targetHz)
        {
            float cycles = Mathf.Max(1f, Mathf.Round(targetHz * DurationSec));
            return cycles / DurationSec;
        }

        private static void WriteMenuStem(string path)
        {
            float[] L = new float[TotalSamples];
            float[] R = new float[TotalSamples];
            float lpL = 0f, lpR = 0f, padLpL = 0f, padLpR = 0f, radioLp = 0f, radioHp = 0f, radioPrev = 0f;
            float[] voicePhases = new float[8];
            var radioRng = new System.Random(1939);

            for (int i = 0; i < TotalSamples; i++)
            {
                float t = i / (float)SampleRate;
                int bar = Mathf.Clamp((int)(t / 2f), 0, 7);
                float barT = t - bar * 2f;

                float chordEnv = Mathf.Clamp01(barT / 0.14f) * Mathf.Clamp01((2f - barT) / 0.12f);
                var chord = PadChords[bar];
                float padSumL = 0f, padSumR = 0f;
                for (int v = 0; v < chord.Length; v++)
                {
                    float fBase = MidiToHz(chord[v]);
                    float det = v % 2 == 0 ? 1.0022f : 0.9978f;
                    voicePhases[v] += fBase / SampleRate;
                    voicePhases[v + 4] += (fBase * det) / SampleRate;
                    float oscA = Tri(voicePhases[v]) * 0.65f + Mathf.Sin(voicePhases[v] * 2f * Mathf.PI) * 0.35f;
                    float oscB = Tri(voicePhases[v + 4]) * 0.65f + Mathf.Sin(voicePhases[v + 4] * 2f * Mathf.PI) * 0.35f;
                    padSumL += oscA * (v % 2 == 0 ? 0.7f : 0.45f) + oscB * 0.35f;
                    padSumR += oscB * (v % 2 == 0 ? 0.45f : 0.7f) + oscA * 0.35f;
                }
                float sweep = 420f + 320f * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * t / DurationSec));
                float pL = SoftLowpass(ref padLpL, padSumL * 0.14f * chordEnv, sweep);
                float pR = SoftLowpass(ref padLpR, padSumR * 0.14f * chordEnv, sweep);

                float subF = LoopLockedPhaseHz(MidiToHz(BassMidi[bar]));
                float sub = Mathf.Sin(2f * Mathf.PI * subF * t) * 0.22f * Mathf.Clamp01(barT / 0.08f);
                float warDrum = 0f;
                if (barT < 0.36f)
                {
                    float df = Mathf.Lerp(96f, 41f, Mathf.Clamp01(barT * 9f));
                    warDrum = Mathf.Sin(2f * Mathf.PI * df * barT) * Mathf.Exp(-barT * 10.5f) * Mathf.Clamp01(barT / 0.004f) * 0.22f;
                }

                // Subtle briefing sonar ping on bar starts + clean tactical rim-tick
                float sonar = 0f;
                if (bar % 2 == 0 && barT < 0.65f)
                    sonar = Mathf.Sin(2f * Mathf.PI * MidiToHz(74f) * barT) * Mathf.Exp(-barT * 6.5f) * Mathf.Clamp01(barT / 0.004f) * 0.09f;

                float white = (float)(radioRng.NextDouble() * 2.0 - 1.0);
                float crackle = (radioRng.NextDouble() < 0.00045) ? white * 0.28f : 0f;
                float radioRaw = white * 0.018f + crackle;
                radioHp = 0.96f * (radioHp + radioRaw - radioPrev);
                radioPrev = radioRaw;
                float radioDust = SoftLowpass(ref radioLp, radioHp, 1550f);

                int step = Mathf.Clamp((int)(barT / 0.25f), 0, 7);
                float stepT = barT - step * 0.25f;
                float arpMidi = (bar % 2 == 1 && step == 6) ? (ArpNotes[bar][step] - 1f) : ArpNotes[bar][step];
                float noteHz = MidiToHz(arpMidi - 12f);
                float bowAtk = Mathf.Clamp01(stepT / 0.035f);
                float arpEnv = bowAtk * Mathf.Exp(-stepT * 4.8f);
                float bowed = (float)Math.Tanh((Saw(noteHz * stepT) * 0.52f + Tri(noteHz * 2f * stepT) * 0.26f) * 1.35f) * arpEnv * 0.16f;
                float pan = (step % 2 == 0) ? 0.65f : 0.35f;

                float dryL = pL + sub + warDrum + sonar * 0.65f + radioDust + bowed * pan;
                float dryR = pR + sub + warDrum + sonar * 0.35f + radioDust * 0.85f + bowed * (1f - pan);
                L[i] = SoftLowpass(ref lpL, dryL, 4800f);
                R[i] = SoftLowpass(ref lpR, dryR, 4800f);
            }

            AddWarTornOrchestralBed(L, R, 0f, brassWeight: 0.24f, gritDrive: 1.45f, withArtillery: true);
            AddCircularStereoEcho(L, R, Mathf.RoundToInt(0.375f * SampleRate), 0.32f);
            AddFocusLayer(L, R, 0f, 909, amount: 0.9f, drums: true);
            FinalizeStem(L, R, targetRms: 0.125f, maxPeak: 0.74f);
            WriteWav(path, L, R);
        }

        private static void WriteCalmStem(string path, MapThemeSpec theme)
        {
            float[] L = new float[TotalSamples];
            float[] R = new float[TotalSamples];
            float lpL = 0f, lpR = 0f;
            float shift = theme.SemitoneShift;

            // Super-mild opening bed: warm stealth sub, sparse sonar harmonic plucks, zero harsh overdrive
            for (int i = 0; i < TotalSamples; i++)
            {
                float t = i / (float)SampleRate;
                int bar = Mathf.Clamp((int)(t / 2f), 0, 7);
                float barT = t - bar * 2f;

                float fRoot = LoopLockedPhaseHz(MidiToHz(BassMidi[bar] + shift));
                float barFade = Mathf.Clamp01(barT / 0.03f) * Mathf.Clamp01((2f - barT) / 0.03f);
                float sub = (Mathf.Sin(2f * Mathf.PI * fRoot * barT) * 0.24f + Tri(fRoot * barT) * 0.06f) * barFade;
                float fifth = Mathf.Sin(2f * Mathf.PI * (fRoot * 1.5f) * barT + 0.4f) * 0.07f * barFade * (0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 2f * t / DurationSec));

                float pluck = 0f;
                if (barT < 0.75f || (theme.SynthPulse > 0.3f && barT > 1.375f))
                {
                    float localT = barT < 0.75f ? barT : (barT - 1.375f);
                    float noteHz = MidiToHz(ArpNotes[bar][(barT < 0.75f ? 0 : 4 + (theme.Seed % 2))] + shift);
                    pluck = (Mathf.Sin(2f * Mathf.PI * noteHz * localT) + Tri(noteHz * 2f * localT) * theme.SynthPulse * 0.35f)
                            * Mathf.Exp(-localT * 6.8f) * Mathf.Clamp01(localT / 0.005f) * 0.13f;
                }

                float xL = sub + fifth * 0.8f + pluck * 0.62f;
                float xR = sub + fifth * 1.0f + pluck * 0.38f;
                L[i] = SoftLowpass(ref lpL, xL, 3200f * theme.Brightness);
                R[i] = SoftLowpass(ref lpR, xR, 3200f * theme.Brightness);
            }

            AddCircularStereoEcho(L, R, Mathf.RoundToInt(0.375f * SampleRate), 0.26f);
            MixProgressiveGuitar(L, R, GuitarIntensity.CleanCalm, shift);
            AddFocusLayer(L, R, shift, theme.Seed, amount: 1.0f, drums: true);
            FinalizeStem(L, R, targetRms: 0.07f, maxPeak: 0.66f);
            WriteWav(path, L, R);
        }

        private static void WriteTensionStem(string path, MapThemeSpec theme)
        {
            float[] L = new float[TotalSamples];
            float[] R = new float[TotalSamples];
            float bassLpL = 0f, bassLpR = 0f, strLpL = 0f, strLpR = 0f;
            float shift = theme.SemitoneShift;

            for (int i = 0; i < TotalSamples; i++)
            {
                float t = i / (float)SampleRate;
                int bar = Mathf.Clamp((int)(t / 2f), 0, 7);
                float barT = t - bar * 2f;

                int s16 = Mathf.Clamp((int)(barT / 0.125f), 0, 15);
                float stepT = barT - s16 * 0.125f;
                float bassMidi = BassMidi[bar] + shift + (s16 == 14 ? 2f : s16 == 15 ? 3f : 0f);
                float bassHz = MidiToHz(bassMidi);
                float pulseAcc = (s16 % 4 == 0) ? 1.0f : (s16 % 2 == 0) ? 0.72f : (0.38f + theme.SynthPulse * 0.25f);
                float pulseEnv = Mathf.Exp(-stepT * 18f) * Mathf.Clamp01(stepT / 0.002f) * pulseAcc;
                float bassRaw = (Saw(bassHz * stepT) * 0.55f + Tri(bassHz * 2f * stepT) * 0.35f) * pulseEnv;
                float cutoff = (560f + 480f * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 2f * t / DurationSec))) * theme.Brightness;
                float bL = SoftLowpass(ref bassLpL, bassRaw * 0.38f, cutoff);
                float bR = SoftLowpass(ref bassLpR, bassRaw * 0.38f, cutoff * 1.04f);

                float drum = 0f;
                foreach (float hitT in new[] { 0f, 0.75f, 1.375f })
                {
                    if (barT >= hitT && barT < hitT + 0.32f)
                    {
                        float dt = barT - hitT;
                        float df = Mathf.Lerp(105f, 44f, Mathf.Clamp01(dt * 12f));
                        drum += Mathf.Sin(2f * Mathf.PI * df * dt) * Mathf.Exp(-dt * 14f) * Mathf.Clamp01(dt / 0.003f) * 0.34f;
                    }
                }

                float anvil = 0f;
                if (theme.AnvilLevel > 0.1f && barT < 0.18f && (bar % 2 == 1))
                    anvil = Mathf.Sin(2f * Mathf.PI * 330f * barT) * Mathf.Exp(-barT * 18f) * theme.AnvilLevel * 0.30f;

                float strAmt = (bar >= 4 ? Mathf.Clamp01((bar - 3.5f) * 0.35f) : 0.25f) * Mathf.Clamp01(barT / 0.02f) * Mathf.Clamp01((2f - barT) / 0.02f);
                var ch = PadChords[bar];
                float strWaveL = (Saw(MidiToHz(ch[1] + shift) * 1.003f * barT) + Saw(MidiToHz(ch[2] + shift) * 0.997f * barT)) * 0.09f * strAmt;
                float strWaveR = (Saw(MidiToHz(ch[1] + shift) * 0.997f * barT) + Saw(MidiToHz(ch[2] + shift) * 1.003f * barT)) * 0.09f * strAmt;
                float sL = SoftLowpass(ref strLpL, strWaveL, 2600f * theme.Brightness);
                float sR = SoftLowpass(ref strLpR, strWaveR, 2600f * theme.Brightness);

                L[i] = bL + drum + anvil + sL;
                R[i] = bR + drum + anvil + sR;
            }

            AddWarTornOrchestralBed(L, R, shift, brassWeight: 0.28f, gritDrive: 1.75f, withArtillery: true);
            AddCircularStereoEcho(L, R, Mathf.RoundToInt(0.25f * SampleRate), 0.18f);
            MixProgressiveGuitar(L, R, GuitarIntensity.OverdriveTension, shift);
            AddFocusLayer(L, R, shift, theme.Seed + 1, amount: 0.55f, drums: false);
            FinalizeStem(L, R, targetRms: 0.145f, maxPeak: 0.80f);
            WriteWav(path, L, R);
        }

        private static void WriteCombatStem(string path, MapThemeSpec theme)
        {
            float[] L = new float[TotalSamples];
            float[] R = new float[TotalSamples];
            float leadLpL = 0f, leadLpR = 0f, hatLp = 0f;
            var rng = new System.Random(2026 + theme.Seed);
            float shift = theme.SemitoneShift;

            for (int i = 0; i < TotalSamples; i++)
            {
                float t = i / (float)SampleRate;
                int bar = Mathf.Clamp((int)(t / 2f), 0, 7);
                float barT = t - bar * 2f;

                float kick = 0f;
                foreach (float kt in new[] { 0f, 0.5f, 1.0f, 1.375f, 1.5f })
                {
                    if (barT >= kt && barT < kt + 0.22f)
                    {
                        float dt = barT - kt;
                        float kf = Mathf.Lerp(155f, 46f, Mathf.Clamp01(dt * 18f));
                        float amp = (kt == 1.375f) ? 0.65f : 1.0f;
                        kick += (float)Math.Tanh(Mathf.Sin(2f * Mathf.PI * kf * dt) * 1.6f) * Mathf.Exp(-dt * 16f) * amp * 0.42f;
                    }
                }

                float snare = 0f;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                foreach (float st in new[] { 0.5f, 1.5f })
                {
                    if (barT >= st && barT < st + 0.16f)
                    {
                        float dt = barT - st;
                        float body = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(220f, 110f, Mathf.Clamp01(dt * 24f)) * dt) * Mathf.Exp(-dt * 26f);
                        snare += (body * 0.45f + noise * Mathf.Exp(-dt * 32f) * 0.48f) * 0.36f;
                    }
                }

                int s16 = Mathf.Clamp((int)(barT / 0.125f), 0, 15);
                float hatDt = barT - s16 * 0.125f;
                float hatAmp = (s16 % 4 == 2) ? 0.16f : 0.08f;
                float hat = SoftLowpass(ref hatLp, noise * Mathf.Exp(-hatDt * 58f) * hatAmp, 5200f);

                int s8 = Mathf.Clamp((int)(barT / 0.25f), 0, 7);
                int noteIdx = (theme.Seed % 2 == 0) ? s8 : (7 - s8);
                float s8t = barT - s8 * 0.25f;
                float ostNote = MidiToHz(ArpNotes[bar][noteIdx] + shift - 12f);
                float ostEnv = Mathf.Exp(-s8t * 10f) * Mathf.Clamp01(s8t / 0.002f);
                float ostL = (Saw(ostNote * 1.004f * s8t) + Saw(ostNote * 0.5f * s8t)) * ostEnv * 0.24f;
                float ostR = (Saw(ostNote * 0.996f * s8t) + Saw(ostNote * 0.5f * s8t)) * ostEnv * 0.24f;
                float barEdge = Mathf.Clamp01(barT / 0.015f) * Mathf.Clamp01((2f - barT) / 0.015f);
                float bassDrive = (float)Math.Tanh(Saw(MidiToHz(BassMidi[bar] + shift) * hatDt) * 1.7f) * 0.26f * Mathf.Exp(-hatDt * 6f) * barEdge;

                float fCut = (1800f + 1600f * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 2f * t / DurationSec))) * theme.Brightness;

                float rollL = 0f, rollR = 0f;
                if ((bar % 2 == 1 && barT >= 1.50f) || (bar % 2 == 0 && barT >= 1.75f))
                {
                    float rollStart = (bar % 2 == 1) ? 1.50f : 1.75f;
                    float rollLen = 2.0f - rollStart;
                    float localT = barT - rollStart;
                    float subStep = (bar == 3 || bar == 7) ? 0.05f : 0.0625f;
                    int hitIdx = Mathf.FloorToInt(localT / subStep);
                    float dtR = localT - hitIdx * subStep;
                    if (dtR >= 0f && dtR < 0.075f)
                    {
                        float ramp = Mathf.Pow(Mathf.Clamp01(localT / rollLen), 1.35f);
                        float stickAmp = Mathf.Lerp(0.18f, 0.72f, ramp);
                        float drumFreq = (bar >= 6 && hitIdx % 3 == 0)
                            ? Mathf.Lerp(112f, 68f, Mathf.Clamp01(dtR * 18f))
                            : Mathf.Lerp(210f, 138f, Mathf.Clamp01(dtR * 24f));
                        float hitSig = (Mathf.Sin(2f * Mathf.PI * drumFreq * dtR) * 0.58f + noise * Mathf.Exp(-dtR * 36f) * 0.42f)
                                       * Mathf.Exp(-dtR * 28f) * Mathf.Clamp01(dtR / 0.002f) * stickAmp;
                        float rollPan = (hitIdx % 2 == 0) ? 0.44f : 0.56f;
                        rollL = hitSig * (1f - rollPan * 0.25f);
                        rollR = hitSig * (0.75f + rollPan * 0.25f);
                    }
                }

                float anvil = 0f;
                if (barT < 0.24f || (bar % 2 == 1 && barT >= 1.0f && barT < 1.22f))
                {
                    float adt = barT < 0.24f ? barT : (barT - 1.0f);
                    float aEnv = Mathf.Clamp01(adt / 0.002f) * Mathf.Exp(-adt * 15.5f);
                    float modes = Mathf.Sin(2f * Mathf.PI * 310f * adt) * 0.42f
                                + Mathf.Sin(2f * Mathf.PI * 478f * adt) * 0.30f
                                + Mathf.Sin(2f * Mathf.PI * 712f * adt) * 0.18f;
                    anvil = (float)Math.Tanh(modes * 1.55f) * aEnv * theme.AnvilLevel * 0.9f;
                }

                L[i] = kick + snare + hat + bassDrive + rollL + anvil * 0.92f + SoftLowpass(ref leadLpL, ostL, fCut);
                R[i] = kick + snare + hat + bassDrive + rollR + anvil * 1.04f + SoftLowpass(ref leadLpR, ostR, fCut);
            }

            AddWarTornOrchestralBed(L, R, shift, brassWeight: 0.46f, gritDrive: 2.45f, withArtillery: true);
            MixProgressiveGuitar(L, R, GuitarIntensity.HeavyCombat, shift);
            AddCircularStereoEcho(L, R, Mathf.RoundToInt(0.1875f * SampleRate), 0.16f);
            FinalizeStem(L, R, targetRms: 0.235f, maxPeak: 0.88f);
            WriteWav(path, L, R);
        }

        /// <summary>Insane endgame climax stem: rapid double-kick, 32nd-note tom/snare rolls, soaring brass & guitar harmonics.</summary>
        private static void WriteOverdriveStem(string path)
        {
            float[] L = new float[TotalSamples];
            float[] R = new float[TotalSamples];
            float lpL = 0f, lpR = 0f, hatLp = 0f;
            var rng = new System.Random(9090);

            for (int i = 0; i < TotalSamples; i++)
            {
                float t = i / (float)SampleRate;
                int bar = Mathf.Clamp((int)(t / 2f), 0, 7);
                float barT = t - bar * 2f;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

                // Relentless double-kick pattern (every 8th + syncopated 16ths)
                int kStep = Mathf.Clamp((int)(barT / 0.125f), 0, 15);
                float kDt = barT - kStep * 0.125f;
                float kick = 0f;
                if (kStep % 2 == 0 || (kStep >= 11))
                {
                    float kf = Mathf.Lerp(175f, 44f, Mathf.Clamp01(kDt * 22f));
                    kick = (float)Math.Tanh(Mathf.Sin(2f * Mathf.PI * kf * kDt) * 1.9f) * Mathf.Exp(-kDt * 18f) * 0.45f;
                }

                // Rapid 16th/32nd urgent snare drive + octave lead arpeggio
                int s16 = kStep;
                float sDt = kDt;
                float snareHit = (s16 % 4 == 2 || s16 >= 14) ? 0.36f : 0.08f;
                float snare = (Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(235f, 118f, Mathf.Clamp01(sDt * 25f)) * sDt) * 0.48f + noise * 0.52f)
                              * Mathf.Exp(-sDt * 28f) * snareHit;
                float hat = SoftLowpass(ref hatLp, noise * Mathf.Exp(-sDt * 60f) * 0.18f, 6400f);

                float arpHz = MidiToHz(ArpNotes[bar][s16 % 8] + (bar >= 4 ? 12f : 0f));
                float arpEnv = Mathf.Exp(-sDt * 9f) * Mathf.Clamp01(sDt / 0.002f);
                float leadL = (float)Math.Tanh((Saw(arpHz * 1.005f * sDt) + Saw(arpHz * 0.5f * sDt) * 0.65f) * 1.85f) * arpEnv * 0.32f;
                float leadR = (float)Math.Tanh((Saw(arpHz * 0.995f * sDt) + Saw(arpHz * 0.5f * sDt) * 0.65f) * 1.85f) * arpEnv * 0.32f;

                L[i] = kick + snare + hat + SoftLowpass(ref lpL, leadL, 4200f);
                R[i] = kick + snare + hat + SoftLowpass(ref lpR, leadR, 4200f);
            }

            AddWarTornOrchestralBed(L, R, 0f, brassWeight: 0.56f, gritDrive: 2.85f, withArtillery: true);
            MixProgressiveGuitar(L, R, GuitarIntensity.HeavyCombat, 0f);
            AddCircularStereoEcho(L, R, Mathf.RoundToInt(0.125f * SampleRate), 0.18f);
            FinalizeStem(L, R, targetRms: 0.285f, maxPeak: 0.90f);
            WriteWav(path, L, R);
        }

        /// <summary>
        /// B141: Sample-locked 16s mid-match momentum stems matching LR2's guitar/brass/percussion palette.
        /// Winning = upbeat D-major driving lift; Losing = somber D-minor brooding dread & pulse.
        /// </summary>
        private static void WriteMomentumStem(string path, bool winning)
        {
            float[] L = new float[TotalSamples];
            float[] R = new float[TotalSamples];
            var rng = new System.Random(winning ? 2026 : 4040);
            float lpL = 0f, lpR = 0f, hatLp = 0f, padLpL = 0f, padLpR = 0f;

            // D major triumphant progression (D -> G -> Bm/A -> A) vs D minor sinking dread (Dm -> Bb -> Gm -> A/Dm)
            float[] winChords = { 50f, 55f, 59f, 57f, 50f, 55f, 57f, 62f };
            float[] loseChords = { 50f, 46f, 43f, 45f, 50f, 46f, 43f, 38f };
            float[] winArp = { 74f, 78f, 81f, 86f, 81f, 78f, 79f, 81f };
            float[] loseArp = { 62f, 65f, 69f, 65f, 64f, 62f, 60f, 57f };

            for (int i = 0; i < TotalSamples; i++)
            {
                float t = i / (float)SampleRate;
                int bar = Mathf.Clamp((int)(t / 2f), 0, 7);
                float barT = t - bar * 2f;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float edge = Mathf.Clamp01(barT / 0.02f) * Mathf.Clamp01((2f - barT) / 0.02f);

                if (winning)
                {
                    // Upbeat heroic major lift: syncopated driving kick + bright major third + soaring octave motif
                    int kStep = Mathf.Clamp((int)(barT / 0.25f), 0, 7);
                    float kDt = barT - kStep * 0.25f;
                    float kick = (kStep == 0 || kStep == 3 || kStep == 4 || kStep == 6)
                        ? (float)Math.Tanh(Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(155f, 48f, Mathf.Clamp01(kDt * 20f)) * kDt) * 1.7f) * Mathf.Exp(-kDt * 15f) * 0.42f
                        : 0f;
                    int s16 = Mathf.Clamp((int)(barT / 0.125f), 0, 15);
                    float sDt = barT - s16 * 0.125f;
                    float hat = SoftLowpass(ref hatLp, noise * Mathf.Exp(-sDt * 48f) * 0.12f, 6800f);

                    float rootHz = LoopLockedPhaseHz(MidiToHz(winChords[bar] - 12f));
                    float majThirdHz = rootHz * 1.25992f;
                    float fifthHz = rootHz * 1.4983f;
                    float padL = (Saw(rootHz * t) * 0.38f + Saw(majThirdHz * 1.003f * t) * 0.28f + Saw(fifthHz * 0.997f * t) * 0.34f) * edge;
                    float padR = (Saw(rootHz * t) * 0.38f + Saw(majThirdHz * 0.997f * t) * 0.28f + Saw(fifthHz * 1.003f * t) * 0.34f) * edge;

                    float nHz = MidiToHz(winArp[(s16 + bar) % winArp.Length]);
                    float aEnv = Mathf.Clamp01(sDt / 0.003f) * Mathf.Exp(-sDt * 7.5f);
                    float lead = (float)Math.Tanh((Saw(nHz * sDt) + Saw(nHz * 1.5f * sDt) * 0.45f) * 1.4f) * aEnv * 0.28f;

                    L[i] = kick + hat + SoftLowpass(ref padLpL, padL * 0.26f, 2600f) + SoftLowpass(ref lpL, lead, 3800f);
                    R[i] = kick + hat + SoftLowpass(ref padLpR, padR * 0.26f, 2600f) + SoftLowpass(ref lpR, lead * 0.96f, 3800f);
                }
                else
                {
                    // Depressing minor warning layer: slow hollow heartbeat sub + minor-second tension + descending dark cello line
                    int hStep = Mathf.Clamp((int)(barT / 0.5f), 0, 3);
                    float hDt = barT - hStep * 0.5f;
                    float thud = (hStep == 0 || hStep == 2)
                        ? Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(64f, 34f, Mathf.Clamp01(hDt * 6f)) * hDt) * Mathf.Exp(-hDt * 5.8f) * 0.52f
                        : 0f;

                    float rootHz = LoopLockedPhaseHz(MidiToHz(loseChords[bar] - 12f));
                    float minThirdHz = rootHz * 1.1892f;
                    float dimFifthHz = rootHz * 1.4142f;
                    float padL = (Saw(rootHz * t) * 0.42f + Saw(minThirdHz * 0.998f * t) * 0.34f + Saw(dimFifthHz * 1.002f * t) * 0.14f) * edge;
                    float padR = (Saw(rootHz * t) * 0.42f + Saw(minThirdHz * 1.002f * t) * 0.34f + Saw(dimFifthHz * 0.998f * t) * 0.14f) * edge;

                    int mStep = Mathf.Clamp((int)(barT / 0.5f), 0, 3);
                    float mDt = barT - mStep * 0.5f;
                    float mHz = MidiToHz(loseArp[(bar + mStep) % loseArp.Length]);
                    float mEnv = Mathf.Clamp01(mDt / 0.04f) * Mathf.Exp(-mDt * 2.1f);
                    float bow = (Saw(mHz * mDt) * 0.55f + Saw(mHz * 0.5f * mDt) * 0.45f) * mEnv * 0.24f;

                    float filtCut = Mathf.Lerp(950f, 520f, bar / 7f);
                    L[i] = thud + SoftLowpass(ref padLpL, padL * 0.30f, filtCut) + SoftLowpass(ref lpL, bow, 1100f);
                    R[i] = thud + SoftLowpass(ref padLpR, padR * 0.30f, filtCut) + SoftLowpass(ref lpR, bow * 0.95f, 1100f);
                }
            }

            if (winning) MixProgressiveGuitar(L, R, GuitarIntensity.OverdriveTension, 2f);
            AddCircularStereoEcho(L, R, Mathf.RoundToInt((winning ? 0.1875f : 0.375f) * SampleRate), winning ? 0.18f : 0.24f);
            FinalizeStem(L, R, targetRms: winning ? 0.23f : 0.19f, maxPeak: 0.88f);
            WriteWav(path, L, R);
        }

        /// <summary>
        /// B141: 12-second outcome stingers matching LR2's live soundtrack palette.
        /// Win = upbeat, victorious D-major guitar anthem + drums + soaring fifth brass;
        /// Lose = heavy, depressing D-minor slow lament with descending minor chord & fading muffled decay.
        /// </summary>
        private static void WriteOutcomeStinger(string path, bool win)
        {
            const float dur = 12.0f;
            int n = Mathf.RoundToInt(SampleRate * dur);
            float[] L = new float[n];
            float[] R = new float[n];
            var rng = new System.Random(win ? 777 : 666);
            float lpL = 0f, lpR = 0f, hatLp = 0f;

            // Heroic D-major anthem: D5 - F#5 - A5 - B5 - A5 - D6 - F#6 - D6
            // Depressing D-minor descent: D4 - C4 - Bb3 - A3 - G3 - F3 - E3 - D3
            float[] motifMidi = win
                ? new[] { 62f, 66f, 69f, 71f, 69f, 74f, 78f, 86f }
                : new[] { 62f, 60f, 58f, 57f, 55f, 53f, 52f, 50f };
            float[] bassRoots = win
                ? new[] { 38f, 42f, 45f, 47f, 43f, 45f, 50f, 38f }
                : new[] { 38f, 36f, 34f, 33f, 31f, 29f, 28f, 26f };

            float stepDur = dur / 8f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                int seg = Mathf.Clamp((int)(t / stepDur), 0, 7);
                float segT = t - seg * stepDur;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                float globalFade = Mathf.Clamp01(t / 0.03f) * Mathf.Clamp01((dur - t) / (win ? 1.8f : 2.8f));

                float rootF = MidiToHz(bassRoots[seg]);
                float mHz = MidiToHz(motifMidi[seg]);
                float thirdOrFifthHz = mHz * (win ? 1.5f : 1.1892f);

                if (win)
                {
                    // Upbeat rock/EDM-tactical victory groove: punchy kick + crisp snare + driving chugged power chord
                    float beatT = t % 0.375f; // 160 BPM celebratory lift
                    int beatIdx = Mathf.FloorToInt(t / 0.375f);
                    float kick = (beatIdx % 2 == 0 || beatIdx % 4 == 3)
                        ? (float)Math.Tanh(Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(165f, 48f, Mathf.Clamp01(beatT * 20f)) * beatT) * 1.8f) * Mathf.Exp(-beatT * 15f) * 0.48f
                        : 0f;
                    float snare = (beatIdx % 2 == 1)
                        ? (Mathf.Sin(2f * Mathf.PI * 190f * beatT) * 0.4f + noise * 0.6f) * Mathf.Exp(-beatT * 22f) * 0.35f
                        : 0f;
                    float hat = SoftLowpass(ref hatLp, noise * Mathf.Exp(-(t % 0.1875f) * 44f) * 0.14f, 7000f);

                    // Crunchy power-chord guitar strums + soaring major brass lead
                    float chug = (float)Math.Tanh((Saw(rootF * 2f * t) * 0.5f + Saw(rootF * 3f * t) * 0.4f + Saw(rootF * 4f * t) * 0.3f) * 2.4f)
                                 * (0.45f + 0.55f * Mathf.Exp(-(t % 0.1875f) * 8f)) * 0.32f;
                    float atk = Mathf.Clamp01(segT / 0.04f);
                    float brass = (float)Math.Tanh((Saw(mHz * t) * 0.5f + Saw(thirdOrFifthHz * 1.003f * t) * 0.4f) * 1.75f) * atk * 0.42f;
                    float rawL = (kick + snare + hat + chug + brass) * globalFade;
                    float rawR = (kick + snare + hat + chug * 0.94f + brass * 0.97f) * globalFade;
                    L[i] = SoftLowpass(ref lpL, rawL, 5200f);
                    R[i] = SoftLowpass(ref lpR, rawR, 5200f);
                }
                else
                {
                    // Depressing heavy defeat lament: slow muffled tolling sub + weeping detuned minor cello + low-pass collapse
                    float atk = Mathf.Clamp01(segT / 0.18f);
                    float fadeSeg = Mathf.Exp(-segT * 0.65f);
                    float bass = (Mathf.Sin(2f * Mathf.PI * rootF * t) * 0.62f + Saw(rootF * t) * 0.22f);
                    float vibrato = 1f + 0.0055f * Mathf.Sin(2f * Mathf.PI * 4.5f * t);
                    float celloL = (Saw(mHz * vibrato * t) * 0.52f + Saw(thirdOrFifthHz * 0.996f * t) * 0.44f + Saw(mHz * 0.5f * t) * 0.28f) * atk * fadeSeg;
                    float celloR = (Saw(mHz * vibrato * 0.998f * t) * 0.52f + Saw(thirdOrFifthHz * 1.004f * t) * 0.44f + Saw(mHz * 0.5f * t) * 0.28f) * atk * fadeSeg;
                    // Distant hollow toll on bars 0, 2, 4, 6
                    float toll = (seg % 2 == 0 && segT < 1.1f)
                        ? (Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(72f, 36f, Mathf.Clamp01(segT * 4f)) * segT) * Mathf.Exp(-segT * 3.8f) * 0.45f)
                        : 0f;
                    float cut = Mathf.Lerp(1450f, 340f, t / dur);
                    float rawL = (bass * 0.42f + celloL * 0.38f + toll) * globalFade;
                    float rawR = (bass * 0.42f + celloR * 0.38f + toll) * globalFade;
                    L[i] = SoftLowpass(ref lpL, rawL, cut);
                    R[i] = SoftLowpass(ref lpR, rawR, cut);
                }
            }

            AddCircularStereoEcho(L, R, Mathf.RoundToInt((win ? 0.1875f : 0.42f) * SampleRate), win ? 0.18f : 0.28f);
            FinalizeStinger(L, R, win ? 0.24f : 0.145f, 0.88f);
            WriteWav(path, L, R);
        }

        private static void AddWarTornOrchestralBed(float[] L, float[] R, float semitoneShift, float brassWeight, float gritDrive, bool withArtillery)
        {
            int n = L.Length;
            float hornLpL = 0f, hornLpR = 0f, artLp = 0f;
            var rng = new System.Random(1944);
            float[] artStarts = { 0.40f, 5.15f, 9.85f, 13.70f };

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                int bar = Mathf.Clamp((int)(t / 2f), 0, 7);
                float barT = t - bar * 2f;
                float edge = Mathf.Clamp01(barT / 0.04f) * Mathf.Clamp01((2f - barT) / 0.04f);

                float rootF = LoopLockedPhaseHz(MidiToHz(BassMidi[bar] + semitoneShift + 12f));
                float fifthF = (bar == 3 || bar == 7)
                    ? LoopLockedPhaseHz(MidiToHz(BassMidi[bar] + semitoneShift + 13f))
                    : LoopLockedPhaseHz(MidiToHz(BassMidi[bar] + semitoneShift + 19f));
                float swell = (0.35f + 0.65f * Mathf.Clamp01(bar / 7f)) * edge;
                float brassL = (float)Math.Tanh((Saw(rootF * 1.002f * t) + Saw(fifthF * 0.998f * t) * 0.72f) * gritDrive) * brassWeight * swell;
                float brassR = (float)Math.Tanh((Saw(rootF * 0.998f * t) + Saw(fifthF * 1.002f * t) * 0.72f) * gritDrive) * brassWeight * swell;
                float bCut = 880f + 620f * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 0.5f * t / DurationSec));

                float art = 0f;
                if (withArtillery)
                {
                    float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                    for (int a = 0; a < artStarts.Length; a++)
                    {
                        float dt = t - artStarts[a];
                        if (dt >= 0f && dt < 1.15f)
                        {
                            float rumbleEnv = Mathf.Clamp01(dt / 0.025f) * Mathf.Exp(-dt * 3.4f);
                            float subBoom = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(74f, 34f, Mathf.Clamp01(dt * 1.8f)) * dt);
                            art += (subBoom * 0.62f + white * 0.55f) * rumbleEnv;
                        }
                    }
                    art = SoftLowpass(ref artLp, art * 0.16f, 220f);
                }

                L[i] += SoftLowpass(ref hornLpL, brassL, bCut) + art;
                R[i] += SoftLowpass(ref hornLpR, brassR, bCut) + art;
            }
        }

        // ------------------------------------------------------------------ focus layer (11-26)
        // The developer: the soundtrack is "nice, just too robotic/simplish"; it should be music that helps people lock in.
        // Study-music conventions: a steady unhurried pulse, warm low-information harmony, a sparse melody that never
        // demands attention, and nothing perfectly on the grid. This layer adds that on top of the menu, calm and tension
        // stems: electric-piano chords strummed by hand, a quiet answering melody that sometimes rests, a soft swung kit,
        // tape wobble, a faint vinyl bed and a small room. Everything is seeded (the same build gives the same music) and
        // written around the loop point, so the 16 s stems still loop without a seam.

        private static readonly int[][] MelodyA = { new[] { 0, 2 }, new[] { 3, 3 }, new[] { 5, 1 } };             // (eighth, arp index)
        private static readonly int[][] MelodyB = { new[] { 1, 4 }, new[] { 4, 2 }, new[] { 6, 3 } };

        private static void AddFocusLayer(float[] L, float[] R, float semitoneShift, int seed, float amount, bool drums)
        {
            int n = L.Length;
            var fL = new float[n];
            var fR = new float[n];
            var rng = new System.Random(seed * 7919 + 17);
            float Jitter(float ms) => (float)(rng.NextDouble() * 2.0 - 1.0) * ms / 1000f;
            float Vel(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
            const float beat = 0.5f, eighth = 0.25f, swing = 0.035f; // offbeat eighths land 35 ms late

            for (int bar = 0; bar < Bars; bar++)
            {
                float barStart = bar * 2f;
                var chord = PadChords[bar];
                bool restrike = bar % 2 == 1;

                // Electric-piano chord, strummed low to high, with a softer comp on beat 3 every other bar.
                for (int hit = 0; hit < (restrike ? 2 : 1); hit++)
                {
                    float hitTime = barStart + hit * 2f * beat + Jitter(8f);
                    float hitVel = hit == 0 ? Vel(0.62f, 0.82f) : Vel(0.38f, 0.52f);
                    float length = (hit == 0 && restrike ? 2f * beat : 2f) + 0.35f;
                    float strum = 0f;
                    for (int v = 0; v < chord.Length; v++)
                    {
                        strum += 0.008f + (float)rng.NextDouble() * 0.012f;
                        float pan = 0.5f + (v - 1.5f) * 0.18f;
                        AddNote(fL, fR, hitTime + strum, length, MidiToHz(chord[v] + semitoneShift), hitVel * (v == 0 ? 0.8f : 1f) * 0.085f, pan, EPianoSample);
                    }
                }

                // Melody: call (bars 0-3) and answer (bars 4-7); a quarter of the notes rest, the last bar resolves long.
                var motif = bar < 4 ? MelodyA : MelodyB;
                for (int k = 0; k < motif.Length; k++)
                {
                    if (rng.NextDouble() < 0.25 && !(bar == Bars - 1 && k == motif.Length - 1)) continue;
                    float midi = ArpNotes[bar][motif[k][1]] + semitoneShift;
                    while (midi < 67f) midi += 12f;
                    float time = barStart + motif[k][0] * eighth + (motif[k][0] % 2 == 1 ? swing : 0f) + Jitter(12f);
                    float length = bar == Bars - 1 && k == motif.Length - 1 ? 1.6f : Vel(0.55f, 0.9f);
                    AddNote(fL, fR, time, length, MidiToHz(midi), Vel(0.55f, 0.85f) * 0.05f, 0.5f + Jitter(250f), GlassSample);
                }

                if (!drums) continue;
                for (int e = 0; e < 8; e++)
                {
                    float time = barStart + e * eighth + (e % 2 == 1 ? swing : 0f) + Jitter(6f);
                    bool kick = e == 0 || e == 3 || (bar % 2 == 1 && e == 4);
                    if (kick) AddNote(fL, fR, time, 0.45f, 0f, Vel(0.75f, 0.95f) * 0.20f, 0.5f, KickSample);
                    if (e == 2 || e == 6) AddNote(fL, fR, time, 0.25f, 0f, Vel(0.6f, 0.8f) * 0.07f, 0.55f, RimSample);
                    AddNote(fL, fR, time, 0.12f, 0f, (e % 2 == 0 ? Vel(0.6f, 0.8f) : Vel(0.3f, 0.5f)) * 0.035f, 0.62f, HatSample);
                }
            }

            // Faint vinyl/tape bed: low-passed noise with the odd crackle.
            float bedLp = 0f;
            for (int i = 0; i < n; i++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                float bed = SoftLowpass(ref bedLp, white, 2400f) * 0.010f + (rng.NextDouble() < 0.00012 ? white * 0.03f : 0f);
                fL[i] += bed;
                fR[i] += bed * 0.9f;
            }

            AddCircularRoom(fL, fR, 0.22f);
            for (int i = 0; i < n; i++)
            {
                L[i] += fL[i] * amount;
                R[i] += fR[i] * amount;
            }
        }

        private delegate float NoteVoice(float t, float hz, float length, int noiseSeed);

        /// <summary>Adds one note, wrapping around the loop so a tail that runs past the end continues at the start.</summary>
        private static void AddNote(float[] L, float[] R, float start, float length, float hz, float gain, float pan, NoteVoice voice)
        {
            int n = L.Length;
            int s0 = Mathf.RoundToInt(start * SampleRate);
            int count = Mathf.RoundToInt((length + 0.25f) * SampleRate);
            float gl = gain * Mathf.Sqrt(1f - Mathf.Clamp01(pan)), gr = gain * Mathf.Sqrt(Mathf.Clamp01(pan));
            int noiseSeed = s0;
            for (int k = 0; k < count; k++)
            {
                float t = k / (float)SampleRate;
                float release = t > length ? Mathf.Clamp01(1f - (t - length) / 0.25f) : 1f;
                float x = voice(t, hz, length, noiseSeed + k) * release;
                int idx = ((s0 + k) % n + n) % n;
                L[idx] += x * gl;
                R[idx] += x * gr;
            }
        }

        private static float Wow(float t) => 1f + 0.0016f * Mathf.Sin(2f * Mathf.PI * 0.33f * t) + 0.0007f * Mathf.Sin(2f * Mathf.PI * 1.7f * t);

        /// <summary>Soft Rhodes-like tone: a sine carrier with a decaying bell modulation, a short tine, gentle tape wobble.</summary>
        private static float EPianoSample(float t, float hz, float length, int noiseSeed)
        {
            float f = hz * Wow(t);
            float attack = Mathf.Clamp01(t / 0.004f);
            float index = 1.3f * Mathf.Exp(-t * 5.5f);
            float tone = Mathf.Sin(2f * Mathf.PI * f * t + index * Mathf.Sin(2f * Mathf.PI * f * t));
            float tine = Mathf.Sin(2f * Mathf.PI * f * 4.01f * t) * 0.07f * Mathf.Exp(-t * 14f);
            return (tone * Mathf.Exp(-t * 0.85f) + tine) * attack;
        }

        /// <summary>Quiet glassy lead: sine plus a little octave, slow attack, long soft decay.</summary>
        private static float GlassSample(float t, float hz, float length, int noiseSeed)
        {
            float f = hz * Wow(t);
            float attack = Mathf.Clamp01(t / 0.012f);
            return (Mathf.Sin(2f * Mathf.PI * f * t) + 0.18f * Mathf.Sin(4f * Mathf.PI * f * t)) * Mathf.Exp(-t * 2.1f) * attack;
        }

        private static float KickSample(float t, float hz, float length, int noiseSeed)
        {
            float f = Mathf.Lerp(105f, 46f, Mathf.Clamp01(t * 14f));
            return Mathf.Sin(2f * Mathf.PI * f * t) * Mathf.Exp(-t * 9f) * Mathf.Clamp01(t / 0.002f);
        }

        private static float Noise(int seed)
        {
            unchecked
            {
                uint x = (uint)seed * 747796405u + 2891336453u;
                x = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
                return ((x >> 22) ^ x) / (float)uint.MaxValue * 2f - 1f;
            }
        }

        private static float RimSample(float t, float hz, float length, int noiseSeed) =>
            (Noise(noiseSeed) * 0.6f + Mathf.Sin(2f * Mathf.PI * 190f * t) * 0.5f) * Mathf.Exp(-t * 22f);

        private static float HatSample(float t, float hz, float length, int noiseSeed) =>
            (Noise(noiseSeed) - Noise(noiseSeed - 1)) * 0.5f * Mathf.Exp(-t * 48f);

        /// <summary>
        /// Small stereo room (four combs and two all-passes per side). It runs over the loop twice and keeps the second pass,
        /// so the tail at the end of the loop is already sounding at its start: no click or gap at the loop point.
        /// </summary>
        private static void AddCircularRoom(float[] L, float[] R, float wet)
        {
            int n = L.Length;
            var outL = Room(L, new[] { 1557, 1617, 1491, 1422 }, new[] { 225, 556 }, n);
            var outR = Room(R, new[] { 1580, 1640, 1514, 1445 }, new[] { 248, 579 }, n);
            for (int i = 0; i < n; i++)
            {
                L[i] += outL[i] * wet;
                R[i] += outR[i] * wet;
            }
        }

        private static float[] Room(float[] input, int[] combs, int[] allpasses, int n)
        {
            var output = new float[n];
            var combBuf = new float[combs.Length][];
            var combIdx = new int[combs.Length];
            var combLp = new float[combs.Length];
            for (int c = 0; c < combs.Length; c++) combBuf[c] = new float[combs[c]];
            var apBuf = new float[allpasses.Length][];
            var apIdx = new int[allpasses.Length];
            for (int a = 0; a < allpasses.Length; a++) apBuf[a] = new float[allpasses[a]];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    float x = input[i] * 0.25f, sum = 0f;
                    for (int c = 0; c < combs.Length; c++)
                    {
                        float y = combBuf[c][combIdx[c]];
                        combLp[c] = y * 0.72f + combLp[c] * 0.28f; // damping: the room is soft, not metallic
                        combBuf[c][combIdx[c]] = x + combLp[c] * 0.80f;
                        combIdx[c] = (combIdx[c] + 1) % combs[c];
                        sum += y;
                    }
                    for (int a = 0; a < allpasses.Length; a++)
                    {
                        float b = apBuf[a][apIdx[a]];
                        apBuf[a][apIdx[a]] = sum + b * 0.5f;
                        apIdx[a] = (apIdx[a] + 1) % allpasses[a];
                        sum = b - sum;
                    }
                    if (pass == 1) output[i] = sum;
                }
            }
            return output;
        }

        private static void AddCircularStereoEcho(float[] L, float[] R, int delaySamples, float feedbackGain)
        {
            int n = L.Length;
            float[] eL = new float[n], eR = new float[n];
            for (int i = 0; i < n; i++)
            {
                int src = (i - delaySamples + n) % n;
                int src2 = (i - delaySamples * 2 + n) % n;
                eL[i] = R[src] * feedbackGain + L[src2] * (feedbackGain * 0.42f);
                eR[i] = L[src] * feedbackGain + R[src2] * (feedbackGain * 0.42f);
            }
            for (int i = 0; i < n; i++)
            {
                L[i] += eL[i];
                R[i] += eR[i];
            }
        }

        private static void FinalizeStinger(float[] L, float[] R, float targetRms, float maxPeak)
        {
            int n = L.Length;
            double sumSq = 0.0;
            for (int i = 0; i < n; i++)
                sumSq += 0.5 * (L[i] * (double)L[i] + R[i] * (double)R[i]);
            float curRms = Mathf.Sqrt((float)(sumSq / Mathf.Max(1, n)));
            float gain = curRms > 1e-5f ? targetRms / curRms : 1f;
            for (int i = 0; i < n; i++)
            {
                L[i] = Mathf.Clamp(L[i] * gain, -maxPeak, maxPeak);
                R[i] = Mathf.Clamp(R[i] * gain, -maxPeak, maxPeak);
            }
        }

        private static void FinalizeStem(float[] L, float[] R, float targetRms, float maxPeak)
        {
            int n = L.Length;
            double sumSq = 0.0;
            for (int i = 0; i < n; i++)
                sumSq += 0.5 * (L[i] * (double)L[i] + R[i] * (double)R[i]);
            float curRms = Mathf.Sqrt((float)(sumSq / Mathf.Max(1, n)));
            float gain = curRms > 1e-5f ? targetRms / curRms : 1f;

            for (int i = 0; i < n; i++)
            {
                float xL = L[i] * gain;
                float xR = R[i] * gain;
                float softKnee = maxPeak * 0.72f;
                if (Mathf.Abs(xL) > softKnee) xL = Mathf.Sign(xL) * (softKnee + (maxPeak - softKnee) * (float)Math.Tanh((Mathf.Abs(xL) - softKnee) / Mathf.Max(0.05f, maxPeak - softKnee)));
                if (Mathf.Abs(xR) > softKnee) xR = Mathf.Sign(xR) * (softKnee + (maxPeak - softKnee) * (float)Math.Tanh((Mathf.Abs(xR) - softKnee) / Mathf.Max(0.05f, maxPeak - softKnee)));
                L[i] = xL;
                R[i] = xR;
            }

            const int Seam = 256;
            for (int k = 0; k < Seam; k++)
            {
                float a = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * k / Seam);
                L[k] *= a;
                R[k] *= a;
                L[n - 1 - k] *= a;
                R[n - 1 - k] *= a;
            }
        }

        private enum GuitarIntensity { CleanCalm, OverdriveTension, HeavyCombat }

        private static void MixProgressiveGuitar(float[] L, float[] R, GuitarIntensity mode, float semitoneShift)
        {
            int n = TotalSamples;
            float[] rL = new float[n];
            float[] rR = new float[n];
            float[] lead = new float[n];

            int stepSamples = Mathf.RoundToInt(SampleRate * (60f / Bpm) * 0.25f);
            int totalSteps = Bars * 16;

            bool[] chugMask =
            {
                true, false, true, true,  false, true, true, false,
                true, false, true, false, true,  true, false, true
            };
            bool[] openAccentMask =
            {
                true, false, false, false, false, false, true, false,
                true, false, false, false, true,  false, false, false
            };

            float[][] leadBars =
            {
                new float[] { 62f, -1, -1, -1,  -1, -1, 65f, -1,  64f, -1, -1, -1,  -1, -1, 62f, -1 },
                new float[] { 58f, -1, -1, -1,  62f, -1, -1, -1,  65f, -1, -1, -1,  64f, -1, 62f, -1 },
                new float[] { 62f, -1, 65f, -1, 67f, -1, -1, -1,  65f, -1, 64f, -1, 62f, -1, -1, -1 },
                new float[] { 61f, -1, 64f, -1, 69f, -1, -1, -1,  67f, -1, 64f, -1, 61f, -1, 64f, -1 },
                new float[] { 62f, -1, 65f, -1, 69f, -1, 70f, -1, 69f, -1, 67f, -1, 65f, -1, 64f, -1 },
                new float[] { 67f, -1, 69f, -1, 72f, -1, -1, -1,  71f, -1, 69f, -1, 67f, -1, 64f, -1 },
                new float[] { 70f, -1, 69f, -1, 67f, -1, 65f, -1, 69f, -1, 70f, -1, 72f, -1, 74f, -1 },
                new float[] { 73f, -1, 74f, -1, 76f, -1, 74f, -1, 73f, -1, 69f, -1, 67f, -1, 64f, -1 },
            };

            int doubleTrackOffset = Mathf.RoundToInt(0.0065f * SampleRate);

            for (int step = 0; step < totalSteps; step++)
            {
                int bar = step / 16;
                int sInBar = step % 16;
                float barProg = (float)step / (totalSteps - 1);
                int startSample = step * stepSamples;
                float root = BassMidi[bar] + semitoneShift;

                if (mode == GuitarIntensity.CleanCalm)
                {
                    bool playClean = (bar < 4) ? (sInBar == 0 || sInBar == 10) : (sInBar == 0 || sInBar == 4 || sInBar == 8 || sInBar == 12);
                    if (!playClean) continue;
                    float cleanNote = ArpNotes[bar][sInBar % 8] + semitoneShift;
                    float vel = Mathf.Lerp(0.16f, 0.30f, barProg);
                    RenderKarplusNote(rL, startSample, cleanNote, 0.62f, vel, damping: 0.9965f, seed: 101 + step, vibratoCents: 6f);
                    RenderKarplusNote(rR, (startSample + 220) % n, cleanNote + 12f, 0.50f, vel * 0.45f, damping: 0.9955f, seed: 202 + step, vibratoCents: 9f);
                    continue;
                }

                if (mode == GuitarIntensity.OverdriveTension)
                {
                    if (!chugMask[sInBar] && bar < 5) continue;
                    bool muted = !(bar >= 4 && openAccentMask[sInBar]);
                    float noteMidi = (bar == 3 && sInBar >= 12) ? root + 1f : (bar == 7 && sInBar >= 12 ? root + 3f : root);
                    float damp = muted ? Mathf.Lerp(0.954f, 0.972f, barProg) : 0.994f;
                    float dur = muted ? 0.14f : 0.34f;
                    float vel = (muted ? 0.34f : 0.54f) * Mathf.Lerp(0.65f, 1.15f, barProg);
                    RenderKarplusNote(rL, startSample, noteMidi, dur, vel, damp, 301 + step, 0f);
                    RenderKarplusNote(rR, (startSample + doubleTrackOffset) % n, noteMidi, dur, vel * 0.96f, damp, 401 + step, 0f);
                    if (!muted)
                    {
                        RenderKarplusNote(rL, startSample, noteMidi + 7f, dur, vel * 0.78f, damp, 501 + step, 0f);
                        RenderKarplusNote(rR, (startSample + doubleTrackOffset) % n, noteMidi + 7f, dur, vel * 0.78f, damp, 601 + step, 0f);
                    }
                    continue;
                }

                bool gallopExtra = bar >= 4 && (sInBar == 7 || sInBar == 14);
                if (chugMask[sInBar] || gallopExtra)
                {
                    bool open = openAccentMask[sInBar] || (bar >= 6 && sInBar % 4 == 0);
                    float rNote = root;
                    if (bar == 3 && sInBar >= 14) rNote = root + 1f;
                    else if (bar == 4 && sInBar == 10) rNote = 39f + semitoneShift;
                    else if (bar == 7 && sInBar >= 12) rNote = root + (sInBar - 12);

                    float damp = open ? Mathf.Lerp(0.992f, 0.996f, barProg) : Mathf.Lerp(0.958f, 0.975f, barProg);
                    float dur = open ? 0.36f : 0.135f;
                    float vel = (open ? 0.68f : 0.44f) * Mathf.Lerp(0.75f, 1.18f, barProg);

                    RenderKarplusNote(rL, startSample, rNote, dur, vel, damp, 701 + step, open ? 4f : 0f);
                    RenderKarplusNote(rL, startSample, rNote + 7f, dur, vel * 0.85f, damp, 801 + step, 0f);
                    RenderKarplusNote(rR, (startSample + doubleTrackOffset) % n, rNote + 0.04f, dur, vel * 0.98f, damp, 901 + step, open ? -4f : 0f);
                    RenderKarplusNote(rR, (startSample + doubleTrackOffset) % n, rNote + 7.04f, dur, vel * 0.85f, damp, 1001 + step, 0f);
                    if (open && bar >= 3)
                    {
                        RenderKarplusNote(rL, startSample, rNote + 12f, dur * 0.85f, vel * 0.55f, damp, 1101 + step, 6f);
                        RenderKarplusNote(rR, (startSample + doubleTrackOffset) % n, rNote + 12.03f, dur * 0.85f, vel * 0.55f, damp, 1201 + step, -6f);
                    }
                }

                float leadMidi = leadBars[bar][sInBar];
                if (leadMidi > 0f)
                {
                    leadMidi += semitoneShift;
                    float leadGain = Mathf.Lerp(0.22f, 0.68f, barProg);
                    float leadDur = (bar >= 6) ? 0.30f : 0.42f;
                    RenderKarplusNote(lead, startSample, leadMidi, leadDur, leadGain, damping: 0.9968f, seed: 1301 + step, vibratoCents: 14f);
                }
            }

            float hpL = 0f, hpPrevL = 0f, hpR = 0f, hpPrevR = 0f, hpLead = 0f, hpPrevLead = 0f;
            float lp1L = 0f, lp2L = 0f, lp1R = 0f, lp2R = 0f, lp1Lead = 0f, lp2Lead = 0f;
            float baseDrive = mode == GuitarIntensity.CleanCalm ? 0.85f : (mode == GuitarIntensity.OverdriveTension ? 1.55f : 2.55f);
            float endDrive = mode == GuitarIntensity.CleanCalm ? 1.15f : (mode == GuitarIntensity.OverdriveTension ? 3.10f : 4.65f);
            float baseCut = mode == GuitarIntensity.CleanCalm ? 1650f : (mode == GuitarIntensity.OverdriveTension ? 1200f : 2450f);
            float endCut = mode == GuitarIntensity.CleanCalm ? 2600f : (mode == GuitarIntensity.OverdriveTension ? 2250f : 3450f);
            float mixWeight = mode == GuitarIntensity.CleanCalm ? 0.14f : (mode == GuitarIntensity.OverdriveTension ? 0.24f : 0.34f);

            const float HpAlpha = 0.9865f;
            for (int i = 0; i < n; i++)
            {
                float prog = (float)i / n;
                float drive = Mathf.Lerp(baseDrive, endDrive, prog);
                float cabCut = Mathf.Lerp(baseCut, endCut, prog);

                float xL = rL[i];
                float xR = rR[i];
                hpL = HpAlpha * (hpL + xL - hpPrevL); hpPrevL = xL;
                hpR = HpAlpha * (hpR + xR - hpPrevR); hpPrevR = xR;

                float satL = mode == GuitarIntensity.CleanCalm
                    ? (float)Math.Tanh(hpL * drive)
                    : (float)Math.Tanh(hpL * drive + 0.16f * hpL * hpL) / (1f + 0.22f * Mathf.Abs(hpL));
                float satR = mode == GuitarIntensity.CleanCalm
                    ? (float)Math.Tanh(hpR * drive)
                    : (float)Math.Tanh(hpR * drive + 0.16f * hpR * hpR) / (1f + 0.22f * Mathf.Abs(hpR));

                float cabL = SoftLowpass(ref lp2L, SoftLowpass(ref lp1L, satL, cabCut), cabCut * 0.95f);
                float cabR = SoftLowpass(ref lp2R, SoftLowpass(ref lp1R, satR, cabCut), cabCut * 0.95f);

                float xLd = lead[i];
                hpLead = HpAlpha * (hpLead + xLd - hpPrevLead); hpPrevLead = xLd;
                float satLd = (float)Math.Tanh(hpLead * (drive * 1.15f) + 0.18f * hpLead * hpLead);
                float cabLd = SoftLowpass(ref lp2Lead, SoftLowpass(ref lp1Lead, satLd, cabCut + 300f), cabCut + 200f);

                L[i] += (cabL * 0.82f + cabR * 0.18f + cabLd * 0.42f) * mixWeight;
                R[i] += (cabR * 0.82f + cabL * 0.18f + cabLd * 0.50f) * mixWeight;
            }
        }

        private static void RenderKarplusNote(float[] target, int startSample, float midi, float durationSec, float velocity, float damping, int seed, float vibratoCents)
        {
            int n = target.Length;
            float baseFreq = MidiToHz(midi);
            float periodSamples = SampleRate / Mathf.Max(40f, baseFreq);
            int delayLen = Mathf.Max(8, Mathf.CeilToInt(periodSamples) + 4);
            float[] ring = new float[delayLen];

            var rng = new System.Random(seed);
            float pickLp = 0f;
            for (int k = 0; k < delayLen; k++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                pickLp += 0.42f * (white - pickLp);
                ring[k] = pickLp * velocity;
            }

            int totalNoteSamples = Mathf.Max(delayLen, Mathf.RoundToInt(durationSec * SampleRate));
            int ptr = 0;
            int releaseLen = Mathf.Max(256, totalNoteSamples / 5);

            for (int i = 0; i < totalNoteSamples; i++)
            {
                float curFreq = baseFreq;
                if (vibratoCents > 0.1f && i > SampleRate * 0.08f)
                {
                    float vib = Mathf.Sin(2f * Mathf.PI * 5.6f * (i / (float)SampleRate));
                    curFreq *= Mathf.Pow(2f, (vibratoCents * 0.01f * vib) / 12f);
                }

                float curDelay = Mathf.Clamp(SampleRate / curFreq, 4f, delayLen - 2f);
                float readPos = ptr - curDelay;
                while (readPos < 0f) readPos += delayLen;

                int i0 = Mathf.FloorToInt(readPos) % delayLen;
                int i1 = (i0 + 1) % delayLen;
                float frac = readPos - Mathf.Floor(readPos);
                float sample = Mathf.Lerp(ring[i0], ring[i1], frac);

                int prevIdx = (i0 - 1 + delayLen) % delayLen;
                float filtered = 0.5f * (sample + ring[prevIdx]) * damping;
                ring[ptr] = filtered;
                ptr = (ptr + 1) % delayLen;

                float env = 1f;
                int rem = totalNoteSamples - 1 - i;
                if (rem < releaseLen) env = (float)rem / releaseLen;

                int dst = (startSample + i) % n;
                target[dst] += sample * env;
            }
        }

        private static void WriteWav(string path, float[] L, float[] R)
        {
            int numSamples = L.Length;
            int dataBytes = numSamples * 2 * 2;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytes);
            bw.Write(new[] { 'W', 'A', 'V', 'E' });
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)2);
            bw.Write(SampleRate);
            bw.Write(SampleRate * 4);
            bw.Write((short)4);
            bw.Write((short)16);
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytes);
            for (int i = 0; i < numSamples; i++)
            {
                short sL = (short)Mathf.Clamp(Mathf.RoundToInt(L[i] * 32767f), -32767, 32767);
                short sR = (short)Mathf.Clamp(Mathf.RoundToInt(R[i] * 32767f), -32767, 32767);
                bw.Write(sL);
                bw.Write(sR);
            }
        }
    }
}
