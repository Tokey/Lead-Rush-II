using LR2.Game;
using LR2.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using static LR2.EditorTools.LR2EditorUtil;

namespace LR2.EditorTools
{
    /// <summary>Builds LR2_GameUI.prefab (HUD + pause/settings/scoreboard/end) and LR2_MainMenuUI.prefab.</summary>
    public static class LR2UIBuilder
    {
        private const string UiDir = "Assets/LR2/UI";
        private const string PrefabDir = "Assets/LR2/Prefabs/UI";
        private static UITheme _theme;
        private static Sprite _white, _circle, _vignette, _arc, _x, _ring;

        [MenuItem("LR2/UI/Rebuild UI Prefabs", priority = 221)]
        public static void Build()
        {
            _theme = AssetDatabase.LoadAssetAtPath<UITheme>("Assets/LR2/Resources/LR2/UITheme.asset");
            EnsureHeadingFont(_theme);
            MakeSprites();
            BuildGameUI();
            BuildMainMenuUI();
            AssetDatabase.SaveAssets();
            Debug.Log("[LR2] UI prefabs built (LR2_GameUI, LR2_MainMenuUI).");
        }

        private static void EnsureHeadingFont(UITheme theme)
        {
            if (theme == null) return;
            const string ttfPath = "Assets/LR2/UI/Fonts/SairaStencilOne-Regular.ttf";
            const string assetPath = "Assets/LR2/UI/Fonts/LR2_SairaStencil SDF.asset";
            const string fallbackPath = "Assets/LR2/UI/Fonts/LR2_Bebas SDF.asset";
            var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (fa == null)
            {
                AssetDatabase.ImportAsset(ttfPath, ImportAssetOptions.ForceSynchronousImport);
                var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
                if (font != null)
                {
                    fa = TMP_FontAsset.CreateFontAsset(font);
                    if (fa != null)
                    {
                        AssetDatabase.CreateAsset(fa, assetPath);
                        if (fa.atlasTextures != null)
                            foreach (var tex in fa.atlasTextures)
                                if (tex != null) { tex.name = fa.name + " Atlas"; AssetDatabase.AddObjectToAsset(tex, fa); }
                        if (fa.material != null)
                        {
                            fa.material.name = fa.name + " Material";
                            AssetDatabase.AddObjectToAsset(fa.material, fa);
                        }
                    }
                }
            }
            if (fa != null)
            {
                var fb = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fallbackPath);
                if (fb != null)
                {
                    fa.fallbackFontAssetTable ??= new System.Collections.Generic.List<TMP_FontAsset>();
                    if (!fa.fallbackFontAssetTable.Contains(fb)) fa.fallbackFontAssetTable.Add(fb);
                }
                EditorUtility.SetDirty(fa);
                theme.headingFont = fa;
                EditorUtility.SetDirty(theme);
            }
        }

        public static void EnsureEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        // ------------------------------------------------------------------ sprites
        private static void MakeSprites()
        {
            string dir = UiDir + "/Sprites";
            _white = Sp(Solid(8), dir + "/S_White.png");
            _circle = Sp(Circle(128, 0.96f, false), dir + "/S_Circle.png");
            _ring = Sp(Circle(256, 0.97f, true), dir + "/S_Ring.png");
            _vignette = Sp(Vignette(512), dir + "/S_Vignette.png");
            _arc = Sp(Arc(256, 96), dir + "/S_DamageArc.png");
            _x = Sp(Hitmarker(64), dir + "/S_Hitmarker.png");
        }

