# B136g — Battlefield 6-Style Soldier Candidate Research & 1-Flag Revertible Swap Spec

**Status:** Research & Design Complete (`2026-11-22`, zero changes applied to `B136` OG Kinemation `FPSGenericPlayer` robot prefabs per user instruction)  
**Owners:** Gemini + Codex (cross-review)  
**Goal:** Evaluate realistic tactical soldier visual upgrades inspired by *Battlefield 6* (**NATO** conventional western infantry vs **Pax Armata** sleek PMC/hybrid operators) while guaranteeing a **1-flag, zero-risk revertible swap** over LeadRush 2's current OG Kinemation Generic rig (`FPSGenericPlayer` / `LR2ContentBuilder.Robot.cs`).

---

## 1. Visual Target: *Battlefield 6* Faction Readability & Silhouette

Based on *Battlefield 6* faction art-direction analysis (`bo3.gg`, `insider-gaming.com`, community breakdowns):

| Faction Aesthetic | Silhouette & Tactical Gear | LR2 Team Role Mapping | Palette / Contrast Constraints in LR2 |
|---|---|---|---|
| **NATO-Style Allied Infantry** (Coyote Squad / United Albion) | High-cut ballistic helmet with NVG shroud + ear pro, structured JPC/CPC modular plate carrier, pronounced shoulder pads, pouch webbing, kneepads. | **Team A (Friendly / Blue)** | Muted ranger green / matte coyote / slate grey with subtle sapphire/blue shoulder/IFF patches so friendly silhouettes stay grounded yet distinct against concrete (`LuminanceFloor >= 0.055`). |
| **Pax Armata-Style OpFor PMC** (Vedmak / Espada) | Streamlined dark contractor chassis, aggressive low-profile plate carrier, balaclava/full-face ballistic mask or tactical visor, fitted harness, lighter limb clutter. | **Team B (Hostile / Red)** | Charcoal/graphite base with crimson/rust chest rig trim & matte skull/balaclava contrast so `EnemyReadability >= 0.03` holds across smoky `Saltworks`, `Deadsector`, and shadowed `Foundry`. |

**Critical LR2 Constraint (learned from pre-`B136` SWAT failures):**  
The visual mesh **must never drive the first-person weapon IK skeleton directly** unless calibrated per-bone; Kinemation's `FPSGenericPlayer` Generic rig (`B136`) drives spotless weapon hold, ADS sight picture, fingers, reloads, and grenade/knife/torch poses.

---

## 2. Candidate Evaluation (Ranked by Fidelity, License Safety & Integration Effort)

| Rank | Candidate | Source / Path Verified on Disk or Web | Rig / Pipeline | Pros | Risks / License Notes |
|---|---|---|---|---|---|
| **1 (Recommended Pilot)** | **In-Repo `MixamoSwat` + NATO/PMC Gear Kitbash** (Helmets, Plate Carriers, Skull Balaclava Material Variants) | `Assets/KinemationDemo/Meshes/Character/MixamoSwat/Swat.fbx` + `Materials/Soldier_body1.mat`, `Soldier_head6.mat` | Unity **Humanoid** (`FPSHumanoidPlayer.prefab` compatible) | Zero external download, already HDRP Lit (`Soldier_Body_d/n/s`), already proven with `LR2MapBuilder` SWAT fallback (`B136b`), zero weight-paint bugs. | Single silhouette for both teams unless accented with team helmet/vest trim meshes (`B19`). |
| **2 (High-Fidelity Free/Cache Reference)** | **Time Ghost Character - HDRP** | Local cache: `%AppData%\Unity\Asset Store-5.x\Unity Technologies\Complete ProjectsUnity Tech Demos\Time Ghost Character - HDRP.unitypackage` (`1,499 MB`) | Native **Unity 6 HDRP** Cloth/Skin/Armor shaders | AAA material reference for wet fabric, micro-normal cloth, and subsurface skin in HDRP 17.5. | Large tech-demo package; check `THIRD_PARTY_ASSETS.md` gitignore rules (`Assets/UnityTechnologies/`), heavier draw calls/bone count than tactical combat requires (`P3` frame budget `< 10 ms` med). |
| **3 (Open / Git-Redistributable Upgrade)** | **CC0 Military Character Kit + AmbientCG/Poly Haven Tactical Fabric + Mixamo Autorig** | Sketchfab `Military_Character_Kit_Textured (CC0)` + `ambientcg.com` (`CC0 1.0`) camo/cordura maps | Custom Blender kitbash -> Mixamo / Unity **Humanoid** | Can be committed cleanly in public GitHub releases (`B33`) with zero Asset Store license conflicts; easy to sculpt distinct NATO high-cut helmet vs Pax Armata contractor hood silhouettes. | Requires offline Blender LOD + Humanoid T-pose weight verification and Built-in -> HDRP Mask Map packing (`Metallic/AO/Detail/Smoothness`). |
| **4 (Modular Paid/Store Option)** | **Unity Asset Store Modular Soldier / PMC Packs** (upgraded to HDRP via *Upgrade Project Materials to HDRP*) | Asset Store modular military kits (`Soldier` / `PMC`) | Unity **Humanoid** | Fastest route to AAA multi-head/multi-vest NATO vs PMC variety (pouches, COMTAC headsets, IR strobes). | Paid / EULA-restricted -> must stay in gitignored `Assets/` folders (`THIRD_PARTY_ASSETS.md`). |

