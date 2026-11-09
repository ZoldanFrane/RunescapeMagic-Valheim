using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace RunescapeMagicValheim
{
    public class TopModePlateUI : MonoBehaviour
    {
        public static TopModePlateUI Instance { get; private set; }

        private CanvasGroup _canvasGroup;
        private RectTransform _plateRect;
        private Image _plateBg;
        private Image _spellIcon;
        private Image _iconFrame;
        private Text _modeText;
        private Outline _textOutline;

        private SpellType _lastSpell = (SpellType)(-1);
        private int _lastModeIndex = -1;
        private float _punchScale = 1f;

        private static readonly Dictionary<SpellType, string> SpellIconFiles = new Dictionary<SpellType, string>
        {
            { SpellType.SpectralAxe, "icon_spectral_axe.png" },
            { SpellType.Splinter, "icon_splinger.png" },
            { SpellType.OreBlast, "icon_ore_blast.png" },
            { SpellType.AreaHarvest, "icon_area_harvest.png" },
            { SpellType.InstantGrow, "icon_instant_grow.png" },
            { SpellType.InventoryRepair, "icon_inventory_repair.png" },
            { SpellType.PieceRepair, "icon_piece_repair.png" },
            { SpellType.Overdrive, "icon_overdrive.png" }
        };

        public static void Init(Transform hudRoot)
        {
            if (Instance != null) return;

            GameObject go = new GameObject("RunescapeTopModePlateUI", typeof(RectTransform));
            go.transform.SetParent(hudRoot, false);
            Instance = go.AddComponent<TopModePlateUI>();
            Instance.BuildUI();
        }

        private void BuildUI()
        {
            _plateRect = GetComponent<RectTransform>();
            _plateRect.anchorMin = new Vector2(0.5f, 1f);
            _plateRect.anchorMax = new Vector2(0.5f, 1f);
            _plateRect.pivot = new Vector2(0.5f, 1f);
            _plateRect.anchoredPosition = new Vector2(0f, -25f);
            _plateRect.sizeDelta = new Vector2(460f, 114f);

            _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            // 1. Фон плашки (ui_top_mode_plate.png)
            _plateBg = gameObject.AddComponent<Image>();
            Sprite plateSprite = SpellRadialMenu.GetCustomSprite("ui_top_mode_plate.png");
            if (plateSprite != null)
            {
                _plateBg.sprite = plateSprite;
                _plateBg.color = Color.white;
            }
            else
            {
                _plateBg.color = new Color(0.12f, 0.08f, 0.05f, 0.90f);
            }
            _plateBg.preserveAspect = true;
            _plateBg.raycastTarget = false;

            // 2. Иконка заклинания слева
            GameObject iconObj = new GameObject("SpellIcon", typeof(RectTransform), typeof(Image));
            iconObj.transform.SetParent(transform, false);
            RectTransform iconRect = iconObj.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(-130f, 0f);
            iconRect.sizeDelta = new Vector2(46f, 46f);

            _spellIcon = iconObj.GetComponent<Image>();
            _spellIcon.preserveAspect = true;
            _spellIcon.raycastTarget = false;

            // 3. Рамка иконки поверх
            GameObject frameObj = new GameObject("IconFrame", typeof(RectTransform), typeof(Image));
            frameObj.transform.SetParent(iconObj.transform, false);
            RectTransform frameRect = frameObj.GetComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0.5f, 0.5f);
            frameRect.anchorMax = new Vector2(0.5f, 0.5f);
            frameRect.pivot = new Vector2(0.5f, 0.5f);
            frameRect.anchoredPosition = Vector2.zero;
            frameRect.sizeDelta = new Vector2(52f, 52f);

            _iconFrame = frameObj.GetComponent<Image>();
            Sprite frameSprite = SpellRadialMenu.GetCustomSprite("Slot_Frame.png");
            if (frameSprite != null)
            {
                _iconFrame.sprite = frameSprite;
                _iconFrame.color = Color.white;
            }
            _iconFrame.preserveAspect = true;
            _iconFrame.raycastTarget = false;

            // 4. Текст режима по центру таблички (сбалансирован по центру без перекрытия иконки)
            GameObject textObj = new GameObject("ModeText", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObj.transform.SetParent(transform, false);
            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = new Vector2(20f, -2f);
            textRect.sizeDelta = new Vector2(270f, 60f);

            _modeText = textObj.GetComponent<Text>();
            Text hudText = Hud.instance != null ? Hud.instance.GetComponentInChildren<Text>(true) : null;
            _modeText.font = hudText != null ? hudText.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            _modeText.fontSize = 23;
            _modeText.fontStyle = FontStyle.Bold;
            _modeText.alignment = TextAnchor.MiddleCenter;
            _modeText.color = new Color(1f, 0.90f, 0.30f, 1f); // Красивое золотое свечение рун
            _modeText.raycastTarget = false;

            _textOutline = textObj.GetComponent<Outline>();
            _textOutline.effectColor = new Color(0.12f, 0.06f, 0.01f, 0.95f);
            _textOutline.effectDistance = new Vector2(1.5f, -1.5f);
        }

        private void Update()
        {
            Player player = Player.m_localPlayer;
            bool shouldShow = player != null 
                              && WandInputPatches.IsHoldingMagicWand(player) 
                              && (Hud.instance == null || (!Hud.IsUserHidden() && !Menu.IsVisible()));

            float targetAlpha = shouldShow ? 1f : 0f;
            _canvasGroup.alpha = Mathf.MoveTowards(_canvasGroup.alpha, targetAlpha, Time.deltaTime * 5f);

            if (_canvasGroup.alpha > 0.01f)
            {
                SpellType curSpell = SpellRadialMenu.GetActiveOrHoveredSpell();
                int curMode = SpellModeManager.GetCurrentMode(curSpell);

                if (curSpell != _lastSpell || curMode != _lastModeIndex)
                {
                    RefreshDisplay(player, curSpell, curMode);
                }
            }

            // Плавное возвращение масштаба после анимации переключения режима
            if (_punchScale > 1f)
            {
                _punchScale = Mathf.MoveTowards(_punchScale, 1f, Time.deltaTime * 2.5f);
                _plateRect.localScale = Vector3.one * _punchScale;
            }
        }

        public void OnModeChanged()
        {
            _punchScale = 1.07f;
            _plateRect.localScale = Vector3.one * _punchScale;

            Player player = Player.m_localPlayer;
            SpellType curSpell = SpellRadialMenu.GetActiveOrHoveredSpell();
            int curMode = SpellModeManager.GetCurrentMode(curSpell);
            RefreshDisplay(player, curSpell, curMode);
        }

        private void RefreshDisplay(Player player, SpellType spell, int modeIndex)
        {
            _lastSpell = spell;
            _lastModeIndex = modeIndex;

            // Обновляем иконку
            if (SpellIconFiles.TryGetValue(spell, out string iconFile))
            {
                Sprite s = SpellRadialMenu.GetCustomSprite(iconFile);
                if (s != null)
                {
                    _spellIcon.sprite = s;
                    _spellIcon.enabled = true;
                }
            }

            // Обновляем название режима
            SpellModeInfo modeInfo = SpellModeManager.GetCurrentModeInfo(player, spell);
            _modeText.text = modeInfo.Name;
        }
    }
}