        private static Sprite Sp(Texture2D t, string path)
        {
            SaveTexture(t, path, true);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti != null && (ti.textureType != TextureImporterType.Sprite || ti.spriteImportMode != SpriteImportMode.Single || !ti.alphaIsTransparency))
            {
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.alphaIsTransparency = true;
                ti.mipmapEnabled = false;
                ti.sRGBTexture = true;
                ti.SaveAndReimport();
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var sp = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sp != null) return sp;
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                if (obj is Sprite sub) return sub;
            return null;
        }

        private static Texture2D Tex(int w, int h) => new Texture2D(w, h, TextureFormat.RGBA32, false);

        private static Texture2D Solid(int s)
        {
            var t = Tex(s, s);
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++) t.SetPixel(x, y, Color.white);
            t.Apply();
            return t;
        }

        private static Texture2D Circle(int s, float edge, bool ring)
        {
            var t = Tex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s / 2f, s / 2f)) / (s / 2f);
                    float a = Mathf.Clamp01((1f - d) / (1f - edge));
                    if (ring) a *= Mathf.Clamp01((d - 0.93f) / 0.02f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            t.Apply();
            return t;
        }

        private static Texture2D Vignette(int s)
        {
            var t = Tex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(s / 2f, s / 2f)) / (s / 2f);
                    float a = Mathf.Clamp01((d - 0.45f) / 0.6f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            t.Apply();
            return t;
        }

        private static Texture2D Arc(int w, int h)
        {
            var t = Tex(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w * 2f - 1f;
                    float v = (y + 0.5f) / h;
                    float curve = 1f - u * u * 0.35f;
                    float band = Mathf.Clamp01(1f - Mathf.Abs(v - curve * 0.7f) / 0.18f);
                    float fade = Mathf.Clamp01(1f - Mathf.Abs(u));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, band * fade));
                }
            t.Apply();
            return t;
        }

        private static Texture2D Hitmarker(int s)
        {
            var t = Tex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s * 2f - 1f, v = (y + 0.5f) / s * 2f - 1f;
                    float d1 = Mathf.Abs(u - v) / 1.414f, d2 = Mathf.Abs(u + v) / 1.414f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float line = Mathf.Clamp01(1f - Mathf.Min(d1, d2) / 0.07f);
                    float gap = Mathf.Clamp01((r - 0.32f) / 0.05f) * Mathf.Clamp01((0.98f - r) / 0.05f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, line * gap));
                }
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------ element helpers
        private static RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, Vector2? pivot = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot ?? new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private static RectTransform Stretch(string name, Transform parent)
        {
            var rt = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return rt;
        }

        private static Image Img(string name, Transform parent, Sprite sprite, Color color, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rt = Rect(name, parent, anchor, anchor, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static TMP_Text Txt(string name, Transform parent, string text, float size, TextAlignmentOptions align, Vector2 anchor, Vector2 pos, Vector2 box, bool heading = false, Color? color = null)
        {
            var rt = Rect(name, parent, anchor, anchor, pos, box);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color ?? (_theme != null ? _theme.text : Color.white);
            var font = _theme != null ? (heading ? _theme.headingFont : _theme.bodyFont) : null;
            if (font != null) t.font = font;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = true;
            return t;
        }

        private static TMP_Text AddShadow(TMP_Text t)
        {
            if (t == null) return null;
            var sh = t.gameObject.GetComponent<Shadow>() ?? t.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.82f);
            sh.effectDistance = new Vector2(1.5f, -1.5f);
            return t;
        }

        private static void CardBacking(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color accent)
        {
            var bg = Img(name, parent, _white, new Color(0.03f, 0.035f, 0.045f, 0.80f), anchor, pos, size);
            Img(name + "_Top", bg.transform, _white, accent, new Vector2(0.5f, 1f), new Vector2(0f, -1.5f), new Vector2(size.x, 3f));
        }

        private static CanvasGroup Panel(string name, Transform parent, Color bg)
        {
            var rt = Stretch(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _white;
            img.color = bg;
            return rt.gameObject.AddComponent<CanvasGroup>();
        }

        private static Button Btn(string label, Transform parent, Vector2 pos, Vector2 size)
        {
            var rt = Rect("Button_" + label.Replace(" ", ""), parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _white;
            img.color = new Color(0.07f, 0.08f, 0.09f, 0.82f);
            var b = rt.gameObject.AddComponent<Button>();
            var colors = b.colors;
            colors.highlightedColor = new Color(0.22f, 0.2f, 0.16f, 1f);
            colors.pressedColor = new Color(0.4f, 0.32f, 0.18f, 1f);
            colors.selectedColor = colors.normalColor;
            b.colors = colors;
            var accent = Img("Accent", rt, _white, _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f), new Vector2(0f, 0.5f), new Vector2(3f, 0f), new Vector2(6f, size.y));
            accent.rectTransform.pivot = new Vector2(0f, 0.5f);
            accent.rectTransform.localScale = new Vector3(0f, 1f, 1f);
            var t = Txt("Label", rt, label.ToUpperInvariant(), size.y * 0.5f, TextAlignmentOptions.MidlineLeft, new Vector2(0.5f, 0.5f), new Vector2(18f, 0f), size, true);
            var fx = rt.gameObject.AddComponent<UIButtonFX>();
            fx.accentBar = accent.rectTransform;
            return b;
        }

        private static TMP_DefaultControls.Resources TmpRes() => new TMP_DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd"),
        };

        private static DefaultControls.Resources UgRes() => new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd"),
        };

        private static void ColRow(Transform parent, string label, float colLeft, float y, float labelW, float slotW, out RectTransform slot, out TMP_Text valueText)
        {
            var anchorTopCenter = new Vector2(0.5f, 1f);
            var lbl = Txt("Label_" + label, parent, label, 21f, TextAlignmentOptions.MidlineLeft, anchorTopCenter, new Vector2(colLeft, y), new Vector2(labelW, 38f));
            ((RectTransform)lbl.transform).pivot = new Vector2(0f, 0.5f);
            lbl.enableAutoSizing = true;
            lbl.fontSizeMin = 14f;
            lbl.fontSizeMax = 21f;
            lbl.overflowMode = TextOverflowModes.Ellipsis;

            slot = Rect("Slot_" + label, parent, anchorTopCenter, anchorTopCenter, new Vector2(colLeft + labelW + 14f, y), new Vector2(slotW, 34f), new Vector2(0f, 0.5f));

            Color accent = _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f);
            valueText = Txt("Value_" + label, parent, "", 19f, TextAlignmentOptions.MidlineRight, anchorTopCenter, new Vector2(colLeft + labelW + 14f + slotW + 12f, y), new Vector2(105f, 34f), false, accent);
            ((RectTransform)valueText.transform).pivot = new Vector2(0f, 0.5f);
        }

        private static Slider SliderColRow(Transform parent, string label, float colLeft, float y, out TMP_Text valueText, float labelW = 248f, float slotW = 264f)
        {
            ColRow(parent, label, colLeft, y, labelW, slotW, out var slot, out valueText);
            var go = DefaultControls.CreateSlider(UgRes());
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            var fill = go.transform.Find("Fill Area/Fill");
            if (fill != null) fill.GetComponent<Image>().color = _theme != null ? _theme.accent : Color.white;
            return go.GetComponent<Slider>();
        }

        private static Toggle ToggleColRow(Transform parent, string label, float colLeft, float y, float labelW = 248f, float slotW = 264f)
        {
            ColRow(parent, label, colLeft, y, labelW, slotW, out var slot, out var val);
            Object.DestroyImmediate(val.gameObject);
            var go = DefaultControls.CreateToggle(UgRes());
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            var bg = go.transform.Find("Background")?.GetComponent<Image>();
            if (bg != null)
            {
                bg.sprite = _white;
                bg.color = new Color(0.14f, 0.17f, 0.22f, 0.98f);
                var bgRt = (RectTransform)bg.transform;
                bgRt.sizeDelta = new Vector2(22f, 22f);
                var ol = bg.gameObject.AddComponent<Outline>();
                ol.effectColor = new Color(0.84f, 0.88f, 0.94f, 0.50f);
                ol.effectDistance = new Vector2(1.5f, -1.5f);
            }
            var chk = go.transform.Find("Background/Checkmark")?.GetComponent<Image>();
            if (chk != null)
            {
                chk.sprite = _white;
                chk.color = _theme != null ? new Color(Mathf.Min(1f, _theme.accent.r * 1.08f), Mathf.Min(1f, _theme.accent.g * 1.12f), Mathf.Min(1f, _theme.accent.b * 1.15f), 1f) : new Color(1f, 0.74f, 0.22f, 1f);
                var chkRt = (RectTransform)chk.transform;
                chkRt.anchorMin = Vector2.zero;
                chkRt.anchorMax = Vector2.one;
                chkRt.offsetMin = new Vector2(4f, 4f);
                chkRt.offsetMax = new Vector2(-4f, -4f);
            }
            var label2 = go.GetComponentInChildren<Text>();
            if (label2 != null) label2.text = "";
            return go.GetComponent<Toggle>();
        }

        private static void BuildStepper(Transform slot, float height, out Button prev, out Button next, out TMP_Text valueText, float fontSize = 18f, Color? accentStripe = null)
        {
            Img("Bg", slot, _white, new Color(0.03f, 0.038f, 0.052f, 0.92f), new Vector2(0.5f, 0.5f), Vector2.zero, ((RectTransform)slot).sizeDelta);
            Img("TopBorder", slot, _white, accentStripe ?? new Color(1f, 1f, 1f, 0.14f), new Vector2(0.5f, 1f), new Vector2(0f, -1f), new Vector2(((RectTransform)slot).sizeDelta.x, 2f));
            Button StepBtn(string sym, float anchorX, float xOff)
            {
                var b = Btn(sym, slot, Vector2.zero, new Vector2(height, height));
                var rt = (RectTransform)b.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(anchorX, 0.5f);
                rt.anchoredPosition = new Vector2(xOff, 0f);
                var lbl = b.GetComponentInChildren<TMP_Text>();
                if (lbl != null)
                {
                    lbl.alignment = TextAlignmentOptions.Center;
                    lbl.fontSize = Mathf.Max(18f, fontSize);
                    lbl.rectTransform.anchoredPosition = Vector2.zero;
                }
                return b;
            }
            prev = StepBtn("<", 0f, height * 0.5f);
            next = StepBtn(">", 1f, -height * 0.5f);
            float textW = Mathf.Max(80f, ((RectTransform)slot).sizeDelta.x - height * 2f - 16f);
            valueText = AddShadow(Txt("Value", slot, "", fontSize, TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(textW, height), true));
            valueText.enableAutoSizing = true;
            valueText.fontSizeMin = 12f;
            valueText.fontSizeMax = fontSize;
            valueText.overflowMode = TextOverflowModes.Ellipsis;
        }

        private static void SelectorColRow(Transform parent, string label, float colLeft, float y, out Button prev, out Button next, out TMP_Text valueText, float labelW = 248f, float slotW = 360f)
        {
            ColRow(parent, label, colLeft, y, labelW, slotW, out var slot, out var val);
            Object.DestroyImmediate(val.gameObject);
            BuildStepper(slot, 34f, out prev, out next, out valueText, 18f);
        }

        private static SettingsPanel.KeybindSlot KeybindColRow(Transform parent, string actionName, string displayLabel, float colLeft, float y, float labelW = 248f, float slotW = 360f)
        {
            ColRow(parent, displayLabel, colLeft, y, labelW, slotW, out var slot, out var val);
            Object.DestroyImmediate(val.gameObject);
            var btn = Btn(actionName, slot, Vector2.zero, new Vector2(slotW, 34f));
            var bRt = (RectTransform)btn.transform;
            bRt.anchorMin = bRt.anchorMax = new Vector2(0.5f, 0.5f);
            bRt.anchoredPosition = Vector2.zero;
            var lbl = btn.GetComponentInChildren<TMP_Text>();
            if (lbl != null)
            {
                lbl.alignment = TextAlignmentOptions.Center;
                lbl.fontSize = 18f;
                lbl.rectTransform.anchoredPosition = Vector2.zero;
            }
            return new SettingsPanel.KeybindSlot { actionName = actionName, button = btn, label = lbl };
        }

        private static Canvas NewCanvas(string name, int order)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private static SettingsPanel BuildSettings(Transform parent, out Button back)
        {
            var group = Panel("SettingsPanel", parent, new Color(0.015f, 0.02f, 0.026f, 0.95f));
            var t = group.transform;
            Color accent = _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f);
            Color dim = _theme != null ? _theme.textDim : new Color(1f, 1f, 1f, 0.62f);

            Txt("Title", t, "SETTINGS", 54f, TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0f, -54f), new Vector2(900f, 64f), true);
            Img("TitleRule", t, _white, accent, new Vector2(0.5f, 1f), new Vector2(0f, -92f), new Vector2(220f, 2f));
            CardBacking(t, "LeftColCard", new Vector2(0.5f, 1f), new Vector2(-420f, -476f), new Vector2(720f, 598f), accent);
            CardBacking(t, "RightColCard", new Vector2(0.5f, 1f), new Vector2(420f, -476f), new Vector2(720f, 598f), accent);

            var s = group.gameObject.AddComponent<SettingsPanel>();
            // B136 (user 11-22: "settings in the skirmish and in the settings panel are conflicting ... add tabs? remove
            // redundancies"): bot difficulty and bot team mode lived here AND in the Skirmish lobby (per bot, plus ALL BOTS);
            // the lobby now owns every match option. Settings are player preferences only, in five tabs.
            string[] tabTitles = { "GAMEPLAY", "GRAPHICS", "CONTROLS", "KEYBINDS", "AUDIO" };
            s.tabButtons = new Button[tabTitles.Length];
            s.tabPanels = new GameObject[tabTitles.Length];
            for (int ti = 0; ti < tabTitles.Length; ti++)
            {
                float tx = -560f + ti * 280f;
                var tb = Btn(tabTitles[ti], t, new Vector2(tx, 412f), new Vector2(256f, 42f));
                var tLbl = tb.GetComponentInChildren<TMP_Text>();
                if (tLbl != null)
                {
                    tLbl.alignment = TextAlignmentOptions.Center;
                    tLbl.fontSize = 20f;
                    tLbl.rectTransform.anchoredPosition = Vector2.zero;
                }
                s.tabButtons[ti] = tb;

                var tabGo = Stretch("Tab_" + tabTitles[ti].Replace(" ", ""), t);
                s.tabPanels[ti] = tabGo.gameObject;
            }

            const float leftCol = -752f;
            const float rightCol = 88f;
            const float step = 58f;
            const float startY = -246f;

            void AddSectionHeader(Transform tabRoot, string name, string text, float x)
            {
                var h = Txt(name, tabRoot, text, 21f, TextAlignmentOptions.MidlineLeft, new Vector2(0.5f, 1f), new Vector2(x, -196f), new Vector2(540f, 28f), true, accent);
                ((RectTransform)h.transform).pivot = new Vector2(0f, 0.5f);
            }

            // Tab 0: GAMEPLAY (feedback and HUD preferences)
            var playTab = s.tabPanels[0].transform;
            AddSectionHeader(playTab, "PlayLeft", "COMBAT FEEDBACK", leftCol);
            float y = startY;
            SelectorColRow(playTab, "HITMARKER PALETTE", leftCol, y, out s.colorBlindPrev, out s.colorBlindNext, out s.colorBlindValue); y -= step;
            s.laserSight = ToggleColRow(playTab, "LASER SIGHT [L]", leftCol, y); y -= step;
            s.minimapBottom = ToggleColRow(playTab, "MINIMAP ABOVE HEALTH (OFF: TOP-LEFT)", leftCol, y); // B96
            AddSectionHeader(playTab, "PlayRight", "MATCH OPTIONS", rightCol);
            var matchHint = Txt("MatchHint", playTab, "Mode, map, bots (difficulty and team per bot), score and time limits are set in SKIRMISH, " +
                "so a match always plays exactly as its lobby shows.", 20f, TextAlignmentOptions.TopLeft, new Vector2(0.5f, 1f),
                new Vector2(rightCol, startY + 12f), new Vector2(620f, 180f), false, dim);
            matchHint.textWrappingMode = TextWrappingModes.Normal;
            matchHint.overflowMode = TextOverflowModes.Truncate;
            ((RectTransform)matchHint.transform).pivot = new Vector2(0f, 1f);

            // Tab 1: GRAPHICS (P5 + B43 + B48)
            var gfxTab = s.tabPanels[1].transform;
            AddSectionHeader(gfxTab, "GfxLeft", "CAMERA & POST-PROCESSING", leftCol);
            y = startY;
            s.fieldOfView = SliderColRow(gfxTab, "FOV (HORIZONTAL)", leftCol, y, out s.fovValue); y -= step;
            s.cameraShake = SliderColRow(gfxTab, "CAMERA SHAKE", leftCol, y, out s.cameraShakeValue); y -= step;
            s.bodycamMotion = SliderColRow(gfxTab, "BODYCAM MOTION", leftCol, y, out s.bodycamMotionValue); y -= step;
            s.bodycamLook = ToggleColRow(gfxTab, "BODYCAM LENS LOOK", leftCol, y); y -= step;
            s.motionBlur = ToggleColRow(gfxTab, "MOTION BLUR", leftCol, y); y -= step;
            s.filmGrain = ToggleColRow(gfxTab, "FILM GRAIN", leftCol, y);

            AddSectionHeader(gfxTab, "GfxRight", "QUALITY, UPSCALER & DISPLAY", rightCol);
            y = startY;
            SelectorColRow(gfxTab, "GRAPHICS PRESET", rightCol, y, out s.qualityPrev, out s.qualityNext, out s.qualityValue); y -= step;
            SelectorColRow(gfxTab, "UPSCALER MODE", rightCol, y, out s.upscalerPrev, out s.upscalerNext, out s.upscalerValue); y -= step;
            SelectorColRow(gfxTab, "ANTI-ALIASING", rightCol, y, out s.antiAliasingPrev, out s.antiAliasingNext, out s.antiAliasingValue); y -= step;
            s.vsync = ToggleColRow(gfxTab, "V-SYNC", rightCol, y); y -= step;
            SelectorColRow(gfxTab, "FRAME LIMIT", rightCol, y, out s.frameLimitPrev, out s.frameLimitNext, out s.frameLimitValue); y -= step;
            s.showFps = ToggleColRow(gfxTab, "SHOW FPS", rightCol, y); y -= step;
            s.rayTracing = ToggleColRow(gfxTab, "RAY TRACING (DXR)", rightCol, y); y -= step;
            s.upscalerStatusNote = Txt("UpscalerStatusNote", gfxTab, "", 16f, TextAlignmentOptions.MidlineLeft, new Vector2(0.5f, 1f), new Vector2(rightCol, y), new Vector2(640f, 26f), false, dim);
            ((RectTransform)s.upscalerStatusNote.transform).pivot = new Vector2(0f, 0.5f);

            // Tab 2: CONTROLS (B136q: complete aiming/sprint toggles + full tactical key legend)
            var ctrlTab = s.tabPanels[2].transform;
            AddSectionHeader(ctrlTab, "CtrlLeft", "AIMING & INPUT BEHAVIOUR", leftCol);
            y = startY;
            s.sensitivity = SliderColRow(ctrlTab, "MOUSE SENSITIVITY", leftCol, y, out s.sensitivityValue); y -= step;
            s.adsSensitivity = SliderColRow(ctrlTab, "ADS SENSITIVITY", leftCol, y, out s.adsSensitivityValue); y -= step;
            s.invertY = ToggleColRow(ctrlTab, "INVERT Y AXIS", leftCol, y); y -= step;
            s.toggleAim = ToggleColRow(ctrlTab, "TOGGLE AIM (OFF: HOLD)", leftCol, y); y -= step;
            s.toggleSprint = ToggleColRow(ctrlTab, "TOGGLE SPRINT (OFF: HOLD)", leftCol, y); y -= step;
            s.autoSprint = ToggleColRow(ctrlTab, "AUTO SPRINT FORWARD", leftCol, y);

            AddSectionHeader(ctrlTab, "CtrlRight", "COMPLETE CONTROLS REFERENCE", rightCol);
            string controlsLegend =
                "MOVEMENT:  WASD Move · SHIFT Sprint · SPACE Jump / Mantle\n\n" +
                "STANCES:  C / CTRL Crouch · X Hold Crouch · XX Slide · Z Prone\n\n" +
                "COMBAT:  LMB Fire · RMB Aim (ADS) · R Reload · G Frag Grenade\n\n" +
                "TACTICAL:  Q Lean Left · E Lean Right · L Laser · F Resupply\n\n" +
                "LOADOUT:  1 Primary · 2 Sidearm · 3 Knife · 4 Torch · SCROLL Swap\n\n" +
                "OPTICS & MODS:  B Fire Mode · V Switch Sight (ADS) · I Inspect (1/2/3)\n\n" +
                "SQUAD & MATCH:  T Squad Chat Orders · TAB Scoreboard · ESC Pause\n\n" +
                "All movement, stance and gear keys can be remapped under KEYBINDS.";
            var ctrlHint = Txt("CtrlHint", ctrlTab, controlsLegend, 18f,
                TextAlignmentOptions.TopLeft, new Vector2(0.5f, 1f), new Vector2(rightCol, startY + 12f), new Vector2(640f, 470f), false, dim);
            ctrlHint.textWrappingMode = TextWrappingModes.Normal;
            ctrlHint.overflowMode = TextOverflowModes.Truncate;
            ((RectTransform)ctrlHint.transform).pivot = new Vector2(0f, 1f);

            // Tab 3: KEYBINDS (LR2Bindings B44 + B136/B136q complete action set)
            var bindTab = s.tabPanels[3].transform;
            AddSectionHeader(bindTab, "BindLeft", "MOVEMENT & COMBAT", leftCol);
            AddSectionHeader(bindTab, "BindRight", "STANCES, GEAR & LOADOUT", rightCol);
            (string action, string label)[] leftBinds = {
                ("Move", "MOVE FORWARD"), ("Fire", "FIRE WEAPON"), ("Aim", "AIM DOWN SIGHTS"),
                ("Sprint", "SPRINT"), ("Jump", "JUMP / MANTLE"), ("Reload", "RELOAD"),
                ("Grenade", "THROW GRENADE"), ("FireMode", "FIRE MODE"), ("CycleScope", "SWITCH SIGHT (ADS)"),
                ("Slot1", "PRIMARY WEAPON"), ("Slot2", "SIDEARM")
            };
            (string action, string label)[] rightBinds = {
                ("Crouch", "CROUCH (TOGGLE)"), ("CrouchHold", "CROUCH HOLD · XX SLIDE"), ("Prone", "PRONE"),
                ("LeanLeft", "LEAN LEFT"), ("LeanRight", "LEAN RIGHT"), ("Interact", "INTERACT / RESUPPLY"),
                ("Laser", "TOGGLE LASER"), ("Inspect", "INSPECT / MODS"),
                ("Slot3", "KNIFE"), ("Slot4", "TORCH")
            };
            const float bindStep = 43f;
            const float bindStartY = -236f;
            for (int bi = 0; bi < leftBinds.Length; bi++)
                s.keybindSlots.Add(KeybindColRow(bindTab, leftBinds[bi].action, leftBinds[bi].label, leftCol, bindStartY - bi * bindStep));
            for (int bi = 0; bi < rightBinds.Length; bi++)
                s.keybindSlots.Add(KeybindColRow(bindTab, rightBinds[bi].action, rightBinds[bi].label, rightCol, bindStartY - bi * bindStep));
            s.rebindStatusText = Txt("RebindStatus", bindTab, "CLICK ANY ACTION TO REBIND (ESC TO CANCEL)", 17f, TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0f, -736f), new Vector2(900f, 28f), false, accent);
            s.resetBindingsButton = Btn("Reset Keybinds", bindTab, new Vector2(0f, -248f), new Vector2(280f, 40f));

            // Tab 4: AUDIO (B5, P4)
            var audioTab = s.tabPanels[4].transform;
            AddSectionHeader(audioTab, "AudioLeft", "AUDIO CHANNELS", leftCol);
            y = startY;
            s.masterVolume = SliderColRow(audioTab, "MASTER VOLUME", leftCol, y, out s.masterVolumeValue); y -= step;
            s.sfxVolume = SliderColRow(audioTab, "EFFECTS VOLUME", leftCol, y, out s.sfxVolumeValue); y -= step;
            s.musicVolume = SliderColRow(audioTab, "MUSIC VOLUME", leftCol, y, out s.musicVolumeValue); y -= step;
            s.uiVolume = SliderColRow(audioTab, "UI VOLUME", leftCol, y, out s.uiVolumeValue); y -= step;
            s.hitmarkerSound = ToggleColRow(audioTab, "HITMARKER SOUND", leftCol, y);

            s.resetDefaultsButton = Btn("Reset Defaults", t, new Vector2(-190f, -446f), new Vector2(310f, 54f));
            back = Btn("Back", t, new Vector2(190f, -446f), new Vector2(310f, 54f));
            return s;
        }

        // ------------------------------------------------------------------ in-game UI
        private static void BuildGameUI()
        {
            var canvas = NewCanvas("LR2_GameUI", 10);
            var root = canvas.transform;
            var hudRoot = Stretch("HUD", root);
            var hudGroup = hudRoot.gameObject.AddComponent<CanvasGroup>();
            hudGroup.blocksRaycasts = false;
            var hud = canvas.gameObject.AddComponent<HUDController>();
            hud.hudGroup = hudGroup;
            Color accent = _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f);
            Color dim = _theme != null ? _theme.textDim : new Color(1f, 1f, 1f, 0.55f);
            Vector2 c = new Vector2(0.5f, 0.5f);

            // Screen overlays (behind everything else)
            hud.damageVignette = Img("DamageVignette", hudRoot, _vignette, new Color(0.55f, 0f, 0f, 0f), c, Vector2.zero, Vector2.zero);
            hud.damageVignette.rectTransform.anchorMin = Vector2.zero;
            hud.damageVignette.rectTransform.anchorMax = Vector2.one;
            hud.lowHealthOverlay = Img("LowHealthOverlay", hudRoot, _vignette, new Color(0.4f, 0f, 0f, 0f), c, Vector2.zero, Vector2.zero);
            hud.lowHealthOverlay.rectTransform.anchorMin = Vector2.zero;
            hud.lowHealthOverlay.rectTransform.anchorMax = Vector2.one;

            // Crosshair
            var cross = Rect("Crosshair", hudRoot, c, c, Vector2.zero, new Vector2(120f, 120f));
            hud.crosshairGroup = cross.gameObject.AddComponent<CanvasGroup>();
            Vector2[] sizes = { new Vector2(2f, 11f), new Vector2(2f, 11f), new Vector2(11f, 2f), new Vector2(11f, 2f) };
            for (int i = 0; i < 4; i++) hud.crosshairLines[i] = Img("Line" + i, cross, _white, new Color(1f, 1f, 1f, 0.9f), c, Vector2.zero, sizes[i]).rectTransform;
            hud.crosshairDot = Img("Dot", cross, _circle, new Color(1f, 1f, 1f, 0.85f), c, Vector2.zero, new Vector2(3f, 3f));
            hud.hitmarker = Img("Hitmarker", hudRoot, _x, Color.clear, c, Vector2.zero, new Vector2(46f, 46f));
            hud.reloadPrompt = Txt("ReloadPrompt", hudRoot, "", 24f, TextAlignmentOptions.Center, c, new Vector2(0f, -68f), new Vector2(300f, 32f), true);
            hud.reloadBarRoot = Rect("ReloadBar", hudRoot, c, c, new Vector2(0f, -94f), new Vector2(150f, 5f));
            Img("ReloadTrack", hud.reloadBarRoot, _white, new Color(0f, 0f, 0f, 0.55f), c, Vector2.zero, new Vector2(150f, 5f));
            hud.reloadBarFill = Img("ReloadFill", hud.reloadBarRoot, _white, accent, c, Vector2.zero, new Vector2(150f, 5f));
            hud.reloadBarFill.type = Image.Type.Filled;
            hud.reloadBarFill.fillMethod = Image.FillMethod.Horizontal;
            hud.reloadBarFill.fillAmount = 0f;
            hud.reloadBarRoot.gameObject.SetActive(false);

            // Damage direction
            hud.damageIndicatorRoot = Rect("DamageIndicators", hudRoot, c, c, Vector2.zero, new Vector2(10f, 10f));
            var arc = Img("DamageArc", hud.damageIndicatorRoot, _arc, new Color(0.9f, 0.08f, 0.05f, 0f), c, Vector2.zero, new Vector2(220f, 82f));
            arc.rectTransform.pivot = new Vector2(0.5f, -2.3f);
            hud.damageIndicatorTemplate = arc;

            // Ammo (bottom-right, B136j: generous separation between weaponName + grenade readout and magText)
            var ammo = Rect("Ammo", hudRoot, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-70f, 60f), new Vector2(500f, 152f), new Vector2(1f, 0f));
            hud.magText = AddShadow(Txt("Mag", ammo, "30", 82f, TextAlignmentOptions.BottomRight, new Vector2(1f, 0f), new Vector2(-105f, 8f), new Vector2(200f, 88f), true));
            ((RectTransform)hud.magText.transform).pivot = new Vector2(1f, 0f);
            hud.reserveText = AddShadow(Txt("Reserve", ammo, "120", 36f, TextAlignmentOptions.BottomLeft, new Vector2(1f, 0f), new Vector2(-92f, 14f), new Vector2(100f, 50f), true, dim));
            ((RectTransform)hud.reserveText.transform).pivot = new Vector2(0f, 0f);
            hud.weaponName = AddShadow(Txt("WeaponName", ammo, "AK-12", 22f, TextAlignmentOptions.BottomRight, new Vector2(1f, 0f), new Vector2(0f, 118f), new Vector2(480f, 30f), false, dim));
            ((RectTransform)hud.weaponName.transform).pivot = new Vector2(1f, 0f);
            hud.fireModeText = AddShadow(Txt("FireMode", ammo, "AUTO", 20f, TextAlignmentOptions.BottomRight, new Vector2(1f, 0f), new Vector2(0f, -18f), new Vector2(320f, 24f), false, accent));
            ((RectTransform)hud.fireModeText.transform).pivot = new Vector2(1f, 0f);

            // Health (bottom-left)
            var health = Rect("Health", hudRoot, Vector2.zero, Vector2.zero, new Vector2(70f, 70f), new Vector2(320f, 40f), Vector2.zero);
            Img("HealthBack", health, _white, new Color(0f, 0f, 0f, 0.45f), new Vector2(0f, 0f), new Vector2(160f, 6f), new Vector2(320f, 8f));
            hud.healthFill = Img("HealthFill", health, _white, Color.white, new Vector2(0f, 0f), new Vector2(160f, 6f), new Vector2(320f, 8f));
            hud.healthFill.type = Image.Type.Filled;
            hud.healthFill.fillMethod = Image.FillMethod.Horizontal;
            hud.healthText = AddShadow(Txt("HealthText", health, "100", 30f, TextAlignmentOptions.BottomLeft, new Vector2(0f, 0f), new Vector2(50f, 32f), new Vector2(100f, 36f), true));

            // Minimap (top-left) with circular mask
            var mapRoot = Rect("Minimap", hudRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(150f, -150f), new Vector2(232f, 232f));
            Img("MapBack", mapRoot, _circle, new Color(0.02f, 0.03f, 0.03f, 0.6f), c, Vector2.zero, new Vector2(232f, 232f));
            var mask = Img("Mask", mapRoot, _circle, Color.white, c, Vector2.zero, new Vector2(224f, 224f));
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            hud.minimapRotator = Rect("Rotator", mask.transform, c, c, Vector2.zero, new Vector2(10f, 10f));
            var raw = Rect("MapImage", hud.minimapRotator, c, c, Vector2.zero, new Vector2(400f, 400f)).gameObject.AddComponent<RawImage>();
            raw.color = new Color(1f, 1f, 1f, 0.85f);
            raw.raycastTarget = false;
            hud.minimapImage = raw;
            hud.minimapIconRoot = Rect("Icons", mask.transform, c, c, Vector2.zero, new Vector2(10f, 10f));
            hud.minimapIconTemplate = Img("Icon", hud.minimapIconRoot, _circle, Color.red, c, Vector2.zero, new Vector2(10f, 10f));
            Img("Ring", mapRoot, _ring, new Color(1f, 1f, 1f, 0.35f), c, Vector2.zero, new Vector2(232f, 232f));
            var arrow = Txt("PlayerArrow", mapRoot, "▲", 22f, TextAlignmentOptions.Center, c, Vector2.zero, new Vector2(30f, 30f), false, accent);
            hud.minimapRadius = 110f;

            // Match status under the minimap
            var status = Rect("MatchStatus", hudRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -290f), new Vector2(300f, 110f), new Vector2(0f, 1f));
            hud.modeText = AddShadow(Txt("Mode", status, "DUEL", 22f, TextAlignmentOptions.TopLeft, new Vector2(0f, 1f), new Vector2(150f, -14f), new Vector2(300f, 28f), false, dim));
            hud.timerText = AddShadow(Txt("Timer", status, "8:00", 38f, TextAlignmentOptions.TopRight, new Vector2(0f, 1f), new Vector2(150f, -10f), new Vector2(300f, 40f), true));
            hud.myScoreText = AddShadow(Txt("MyScore", status, "0", 30f, TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(26f, -62f), new Vector2(60f, 34f), true, TeamColor(true)));
            hud.myScoreBar = Bar(status, new Vector2(190f, -62f), TeamColor(true));
            hud.enemyScoreText = AddShadow(Txt("EnemyScore", status, "0", 30f, TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(26f, -94f), new Vector2(60f, 34f), true, TeamColor(false)));
            hud.enemyScoreBar = Bar(status, new Vector2(190f, -94f), TeamColor(false));
            hud.limitText = AddShadow(Txt("Limit", status, "15", 18f, TextAlignmentOptions.Right, new Vector2(0f, 1f), new Vector2(300f, -78f), new Vector2(60f, 24f), false, dim));

            // Compass (top-center)
            hud.compassRoot = Rect("Compass", hudRoot, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(640f, 30f));
            Img("CompassTick", hud.compassRoot, _white, new Color(1f, 1f, 1f, 0.8f), c, new Vector2(0f, -20f), new Vector2(2f, 10f));
            hud.compassTemplate = AddShadow(Txt("Bearing", hud.compassRoot, "N", 20f, TextAlignmentOptions.Center, c, Vector2.zero, new Vector2(60f, 30f)));

            // Kill feed (top-right)
            var feed = Rect("KillFeed", hudRoot, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -40f), new Vector2(620f, 260f), new Vector2(1f, 1f));
            var vlg = feed.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperRight;
            vlg.spacing = 4f;
            vlg.childControlHeight = false;
            vlg.childControlWidth = false;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = false;
            hud.killFeedRoot = feed;
            hud.killFeedTemplate = AddShadow(Txt("Entry", feed, "", 24f, TextAlignmentOptions.Right, new Vector2(1f, 1f), Vector2.zero, new Vector2(620f, 30f)));

            // Score popups + medal banner (placed cleanly right of center reticle so they never overlap BF6 hitmarker / skull)
            hud.popupRoot = Rect("Popups", hudRoot, c, c, new Vector2(135f, -30f), new Vector2(360f, 200f), new Vector2(0f, 0.5f));
            hud.popupTemplate = AddShadow(Txt("Popup", hud.popupRoot, "+100 KILL", 26f, TextAlignmentOptions.Left, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(360f, 30f), true));
            ((RectTransform)hud.popupTemplate.transform).pivot = new Vector2(0f, 0.5f);
            var medal = Rect("Medal", hudRoot, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(700f, 70f));
            hud.medalGroup = medal.gameObject.AddComponent<CanvasGroup>();
            Img("MedalBar", medal, _white, accent, c, new Vector2(0f, -30f), new Vector2(160f, 3f));
            hud.medalText = AddShadow(Txt("MedalText", medal, "", 48f, TextAlignmentOptions.Center, c, Vector2.zero, new Vector2(700f, 60f), true, accent));

            // Center notifications
            hud.centerText = AddShadow(Txt("CenterText", hudRoot, "", 72f, TextAlignmentOptions.Center, c, new Vector2(0f, 190f), new Vector2(1400f, 90f), true));
            hud.subText = AddShadow(Txt("SubText", hudRoot, "", 28f, TextAlignmentOptions.Center, c, new Vector2(0f, 250f), new Vector2(1000f, 40f), false, dim));

            // Misc
            hud.fpsText = Txt("FPS", hudRoot, "", 18f, TextAlignmentOptions.TopRight, new Vector2(1f, 1f), new Vector2(-40f, -10f), new Vector2(200f, 24f), false, dim);
            ((RectTransform)hud.fpsText.transform).pivot = new Vector2(1f, 1f);
            hud.bodycamOverlay = Txt("BodycamOverlay", hudRoot, "", 22f, TextAlignmentOptions.TopRight, new Vector2(1f, 1f), new Vector2(-40f, -310f), new Vector2(520f, 60f), false, new Color(1f, 1f, 1f, 0.7f));
            ((RectTransform)hud.bodycamOverlay.transform).pivot = new Vector2(1f, 1f);

            canvas.gameObject.AddComponent<CombatPostFX>();

            // Menus
            var menus = canvas.gameObject.AddComponent<GameMenuController>();
            menus.pausePanel = Panel("PausePanel", root, new Color(0.01f, 0.012f, 0.015f, 0.78f));
            Txt("Title", menus.pausePanel.transform, "PAUSED", 96f, TextAlignmentOptions.Center, c, new Vector2(0f, 260f), new Vector2(900f, 110f), true);
            menus.resumeButton = Btn("Resume", menus.pausePanel.transform, new Vector2(0f, 110f), new Vector2(380f, 62f));
            menus.settingsButton = Btn("Settings", menus.pausePanel.transform, new Vector2(0f, 36f), new Vector2(380f, 62f));
            menus.restartButton = Btn("Restart Match", menus.pausePanel.transform, new Vector2(0f, -38f), new Vector2(380f, 62f));
            menus.menuButton = Btn("Main Menu", menus.pausePanel.transform, new Vector2(0f, -112f), new Vector2(380f, 62f));
            menus.quitButton = Btn("Quit", menus.pausePanel.transform, new Vector2(0f, -186f), new Vector2(380f, 62f));
            Txt("ControlsHint", menus.pausePanel.transform,
                "WASD MOVE  ·  SHIFT SPRINT  ·  SPACE JUMP / MANTLE  ·  C / CTRL CROUCH / SLIDE  ·  Z PRONE  ·  Q / E LEAN  ·  R RELOAD  ·  RMB ADS  ·  1 / 2 SWAP  ·  TAB SCORE",
                19f, TextAlignmentOptions.Center, c, new Vector2(0f, -274f), new Vector2(1680f, 34f), false, dim);
            var settings = BuildSettings(root, out var settingsBack);
            menus.settingsPanel = settings.GetComponent<CanvasGroup>();
            menus.settingsBackButton = settingsBack;

            menus.scoreboardPanel = Panel("Scoreboard", root, new Color(0f, 0f, 0f, 0.55f));
            menus.scoreboardTitle = Txt("Title", menus.scoreboardPanel.transform, "SCOREBOARD", 56f, TextAlignmentOptions.Center, c, new Vector2(0f, 300f), new Vector2(900f, 70f), true);
            menus.scoreboardBody = Txt("Body", menus.scoreboardPanel.transform, "", 30f, TextAlignmentOptions.TopLeft, c, new Vector2(0f, 20f), new Vector2(900f, 480f));
            menus.scoreboardBody.textWrappingMode = TextWrappingModes.Normal;

            menus.endPanel = Panel("EndScreen", root, new Color(0.01f, 0.012f, 0.015f, 0.86f));
            menus.endTitle = Txt("Result", menus.endPanel.transform, "VICTORY", 130f, TextAlignmentOptions.Center, c, new Vector2(0f, 330f), new Vector2(1200f, 150f), true);
            menus.endSubtitle = Txt("Subtitle", menus.endPanel.transform, "", 30f, TextAlignmentOptions.Center, c, new Vector2(0f, 240f), new Vector2(1200f, 40f), false, dim);
            menus.endScoreboard = Txt("Scoreboard", menus.endPanel.transform, "", 30f, TextAlignmentOptions.TopLeft, c, new Vector2(0f, -10f), new Vector2(900f, 400f));
            menus.endRestartButton = Btn("Play Again", menus.endPanel.transform, new Vector2(-200f, -320f), new Vector2(360f, 64f));
            menus.endMenuButton = Btn("Main Menu", menus.endPanel.transform, new Vector2(200f, -320f), new Vector2(360f, 64f));

            SavePrefab(canvas.gameObject, PrefabDir + "/LR2_GameUI.prefab");
        }

        private static Color TeamColor(bool mine) => _theme != null ? (mine ? _theme.accent : _theme.enemy) : (mine ? new Color(1f, 0.71f, 0.28f) : new Color(1f, 0.3f, 0.27f));

        private static Image Bar(Transform parent, Vector2 pos, Color color)
        {
            Img("BarBack", parent, _white, new Color(0f, 0f, 0f, 0.45f), new Vector2(0f, 1f), pos, new Vector2(200f, 6f));
            var fill = Img("BarFill", parent, _white, color, new Vector2(0f, 1f), pos, new Vector2(200f, 6f));
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = 0f;
            return fill;
        }

        // ------------------------------------------------------------------ main menu UI
        private static void BuildMainMenuUI()
        {
            var canvas = NewCanvas("LR2_MainMenuUI", 10);
            var root = canvas.transform;
            var mm = canvas.gameObject.AddComponent<MainMenuController>();
            Vector2 left = new Vector2(0f, 0.5f);
            Vector2 mid = new Vector2(0.5f, 0.5f);
            Color accent = _theme != null ? _theme.accent : new Color(1f, 0.71f, 0.28f);
            Color dim = _theme != null ? _theme.textDim : new Color(1f, 1f, 1f, 0.55f);

            var grad = Img("LeftShade", root, _white, new Color(0f, 0f, 0f, 0.52f), left, new Vector2(320f, 0f), new Vector2(640f, 1080f));
            grad.rectTransform.anchorMin = new Vector2(0f, 0f);
            grad.rectTransform.anchorMax = new Vector2(0f, 1f);
            grad.rectTransform.sizeDelta = new Vector2(640f, 0f);

            var main = Rect("MainPanel", root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            mm.mainPanel = main.gameObject.AddComponent<CanvasGroup>();
            Txt("Title", main, "LEADRUSH 2", 118f, TextAlignmentOptions.Left, left, new Vector2(465f, 345f), new Vector2(800f, 130f), true);

            Button MenuBtn(string label, float y, float h = 60f)
            {
                var b = Btn(label, main, Vector2.zero, new Vector2(455f, h));
                var rt = (RectTransform)b.transform;
                rt.anchorMin = rt.anchorMax = left;
                rt.anchoredPosition = new Vector2(320f, y);
                return b;
            }
            mm.duelButton = MenuBtn("1v1 Deathmatch", 226f, 52f);
            mm.ctfButton = MenuBtn("Capture The Flag", 166f, 52f);
            mm.ffaButton = MenuBtn("Free For All", 106f, 52f);
            mm.tdmButton = MenuBtn("Team Deathmatch", 46f, 52f);
            mm.modeRulesButton = MenuBtn("Mode Rules  -  Customize", -34f, 44f);
            mm.settingsButton = MenuBtn("Settings", -88f, 44f);
            mm.quitButton = MenuBtn("Quit", -142f, 44f);

            // Bot difficulty roster selector (left/right stepper matching the Map Recon card; map dropdown removed)
            Txt("BotDifficultyLabel", main, "BOT DIFFICULTY", 17f, TextAlignmentOptions.MidlineLeft, left, new Vector2(320f, -208f), new Vector2(455f, 24f), true, accent);
            var diffRoster = Rect("BotDifficultyRoster", main, left, left, new Vector2(320f, -246f), new Vector2(455f, 48f));
            BuildStepper(diffRoster, 48f, out mm.difficultyPrevButton, out mm.difficultyNextButton, out mm.difficultyValueText, 22f, accent);

            // Right-hand Map Preview Card (kept clear of center Operator silhouette)
            var card = Rect("MapPreviewCard", main, mid, mid, new Vector2(460f, 10f), new Vector2(720f, 560f));
            Img("CardBG", card, _white, new Color(0.025f, 0.032f, 0.042f, 0.88f), mid, Vector2.zero, new Vector2(720f, 560f));
            Img("CardTopAccent", card, _white, accent, new Vector2(0.5f, 1f), new Vector2(0f, -2f), new Vector2(720f, 4f));

            var reconHdr = Txt("ReconHeader", card, "TACTICAL SECTOR // MAP RECON", 18f, TextAlignmentOptions.MidlineLeft, new Vector2(0f, 1f), new Vector2(22f, -28f), new Vector2(400f, 26f), true, accent);
            ((RectTransform)reconHdr.transform).pivot = new Vector2(0f, 0.5f);

            mm.mapPreviewIndexText = Txt("SectorIndex", card, "SECTOR 01 / 04", 18f, TextAlignmentOptions.MidlineRight, new Vector2(1f, 1f), new Vector2(-126f, -28f), new Vector2(180f, 26f), false, dim);
            ((RectTransform)mm.mapPreviewIndexText.transform).pivot = new Vector2(1f, 0.5f);

            Button ArrowBtn(string sym, float xPos)
            {
                var b = Btn(sym, card, Vector2.zero, new Vector2(42f, 30f));
                var rt = (RectTransform)b.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(xPos, -28f);
                var lbl = b.GetComponentInChildren<TMP_Text>();
                if (lbl != null)
                {
                    lbl.alignment = TextAlignmentOptions.Center;
                    lbl.fontSize = 20f;
                    lbl.rectTransform.anchoredPosition = Vector2.zero;
                }
                return b;
            }
            mm.mapPrevButton = ArrowBtn("<", -86f);
            mm.mapNextButton = ArrowBtn(">", -36f);

            Img("PreviewBorder", card, _white, new Color(1f, 1f, 1f, 0.12f), mid, new Vector2(0f, 36f), new Vector2(684f, 386f));
            mm.mapPreviewImage = Img("PreviewImage", card, _white, Color.white, mid, new Vector2(0f, 36f), new Vector2(680f, 382f));
            mm.mapPreviewImage.preserveAspect = false;

            // Subtle bottom gradient inside preview frame for readable captioning
            Img("PreviewShade", mm.mapPreviewImage.transform, _white, new Color(0f, 0f, 0f, 0.25f), new Vector2(0.5f, 0f), new Vector2(0f, 20f), new Vector2(680f, 40f));

            mm.mapPreviewTitle = Txt("MapTitle", card, "SALTWORKS  -  RECLAIMED HARBOUR", 26f, TextAlignmentOptions.MidlineLeft, new Vector2(0f, 0f), new Vector2(22f, 102f), new Vector2(676f, 34f), true, Color.white);
            ((RectTransform)mm.mapPreviewTitle.transform).pivot = new Vector2(0f, 0.5f);

            mm.mapPreviewDescription = Txt("MapDescription", card, "", 17f, TextAlignmentOptions.TopLeft, new Vector2(0f, 0f), new Vector2(22f, 76f), new Vector2(676f, 42f), false, dim);
            ((RectTransform)mm.mapPreviewDescription.transform).pivot = new Vector2(0f, 1f);
            mm.mapPreviewDescription.textWrappingMode = TextWrappingModes.Normal;

            mm.mapPreviewStatusText = Txt("RulesSummary", card, "", 16f, TextAlignmentOptions.MidlineLeft, new Vector2(0f, 0f), new Vector2(22f, 22f), new Vector2(676f, 24f), false, accent);
            ((RectTransform)mm.mapPreviewStatusText.transform).pivot = new Vector2(0f, 0.5f);

            mm.versionText = Txt("Version", root, "", 18f, TextAlignmentOptions.BottomRight, new Vector2(1f, 0f), new Vector2(-30f, 20f), new Vector2(500f, 24f), false, dim);
            ((RectTransform)mm.versionText.transform).pivot = new Vector2(1f, 0f);

            // Layer 2 Deployment Lobby + Per-mode rules customizer panel
            BuildDeployPanel(root, mm, accent, dim);
            BuildModeRulesPanel(root, mm, accent, dim);

            var settings = BuildSettings(root, out var back);
            mm.settingsPanel = settings.GetComponent<CanvasGroup>();
            mm.settingsBackButton = back;
            mm.loadingPanel = Panel("Loading", root, new Color(0.035f, 0.042f, 0.055f, 1f));
            var lBrand = Txt("BrandWordmark", mm.loadingPanel.transform, "LEADRUSH II", 13f, TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(48f, -42f), new Vector2(320f, 26f), true, new Color(1f, 1f, 1f, 0.34f));
            lBrand.rectTransform.pivot = new Vector2(0f, 1f);
            lBrand.characterSpacing = 6f;
            var lEye = Txt("LoadingEyebrow", mm.loadingPanel.transform, "SECTOR DEPLOYMENT", 12f, TextAlignmentOptions.Center,
                mid, new Vector2(0f, 84f), new Vector2(760f, 26f), true, new Color(0.79f, 0.60f, 0.31f, 0.92f));
            lEye.characterSpacing = 8f;
            lEye.textWrappingMode = TextWrappingModes.NoWrap;
            var lTitle = Txt("LoadingText", mm.loadingPanel.transform, "DEPLOYING", 44f, TextAlignmentOptions.Center,
                mid, new Vector2(0f, 30f), new Vector2(860f, 66f), true, new Color(0.95f, 0.96f, 0.97f, 1f));
            lTitle.characterSpacing = 5f;
            lTitle.textWrappingMode = TextWrappingModes.NoWrap;
            Img("BarTrack", mm.loadingPanel.transform, _white, new Color(1f, 1f, 1f, 0.11f), mid, new Vector2(0f, -24f), new Vector2(420f, 2f));
            var barFill = Img("BarFill", mm.loadingPanel.transform, _white, new Color(0.89f, 0.66f, 0.29f, 1f), mid, new Vector2(0f, -24f), new Vector2(420f, 2f));
            barFill.type = Image.Type.Filled;
            barFill.fillMethod = Image.FillMethod.Horizontal;
            barFill.fillAmount = 0f;
            mm.loadingSpinner = null;
            mm.loadingBarFill = barFill;
            mm.loadingStatusText = Txt("LoadingStatus", mm.loadingPanel.transform, "PREPARING ARENA  \u00b7  0%", 13f, TextAlignmentOptions.Center,
                mid, new Vector2(0f, -56f), new Vector2(820f, 26f), false, new Color(0.58f, 0.62f, 0.66f, 0.95f));
            mm.loadingStatusText.characterSpacing = 3f;
            mm.loadingStatusText.textWrappingMode = TextWrappingModes.NoWrap;

            mm.duelSettings = AssetDatabase.LoadAssetAtPath<MatchSettings>("Assets/LR2/Data/Game/MatchSettings_Duel.asset");
            mm.ctfSettings = AssetDatabase.LoadAssetAtPath<MatchSettings>("Assets/LR2/Data/Game/MatchSettings_CTF.asset");
            mm.ffaSettings = AssetDatabase.LoadAssetAtPath<MatchSettings>("Assets/LR2/Data/Game/MatchSettings_FFA.asset");
            mm.tdmSettings = AssetDatabase.LoadAssetAtPath<MatchSettings>("Assets/LR2/Data/Game/MatchSettings_TDM.asset");
            SavePrefab(canvas.gameObject, PrefabDir + "/LR2_MainMenuUI.prefab");
        }

        private static void BuildDeployPanel(Transform root, MainMenuController mm, Color accent, Color dim)
        {
            mm.deployPanel = Panel("DeployPanel", root, new Color(0.015f, 0.02f, 0.028f, 0.94f));
            var t = mm.deployPanel.transform;
            Vector2 topC = new Vector2(0.5f, 1f);

            mm.deployModeTitleText = Txt("DeployTitle", t, "1V1 DEATHMATCH  ·  STAGING", 56f, TextAlignmentOptions.Center, topC, new Vector2(0f, -64f), new Vector2(1200f, 68f), true);
            Img("DeployRule", t, _white, accent, topC, new Vector2(0f, -106f), new Vector2(260f, 2f));
            mm.deployModeDescText = Txt("DeployDesc", t, "", 18.5f, TextAlignmentOptions.Center, topC, new Vector2(0f, -146f), new Vector2(1260f, 52f), false, dim);
            mm.deployModeDescText.textWrappingMode = TextWrappingModes.Normal;
            mm.deployModeDescText.enableAutoSizing = true;
            mm.deployModeDescText.fontSizeMin = 14f;
            mm.deployModeDescText.fontSizeMax = 18.5f;

            CardBacking(t, "DeployLeftCard", topC, new Vector2(-392f, -458f), new Vector2(730f, 466f), accent);
            CardBacking(t, "DeployRightCard", topC, new Vector2(392f, -458f), new Vector2(730f, 466f), accent);

            const float leftCol = -730f;
            float y = -270f;
            const float step = 62f;
            SelectorColRow(t, "PRIMARY WEAPON", leftCol, y, out mm.deployWeaponPrevButton, out mm.deployWeaponNextButton, out mm.deployWeaponValueText); y -= 40f;
            mm.deployWeaponInfoText = Txt("WeaponBlurb", t, "", 14.5f, TextAlignmentOptions.MidlineLeft, topC, new Vector2(leftCol, y), new Vector2(680f, 22f), false, accent);
            ((RectTransform)mm.deployWeaponInfoText.transform).pivot = new Vector2(0f, 0.5f);
            mm.deployWeaponInfoText.enableAutoSizing = true;
            mm.deployWeaponInfoText.fontSizeMin = 11f;
            mm.deployWeaponInfoText.fontSizeMax = 14.5f;
            mm.deployWeaponInfoText.overflowMode = TextOverflowModes.Ellipsis;
            y -= 44f;
            SelectorColRow(t, "SECTOR / MAP", leftCol, y, out mm.deployMapPrevButton, out mm.deployMapNextButton, out mm.deployMapValueText); y -= step;
            SelectorColRow(t, "BOT DIFFICULTY", leftCol, y, out mm.deployDiffPrevButton, out mm.deployDiffNextButton, out mm.deployDiffValueText); y -= step;
            SelectorColRow(t, "BOT TEAM MODE", leftCol, y, out mm.deployBotTeamPrevButton, out mm.deployBotTeamNextButton, out mm.deployBotTeamValueText); y -= step;
            mm.deployScoreSlider = SliderColRow(t, "SCORE LIMIT", leftCol, y, out mm.deployScoreValueText); y -= step;
            mm.deployBotsSlider = SliderColRow(t, "BOT COUNT", leftCol, y, out mm.deployBotsValueText);

            var rightCenter = topC;
            var previewLbl = Txt("PreviewLbl", t, "LIVE SECTOR RECON", 18f, TextAlignmentOptions.MidlineLeft, rightCenter, new Vector2(58f, -254f), new Vector2(500f, 26f), true, accent);
            ((RectTransform)previewLbl.transform).pivot = new Vector2(0f, 0.5f);
            Img("DeployPreviewFrame", t, _white, new Color(1f, 1f, 1f, 0.14f), rightCenter, new Vector2(392f, -478f), new Vector2(668f, 368f));
            mm.deployMapPreviewImage = Img("DeployPreviewImg", t, _white, Color.white, rightCenter, new Vector2(392f, -478f), new Vector2(660f, 360f));
            mm.deployMapPreviewImage.preserveAspect = false;

            mm.deployBackButton = Btn("Back", t, new Vector2(-360f, -364f), new Vector2(280f, 56f));
            mm.deployRulesButton = Btn("All Mode Rules", t, new Vector2(0f, -364f), new Vector2(300f, 56f));
            mm.deployStartButton = Btn("Deploy Now", t, new Vector2(360f, -364f), new Vector2(320f, 58f));
            var startImg = mm.deployStartButton.GetComponent<Image>();
            if (startImg != null) startImg.color = new Color(0.28f, 0.21f, 0.10f, 0.96f);
        }

        private static void BuildModeRulesPanel(Transform root, MainMenuController mm, Color accent, Color dim)
        {
            mm.modeRulesPanel = Panel("ModeRulesPanel", root, new Color(0.015f, 0.02f, 0.028f, 0.95f));
            var t = mm.modeRulesPanel.transform;

            Txt("Title", t, "MODE RULES & MATCH SETUP", 56f, TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(960f, 68f), true);
            mm.modeRulesTitleText = Txt("SubTitle", t, "1V1 DEATHMATCH  ·  COMPETITIVE RULES", 22f, TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0f, -112f), new Vector2(900f, 32f), false, accent);
            CardBacking(t, "RulesLeftCard", new Vector2(0.5f, 1f), new Vector2(-448f, -435f), new Vector2(700f, 420f), accent);
            CardBacking(t, "RulesRightCard", new Vector2(0.5f, 1f), new Vector2(392f, -435f), new Vector2(700f, 420f), accent);

            Button Tab(string label, float x)
            {
                var b = Btn(label, t, Vector2.zero, new Vector2(260f, 50f));
                var rt = (RectTransform)b.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(x, -174f);
                return b;
            }
            mm.modeRulesDuelTab = Tab("1v1 Deathmatch", -420f);
            mm.modeRulesCtfTab = Tab("Capture The Flag", -140f);
            mm.modeRulesFfaTab = Tab("Free For All", 140f);
            mm.modeRulesTdmTab = Tab("Team Deathmatch", 420f);

            const float leftCol = -770f;
            const float rightCol = 70f;
            const float step = 62f;
            float yL = -266f;
            mm.rulesScoreLimitSlider = SliderColRow(t, "SCORE LIMIT", leftCol, yL, out mm.rulesScoreLimitValue); yL -= step;
            mm.rulesTimeLimitSlider = SliderColRow(t, "TIME LIMIT (MIN)", leftCol, yL, out mm.rulesTimeLimitValue); yL -= step;
            mm.rulesRespawnSlider = SliderColRow(t, "RESPAWN DELAY", leftCol, yL, out mm.rulesRespawnValue); yL -= step;
            mm.rulesCountdownSlider = SliderColRow(t, "PRE-MATCH COUNTDOWN", leftCol, yL, out mm.rulesCountdownValue); yL -= step;
            SelectorColRow(t, "MODE BOT DIFFICULTY", leftCol, yL, out mm.rulesBotDiffPrevButton, out mm.rulesBotDiffNextButton, out mm.rulesBotDiffValueText);

            float yR = -266f;
            mm.rulesEnemyBotsSlider = SliderColRow(t, "ENEMY BOTS", rightCol, yR, out mm.rulesEnemyBotsValue); yR -= step;
            mm.rulesFriendlyBotsSlider = SliderColRow(t, "FRIENDLY BOTS", rightCol, yR, out mm.rulesFriendlyBotsValue); yR -= step;
            SelectorColRow(t, "BOT TEAM MODE", rightCol, yR, out mm.rulesBotTeamPrevButton, out mm.rulesBotTeamNextButton, out mm.rulesBotTeamValueText); yR -= step;
            mm.rulesSeekerDronesToggle = ToggleColRow(t, "SEEKER DRONES", rightCol, yR); yR -= step;
            mm.rulesMaxOrbsSlider = SliderColRow(t, "MAX SEEKER DRONES", rightCol, yR, out mm.rulesMaxOrbsValue); yR -= step;
            mm.rulesOrbIntervalSlider = SliderColRow(t, "DRONE SPAWN INTERVAL", rightCol, yR, out mm.rulesOrbIntervalValue);

            Txt("AutoSaveHint", t, "ALL CHANGES SAVE AUTOMATICALLY PER GAME MODE", 18f, TextAlignmentOptions.Center, new Vector2(0.5f, 0f), new Vector2(0f, 146f), new Vector2(820f, 28f), false, dim);
            mm.modeRulesResetButton = Btn("Reset Mode", t, new Vector2(-340f, -446f), new Vector2(280f, 54f));
            mm.modeRulesPlayButton = Btn("Deploy Mode", t, new Vector2(0f, -446f), new Vector2(300f, 54f));
            mm.modeRulesBackButton = Btn("Back", t, new Vector2(340f, -446f), new Vector2(280f, 54f));
        }
    }
}
