using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace RunescapeMagicValheim
{
    public class SpellRadialMenu : MonoBehaviour
    {
        public static SpellRadialMenu Instance { get; private set; }
        public static bool IsVisible => Instance != null && Instance._menuRoot != null && Instance._menuRoot.activeSelf;

        private GameObject _menuRoot;
        private GameObject _highlightObj;
        private RectTransform _highlightRect;
        private float _currentHighlightAngle;
        private float _targetHighlightAngle;

        private Text _titleText;
        private Text _costText;
        private Text _reqText;
        private Text _statsText;

        // Панель улучшений справа от радиального меню
        private GameObject _upgradesPanel;
        private Text _upgradesHeaderTitle;
        private Text _upgradesHeaderSubtitle;
        private Text _upgradesContentText;

        private readonly List<RadialSlot> _slots = new List<RadialSlot>();
        private int _hoveredIndex = -1;

        private static Sprite _circleSprite;
        private static Sprite _ringSprite;
        private static Sprite _lockSprite;
        private static Sprite _panelSprite;
        private static Font _gameFont;

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

        private class RadialSlot
        {
            public SpellType Spell;
            public RectTransform Rect;
            public Image Icon;
            public Image Frame;
            public Image LockOverlay;
            public bool IsUnlocked;
        }

        public struct SpellUIInfo
        {
            public SpellType Spell;
            public string Name;
            public string IconPrefab;
            public Color Tint;
            public Skills.SkillType Skill;
            public float BaseLevel;
            public float EffectiveLevel;
            public float ReqLevel;
            public float StaminaCost;
            public float HealthCost;
            public string ExtraStats;
        }

        public struct SpellMilestone
        {
            public float Level;
            public string Description;

            public SpellMilestone(float level, string description)
            {
                Level = level;
                Description = description;
            }
        }

        public static void Init(Transform hudRoot)
        {
            if (Instance != null) return;

            GameObject go = new GameObject("RunescapeSpellRadialMenu");
            go.transform.SetParent(hudRoot, false);
            Instance = go.AddComponent<SpellRadialMenu>();
            Instance.BuildUI();
        }

        private void BuildUI()
        {
            Text existingText = Hud.instance.GetComponentInChildren<Text>(true);
            _gameFont = existingText != null ? existingText.font : Resources.GetBuiltinResource<Font>("Arial.ttf");

            _circleSprite = CreateCircleSprite(256, 0f, 1f);
            _ringSprite = CreateCircleSprite(512, 0.44f, 0.98f);
            _lockSprite = CreateLockSprite(128);
            _panelSprite = CreatePanelSprite(48, 48, 4);

            _menuRoot = new GameObject("MenuRoot", typeof(RectTransform));
            _menuRoot.transform.SetParent(transform, false);
            RectTransform rootRect = _menuRoot.GetComponent<RectTransform>();
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(560f, 560f);

            // 1. Основное фоновое резное кольцо Wheel_bg (560x560)
            GameObject wheelBg = new GameObject("WheelBg", typeof(RectTransform), typeof(Image));
            wheelBg.transform.SetParent(_menuRoot.transform, false);
            RectTransform wheelRect = wheelBg.GetComponent<RectTransform>();
            wheelRect.sizeDelta = new Vector2(560f, 560f);
            Image wheelImg = wheelBg.GetComponent<Image>();
            Sprite wheelSprite = GetCustomSprite("Wheel_bg.png");
            if (wheelSprite != null)
            {
                wheelImg.sprite = wheelSprite;
                wheelImg.color = Color.white;
            }
            else
            {
                wheelImg.sprite = _ringSprite;
                wheelImg.color = new Color(0.16f, 0.12f, 0.09f, 0.94f);
            }
            wheelImg.preserveAspect = true;
            wheelImg.raycastTarget = false;

            // 2. Вращающееся обрамление/подсветка сектора (560x560)
            _highlightObj = new GameObject("SectorHighlight", typeof(RectTransform), typeof(Image));
            _highlightObj.transform.SetParent(_menuRoot.transform, false);
            _highlightRect = _highlightObj.GetComponent<RectTransform>();
            _highlightRect.sizeDelta = new Vector2(560f, 560f);
            Image highlightImg = _highlightObj.GetComponent<Image>();
            Sprite highlightSprite = GetCustomSprite("sector_highlight.png");
            if (highlightSprite != null)
            {
                highlightImg.sprite = highlightSprite;
                highlightImg.color = Color.white;
            }
            else
            {
                highlightImg.color = Color.clear;
            }
            highlightImg.preserveAspect = true;
            highlightImg.raycastTarget = false;

            // 3. Слоты заклинаний (8 позиций по кругу, радиус 185)
            SpellType[] spells = (SpellType[])Enum.GetValues(typeof(SpellType));
            float radius = 185f;
            Sprite frameSprite = GetCustomSprite("Slot_Frame.png");

            for (int i = 0; i < spells.Length; i++)
            {
                // Шаг 45 градусов, начиная строго сверху (90 град) по часовой стрелке
                float angleDeg = 90f - i * (360f / spells.Length);
                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector2 pos = new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad)) * radius;

                GameObject slotObj = new GameObject($"Slot_{spells[i]}", typeof(RectTransform));
                slotObj.transform.SetParent(_menuRoot.transform, false);
                RectTransform slotRect = slotObj.GetComponent<RectTransform>();
                slotRect.anchoredPosition = pos;
                slotRect.sizeDelta = new Vector2(88f, 88f);

                // 3.1. Иконка способности (нижний слой слота)
                GameObject iconObj = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconObj.transform.SetParent(slotObj.transform, false);
                RectTransform iconRect = iconObj.GetComponent<RectTransform>();
                iconRect.sizeDelta = new Vector2(88f, 88f);
                Image iconImg = iconObj.GetComponent<Image>();
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;

                if (SpellIconFiles.TryGetValue(spells[i], out string iconFile))
                {
                    Sprite iconSp = GetCustomSprite(iconFile);
                    if (iconSp != null) iconImg.sprite = iconSp;
                }

                // 3.2. Обрамление слота Slot_frame (накладывается внутренним краем на внешние края иконки)
                GameObject frameObj = new GameObject("SlotFrame", typeof(RectTransform), typeof(Image));
                frameObj.transform.SetParent(slotObj.transform, false);
                RectTransform frameRect = frameObj.GetComponent<RectTransform>();
                frameRect.sizeDelta = new Vector2(88f, 88f);
                Image frameImg = frameObj.GetComponent<Image>();
                if (frameSprite != null) frameImg.sprite = frameSprite;
                frameImg.preserveAspect = true;
                frameImg.raycastTarget = false;

                // 3.3. Замок для заблокированных способностей
                GameObject lockObj = new GameObject("LockOverlay", typeof(RectTransform), typeof(Image));
                lockObj.transform.SetParent(slotObj.transform, false);
                RectTransform lockRect = lockObj.GetComponent<RectTransform>();
                lockRect.sizeDelta = new Vector2(36f, 36f);
                Image lockImg = lockObj.GetComponent<Image>();
                lockImg.sprite = _lockSprite;
                lockImg.color = new Color(0.95f, 0.35f, 0.35f, 0.95f);
                lockImg.raycastTarget = false;
                lockObj.SetActive(false);

                _slots.Add(new RadialSlot
                {
                    Spell = spells[i],
                    Rect = slotRect,
                    Icon = iconImg,
                    Frame = frameImg,
                    LockOverlay = lockImg
                });
            }

            // 4. Центральный медальон и тексты описания способности
            GameObject centerContainer = new GameObject("CenterContainer", typeof(RectTransform), typeof(Image));
            centerContainer.transform.SetParent(_menuRoot.transform, false);
            RectTransform centerRect = centerContainer.GetComponent<RectTransform>();
            centerRect.sizeDelta = new Vector2(230f, 230f);
            Image centerImg = centerContainer.GetComponent<Image>();
            centerImg.sprite = _circleSprite;
            centerImg.color = new Color(0.06f, 0.05f, 0.04f, 0.55f);
            centerImg.raycastTarget = false;

            _titleText = CreateText(centerContainer.transform, "Title", new Vector2(0f, 52f), 18, new Color(1f, 0.84f, 0.38f), FontStyle.Bold);
            _costText  = CreateText(centerContainer.transform, "Cost",  new Vector2(0f, 14f), 14, Color.white, FontStyle.Normal);
            _statsText = CreateText(centerContainer.transform, "Stats", new Vector2(0f, -18f), 13, new Color(0.72f, 0.86f, 1f), FontStyle.Normal);
            _reqText   = CreateText(centerContainer.transform, "Req",   new Vector2(0f, -54f), 13, new Color(0.88f, 0.88f, 0.88f), FontStyle.Bold);

            // ==========================================
            // Панель улучшений справа от радиального меню
            // ==========================================
            _upgradesPanel = new GameObject("UpgradesPanel", typeof(RectTransform), typeof(Image));
            _upgradesPanel.transform.SetParent(_menuRoot.transform, false);
            RectTransform upgRect = _upgradesPanel.GetComponent<RectTransform>();
            upgRect.anchorMin = new Vector2(0.5f, 0.5f);
            upgRect.anchorMax = new Vector2(0.5f, 0.5f);
            upgRect.pivot = new Vector2(0f, 0.5f);
            upgRect.anchoredPosition = new Vector2(310f, 0f);
            upgRect.sizeDelta = new Vector2(370f, 520f);

            Image upgBg = _upgradesPanel.GetComponent<Image>();
            upgBg.sprite = _panelSprite;
            upgBg.type = Image.Type.Sliced;
            upgBg.color = Color.white;

            // Заголовок панели
            GameObject titleObj = new GameObject("HeaderTitle", typeof(RectTransform), typeof(Text), typeof(Shadow));
            titleObj.transform.SetParent(_upgradesPanel.transform, false);
            RectTransform titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -14f);
            titleRect.sizeDelta = new Vector2(-24f, 26f);

            _upgradesHeaderTitle = titleObj.GetComponent<Text>();
            _upgradesHeaderTitle.font = _gameFont;
            _upgradesHeaderTitle.fontSize = 17;
            _upgradesHeaderTitle.fontStyle = FontStyle.Bold;
            _upgradesHeaderTitle.alignment = TextAnchor.MiddleCenter;
            _upgradesHeaderTitle.color = new Color(1f, 0.82f, 0.35f);
            _upgradesHeaderTitle.text = "ОПИСАНИЕ";
            _upgradesHeaderTitle.supportRichText = true;

            Shadow titleShadow = titleObj.GetComponent<Shadow>();
            titleShadow.effectColor = new Color(0f, 0f, 0f, 0.95f);
            titleShadow.effectDistance = new Vector2(1.5f, -1.5f);

            // Подзаголовок: название способности и навык
            GameObject subObj = new GameObject("HeaderSubtitle", typeof(RectTransform), typeof(Text), typeof(Shadow));
            subObj.transform.SetParent(_upgradesPanel.transform, false);
            RectTransform subRect = subObj.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0f, 1f);
            subRect.anchorMax = new Vector2(1f, 1f);
            subRect.pivot = new Vector2(0.5f, 1f);
            subRect.anchoredPosition = new Vector2(0f, -42f);
            subRect.sizeDelta = new Vector2(-24f, 22f);

            _upgradesHeaderSubtitle = subObj.GetComponent<Text>();
            _upgradesHeaderSubtitle.font = _gameFont;
            _upgradesHeaderSubtitle.fontSize = 13;
            _upgradesHeaderSubtitle.fontStyle = FontStyle.Normal;
            _upgradesHeaderSubtitle.alignment = TextAnchor.MiddleCenter;
            _upgradesHeaderSubtitle.color = new Color(0.9f, 0.9f, 0.9f);
            _upgradesHeaderSubtitle.supportRichText = true;

            Shadow subShadow = subObj.GetComponent<Shadow>();
            subShadow.effectColor = new Color(0f, 0f, 0f, 0.95f);
            subShadow.effectDistance = new Vector2(1.2f, -1.2f);

            // Разделительная золотистая линия
            GameObject dividerObj = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            dividerObj.transform.SetParent(_upgradesPanel.transform, false);
            RectTransform divRect = dividerObj.GetComponent<RectTransform>();
            divRect.anchorMin = new Vector2(0f, 1f);
            divRect.anchorMax = new Vector2(1f, 1f);
            divRect.pivot = new Vector2(0.5f, 1f);
            divRect.anchoredPosition = new Vector2(0f, -68f);
            divRect.sizeDelta = new Vector2(-28f, 2f);
            Image divImg = dividerObj.GetComponent<Image>();
            divImg.color = new Color(0.72f, 0.55f, 0.25f, 0.75f);

            // Текстовая область для списка улучшений
            GameObject contentObj = new GameObject("ContentText", typeof(RectTransform), typeof(Text), typeof(Shadow));
            contentObj.transform.SetParent(_upgradesPanel.transform, false);
            RectTransform contentRect = contentObj.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 0f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 0.5f);
            contentRect.offsetMin = new Vector2(18f, 14f);
            contentRect.offsetMax = new Vector2(-18f, -76f);

            _upgradesContentText = contentObj.GetComponent<Text>();
            _upgradesContentText.font = _gameFont;
            _upgradesContentText.fontSize = 13;
            _upgradesContentText.lineSpacing = 1.15f;
            _upgradesContentText.fontStyle = FontStyle.Normal;
            _upgradesContentText.alignment = TextAnchor.UpperLeft;
            _upgradesContentText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _upgradesContentText.verticalOverflow = VerticalWrapMode.Overflow;
            _upgradesContentText.supportRichText = true;
            _upgradesContentText.color = Color.white;

            Shadow contentShadow = contentObj.GetComponent<Shadow>();
            contentShadow.effectColor = new Color(0f, 0f, 0f, 0.95f);
            contentShadow.effectDistance = new Vector2(1f, -1f);

            _menuRoot.SetActive(false);
        }

        public void Show(Player player)
        {
            if (_menuRoot == null || player == null) return;
            _menuRoot.SetActive(true);
            RefreshSlots(player);

            _hoveredIndex = (int)RunescapeMagicPlugin.CurrentSpell;

            // Мгновенная ориентация подсветки на выбранное заклинание при открытии
            float initAngle = 45f - _hoveredIndex * 45f;
            _currentHighlightAngle = initAngle;
            _targetHighlightAngle = initAngle;
            if (_highlightRect != null)
            {
                _highlightRect.localRotation = Quaternion.Euler(0f, 0f, initAngle);
            }

            UpdateSelectionVisuals(player);
            TopModePlateUI.Instance?.OnModeChanged();

            // Разблокируем курсор через менеджер курсора Valheim
            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
        }

        public void Hide(Player player)
        {
            if (_menuRoot == null || !_menuRoot.activeSelf) return;

            if (_hoveredIndex >= 0 && _hoveredIndex < _slots.Count && player != null)
            {
                RadialSlot selected = _slots[_hoveredIndex];
                if (selected.IsUnlocked)
                {
                    RunescapeMagicPlugin.CurrentSpell = selected.Spell;
                    TopModePlateUI.Instance?.OnModeChanged();
                    SpellUIInfo info = GetSpellInfo(player, selected.Spell);
                    player.Message(MessageHud.MessageType.TopLeft, $"Выбрано заклинание: {info.Name}");
                }
                else
                {
                    player.Message(MessageHud.MessageType.Center, "Это заклинание еще не открыто!");
                }
            }

            _menuRoot.SetActive(false);

            // Возвращаем захват курсора игре
            if (GameCamera.instance != null)
            {
                ZCursor.LockState = CursorLockMode.Locked;
                ZCursor.Hide();
            }
        }

        private void Update()
        {
            if (!IsVisible || Player.m_localPlayer == null) return;

            Player player = Player.m_localPlayer;

            // Принудительно удерживаем курсор видимым и свободным для выбора в радиальном меню
            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();

            // Переключение режимов колесиком мыши при открытом меню (удержание ПКМ)
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.01f)
            {
                int dir = scroll > 0f ? 1 : -1;
                int targetIdx = (_hoveredIndex >= 0 && _hoveredIndex < _slots.Count) ? _hoveredIndex : (int)RunescapeMagicPlugin.CurrentSpell;
                if (targetIdx >= 0 && targetIdx < _slots.Count)
                {
                    SpellType spell = _slots[targetIdx].Spell;
                    SpellModeManager.CycleMode(player, spell, dir);
                    TopModePlateUI.Instance?.OnModeChanged();
                    UpdateSelectionVisuals(player);
            TopModePlateUI.Instance?.OnModeChanged();
                }
            }

            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector2 mouseOffset = (Vector2)Input.mousePosition - center;

            // Если курсор отодвинут от центра круга дальше 35 пикселей - выбираем сектор и ориентируем подсветку
            if (mouseOffset.magnitude > 35f)
            {
                float mouseAngle = Mathf.Atan2(mouseOffset.y, mouseOffset.x) * Mathf.Rad2Deg;

                // В sector_highlight.png сектор направлен в правый верхний угол (45 градусов).
                // Поэтому угол поворота = mouseAngle - 45 градусов:
                _targetHighlightAngle = mouseAngle - 45f;

                float clockwiseFromTop = (90f - mouseAngle + 360f + 22.5f) % 360f;
                int sector = (int)(clockwiseFromTop / 45f) % _slots.Count;

                if (sector != _hoveredIndex)
                {
                    _hoveredIndex = sector;
                    UpdateSelectionVisuals(player);
            TopModePlateUI.Instance?.OnModeChanged();
                }
            }
            else
            {
                // В центральной мертвой зоне подсветка мягко центрируется на текущем выбранном слоте
                if (_hoveredIndex >= 0 && _hoveredIndex < _slots.Count)
                {
                    _targetHighlightAngle = 45f - _hoveredIndex * 45f;
                }
            }

            // Плавное вращение подсветки сектора следом за мышкой
            if (_highlightRect != null)
            {
                _currentHighlightAngle = Mathf.LerpAngle(_currentHighlightAngle, _targetHighlightAngle, Time.unscaledDeltaTime * 25f);
                _highlightRect.localRotation = Quaternion.Euler(0f, 0f, _currentHighlightAngle);
            }
        }

        private void RefreshSlots(Player player)
        {
            Sprite frameSprite = GetCustomSprite("Slot_Frame.png");

            foreach (RadialSlot slot in _slots)
            {
                SpellUIInfo info = GetSpellInfo(player, slot.Spell);
                slot.IsUnlocked = info.EffectiveLevel >= info.ReqLevel;

                // Загружаем кастомную иконку способности
                if (SpellIconFiles.TryGetValue(slot.Spell, out string iconFile))
                {
                    Sprite customIcon = GetCustomSprite(iconFile);
                    if (customIcon != null)
                    {
                        slot.Icon.sprite = customIcon;
                    }
                    else
                    {
                        Sprite itemIcon = GetVanillaItemIcon(info.IconPrefab);
                        if (itemIcon != null) slot.Icon.sprite = itemIcon;
                    }
                }

                if (frameSprite != null)
                {
                    slot.Frame.sprite = frameSprite;
                }

                slot.Icon.color = slot.IsUnlocked ? Color.white : new Color(0.35f, 0.35f, 0.35f, 0.6f);
                slot.Frame.color = slot.IsUnlocked ? new Color(0.9f, 0.85f, 0.75f, 0.95f) : new Color(0.5f, 0.45f, 0.4f, 0.6f);
                slot.LockOverlay.gameObject.SetActive(!slot.IsUnlocked);
            }
        }

        private void UpdateSelectionVisuals(Player player)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                bool isHovered = (i == _hoveredIndex);
                RadialSlot slot = _slots[i];

                slot.Rect.localScale = isHovered ? Vector3.one * 1.15f : Vector3.one;

                if (isHovered)
                {
                    slot.Frame.color = slot.IsUnlocked
                        ? new Color(1f, 0.96f, 0.6f, 1f)     // Яркое золото при наведении
                        : new Color(0.95f, 0.4f, 0.4f, 0.95f); // Красный оттенок для заблокированного
                    slot.Icon.color = slot.IsUnlocked
                        ? Color.white
                        : new Color(0.45f, 0.35f, 0.35f, 0.75f);
                }
                else
                {
                    slot.Frame.color = slot.IsUnlocked
                        ? new Color(0.85f, 0.8f, 0.72f, 0.9f)
                        : new Color(0.45f, 0.4f, 0.35f, 0.55f);
                    slot.Icon.color = slot.IsUnlocked
                        ? new Color(0.9f, 0.9f, 0.9f, 0.95f)
                        : new Color(0.35f, 0.35f, 0.35f, 0.6f);
                }
            }

            if (_hoveredIndex >= 0 && _hoveredIndex < _slots.Count)
            {
                SpellUIInfo info = GetSpellInfo(player, _slots[_hoveredIndex].Spell);
                _titleText.text = info.Name.ToUpperInvariant();

                string costStr = $"{info.StaminaCost} ВЫНОСЛ.";
                if (info.HealthCost > 0f)
                    costStr += $"  /  <color=#ff6666>{info.HealthCost} ОЗ</color>";
                _costText.text = costStr;

                SpellModeInfo curMode = SpellModeManager.GetCurrentModeInfo(player, _slots[_hoveredIndex].Spell);
                _statsText.text = $"{info.ExtraStats}  •  <color=#FFDE59>[{curMode.Name}]</color>";

                float bonus = info.EffectiveLevel - info.BaseLevel;
                string bonusStr = bonus > 0f ? $" <color=#66ff66>({info.BaseLevel}+{bonus})</color>" : "";
                bool unlocked = info.EffectiveLevel >= info.ReqLevel;
                string reqColor = unlocked ? "#aaddaa" : "#ff5555";

                string skillRu = GetSkillNameRu(info.Skill);
                _reqText.text = $"<color={reqColor}>ТРЕБ: {skillRu} УР. {info.ReqLevel}\n(ТЕК: {info.EffectiveLevel}{bonusStr})</color>";

                // Обновляем панель улучшений справа от колеса
                if (_upgradesPanel != null)
                {
                    _upgradesHeaderTitle.text = "ОПИСАНИЕ";
                    _upgradesHeaderSubtitle.text = $"<color=#{ColorUtility.ToHtmlStringRGB(info.Tint)}>{info.Name}</color> — {skillRu} ({info.EffectiveLevel} ур.)";

                    System.Text.StringBuilder sb = new System.Text.StringBuilder();

                    // 1. Описание режимов (смена колесиком мыши)
                    sb.AppendLine("<color=#FFD700><b>Описание режимов:</b></color> <color=#A0A0A0><i>(Смена: Колесо мыши)</i></color>");
                    if (SpellModeManager.Modes.TryGetValue(info.Spell, out SpellModeInfo[] modeList))
                    {
                        int activeModeIdx = SpellModeManager.GetCurrentMode(info.Spell);
                        for (int mIdx = 0; mIdx < modeList.Length; mIdx++)
                        {
                            SpellModeInfo mInfo = modeList[mIdx];
                            bool isCurrent = (mIdx == activeModeIdx);
                            bool isUnlocked = mInfo.IsUnlocked(player);

                            if (isCurrent)
                            {
                                sb.AppendLine($"▶ <color=#FFE066><b>{mInfo.Name}</b></color> — <color=#FFF5CC>{mInfo.Description}</color>");
                            }
                            else if (isUnlocked)
                            {
                                sb.AppendLine($"   <color=#C8C8C8><b>{mInfo.Name}</b></color> — <color=#A8A8A8>{mInfo.Description}</color>");
                            }
                            else
                            {
                                sb.AppendLine($"   <color=#666666><b>{mInfo.Name}</b> — {mInfo.Description} <color=#FF6666>[Требуется {mInfo.ReqLevel:0} ур.]</color></color>");
                            }
                        }
                    }

                    sb.AppendLine("\n<color=#A07830>───────────────────────────────────────</color>\n");

                    // 2. Прогресс и улучшения
                    sb.AppendLine("<color=#FFD700><b>Прогресс и улучшения:</b></color>");
                    List<SpellMilestone> milestones = GetSpellMilestones(info.Spell);
                    foreach (var m in milestones)
                    {
                        bool isMilestoneUnlocked = info.EffectiveLevel >= m.Level;
                        string colorHex = isMilestoneUnlocked ? "#FFD700" : "#707070";
                        sb.AppendLine($"<color={colorHex}>• <b>Ур. {m.Level}:</b> {m.Description}</color>");
                    }

                    _upgradesContentText.text = sb.ToString().TrimEnd();

                    // Динамическое расширение высоты панели при большом количестве текста
                    RectTransform upgRect = _upgradesPanel.GetComponent<RectTransform>();
                    if (upgRect != null)
                    {
                        float preferredHeight = _upgradesContentText.preferredHeight;
                        upgRect.sizeDelta = new Vector2(380f, Mathf.Max(520f, preferredHeight + 95f));
                    }
                }
            }
        }

        public static SpellType GetActiveOrHoveredSpell()
        {
            if (IsVisible && Instance != null && Instance._hoveredIndex >= 0 && Instance._hoveredIndex < Instance._slots.Count)
            {
                return Instance._slots[Instance._hoveredIndex].Spell;
            }
            return RunescapeMagicPlugin.CurrentSpell;
        }

        public static SpellUIInfo GetSpellInfo(Player player, SpellType spell)
        {
            SpellUIInfo info = new SpellUIInfo();
            info.Spell = spell;
            int mode = SpellModeManager.GetCurrentMode(spell);

            switch (spell)
            {
                case SpellType.SpectralAxe:
                    FillSkillLevels(player, Skills.SkillType.WoodCutting, ref info);
                    info.Name = "Призрачный топор";
                    info.IconPrefab = "AxeBronze";
                    info.Tint = new Color(0.5f, 1f, 0.9f);
                    info.ReqLevel = 10f;
                    info.StaminaCost = info.EffectiveLevel >= 75f ? 5f : (info.EffectiveLevel >= 35f ? 15f : 25f);
                    info.HealthCost = 0f;
                    float axeRadius = info.EffectiveLevel >= 55f ? 10f : 5f;
                    info.ExtraStats = mode == 1
                        ? $"Кусторез | Дальность: 20м | Радиус: {axeRadius}м"
                        : $"Лесоруб | Дальность: 20м | Радиус: {axeRadius}м";
                    break;

                case SpellType.Splinter:
                    FillSkillLevels(player, Skills.SkillType.WoodCutting, ref info);
                    info.Name = "Расщепление";
                    info.IconPrefab = "Wood";
                    info.Tint = new Color(1f, 0.85f, 0.45f);
                    info.ReqLevel = 25f;
                    info.StaminaCost = info.EffectiveLevel >= 75f ? 15f : 25f;
                    info.HealthCost = (mode == 1 || info.EffectiveLevel >= 55f) ? 0f : 10f;
                    float splRadius = info.EffectiveLevel >= 90f ? 25f : (info.EffectiveLevel >= 35f ? 15f : 10f);
                    info.ExtraStats = mode == 1
                        ? "Точечное расщепление (дерево / пенёк)"
                        : $"Радиус расщепления: {splRadius}м";
                    break;

                case SpellType.OreBlast:
                    FillSkillLevels(player, Skills.SkillType.Pickaxes, ref info);
                    info.Name = "Рудный взрыв";
                    info.IconPrefab = "PickaxeIron";
                    info.Tint = new Color(0.55f, 0.85f, 1f);
                    info.ReqLevel = 0f;
                    float oreRadius = info.EffectiveLevel >= 75f ? 6f : (info.EffectiveLevel >= 35f ? 4f : 2f);
                    if (mode == 1) // Компактный режим
                    {
                        info.StaminaCost = Mathf.Ceil((info.EffectiveLevel >= 55f ? 10f : 25f) * 0.5f);
                        info.HealthCost = 0f;
                        info.ExtraStats = $"Компактный радиус: {oreRadius * 0.5f:0.#}м";
                    }
                    else if (mode == 2) // Режим кирки
                    {
                        info.StaminaCost = info.EffectiveLevel >= 55f ? 5f : 15f;
                        info.HealthCost = 0f;
                        info.ExtraStats = "Точечный удар киркой";
                    }
                    else
                    {
                        info.StaminaCost = info.EffectiveLevel >= 55f ? 10f : 25f;
                        info.HealthCost = 0f;
                        info.ExtraStats = $"Радиус взрыва: {oreRadius}м";
                    }
                    break;

                case SpellType.AreaHarvest:
                    FillSkillLevels(player, Skills.SkillType.Farming, ref info);
                    info.Name = "Сбор в радиусе";
                    info.IconPrefab = "Cultivator";
                    info.Tint = new Color(0.75f, 1f, 0.4f);
                    info.ReqLevel = 0f;
                    info.StaminaCost = info.EffectiveLevel >= 75f ? 5f : (info.EffectiveLevel >= 35f ? 10f : 15f);
                    info.HealthCost = 0f;
                    float harvRadius = info.EffectiveLevel >= 75f ? 15f : (info.EffectiveLevel >= 20f ? 10f : 5f);
                    if (mode == 1)
                        info.ExtraStats = $"Сбор с земли в инвентарь: {harvRadius}м";
                    else if (mode == 2)
                        info.ExtraStats = $"Точечный сбор одного вида: {harvRadius}м";
                    else
                        info.ExtraStats = info.EffectiveLevel >= 50f ? $"Радиус: {harvRadius}м (Автоподбор)" : $"Радиус: {harvRadius}м";
                    break;

                case SpellType.InstantGrow:
                    FillSkillLevels(player, Skills.SkillType.Farming, ref info);
                    info.Name = "Мгновенный рост";
                    info.IconPrefab = "Raspberries";
                    info.Tint = Color.white;
                    info.ReqLevel = 25f;
                    float baseGrowStam = info.EffectiveLevel >= 75f ? 15f : 30f;
                    float baseGrowHp = info.EffectiveLevel >= 90f ? 0f : (info.EffectiveLevel >= 75f ? 5f : 15f);
                    float growRadius = info.EffectiveLevel >= 90f ? 15f : (info.EffectiveLevel >= 50f ? 10f : 6f);
                    if (mode == 1)
                    {
                        info.StaminaCost = Mathf.Ceil(baseGrowStam * 0.5f);
                        info.HealthCost = Mathf.Ceil(baseGrowHp * 0.5f);
                        info.ExtraStats = $"Компактный радиус: {growRadius * 0.5f:0.#}м";
                    }
                    else if (mode == 2)
                    {
                        info.StaminaCost = Mathf.Ceil(baseGrowStam * 0.5f);
                        info.HealthCost = Mathf.Ceil(baseGrowHp * 0.5f);
                        info.ExtraStats = $"Рост культуры одного вида: {growRadius}м";
                    }
                    else
                    {
                        info.StaminaCost = baseGrowStam;
                        info.HealthCost = baseGrowHp;
                        info.ExtraStats = $"Радиус роста: {growRadius}м";
                    }
                    break;

                case SpellType.InventoryRepair:
                    FillSkillLevels(player, Skills.SkillType.Crafting, ref info);
                    info.Name = "Ремонт снаряжения";
                    info.IconPrefab = "ArmorIronChest";
                    info.Tint = new Color(1f, 0.9f, 0.6f);
                    info.ReqLevel = 10f;
                    info.StaminaCost = info.EffectiveLevel >= 65f ? 15f : 30f;
                    info.HealthCost = info.EffectiveLevel >= 85f ? 0f : (info.EffectiveLevel >= 65f ? 10f : 20f);
                    int repPct = info.EffectiveLevel >= 50f ? 100 : (info.EffectiveLevel >= 40f ? 80 : (info.EffectiveLevel >= 30f ? 60 : (info.EffectiveLevel >= 20f ? 40 : 20)));
                    info.ExtraStats = mode == 1
                        ? $"Сундук / стойка / союзник ({repPct}%)"
                        : $"Свой инвентарь ({repPct}% прочности)";
                    break;

                case SpellType.PieceRepair:
                    FillSkillLevels(player, Skills.SkillType.Crafting, ref info);
                    info.Name = "Ремонт построек";
                    info.IconPrefab = "Hammer";
                    info.Tint = new Color(0.95f, 0.75f, 0.5f);
                    info.ReqLevel = 0f;
                    info.StaminaCost = info.EffectiveLevel >= 60f ? 10f : 25f;
                    info.HealthCost = mode == 1 ? 10f : 0f;
                    float repRadius = info.EffectiveLevel >= 80f ? 25f : (info.EffectiveLevel >= 25f ? 15f : 10f);
                    info.ExtraStats = mode == 1
                        ? $"Укрепление до 150%: {repRadius}м"
                        : $"Ремонт построек: {repRadius}м";
                    break;

                case SpellType.Overdrive:
                    FillSkillLevels(player, Skills.SkillType.Crafting, ref info);
                    info.Name = "Овердрайв механизмов";
                    info.IconPrefab = "SurtlingCore";
                    info.Tint = new Color(1f, 0.45f, 0.2f);
                    info.ReqLevel = 30f;
                    float baseOdStam = info.EffectiveLevel >= 75f ? 20f : 40f;
                    float baseOdHp = info.EffectiveLevel >= 90f ? 0f : (info.EffectiveLevel >= 75f ? 10f : 25f);
                    float odRadius = info.EffectiveLevel >= 60f ? 15f : 10f;
                    string countStr = info.EffectiveLevel >= 90f ? "Всё сырьё" : (info.EffectiveLevel >= 60f ? "До 10 ед." : (info.EffectiveLevel >= 45f ? "До 5 ед." : "До 3 ед."));
                    if (mode == 1)
                    {
                        info.StaminaCost = Mathf.Ceil(baseOdStam * 0.5f);
                        info.HealthCost = Mathf.Ceil(baseOdHp * 0.5f);
                        info.ExtraStats = $"Точечный разгон ({countStr})";
                    }
                    else
                    {
                        info.StaminaCost = baseOdStam;
                        info.HealthCost = baseOdHp;
                        info.ExtraStats = $"Радиус: {odRadius}м ({countStr})";
                    }
                    break;
            }

            return info;
        }

        private static string GetSkillNameRu(Skills.SkillType skill)
        {
            switch (skill)
            {
                case Skills.SkillType.WoodCutting: return "РУБКА ЛЕСА";
                case Skills.SkillType.Pickaxes: return "ШАХТЕР";
                case Skills.SkillType.Farming: return "ЗЕМЛЕДЕЛИЕ";
                case Skills.SkillType.Crafting: return "РЕМЕСЛО";
                default: return skill.ToString().ToUpperInvariant();
            }
        }

        private static void FillSkillLevels(Player player, Skills.SkillType skill, ref SpellUIInfo info)
        {
            info.Skill = skill;
            if (player == null) return;
            info.BaseLevel = Mathf.Floor(player.GetSkills().GetSkill(skill).m_level);
            info.EffectiveLevel = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, skill);
        }

        private static Sprite GetVanillaItemIcon(string prefabName)
        {
            if (ObjectDB.instance == null || string.IsNullOrEmpty(prefabName)) return null;

            // Если запрашивается куст малины, проверяем альтернативные ягоды на случай различий в сборках
            string[] candidates = (prefabName == "Raspberries")
                ? new[] { "Raspberries", "Blueberries", "Cloudberry", "Dandelion" }
                : new[] { prefabName };

            foreach (string name in candidates)
            {
                GameObject prefab = ObjectDB.instance.GetItemPrefab(name);
                if (prefab != null && prefab.TryGetComponent(out ItemDrop item) && item.m_itemData != null && item.m_itemData.m_shared != null)
                {
                    if (item.m_itemData.m_shared.m_icons != null && item.m_itemData.m_shared.m_icons.Length > 0)
                        return item.m_itemData.m_shared.m_icons[0];
                }
            }

            return null;
        }

        private static Text CreateText(Transform parent, string name, Vector2 pos, int fontSize, Color color, FontStyle style)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(220f, 48f);

            Text txt = go.GetComponent<Text>();
            txt.font = _gameFont;
            txt.fontSize = fontSize;
            txt.color = color;
            txt.fontStyle = style;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.supportRichText = true;

            Shadow shadow = go.GetComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.95f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);

            return txt;
        }

        private static Sprite CreateCircleSprite(int size, float innerRadiusRatio, float outerRadiusRatio)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] colors = new Color[size * size];
            float center = size * 0.5f;
            float maxR = size * 0.5f;
            float innerR = maxR * innerRadiusRatio;
            float outerR = maxR * outerRadiusRatio;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    float alpha = 0f;

                    if (dist >= innerR && dist <= outerR)
                    {
                        float edgeOuter = Mathf.Clamp01((outerR - dist) / 2f);
                        float edgeInner = innerRadiusRatio > 0f ? Mathf.Clamp01((dist - innerR) / 2f) : 1f;
                        alpha = edgeOuter * edgeInner;
                    }

                    colors[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            tex.SetPixels(colors);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static Sprite CreateLockSprite(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] colors = new Color[size * size];

            for (int i = 0; i < colors.Length; i++)
                colors[i] = Color.clear;

            float cx = size * 0.5f;
            int bodyW = (int)(size * 0.50f);
            int bodyH = (int)(size * 0.40f);
            int bodyBottom = (int)(size * 0.15f);

            // Тело замка
            for (int y = bodyBottom; y < bodyBottom + bodyH; y++)
            {
                for (int x = (int)(cx - bodyW / 2); x <= (int)(cx + bodyW / 2); x++)
                {
                    colors[y * size + x] = Color.white;
                }
            }

            // Дужка замка
            float shackleR = size * 0.22f;
            float shackleInnerR = size * 0.14f;
            float shackleCenterY = bodyBottom + bodyH;

            for (int y = (int)shackleCenterY; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(cx, shackleCenterY));
                    if (dist >= shackleInnerR && dist <= shackleR)
                    {
                        colors[y * size + x] = Color.white;
                    }
                }
            }

            // Замочная скважина
            int holeR = (int)(size * 0.05f);
            float holeCY = bodyBottom + bodyH * 0.6f;
            for (int y = (int)(holeCY - holeR); y <= (int)(holeCY + holeR); y++)
            {
                for (int x = (int)(cx - holeR); x <= (int)(cx + holeR); x++)
                {
                    if (Vector2.Distance(new Vector2(x, y), new Vector2(cx, holeCY)) <= holeR)
                        colors[y * size + x] = Color.clear;
                }
            }

            tex.SetPixels(colors);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static Sprite CreatePanelSprite(int w, int h, int border)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            Color[] colors = new Color[w * h];
            Color borderColor = new Color(0.72f, 0.55f, 0.25f, 0.95f);
            Color innerBorder = new Color(0.35f, 0.25f, 0.12f, 0.85f);
            Color innerColor = new Color(0.10f, 0.08f, 0.06f, 0.96f);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool isOuterBorder = (x < 2 || x >= w - 2 || y < 2 || y >= h - 2);
                    bool isInnerBorder = (x < border || x >= w - border || y < border || y >= h - border);

                    if (isOuterBorder)
                        colors[y * w + x] = borderColor;
                    else if (isInnerBorder)
                        colors[y * w + x] = innerBorder;
                    else
                        colors[y * w + x] = innerColor;
                }
            }

            tex.SetPixels(colors);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }

        public static List<SpellMilestone> GetSpellMilestones(SpellType spell)
        {
            List<SpellMilestone> list = new List<SpellMilestone>();

            switch (spell)
            {
                case SpellType.SpectralAxe:
                    list.Add(new SpellMilestone(10f, "Срубает деревья на 15м (требуется топор в инвентаре)"));
                    list.Add(new SpellMilestone(35f, "Затраты выносливости снижены до 15"));
                    list.Add(new SpellMilestone(55f, "Радиус срубания увеличен до 10 метров"));
                    list.Add(new SpellMilestone(75f, "Затраты выносливости снижены до 5"));
                    break;

                case SpellType.Splinter:
                    list.Add(new SpellMilestone(25f, "Расщепляет бревна в радиусе 10м (требуется топор в инвентаре)"));
                    list.Add(new SpellMilestone(35f, "Радиус расщепления увеличен до 15 метров"));
                    list.Add(new SpellMilestone(55f, "Затраты здоровья убраны (0 ОЗ)"));
                    list.Add(new SpellMilestone(75f, "Затраты выносливости снижены до 15"));
                    list.Add(new SpellMilestone(90f, "Радиус расщепления увеличен до 25 метров"));
                    break;

                case SpellType.OreBlast:
                    list.Add(new SpellMilestone(0f, "Добыча камней в радиусе 2м (требуется кирка в инвентаре)"));
                    list.Add(new SpellMilestone(10f, "Добыча меди и олова (любая кирка)"));
                    list.Add(new SpellMilestone(20f, "Добыча железа (бронзовая кирка или лучше)"));
                    list.Add(new SpellMilestone(30f, "Добыча серебра и обсидиана (железная кирка или лучше)"));
                    list.Add(new SpellMilestone(35f, "Радиус взрыва увеличен до 4 метров"));
                    list.Add(new SpellMilestone(40f, "Добыча черного мрамора (кирка из черного металла+)"));
                    list.Add(new SpellMilestone(50f, "Добыча огнеметалла (пепловая кирка)"));
                    list.Add(new SpellMilestone(55f, "Затраты выносливости снижены до 10"));
                    list.Add(new SpellMilestone(60f, "Добыча редких руд и льда"));
                    list.Add(new SpellMilestone(75f, "Радиус взрыва увеличен до 6 метров"));
                    break;

                case SpellType.AreaHarvest:
                    list.Add(new SpellMilestone(0f, "Сбор растений и ягод в радиусе 5м (15 выносливости)"));
                    list.Add(new SpellMilestone(20f, "Радиус сбора увеличен до 10 метров"));
                    list.Add(new SpellMilestone(35f, "Затраты выносливости снижены до 10"));
                    list.Add(new SpellMilestone(50f, "Автосбор ресурсов сразу в инвентарь"));
                    list.Add(new SpellMilestone(75f, "Радиус увеличен до 15м, затраты снижены до 5"));
                    break;

                case SpellType.InstantGrow:
                    list.Add(new SpellMilestone(25f, "Рост овощей и саженцев (Радиус: 6м, 30 вын., 15 ОЗ)"));
                    list.Add(new SpellMilestone(40f, "Выращивание ячменя, льна, берез и дубов"));
                    list.Add(new SpellMilestone(50f, "Радиус мгновенного роста увеличен до 10 метров"));
                    list.Add(new SpellMilestone(60f, "Выращивание маг-грибов, паффов и пеплоносца"));
                    list.Add(new SpellMilestone(75f, "Затраты снижены до 15 выносливости и 5 ОЗ"));
                    list.Add(new SpellMilestone(90f, "Радиус 15м, затраты здоровья убраны (0 ОЗ)"));
                    break;

                case SpellType.InventoryRepair:
                    list.Add(new SpellMilestone(10f, "Починка любого снаряжения на 20% (30 вын., 20 ОЗ)"));
                    list.Add(new SpellMilestone(20f, "Починка любого снаряжения на 40% прочности"));
                    list.Add(new SpellMilestone(30f, "Починка любого снаряжения на 60% прочности"));
                    list.Add(new SpellMilestone(40f, "Починка любого снаряжения на 80% прочности"));
                    list.Add(new SpellMilestone(50f, "Полная починка любого снаряжения на 100% за применение"));
                    list.Add(new SpellMilestone(65f, "Затраты снижены до 15 выносливости и 10 ОЗ"));
                    list.Add(new SpellMilestone(85f, "Затраты здоровья убраны (0 ОЗ)"));
                    break;

                case SpellType.PieceRepair:
                    list.Add(new SpellMilestone(0f, "Ремонт построек в радиусе 10м (25 вын., нужен верстак)"));
                    list.Add(new SpellMilestone(25f, "Радиус ремонта увеличен до 15 метров"));
                    list.Add(new SpellMilestone(40f, "Ремонт построек без наличия верстака рядом"));
                    list.Add(new SpellMilestone(60f, "Затраты выносливости снижены до 10"));
                    list.Add(new SpellMilestone(80f, "Радиус ремонта увеличен до 25 метров"));
                    break;

                case SpellType.Overdrive:
                    list.Add(new SpellMilestone(30f, "Переработка 3 ед. сырья в механизмах 10м (40 вын., 25 ОЗ)"));
                    list.Add(new SpellMilestone(45f, "Мгновенная переработка до 5 ед. сырья"));
                    list.Add(new SpellMilestone(60f, "Радиус 15м, переработка до 10 ед. сырья"));
                    list.Add(new SpellMilestone(75f, "Затраты снижены до 20 выносливости и 10 ОЗ"));
                    list.Add(new SpellMilestone(90f, "Радиус 15м, переработка ВСЕГО сырья без затрат ОЗ"));
                    break;
            }

            return list;
        }

        #region Custom UI Sprites Loader

        private static readonly Dictionary<string, Sprite> _customSpriteCache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        // Чистая рефлексия загрузки текстур согласно требованиям модерации Thunderstore через AccessTools
        private static System.Reflection.MethodInfo LoadImageMethod { get; } = AccessTools.Method(
            "UnityEngine.ImageConversion:LoadImage",
            new Type[] { typeof(Texture2D), typeof(byte[]) }
        );

        private static bool LoadImageIntoTexture(Texture2D tex, byte[] data)
        {
            if (tex == null || data == null || data.Length == 0) return false;

            try
            {
                if (LoadImageMethod != null)
                {
                    return (bool)LoadImageMethod.Invoke(null, new object[] { tex, data });
                }
            }
            catch (Exception ex)
            {
                RunescapeMagicPlugin.Log.LogError($"[UI] Ошибка выполнения LoadImage: {ex}");
            }

            return false;
        }

        public static Sprite GetCustomSprite(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            if (_customSpriteCache.TryGetValue(fileName, out Sprite cached) && cached != null)
                return cached;

            Sprite loaded = LoadSpriteFromDisk(fileName);
            if (loaded != null)
            {
                _customSpriteCache[fileName] = loaded;
            }
            return loaded;
        }

        private static Sprite LoadSpriteFromDisk(string fileName)
        {
            try
            {
                byte[] data = null;

                // 1. Поиск на диске по всем возможным путям
                string fullPath = FindUIFile(fileName);
                if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                {
                    try
                    {
                        data = File.ReadAllBytes(fullPath);
                    }
                    catch { }
                }

                // 2. Если файл не найден на диске, загружаем из встроенных ресурсов DLL (Embedded Resources)
                if (data == null || data.Length == 0)
                {
                    data = LoadEmbeddedBytes(fileName);
                }

                if (data == null || data.Length == 0)
                {
                    RunescapeMagicPlugin.Log.LogWarning($"[UI] Не найден файл или ресурс иконки: {fileName}");
                    return null;
                }

                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (LoadImageIntoTexture(tex, data))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.filterMode = FilterMode.Bilinear;
                    Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    sprite.name = Path.GetFileNameWithoutExtension(fileName);
                    return sprite;
                }
            }
            catch (Exception ex)
            {
                RunescapeMagicPlugin.Log.LogError($"[UI] Ошибка создания спрайта {fileName}: {ex}");
            }
            return null;
        }

        private static byte[] LoadEmbeddedBytes(string fileName)
        {
            try
            {
                var asm = typeof(SpellRadialMenu).Assembly;
                string resourceName = null;
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        resourceName = name;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(resourceName))
                {
                    using (Stream stream = asm.GetManifestResourceStream(resourceName))
                    {
                        if (stream != null)
                        {
                            byte[] buffer = new byte[stream.Length];
                            stream.Read(buffer, 0, buffer.Length);
                            return buffer;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RunescapeMagicPlugin.Log.LogError($"[UI] Ошибка чтения embedded resource {fileName}: {ex}");
            }
            return null;
        }

        private static string FindUIFile(string fileName)
        {
            // 1. Папка UI или корень рядом со сборкой DLL
            try
            {
                string asmLoc = typeof(SpellRadialMenu).Assembly.Location;
                if (!string.IsNullOrEmpty(asmLoc))
                {
                    string dir = Path.GetDirectoryName(asmLoc);
                    string candidate1 = Path.Combine(dir, "UI", fileName);
                    if (File.Exists(candidate1)) return candidate1;

                    string candidate2 = Path.Combine(dir, fileName);
                    if (File.Exists(candidate2)) return candidate2;
                }
            }
            catch { }

            // 2. В папках плагинов BepInEx
            try
            {
                string pluginDir = BepInEx.Paths.PluginPath;
                if (Directory.Exists(pluginDir))
                {
                    string c1 = Path.Combine(pluginDir, "ZoldanTeam-RunescapeMagic", "UI", fileName);
                    if (File.Exists(c1)) return c1;

                    string c2 = Path.Combine(pluginDir, "ZoldanTeam-RunescapeMagic", fileName);
                    if (File.Exists(c2)) return c2;

                    string c3 = Path.Combine(pluginDir, "RunescapeMagic", "UI", fileName);
                    if (File.Exists(c3)) return c3;

                    string c4 = Path.Combine(pluginDir, "RunescapeMagic", fileName);
                    if (File.Exists(c4)) return c4;
                }
            }
            catch { }

            // 3. Прямой путь к папке разработки
            string devCandidate = Path.Combine(@"C:\Users\ZoldanFrane\Desktop\Проекты GPT\RunescapeMagic in Valheim\UI", fileName);
            if (File.Exists(devCandidate)) return devCandidate;

            return null;
        }

        #endregion
    }

    [HarmonyPatch]
    public static class RadialMenuHudPatches
    {
        [HarmonyPatch(typeof(Hud), nameof(Hud.Awake))]
        [HarmonyPostfix]
        public static void OnHudAwakePostfix(Hud __instance)
        {
            SpellRadialMenu.Init(__instance.m_rootObject.transform);
            TopModePlateUI.Init(__instance.m_rootObject.transform);
        }

        // Блокируем вращение взгляда игрока во время выбора заклинания
        [HarmonyPatch(typeof(Player), nameof(Player.SetMouseLook))]
        [HarmonyPrefix]
        public static bool OnSetMouseLookPrefix()
        {
            return !SpellRadialMenu.IsVisible;
        }

        // Сообщаем системе Valheim, что сейчас активно радиальное меню
        // Это автоматически освобождает курсор в GameCamera.UpdateMouseCapture и блокирует вращение камеры в PlayerController.LateUpdate
        [HarmonyPatch(typeof(Hud), nameof(Hud.InRadial))]
        [HarmonyPostfix]
        public static void OnHudInRadialPostfix(ref bool __result)
        {
            if (SpellRadialMenu.IsVisible)
            {
                __result = true;
            }
        }
    }
}