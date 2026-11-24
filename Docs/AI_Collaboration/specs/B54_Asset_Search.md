# B54 — Local Repo, Unity Asset Store Cache, Sibling Project & Internet Asset Search

**Goal:** Identify higher-fidelity, HDRP-ready, and public-repo-compatible assets across (1) unused assets already inside `LeadRush 2/Assets/`, (2) the local Unity Asset Store `.unitypackage` cache, (3) sibling repositories under `H:\Unity Projects\`, and (4) free / CC0 / open-source internet sources.

---

## Tier 1 — Unused High-Value Assets Already Inside `LeadRush 2/Assets/`

| Asset / Folder | Concrete Opportunity in LR2 | Status / Notes |
|---|---|---|
| `Assets/KinemationDemo/Meshes/Attachments/` (`Holosight/HoloSight.prefab`, `Reddot/Mesh/Reddot_Mesh.prefab`, `Acog/Trijicon.prefab`, `1P29/1P29.prefab`, `LongRangeScope/SniperScope.prefab`) | Optic sights (`HoloSight`, `Reddot`, `ACOG+RMR`) are already modeled with mount meshes and prefabs — currently rifles default to iron sights. | Immediate drop-in upgrade for AK-12, AK-74N, Mk18, FAL (`ScopeGroups`). |
| `Assets/KinemationDemo/Meshes/Attachments/Grips/` (`RK-2.fbx`, `AngledGripMount_45.fbx`, `GripMount.fbx`) & Suppressors (`HexagonSuppressor/AKHexagonSuppressor.prefab`, `Suppressor_Mk18/Supperssor_Mk18.fbx`) | Vertical/angled foregrips and rifle suppressors exist in-repo and are not mounted on the default LR2 weapons. | Improves first-person silhouettes and recoil-attenuation feel. |
| `Assets/KinemationDemo/Animations/Locomotion/Humanoid/UnarmedSet/` (`C_Sliding_Humanoid.fbx`) | Humanoid tactical slide clip is available under the `UnarmedSet` subfolder; other crouch/prone candidates require separate path verification. | Pair directly with `B37` smooth motion & `B50` walk-bob polish. |
| `Assets/LeartesStudios/`, `Assets/ScansFactory/`, `Assets/UnityTechnologies/`, `Assets/AllSkyFree/`, `Assets/Polyart/` | Rich industrial, forest, HDRI sky, and rock meshes already imported into the local gitignored `Assets/` tree. | Reuse more hero props (`LeartesStudios` barrels/crates/fences) + `UnityTechnologies` HDRI packs to address `B1`/`B42`. |

---

## Tier 2 — Downloaded `.unitypackage` Packs in Local Unity Cache (`%AppData%\Unity\Asset Store-5.x\`)

Observed directly in vendor subfolders under `C:\Users\soulr\AppData\Roaming\Unity\Asset Store-5.x\`:

| Package File | Size | Category | Why Better for Lead Rush II |
|---|---|---|---|
| `AK-74 HDRP.unitypackage` (Creation Wasteland) | `85.4 MB` | Weapons | Native **HDRP** 4K-textured AK-74 model/materials (far sharper metal wear, wood/polymer normal/mask maps than legacy demo guns). |
| `FPS Gun 4K - Assault Rifle 1.unitypackage` (Heart State Games) | `107.4 MB` | Weapons | 4K PBR assault rifle mesh + textures; strong candidate for an alternate tactical rifle or hero menu display gun. |
| `Apocalypse Playground Environment.unitypackage` (Hayq Studios) | `1,071.6 MB` | Urban Maps | War-torn vehicles, concrete barriers, ruins, and urban quarantine clutter to enrich `Deadsector` & `Saltworks`. |
| `HQ Hangar Free.unitypackage` (Purple Sky) | `85.3 MB` | Industrial Maps | High-detail structural hangar architecture, beams, and lamps for `Foundry` / indoor CQB zones. |
| `8K Skybox Pack Free.unitypackage` (`BG Studio`) & `HDRI Pack Starter Assets.unitypackage` (`Unity Technologies`) | `262.8 MB` / `784.1 MB` | Skies / HDRI | Sharper overcast / storm / golden-hour HDRIs for `HDRISky` lighting (`B1`, `WeatherSystem`). |
| `Particle Pack Starter Assets.unitypackage` (`Unity Technologies`) | `160.2 MB` | VFX | Flipbook fire, smoke, explosion, and fluid textures for `FireHazard` (`B38`), muzzle smoke (`B11`), and impact FX (`P2`). |
| `Time Ghost Character - HDRP.unitypackage` (`Unity Technologies`) & `The Toby Foliage Engine Light.unitypackage` | `1,499 MB` / `648.7 MB` | Character / Foliage | High-end Unity 6 HDRP reference cloth/skin shader setups and wind-driven foliage. |

*(Reminder: keep all Asset Store packs in gitignored folders per `THIRD_PARTY_ASSETS.md`.)*

---

## Tier 3 — Sibling Local Repositories (`H:\Unity Projects\`)

Observed in `H:\Unity Projects\`:
- `H:\Unity Projects\FPSAnimator_DemoProject` & `H:\Unity Projects\FPSAnimator_Zombiefield_23` (reference Kinemation rig/attachment scenes & zombie/soldier setups).
- `H:\Unity Projects\GitHub Repos\LeadRushHD` & `LeadRushHDRP` (prior mother-project textures, meshes, and lighting profiles).
- `H:\Unity Projects\Audio\Modern Warfare SFX` (private local gun/foley audio library — see `B33` public-release license decision in `AGENT_COORDINATION.md` Section 2).

---

## Tier 4 — Internet Free / CC0 / GitHub Sources (Safe for Public Git Commits & Pre-Alpha Releases)

1. **PBR Materials & HDRI Skies — Poly Haven ([polyhaven.com](https://polyhaven.com)) & ambientCG ([ambientcg.com](https://ambientcg.com)) — `CC0 1.0` Public Domain:**
   - 4K–8K concrete, rusted sheet metal, cracked asphalt, brick masonry, camouflage fabric, and dynamic outdoor HDRI cubemaps.
   - Completely redistributable on GitHub (unlike Unity Asset Store packs), enabling replacement of procedural flat textures on walls/containers with real packed HDRP Mask Maps (`Metallic / AO / Detail / Smoothness`).
2. **`Unity-Technologies/MeasuredMaterialLibraryHDRP` (GitHub):**
   - Official physically measured HDRP Lit material calibration library for realistic steel, polymer, cloth, concrete, and glass reflectance.
3. **`Unity-Technologies/FPSSample` (GitHub reference) & Mixamo ([mixamo.com](https://www.mixamo.com)):**
   - Free humanoid rigged soldier variations + motion-captured reload/crawl/sprint locomotion clips compatible with Unity Humanoid retargeting.
4. **Public-Safe CC0 Tactical Audio (`Freesound.org` CC0 filter / `Kenney.nl` / `OpenGameArt` CC0):**
   - Replacing or providing a fallback CC0 gunshot/footstep/UI/ambient SFX bank in git would unblock public GitHub releases under **`B33`** without redistributing private MW2019 audio.