---

## 3. Architecture: 1-Flag, Instantly Revertible Dual-Rig Visual Proxy (`UseTacticalSoldierVisuals`)

**Why `B136` works cleanly right now:** `LR2ContentBuilder.Robot.cs` builds `LR2_Soldier_Player` and `LR2_Soldier_Bot` on `FPSGenericPlayer`'s Generic skeleton so all Kinemation weapon layers, optics, recoil, and reload bridges work identically out of the box.

To test or ship a BF6-style visual character **without ever breaking Kinemation Generic weapon IK**:

1. **Single Compile/Builder Switch (Default `false`):**
   ```csharp
   // Inside LR2ContentBuilder.Robot.cs (future B136g implementation)
   public static bool UseTacticalSoldierVisuals = false; // false = OG Kinemation Robot (B136 baseline)
   ```
2. **Driver-vs-Puppet Split (Same Pattern Tested in `B108`/`B136b`):**
   - Keep `FPSGenericPlayer` (`Animator` + Kinemation `FPSAnimator` + `AttachHandLayer` + `LR2Firearm` sockets) intact as the **authoritative locomotion & weapon IK driver**.
   - **First-Person Camera:** Always renders the calibrated OG weapon + hands layer (`fpsHandsOnly`) or matches Humanoid arm skin cleanly; zero weapon grip/clipping regressions (`D63`/`D77` eliminated).
   - **Third-Person / Killcam / Menu Visual Root (`TacticalVisual_NATO` / `TacticalVisual_Pax`):**
     - Child Humanoid/skinned puppet driven by pelvis/spine/limb transforms or `Animator.Copy` from the Generic driver (hiding `Body`/`Head` robot meshes only on the 3P layer while first-person shadow/hands remain unaffected).
     - `SoldierController` team-tint & killcam head-hide loops already support both Robot (`Body`/`Head`) and SWAT (`SWAT_Visual`) renderers (`B136b` / `KillcamDirector`).
3. **One-Line Revert Guarantee:**
   - Setting `UseTacticalSoldierVisuals = false` and re-running `LR2BuildPipeline.BuildAllBatch` restores `100%` OG Kinemation robot prefabs, Main Menu lineup, and hitboxes with zero migration cost.

---

## 4. Verification Gates Before Enabling `UseTacticalSoldierVisuals = true`

1. **Prop & Hitbox Parity:** Ragdoll colliders and `Health` capsule (`r=0.35m, h=1.8m`) remain unchanged so `LR2PropAudit` spawn clearances (`0/N`) stay identical.
2. **First-Person Cleanliness:** Zero chest/helmet mesh bleeding into the ADS camera (`nearClip=0.03m`) or blocking PiP scopes (`LR2/PipScope`).
3. **Readability & Performance (`P3`):** `EnemyReadability >= 0.030` across all 4 maps and `< 0.4 ms` delta on 16-bot `LR2_Saltworks` GPU cost.
