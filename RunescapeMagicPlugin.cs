using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RunescapeMagicValheim
{
    public enum SpellType
    {
        SpectralAxe,     // 0: Призрачный топор (Верх, 12:00)
        Splinter,        // 1: Расщепление (1:30)
        OreBlast,        // 2: Рудный взрыв (3:00)
        AreaHarvest,     // 3: Сбор в радиусе (4:30)
        InstantGrow,     // 4: Мгновенное выращивание (6:00)
        InventoryRepair, // 5: Ремонт снаряжения (7:30)
        PieceRepair,     // 6: Ремонт построек (9:00)
        Overdrive        // 7: Овердрайв (10:30)
    }

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class RunescapeMagicPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.custom.runescapemagicvalheim";
        public const string PluginName = "Runescape Magic in Valheim";
        public const string PluginVersion = "1.1.2";

        internal static ManualLogSource Log;
        internal static RunescapeMagicPlugin Instance;
        private readonly Harmony _harmony = new Harmony(PluginGUID);

        public static SpellType CurrentSpell = SpellType.SpectralAxe;
        public const string WandPrefabName = "RunescapeMagicWand";

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                TryPatchSeedBedMod(_harmony);
                Log.LogInfo($"{PluginName} v{PluginVersion} успешно загружен!");
            }
            catch (Exception ex)
            {
                Log.LogError($"[RunescapeMagic] Ошибка при инициализации Harmony: {ex}");
            }
        }

        private static void TryPatchSeedBedMod(Harmony harmony)
        {
            try
            {
                Type gType = AccessTools.TypeByName("SeedBed.Functions.Germination");
                if (gType == null) return;

                MethodInfo getGermTime = AccessTools.Method(gType, "GetGerminationTime");
                if (getGermTime != null)
                {
                    harmony.Patch(getGermTime, prefix: new HarmonyMethod(AccessTools.Method(typeof(SeedBedDynamicPatches), nameof(SeedBedDynamicPatches.GetGerminationTimePrefix))));
                }

                MethodInfo rpcTap = AccessTools.Method(gType, "RPC_Tap");
                if (rpcTap != null)
                {
                    harmony.Patch(rpcTap, prefix: new HarmonyMethod(AccessTools.Method(typeof(SeedBedDynamicPatches), nameof(SeedBedDynamicPatches.RpcTapPrefix))));
                }

                MethodInfo rpcAddItem = AccessTools.Method(gType, "RPC_AddItem");
                if (rpcAddItem != null)
                {
                    harmony.Patch(rpcAddItem, prefix: new HarmonyMethod(AccessTools.Method(typeof(SeedBedDynamicPatches), nameof(SeedBedDynamicPatches.RpcAddItemPrefix))));
                }

                Log.LogInfo("[RunescapeMagic] Интеграция с модом SeedBed успешно подключена.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[RunescapeMagic] Не удалось подключить патч к SeedBed: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }

        /// <summary>
        /// Вычисляет эффективный уровень навыка игрока (базовый уровень + бонусы от активных бафов/экипировки).
        /// Автоматически учитывает потерю бафа или снижение навыка после смерти.
        /// </summary>
        public static float GetEffectiveSkillLevel(Player player, Skills.SkillType skillType)
        {
            if (player == null) return 0f;

            Skills skills = player.GetSkills();
            float baseLevel = skills.GetSkill(skillType).m_level;

            // Прогоняем через менеджер статус-эффектов для учета всех бафов/штрафов
            player.GetSEMan().ModifySkillLevel(skillType, ref baseLevel);
            return Mathf.Floor(baseLevel);
        }

        /// <summary>
        /// Проверяет, достаточно ли у игрока ресурсов (выносливости и здоровья) для каста заклинания.
        /// </summary>
        public static bool HasResources(Player player, float staminaCost, float healthCost = 0f)
        {
            if (player == null) return false;

            if (staminaCost > 0f && !player.HaveStamina(staminaCost))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_notenoughstamina");
                return false;
            }

            if (healthCost > 0f)
            {
                if (player.GetHealth() <= healthCost)
                {
                    player.Message(MessageHud.MessageType.Center, "Недостаточно здоровья для сотворения заклинания!");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Проверяет и списывает выносливость и здоровье за каст способности.
        /// Не позволяет игроку случайно убить себя кастом заклинания.
        /// </summary>
        public static bool TryConsumeResources(Player player, float staminaCost, float healthCost = 0f)
        {
            if (!HasResources(player, staminaCost, healthCost))
                return false;

            if (staminaCost > 0f)
                player.UseStamina(staminaCost);

            if (healthCost > 0f)
                player.SetHealth(player.GetHealth() - healthCost);

            return true;
        }
    }

    [HarmonyPatch]
    public static class WandInputPatches
    {
        private static float _lastCastTime = 0f;
        private const float CastCooldown = 0.65f; // Кулдаун между кастами для предотвращения спама

        public static bool IsHoldingMagicWand(Player player)
        {
            if (player == null) return false;

            ItemDrop.ItemData rightItem = player.GetRightItem();
            if (rightItem != null)
            {
                if (rightItem.m_dropPrefab != null && rightItem.m_dropPrefab.name == RunescapeMagicPlugin.WandPrefabName)
                    return true;
                if (rightItem.m_shared != null && rightItem.m_shared.m_name == "Волшебная палочка")
                    return true;
            }

            ItemDrop.ItemData weapon = player.GetCurrentWeapon();
            if (weapon != null)
            {
                if (weapon.m_dropPrefab != null && weapon.m_dropPrefab.name == RunescapeMagicPlugin.WandPrefabName)
                    return true;
                if (weapon.m_shared != null && weapon.m_shared.m_name == "Волшебная палочка")
                    return true;
            }

            return false;
        }

        // Блокируем стандартную стойку блока у персонажа при экипированной палочке
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.IsBlocking))]
        [HarmonyPrefix]
        public static bool OnIsBlockingPrefix(Humanoid __instance, ref bool __result)
        {
            if (__instance == Player.m_localPlayer && IsHoldingMagicWand((Player)__instance))
            {
                __result = false;
                return false;
            }
            return true;
        }

        // Перехват ЛКМ (Применение выбранной способности вместо базового удара оружием)
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        [HarmonyPrefix]
        public static bool OnPrimaryAttackPrefix(Humanoid __instance, Character target, bool secondaryAttack, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || secondaryAttack) return true;

            Player player = (Player)__instance;
            if (!IsHoldingMagicWand(player)) return true;

            // Блокируем каст, если сейчас открыто круговое меню выбора заклинаний
            if (SpellRadialMenu.IsVisible)
            {
                __result = false;
                return false;
            }

            // Защита от мульти-триггера за один клик / зажатие ЛКМ
            if (Time.time - _lastCastTime < CastCooldown)
            {
                __result = false;
                return false;
            }

            _lastCastTime = Time.time;

            // Сбрасываем флаги ввода атаки, чтобы игра не перезапускала каст в следующих кадрах
            player.m_attack = false;
            player.m_attackHold = false;
            player.m_queuedAttackTimer = 0f;

            // Проигрываем красивую анимацию взмаха/каста
            if (player.m_zanim != null)
            {
                player.m_zanim.SetTrigger("swing_club");
            }

            CastSelectedSpell(player, RunescapeMagicPlugin.CurrentSpell);
            __result = true;
            return false;
        }

        // Перехват ПКМ в Player.Update (Открытие радиального меню при зажатии ПКМ и закрытие при отпускании)
        [HarmonyPatch(typeof(Player), nameof(Player.Update))]
        [HarmonyPostfix]
        public static void OnPlayerUpdatePostfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;

            if (!IsHoldingMagicWand(__instance) || !__instance.TakeInput())
            {
                if (SpellRadialMenu.IsVisible)
                    SpellRadialMenu.Instance.Hide(__instance);
                return;
            }

            // Отключаем защитную стойку блока, пока в руках палочка бытовой магии
            __instance.m_blocking = false;

            // Переключение режимов теперь осуществляется при открытом меню (удержание ПКМ) колесиком мыши
            if (Input.GetMouseButtonDown(1))
            {
                SpellRadialMenu.Instance?.Show(__instance);
            }
            else if (Input.GetMouseButtonUp(1) && SpellRadialMenu.IsVisible)
            {
                SpellRadialMenu.Instance?.Hide(__instance);
            }
        }

        private static void CastSelectedSpell(Player player, SpellType spell)
        {
            switch (spell)
            {
                case SpellType.SpectralAxe:
                    SpellMechanics.CastSpectralAxe(player);
                    break;
                case SpellType.Splinter:
                    SpellMechanics.CastSplinter(player);
                    break;
                case SpellType.OreBlast:
                    SpellMechanics.CastOreBlast(player);
                    break;
                case SpellType.AreaHarvest:
                    SpellMechanics.CastAreaHarvest(player);
                    break;
                case SpellType.InstantGrow:
                    SpellMechanics.CastInstantGrow(player);
                    break;
                case SpellType.InventoryRepair:
                    SpellMechanics.CastInventoryRepair(player);
                    break;
                case SpellType.PieceRepair:
                    SpellMechanics.CastPieceRepair(player);
                    break;
                case SpellType.Overdrive:
                    SpellMechanics.CastOverdrive(player);
                    break;
            }
        }
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera))]
    public static class GameCameraScrollPatch
    {
        private static float _savedDistance;

        [HarmonyPrefix]
        public static void Prefix(GameCamera __instance)
        {
            _savedDistance = __instance.m_distance;
        }

        [HarmonyPostfix]
        public static void Postfix(GameCamera __instance)
        {
            if (Player.m_localPlayer != null && WandInputPatches.IsHoldingMagicWand(Player.m_localPlayer))
            {
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                {
                    __instance.m_distance = _savedDistance;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Awake))]
    public static class PlayerRpcPatches
    {
        [HarmonyPostfix]
        public static void OnPlayerAwake(Player __instance)
        {
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.Register<string, float>("RPC_RunescapeRepairInventory", (sender, casterName, repairRatio) =>
                {
                    if (Player.m_localPlayer == null) return;
                    int repaired = 0;
                    foreach (var item in Player.m_localPlayer.GetInventory().GetAllItems())
                    {
                        if (item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability())
                        {
                            float maxDur = item.GetMaxDurability();
                            item.m_durability = Mathf.Min(maxDur, item.m_durability + maxDur * repairRatio);
                            repaired++;
                        }
                    }
                    if (repaired > 0)
                    {
                        SpellMechanics.PlayRepairSound(Player.m_localPlayer.transform.position);
                        Player.m_localPlayer.Message(MessageHud.MessageType.Center, $"{casterName} восстановил прочность вашего снаряжения!");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Полная реализация всех 8 способностей строго по Base.md с учетом эффективных уровней и механик Valheim.
    /// </summary>
    public static class SpellMechanics
    {
        public static readonly Dictionary<string, GameObject> StubToTreePrefab = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Индексирует соответствие пней и префабов деревьев в ZNetScene для мгновенного выращивания.
        /// </summary>
        public static void IndexTreeStubs(ZNetScene znet)
        {
            if (znet == null || znet.m_prefabs == null) return;
            foreach (GameObject prefab in znet.m_prefabs)
            {
                if (prefab == null) continue;
                if (prefab.TryGetComponent<TreeBase>(out TreeBase treeBase))
                {
                    if (treeBase.m_stubPrefab != null)
                    {
                        string stubKey = treeBase.m_stubPrefab.name.ToLowerInvariant();
                        if (!StubToTreePrefab.ContainsKey(stubKey))
                        {
                            StubToTreePrefab[stubKey] = prefab;
                        }
                    }
                }
            }
        }

        public static GameObject ResolveTreePrefabForStub(string stubPrefabName, ZNetScene znet)
        {
            if (string.IsNullOrEmpty(stubPrefabName) || znet == null) return null;

            string cleanName = stubPrefabName.ToLowerInvariant();

            // 1. Из проиндексированного словаря ZNetScene
            if (StubToTreePrefab.TryGetValue(cleanName, out GameObject directPrefab) && directPrefab != null)
                return directPrefab;

            // 2. Из словаря мода Advize_StumpsRegrow (если мод активен)
            try
            {
                Type srType = Type.GetType("Advize_StumpsRegrow.StumpsRegrow, Advize_StumpsRegrow");
                if (srType != null)
                {
                    FieldInfo treesField = srType.GetField("TreesPerStump", BindingFlags.Public | BindingFlags.Static);
                    if (treesField != null && treesField.GetValue(null) is Dictionary<string, List<GameObject>> dict)
                    {
                        if (dict.TryGetValue(stubPrefabName, out List<GameObject> list) && list != null && list.Count > 0)
                            return list[UnityEngine.Random.Range(0, list.Count)];
                    }
                }
            }
            catch { }

            // 3. Fallback по именам стандартных деревьев Valheim
            if (cleanName.Contains("beech")) return znet.GetPrefab("Beech1") ?? znet.GetPrefab("Beech_sapling");
            if (cleanName.Contains("birch")) return znet.GetPrefab("Birch1_aut") ?? znet.GetPrefab("Birch2_aut") ?? znet.GetPrefab("Birch1");
            if (cleanName.Contains("oak")) return znet.GetPrefab("Oak1");
            if (cleanName.Contains("pine")) return znet.GetPrefab("PineTree");
            if (cleanName.Contains("fir")) return znet.GetPrefab("FirTree");
            if (cleanName.Contains("swamp")) return znet.GetPrefab("SwampTree1");
            if (cleanName.Contains("yggdrasil")) return znet.GetPrefab("YggdrasilTree1");
            if (cleanName.Contains("ash")) return znet.GetPrefab("AshTree1");

            return null;
        }

        // ==========================================
        // ПРОВЕРКА ИНСТРУМЕНТОВ В ИНВЕНТАРЕ (КИРКА И ТОПОР)
        // ==========================================
        public static string GetPickaxeTierName(int tier)
        {
            switch (tier)
            {
                case 0: return "Оленья кирка (или любая другая)";
                case 1: return "Бронзовая кирка или лучше";
                case 2: return "Железная кирка или лучше";
                case 3: return "Кирка из черного металла или лучше";
                case 4: return "Пепловая кирка из огнеметалла";
                default: return $"Кирка тира {tier} или выше";
            }
        }

        public static string GetAxeTierName(int tier)
        {
            switch (tier)
            {
                case 0: return "Каменный или кремниевый топор";
                case 1: return "Бронзовый топор или лучше";
                case 2: return "Железный топор или лучше";
                case 3: return "Топор из черного металла или лучше";
                case 4: return "Топор из огнеметалла / пепловый";
                default: return $"Топор тира {tier} или выше";
            }
        }

        public static bool HasPickaxe(Player player, int minToolTier, out string failReason)
        {
            failReason = null;
            if (player == null || player.GetInventory() == null)
            {
                failReason = "Инвентарь недоступен";
                return false;
            }

            int bestTier = -1;
            string bestPickName = null;
            bool hasBrokenMatchingTier = false;

            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_skillType == Skills.SkillType.Pickaxes || item.m_shared.m_damages.m_pickaxe > 0f)
                {
                    if (item.m_shared.m_useDurability && item.m_durability <= 0f)
                    {
                        if (item.m_shared.m_toolTier >= minToolTier)
                            hasBrokenMatchingTier = true;
                        continue;
                    }

                    if (item.m_shared.m_toolTier > bestTier)
                    {
                        bestTier = item.m_shared.m_toolTier;
                        bestPickName = Localization.instance != null ? Localization.instance.Localize(item.m_shared.m_name) : item.m_shared.m_name;
                    }
                }
            }

            if (bestTier == -1)
            {
                if (hasBrokenMatchingTier)
                    failReason = "Кирка в инвентаре сломана!";
                else
                    failReason = "В инвентаре нет кирки!";
                return false;
            }

            if (bestTier < minToolTier)
            {
                failReason = $"Требуется {GetPickaxeTierName(minToolTier)} (в инвентаре: {bestPickName})";
                return false;
            }

            return true;
        }

        public static bool HasAxe(Player player, int minToolTier, out string failReason)
        {
            failReason = null;
            if (player == null || player.GetInventory() == null)
            {
                failReason = "Инвентарь недоступен";
                return false;
            }

            int bestTier = -1;
            string bestAxeName = null;
            bool hasBrokenMatchingTier = false;

            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if ((item.m_shared.m_skillType == Skills.SkillType.Axes || item.m_shared.m_damages.m_chop > 0f) && item.m_shared.m_damages.m_pickaxe <= 0f)
                {
                    if (item.m_shared.m_useDurability && item.m_durability <= 0f)
                    {
                        if (item.m_shared.m_toolTier >= minToolTier)
                            hasBrokenMatchingTier = true;
                        continue;
                    }

                    if (item.m_shared.m_toolTier > bestTier)
                    {
                        bestTier = item.m_shared.m_toolTier;
                        bestAxeName = Localization.instance != null ? Localization.instance.Localize(item.m_shared.m_name) : item.m_shared.m_name;
                    }
                }
            }

            if (bestTier == -1)
            {
                if (hasBrokenMatchingTier)
                    failReason = "Топор в инвентаре сломан!";
                else
                    failReason = "В инвентаре нет топора!";
                return false;
            }

            if (bestTier < minToolTier)
            {
                failReason = $"Требуется {GetAxeTierName(minToolTier)} (в инвентаре: {bestAxeName})";
                return false;
            }

            return true;
        }

        public static float GetMinAxeChopDamage(int toolTier)
        {
            switch (toolTier)
            {
                case 0: return 20f;  // Каменный топор (Stone Axe)
                case 1: return 40f;  // Бронзовый топор (Bronze Axe)
                case 2: return 50f;  // Железный топор (Iron Axe)
                case 3: return 60f;  // Чернометаллический топор (Black Metal Axe)
                default: return 70f; // Топоры поздней игры (Mistlands/Ashlands)
            }
        }

        public static float GetMinPickaxeDamage(int toolTier)
        {
            switch (toolTier)
            {
                case 0: return 18f;  // Роговая кирка (Antler Pickaxe)
                case 1: return 25f;  // Бронзовая кирка (Bronze Pickaxe)
                case 2: return 33f;  // Железная кирка (Iron Pickaxe)
                case 3: return 42f;  // Чернометаллическая кирка (Black Metal Pickaxe)
                default: return 55f; // Кирки поздней игры (Ashlands)
            }
        }

        // ==========================================
        // 1. ДЕРЕВО: ПРИЗРАЧНЫЙ ТОПОР
        // ==========================================
        public static void CastSpectralAxe(Player player)
        {
            if (!HasAxe(player, 0, out string axeReason))
            {
                player.Message(MessageHud.MessageType.Center, axeReason);
                return;
            }

            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.WoodCutting);
            if (level < 10f)
            {
                player.Message(MessageHud.MessageType.Center, $"Требуется 10 ур. Рубки леса (текущий: {level})");
                return;
            }

            float staminaCost = level >= 75f ? 5f : (level >= 35f ? 15f : 25f);
            float radius = level >= 55f ? 10f : 5f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, 0f))
                return;

            RunescapeMagicPlugin.Instance.StartCoroutine(SpectralAxeProjectileRoutine(player, radius, staminaCost));
        }

        private static IEnumerator SpectralAxeProjectileRoutine(Player player, float radius, float staminaCost)
        {
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : player.transform;
            Vector3 currentPos = player.GetEyePoint() + cam.forward * 1.2f;
            Vector3 direction = cam.forward;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) direction = player.transform.forward;
            direction.Normalize();

            // Если земля доступна, сразу выставляем начальную высоту полета (1.4м над поверхностью земли)
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(currentPos, out float initialGroundY))
            {
                currentPos.y = initialGroundY + 1.4f;
            }

            float speed = 12f;
            float maxDistance = 15f;
            float traveled = 0f;
            int axeMode = SpellModeManager.GetCurrentMode(SpellType.SpectralAxe);
            HashSet<TreeBase> hitTrees = new HashSet<TreeBase>();
            HashSet<Destructible> hitDestructibles = new HashSet<Destructible>();
            bool resourcesConsumed = false;

            // Создаем горизонтальный призрачный топор с центром вращения строго на середине рукояти
            GameObject visualAxe = CreateSpectralAxeVisual(currentPos);

            // Проигрываем звук взмаха при запуске
            if (ZNetScene.instance != null)
            {
                GameObject sfxPrefab = ZNetScene.instance.GetPrefab("sfx_axe_swing");
                if (sfxPrefab != null)
                    UnityEngine.Object.Instantiate(sfxPrefab, currentPos, Quaternion.identity);
            }

            while (traveled < maxDistance)
            {
                float step = speed * Time.deltaTime;
                currentPos += direction * step;
                traveled += step;

                // Привязываем высоту полета к рельефу земли
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(currentPos, out float groundY))
                {
                    currentPos.y = Mathf.Lerp(currentPos.y, groundY + 1.4f, Time.deltaTime * 10f);
                }

                if (visualAxe != null)
                {
                    visualAxe.transform.position = currentPos;
                    // Вращаем топор строго в горизонтальной плоскости вокруг вертикальной оси Vector3.up (середина рукояти)
                    visualAxe.transform.Rotate(Vector3.up, 900f * Time.deltaTime, Space.World);
                }

                Collider[] colliders = Physics.OverlapSphere(currentPos, radius);
                foreach (Collider col in colliders)
                {
                    if (col == null) continue;

                    // 1. В режиме лесоруба (axeMode == 0) срубаем только целые стоячие деревья
                    if (axeMode == 0)
                    {
                        TreeBase tree = col.GetComponentInParent<TreeBase>();
                        if (tree != null && !hitTrees.Contains(tree))
                        {
                            if (!HasAxe(player, tree.m_minToolTier, out string axeFail))
                            {
                                continue;
                            }

                            if (!resourcesConsumed)
                            {
                                resourcesConsumed = true;
                                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                            }

                            hitTrees.Add(tree);

                            HitData hit = new HitData();
                            hit.m_damage.m_chop = 999f;
                            hit.m_point = col.bounds.center;
                            hit.m_toolTier = 10;
                            hit.SetAttacker(player);

                            float hits = Mathf.Max(1f, tree.m_health / GetMinAxeChopDamage(tree.m_minToolTier));
                            tree.Damage(hit);
                            player.RaiseSkill(Skills.SkillType.WoodCutting, hits);
                        }
                    }
                    // 2. В режиме кустореза (axeMode == 1) срубаем ТОЛЬКО растительность:
                    // кустарники, вереск, поросль, молодняк (Beech_small / бук, Birch_small / береза, пихты) и старые поваленные декоративные стволы.
                    // Категорически игнорируются:
                    // - Камни, скалы, руды, валуны (Rock, Stone, MineRock)
                    // - Взрослые большие деревья (для них предназначен Режим лесоруба)
                    // - Пеньки деревьев (для них предназначен Точечный режим расщепления)
                    // - Бревна TreeLog (для них предназначено Расщепление)
                    // - Постройки игроков и персонажи
                    else if (axeMode == 1)
                    {
                        if (col.GetComponentInParent<TreeLog>() != null) continue;
                        if (col.GetComponentInParent<Character>() != null) continue;
                        if (col.GetComponentInParent<WearNTear>() != null) continue;
                        if (col.GetComponentInParent<Piece>() != null) continue;
                        if (col.GetComponentInParent<MineRock>() != null || col.GetComponentInParent<MineRock5>() != null) continue;

                        // А) Проверка на TreeBase (молодняк: Beech_small / молодой бук, Birch_small / молодая береза, саженцы)
                        TreeBase tree = col.GetComponentInParent<TreeBase>();
                        if (tree != null)
                        {
                            string tName = tree.gameObject.name.ToLowerInvariant();
                            bool isSmallTree = tName.Contains("small") || tName.Contains("sapling") || tree.m_logPrefab == null;

                            if (isSmallTree && !hitTrees.Contains(tree))
                            {
                                if (!HasAxe(player, tree.m_minToolTier, out string axeFail))
                                    continue;

                                if (!resourcesConsumed)
                                {
                                    resourcesConsumed = true;
                                    RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                                }

                                hitTrees.Add(tree);

                                HitData hit = new HitData();
                                hit.m_damage.m_chop = 999f;
                                hit.m_damage.m_slash = 999f;
                                hit.m_point = col.bounds.center;
                                hit.m_dir = direction;
                                hit.m_toolTier = 10;
                                hit.SetAttacker(player);

                                tree.Damage(hit);
                                player.RaiseSkill(Skills.SkillType.WoodCutting, 1f);
                            }
                            continue;
                        }

                        // Б) Проверка на Destructible (кустарники, бук малый Beech_small, вереск, малые пихты)
                        Destructible dest = col.GetComponentInParent<Destructible>();
                        if (dest != null && !hitDestructibles.Contains(dest))
                        {
                            // Исключаем постройки и существ
                            if (col.GetComponentInParent<WearNTear>() != null || col.GetComponentInParent<Character>() != null)
                                continue;

                            string dName = dest.gameObject.name.ToLowerInvariant();

                            // Исключаем бревна и пеньки деревьев
                            if (col.GetComponentInParent<TreeLog>() != null || dName.Contains("stub") || dName.Contains("stump") || dName.Contains("stubbe"))
                                continue;

                            // Исключаем любые камни, скалы, руды, валуны, кремний и металл
                            if (dName.Contains("rock") || dName.Contains("stone") || dName.Contains("boulder") ||
                                dName.Contains("cliff") || dName.Contains("ore") || dName.Contains("flint") ||
                                dName.Contains("obsidian") || dName.Contains("copper") || dName.Contains("tin") ||
                                dName.Contains("silver") || dName.Contains("iron") || dName.Contains("metal"))
                                continue;

                            // В Valheim камни и минералы имеют иммунитет к рубящему урону (chop) и режущему (slash)
                            if (dest.m_damages.m_chop == HitData.DamageModifier.Immune && dest.m_damages.m_slash == HitData.DamageModifier.Immune)
                                continue;

                            hitDestructibles.Add(dest);
                            if (!resourcesConsumed)
                            {
                                resourcesConsumed = true;
                                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                            }

                            // Наносим рубящий и режущий урон (без ClaimOwnership, чтобы не нарушать доставку RPC_Damage в Valheim)
                            HitData hit = new HitData();
                            hit.m_damage.m_chop = 999f;
                            hit.m_damage.m_slash = 999f;
                            hit.m_point = col.bounds.center;
                            hit.m_dir = direction;
                            hit.m_toolTier = 10;
                            hit.SetAttacker(player);
                            dest.Damage(hit);
                            player.RaiseSkill(Skills.SkillType.WoodCutting, 1f);
                        }
                    }
                }

                yield return null;
            }

            if (visualAxe != null)
                UnityEngine.Object.Destroy(visualAxe);

            if (hitTrees.Count == 0 && hitDestructibles.Count == 0 && player != null)
            {
                player.Message(MessageHud.MessageType.Center, axeMode == 1 ? "Призрачный топор не поразил деревьев или кустарников!" : "Призрачный топор не поразил ни одного дерева!");
            }
        }

        private static GameObject CreateSpectralAxeVisual(Vector3 startPos)
        {
            GameObject root = new GameObject("SpectralAxeProjectile");
            root.transform.position = startPos;
            root.transform.rotation = Quaternion.identity;

            // Ищем префаб боевого топора (BattleaxeCrystal для красивой модели или AxeBronze)
            GameObject axePrefab = ObjectDB.instance != null
                ? (ObjectDB.instance.GetItemPrefab("BattleaxeCrystal") ?? ObjectDB.instance.GetItemPrefab("AxeBronze"))
                : null;

            if (axePrefab != null)
            {
                Transform attach = axePrefab.transform.Find("attach");
                GameObject modelSource = attach != null ? attach.gameObject : axePrefab;

                GameObject modelClone = UnityEngine.Object.Instantiate(modelSource);
                modelClone.SetActive(true);

                // Удаляем коллизии и игровые скрипты с клона модели
                foreach (Collider c in modelClone.GetComponentsInChildren<Collider>(true))
                    UnityEngine.Object.DestroyImmediate(c);
                if (modelClone.TryGetComponent(out Rigidbody rb))
                    UnityEngine.Object.DestroyImmediate(rb);
                if (modelClone.TryGetComponent(out ItemDrop id))
                    UnityEngine.Object.DestroyImmediate(id);
                if (modelClone.TryGetComponent(out ZNetView znv))
                    UnityEngine.Object.DestroyImmediate(znv);

                // Собираем координаты всех вершин в пространстве modelClone
                List<Vector3> vertsList = new List<Vector3>();
                foreach (MeshFilter mf in modelClone.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf != null && mf.sharedMesh != null)
                    {
                        Vector3[] verts = mf.sharedMesh.vertices;
                        Transform t = mf.transform;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            Vector3 worldV = t.TransformPoint(verts[i]);
                            Vector3 localV = modelClone.transform.InverseTransformPoint(worldV);
                            vertsList.Add(localV);
                        }
                    }
                }
                foreach (SkinnedMeshRenderer smr in modelClone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr != null && smr.sharedMesh != null)
                    {
                        Vector3[] verts = smr.sharedMesh.vertices;
                        Transform t = smr.transform;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            Vector3 worldV = t.TransformPoint(verts[i]);
                            Vector3 localV = modelClone.transform.InverseTransformPoint(worldV);
                            vertsList.Add(localV);
                        }
                    }
                }

                GameObject modelHolder = new GameObject("ModelHolder");
                modelHolder.transform.SetParent(root.transform, false);

                float scaleMultiplier = 2.5f;
                Vector3 farthestBladePoint = new Vector3(0.5f, 0f, 0f);

                if (vertsList.Count > 0)
                {
                    Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int i = 0; i < vertsList.Count; i++)
                    {
                        min = Vector3.Min(min, vertsList[i]);
                        max = Vector3.Max(max, vertsList[i]);
                    }

                    Vector3 span = max - min;

                    // 1. Рукоять — самая длинная ось в модели топора
                    int handleAxis = 0;
                    if (span.y > span.x && span.y >= span.z) handleAxis = 1;
                    else if (span.z > span.x && span.z > span.y) handleAxis = 2;

                    // 2. Толщина топора/лезвия — самая узкая ось
                    int thicknessAxis = 0;
                    if (span.y < span.x && span.y <= span.z) thicknessAxis = 1;
                    else if (span.z < span.x && span.z < span.y) thicknessAxis = 2;

                    // 3. Ширина лезвия — оставшаяся ось
                    int bladeAxis = 3 - handleAxis - thicknessAxis;

                    // Середина длины рукояти
                    float handleMid = (min[handleAxis] + max[handleAxis]) * 0.5f;
                    float thicknessMid = (min[thicknessAxis] + max[thicknessAxis]) * 0.5f;

                    // Находим центр оси древка рукояти по нижней части (где лезвие отсутствует)
                    float lowerHandleMax = min[handleAxis] + span[handleAxis] * 0.45f;
                    float bladeHandleCenter = 0f;
                    int shaftCount = 0;
                    for (int i = 0; i < vertsList.Count; i++)
                    {
                        Vector3 v = vertsList[i];
                        float h = (handleAxis == 0) ? v.x : ((handleAxis == 1) ? v.y : v.z);
                        if (h <= lowerHandleMax)
                        {
                            float b = (bladeAxis == 0) ? v.x : ((bladeAxis == 1) ? v.y : v.z);
                            bladeHandleCenter += b;
                            shaftCount++;
                        }
                    }
                    if (shaftCount > 0)
                        bladeHandleCenter /= shaftCount;
                    else
                        bladeHandleCenter = (min[bladeAxis] + max[bladeAxis]) * 0.5f;

                    Vector3 handleCenterPoint = Vector3.zero;
                    if (handleAxis == 0) handleCenterPoint.x = handleMid;
                    else if (handleAxis == 1) handleCenterPoint.y = handleMid;
                    else handleCenterPoint.z = handleMid;

                    if (thicknessAxis == 0) handleCenterPoint.x = thicknessMid;
                    else if (thicknessAxis == 1) handleCenterPoint.y = thicknessMid;
                    else handleCenterPoint.z = thicknessMid;

                    if (bladeAxis == 0) handleCenterPoint.x = bladeHandleCenter;
                    else if (bladeAxis == 1) handleCenterPoint.y = bladeHandleCenter;
                    else handleCenterPoint.z = bladeHandleCenter;

                    // Ориентируем ось толщины вдоль Vector3.up (вертикаль мира),
                    // благодаря чему вся плоскость топора (рукоять и лезвие) ложится СТРОГО ГОРИЗОНТАЛЬНО
                    Vector3 localThicknessDir = Vector3.zero;
                    if (thicknessAxis == 0) localThicknessDir = Vector3.right;
                    else if (thicknessAxis == 1) localThicknessDir = Vector3.up;
                    else localThicknessDir = Vector3.forward;

                    Quaternion toHorizontal = Quaternion.FromToRotation(localThicknessDir, Vector3.up);

                    modelHolder.transform.localRotation = toHorizontal;
                    // Смещаем modelHolder так, чтобы середина рукояти находилась ровно в центре вращения (Vector3.zero)
                    modelHolder.transform.localPosition = -(toHorizontal * (handleCenterPoint * scaleMultiplier));

                    modelClone.transform.SetParent(modelHolder.transform, false);
                    modelClone.transform.localPosition = Vector3.zero;
                    modelClone.transform.localRotation = Quaternion.identity;
                    modelClone.transform.localScale = Vector3.one * scaleMultiplier;

                    // Находим крайнюю точку лезвия для привязки шлейфа
                    float maxDistSq = 0f;
                    Vector3 farthestVert = handleCenterPoint;
                    for (int i = 0; i < vertsList.Count; i++)
                    {
                        float d = (vertsList[i] - handleCenterPoint).sqrMagnitude;
                        if (d > maxDistSq)
                        {
                            maxDistSq = d;
                            farthestVert = vertsList[i];
                        }
                    }
                    farthestBladePoint = farthestVert;
                }
                else
                {
                    // Fallback
                    modelHolder.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    modelHolder.transform.localPosition = new Vector3(0f, 0f, -0.4f * scaleMultiplier);

                    modelClone.transform.SetParent(modelHolder.transform, false);
                    modelClone.transform.localPosition = Vector3.zero;
                    modelClone.transform.localRotation = Quaternion.identity;
                    modelClone.transform.localScale = Vector3.one * scaleMultiplier;
                }

                // Призрачный полупрозрачный материал с эмиссионным бирюзовым свечением
                Shader ghostShader = Shader.Find("Legacy Shaders/Transparent/Specular") ?? Shader.Find("Standard") ?? Shader.Find("Custom/Lit");
                Color ghostColor = new Color(0.2f, 1f, 0.85f, 0.65f);

                foreach (MeshRenderer mr in modelClone.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Material[] newMats = new Material[mr.materials.Length];
                    for (int i = 0; i < newMats.Length; i++)
                    {
                        Shader targetShader = ghostShader ?? mr.materials[i].shader;
                        Material mat = new Material(targetShader);
                        mat.color = ghostColor;
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", new Color(0.15f, 0.85f, 0.7f) * 2.2f);
                        newMats[i] = mat;
                    }
                    mr.materials = newMats;
                }

                // Шлейф лезвия (TrailRenderer) на внешнем кончике топора
                GameObject trailObj = new GameObject("BladeTrail");
                trailObj.transform.SetParent(modelClone.transform, false);
                trailObj.transform.localPosition = farthestBladePoint;

                TrailRenderer trail = trailObj.AddComponent<TrailRenderer>();
                trail.time = 0.25f;
                trail.startWidth = 0.40f;
                trail.endWidth = 0.0f;
                Shader trailShader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
                if (trailShader != null)
                    trail.material = new Material(trailShader);
                trail.startColor = new Color(0.2f, 1f, 0.85f, 0.8f);
                trail.endColor = new Color(0.1f, 0.5f, 1f, 0f);
            }

            // Точечный свет вокруг вращающегося топора
            Light axeLight = root.AddComponent<Light>();
            axeLight.color = new Color(0.2f, 1f, 0.85f);
            axeLight.range = 7f;
            axeLight.intensity = 2.2f;
            axeLight.shadows = LightShadows.None;

            return root;
        }

        // ==========================================
        // 2. ДЕРЕВО: РАСЩЕПЛЕНИЕ
        // ==========================================
        public static void CastSplinter(Player player)
        {
            if (!HasAxe(player, 0, out string axeFail))
            {
                player.Message(MessageHud.MessageType.Center, axeFail);
                return;
            }

            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.WoodCutting);
            if (level < 25f)
            {
                player.Message(MessageHud.MessageType.Center, $"Требуется 25 ур. Рубки леса (текущий: {level})");
                return;
            }

            int mode = SpellModeManager.GetCurrentMode(SpellType.Splinter);
            float radius = level >= 90f ? 25f : (level >= 35f ? 15f : 10f);
            float staminaCost = level >= 75f ? 15f : 25f;
            float healthCost = (mode == 1 || level >= 55f) ? 0f : 10f; // В точечном режиме расход здоровья полностью убирается

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                if (mode == 1)
                {
                    SplinterTargetedAtPoint(player, impactPoint, hitCol, staminaCost);
                }
                else
                {
                    RunescapeMagicPlugin.Instance.StartCoroutine(SplinterRoutine(player, impactPoint, radius, staminaCost, healthCost));
                }
            });
        }

        private static bool IsTreeStump(Destructible dest, GameObject rootObj)
        {
            if (dest == null && rootObj == null) return false;

            // Категорически исключаем живых существ, стоячие деревья и постройки
            if (dest != null && dest.GetComponentInParent<Character>() != null) return false;
            if (rootObj != null && rootObj.GetComponentInParent<TreeBase>() != null) return false;
            if (dest != null && dest.GetComponentInParent<WearNTear>() != null) return false;

            string dName = dest != null ? Utils.GetPrefabName(dest.gameObject).ToLowerInvariant() : "";
            string rName = rootObj != null ? Utils.GetPrefabName(rootObj).ToLowerInvariant() : "";

            // Исключаем любые камни, скалы, руды, металл и кустарники
            if (dName.Contains("rock") || dName.Contains("stone") || dName.Contains("boulder") ||
                dName.Contains("cliff") || dName.Contains("ore") || dName.Contains("flint") ||
                dName.Contains("obsidian") || dName.Contains("copper") || dName.Contains("tin") ||
                dName.Contains("silver") || dName.Contains("iron") || dName.Contains("metal") ||
                dName.Contains("bush") || dName.Contains("shrub") || dName.Contains("sapling") ||
                rName.Contains("rock") || rName.Contains("stone") || rName.Contains("boulder") ||
                rName.Contains("cliff") || rName.Contains("ore") || rName.Contains("flint") ||
                rName.Contains("obsidian") || rName.Contains("copper") || rName.Contains("tin") ||
                rName.Contains("silver") || rName.Contains("iron") || rName.Contains("metal") ||
                rName.Contains("bush") || rName.Contains("shrub") || rName.Contains("sapling"))
            {
                return false;
            }

            // Пни в Valheim имеют имена префабов, содержащие stub, stump или stubbe
            if (dName.Contains("stub") || dName.Contains("stump") || dName.Contains("stubbe") ||
                rName.Contains("stub") || rName.Contains("stump") || rName.Contains("stubbe"))
            {
                return true;
            }

            // Дополнительная проверка типа деструктора
            if (dest != null && dest.GetDestructibleType() == DestructibleType.Tree)
            {
                // Не должно быть иммунитета или игнорирования урона топором (m_chop)
                if (dest.m_damages.m_chop != HitData.DamageModifier.Immune && dest.m_damages.m_chop != HitData.DamageModifier.Ignore)
                {
                    // Убеждаемся, что это не целое дерево без TreeBase
                    if (!dName.Contains("tree") && !dName.Contains("birch") && !dName.Contains("oak") && !dName.Contains("beech"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void SplinterTargetedAtPoint(Player player, Vector3 impactPoint, Collider hitCol, float staminaCost)
        {
            TreeLog targetLog = hitCol != null ? hitCol.GetComponentInParent<TreeLog>() : null;
            Destructible targetDest = hitCol != null ? hitCol.GetComponentInParent<Destructible>() : null;
            GameObject rootObj = hitCol != null ? hitCol.transform.root.gameObject : null;

            if (targetDest != null && !IsTreeStump(targetDest, rootObj))
            {
                targetDest = null;
            }

            // 1. Проверяем объект под прицелом (GetHoverObject)
            if (targetLog == null && targetDest == null && player != null)
            {
                GameObject hover = player.GetHoverObject();
                if (hover != null)
                {
                    targetLog = hover.GetComponentInParent<TreeLog>();
                    Destructible hoverDest = hover.GetComponentInParent<Destructible>();
                    if (hoverDest != null && IsTreeStump(hoverDest, hover.transform.root.gameObject))
                    {
                        targetDest = hoverDest;
                    }
                }
            }

            // 2. Если прямого попадания нет, ищем в радиусе 2.8м от точки падения снаряда
            if (targetLog == null && targetDest == null)
            {
                Collider[] nearCols = Physics.OverlapSphere(impactPoint, 2.8f);
                foreach (Collider c in nearCols)
                {
                    if (c == null) continue;
                    targetLog = c.GetComponentInParent<TreeLog>();
                    if (targetLog != null) break;

                    Destructible nearDest = c.GetComponentInParent<Destructible>();
                    if (nearDest != null && IsTreeStump(nearDest, c.transform.root.gameObject))
                    {
                        targetDest = nearDest;
                        break;
                    }
                }
            }

            if (targetLog != null)
            {
                if (!HasAxe(player, targetLog.m_minToolTier, out string axeFail))
                {
                    player.Message(MessageHud.MessageType.Center, axeFail);
                    return;
                }
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                RunescapeMagicPlugin.Instance.StartCoroutine(SplinterSingleLogRoutine(player, targetLog));
            }
            else if (targetDest != null)
            {
                if (!HasAxe(player, targetDest.m_minToolTier, out string axeFail))
                {
                    player.Message(MessageHud.MessageType.Center, axeFail);
                    return;
                }

                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                if (targetDest.m_nview != null && targetDest.m_nview.IsValid()) targetDest.m_nview.ClaimOwnership();
                HitData hit = new HitData();
                hit.m_damage.m_chop = 999f;
                hit.m_point = targetDest.transform.position;
                hit.m_toolTier = 10;
                hit.SetAttacker(player);
                targetDest.Damage(hit);
                player.RaiseSkill(Skills.SkillType.WoodCutting, 1f);
                player.Message(MessageHud.MessageType.Center, "Пенёк точечно расщеплен!");
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, "Прицельтесь в бревно или пенёк для точечного расщепления!");
            }
        }

        private static IEnumerator SplinterSingleLogRoutine(Player player, TreeLog log)
        {
            if (log == null) yield break;
            float hits = Mathf.Max(1f, log.m_health / GetMinAxeChopDamage(log.m_minToolTier));
            HitData hit = new HitData();
            hit.m_damage.m_chop = 999f;
            hit.m_point = log.transform.position;
            hit.m_toolTier = 10;
            hit.SetAttacker(player);
            log.Damage(hit);
            player.RaiseSkill(Skills.SkillType.WoodCutting, hits);

            yield return new WaitForSeconds(0.35f);

            if (log != null)
            {
                log.Damage(hit);
            }
            player.Message(MessageHud.MessageType.Center, "Бревно точечно расщеплено!");
        }

        private static IEnumerator SplinterRoutine(Player player, Vector3 center, float radius, float staminaCost, float healthCost)
        {
            bool resourcesConsumed = false;
            int logsHit = DamageLogsInRadius(player, center, radius, staminaCost, healthCost, ref resourcesConsumed);
            yield return new WaitForSeconds(1.0f);
            DamageLogsInRadius(player, center, radius + 2f, staminaCost, healthCost, ref resourcesConsumed);

            if (logsHit == 0 && !resourcesConsumed && player != null)
            {
                player.Message(MessageHud.MessageType.Center, "В выбранной зоне нет упавших бревен!");
            }
        }

        private static int DamageLogsInRadius(Player player, Vector3 center, float radius, float staminaCost, float healthCost, ref bool resourcesConsumed)
        {
            Collider[] colliders = Physics.OverlapSphere(center, radius);
            HashSet<TreeLog> hitLogs = new HashSet<TreeLog>();

            foreach (Collider col in colliders)
            {
                if (col == null) continue;
                TreeLog log = col.GetComponentInParent<TreeLog>();
                if (log != null && !hitLogs.Contains(log))
                {
                    if (!HasAxe(player, log.m_minToolTier, out string axeFail))
                    {
                        continue;
                    }

                    if (!resourcesConsumed)
                    {
                        resourcesConsumed = true;
                        RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost);
                    }

                    hitLogs.Add(log);

                    HitData hit = new HitData();
                    hit.m_damage.m_chop = 999f;
                    hit.m_point = col.bounds.center;
                    hit.m_toolTier = 10;
                    hit.SetAttacker(player);

                    float hits = Mathf.Max(1f, log.m_health / GetMinAxeChopDamage(log.m_minToolTier));
                    log.Damage(hit);
                    player.RaiseSkill(Skills.SkillType.WoodCutting, hits);
                }
            }

            return hitLogs.Count;
        }

        // ==========================================
        // 3. РУДА: РУДНЫЙ ВЗРЫВ
        // ==========================================
        private class BlastChunk
        {
            public float Distance;
            public float XpGain;
            public Action DestroyAction;
        }

        private static readonly MethodInfo _mr5UpdateMeshMethod = typeof(MineRock5).GetMethod("UpdateMesh", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static void ForceUpdateMineRock5(MineRock5 mineRock5)
        {
            if (mineRock5 == null) return;
            try
            {
                _mr5UpdateMeshMethod?.Invoke(mineRock5, null);
            }
            catch (Exception ex)
            {
                RunescapeMagicPlugin.Log.LogWarning($"[MineRock5] Ошибка принудительного обновления меша: {ex}");
            }
        }

        private static void SpawnExplosionEffect(Vector3 center, float radius)
        {
            try
            {
                // 1. Вспышка света от магического взрыва
                GameObject flashObj = new GameObject("OreBlastFlash");
                flashObj.transform.position = center;
                Light flashLight = flashObj.AddComponent<Light>();
                flashLight.color = new Color(1f, 0.65f, 0.2f);
                flashLight.intensity = 4.5f;
                flashLight.range = Mathf.Max(radius * 1.5f, 8f);
                UnityEngine.Object.Destroy(flashObj, 0.35f);

                // 2. Аутентичные визуальные эффекты и звук взрыва камней
                if (ZNetScene.instance != null)
                {
                    GameObject vfx = ZNetScene.instance.GetPrefab("vfx_rock_destroyed") ?? ZNetScene.instance.GetPrefab("vfx_stone_destroyed");
                    if (vfx != null)
                    {
                        UnityEngine.Object.Instantiate(vfx, center, Quaternion.identity);
                    }
                    GameObject sfx = ZNetScene.instance.GetPrefab("sfx_rock_destroyed") ?? ZNetScene.instance.GetPrefab("sfx_stone_destroyed");
                    if (sfx != null)
                    {
                        UnityEngine.Object.Instantiate(sfx, center, Quaternion.identity);
                    }
                }
            }
            catch { }
        }

        public static void CastOreBlast(Player player)
        {
            if (!HasPickaxe(player, 0, out string pickaxeFail))
            {
                player.Message(MessageHud.MessageType.Center, pickaxeFail);
                return;
            }

            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.Pickaxes);
            int mode = SpellModeManager.GetCurrentMode(SpellType.OreBlast);
            float radius = level >= 75f ? 6f : (level >= 35f ? 4f : 2f);
            float staminaCost = level >= 55f ? 10f : 25f;

            if (mode == 1) // Компактный режим: уменьшенный вдвое радиус и расход
            {
                radius *= 0.5f;
                staminaCost *= 0.5f;
            }
            else if (mode == 2) // Режим кирки: точечное разрушение (15 на старте, 5 на 55 ур.)
            {
                staminaCost = level >= 55f ? 5f : 15f;
            }

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, 0f))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                if (mode == 2)
                {
                    OreBlastPrecisionAtPoint(player, impactPoint, hitCol, level, staminaCost);
                }
                else
                {
                    RunescapeMagicPlugin.Instance.StartCoroutine(OreBlastWaveRoutine(player, impactPoint, radius, level, hitCol, staminaCost));
                }
            });
        }

        private static void OreBlastPrecisionAtPoint(Player player, Vector3 impactPoint, Collider hitCol, float skillLevel, float staminaCost)
        {
            if (hitCol == null)
            {
                player.Message(MessageHud.MessageType.Center, "Под прицелом нет руды или камня!");
                return;
            }

            if (hitCol.GetComponentInParent<Character>() != null)
            {
                player.Message(MessageHud.MessageType.Center, "Под прицелом нет руды или камня!");
                return;
            }

            IDestructible destructible = hitCol.GetComponentInParent<IDestructible>();
            if (destructible == null)
            {
                player.Message(MessageHud.MessageType.Center, "Под прицелом нет руды или камня!");
                return;
            }

            GameObject rootObj = ((MonoBehaviour)destructible).gameObject;
            string prefabName = Utils.GetPrefabName(rootObj).ToLowerInvariant();

            if (IsVegetation(destructible, rootObj, prefabName))
            {
                player.Message(MessageHud.MessageType.Center, "Под прицелом растительность, а не руда!");
                return;
            }

            if (!CanMineOre(prefabName, skillLevel, player, destructible, out string blockedReason))
            {
                if (!string.IsNullOrEmpty(blockedReason))
                    player.Message(MessageHud.MessageType.Center, blockedReason);
                return;
            }

            if (destructible is MineRock5 mineRock5)
            {
                int areaIndex = mineRock5.GetAreaIndex(hitCol);
                var area = areaIndex >= 0 ? mineRock5.GetHitArea(areaIndex) : null;
                if (area != null && area.m_health > 0f)
                {
                    RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                    DamageText.instance?.ShowText(HitData.DamageModifier.Normal, hitCol.bounds.center, 9999f, false);
                    area.m_health = 0f;
                    if (mineRock5.m_nview != null && mineRock5.m_nview.IsValid())
                    {
                        mineRock5.m_nview.ClaimOwnership();
                        mineRock5.SaveHealth();
                        mineRock5.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_SetAreaHealth", areaIndex, 0f);
                    }
                    hitCol.gameObject.SetActive(false);
                    SpawnExplosionEffect(hitCol.bounds.center, 1.5f);
                    float chunkHealth = mineRock5.m_health > 0f ? mineRock5.m_health : 50f;
                    float xp = Mathf.Max(1f, chunkHealth / GetMinPickaxeDamage(mineRock5.m_minToolTier));
                    player.RaiseSkill(Skills.SkillType.Pickaxes, xp);
                    player.Message(MessageHud.MessageType.Center, "Секция жилы точечно добыта!");
                    return;
                }
            }

            if (destructible is Destructible dest)
            {
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                if (dest.m_nview != null && dest.m_nview.IsValid()) dest.m_nview.ClaimOwnership();
                HitData hit = new HitData();
                hit.m_damage.m_pickaxe = 999f;
                hit.m_point = hitCol.bounds.center;
                hit.m_toolTier = 10;
                hit.SetAttacker(player);
                dest.Damage(hit);
                SpawnExplosionEffect(hitCol.bounds.center, 1.5f);
                player.RaiseSkill(Skills.SkillType.Pickaxes, 2f);
                player.Message(MessageHud.MessageType.Center, "Камень/жила точечно добыта!");
                return;
            }

            if (destructible is MineRock mineRock)
            {
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                if (mineRock.m_nview != null && mineRock.m_nview.IsValid()) mineRock.m_nview.ClaimOwnership();
                HitData hit = new HitData();
                hit.m_damage.m_pickaxe = 999f;
                hit.m_point = hitCol.bounds.center;
                hit.m_toolTier = 10;
                hit.SetAttacker(player);
                mineRock.Damage(hit);
                SpawnExplosionEffect(hitCol.bounds.center, 1.5f);
                player.RaiseSkill(Skills.SkillType.Pickaxes, 2f);
                player.Message(MessageHud.MessageType.Center, "Скала точечно добыта!");
                return;
            }

            player.Message(MessageHud.MessageType.Center, "Под прицелом нет руды или камня!");
        }

        private static bool IsVegetation(IDestructible destructible, GameObject rootObj, string prefabName)
        {
            if (destructible != null && destructible.GetDestructibleType() == DestructibleType.Tree)
                return true;

            if (string.IsNullOrEmpty(prefabName)) return false;

            return prefabName.Contains("bush") || prefabName.Contains("shrub") || prefabName.Contains("berry")
                || prefabName.Contains("flower") || prefabName.Contains("grass") || prefabName.Contains("plant")
                || prefabName.Contains("stub") || prefabName.Contains("branch") || prefabName.Contains("tree")
                || prefabName.Contains("vine") || prefabName.Contains("reed") || prefabName.Contains("carrot")
                || prefabName.Contains("turnip") || prefabName.Contains("onion") || prefabName.Contains("barley")
                || prefabName.Contains("flax") || prefabName.Contains("dandelion") || prefabName.Contains("mushroom")
                || prefabName.Contains("cloudberry") || prefabName.Contains("blueberry") || prefabName.Contains("raspberry")
                || prefabName.Contains("sapling") || prefabName.Contains("vines");
        }

        private static IEnumerator OreBlastWaveRoutine(Player player, Vector3 center, float radius, float skillLevel, Collider directHitCol, float staminaCost)
        {
            Collider[] sphereColliders = Physics.OverlapSphere(center, radius);
            List<Collider> colliders = new List<Collider>(sphereColliders);
            if (directHitCol != null && !colliders.Contains(directHitCol))
            {
                colliders.Add(directHitCol);
            }

            List<BlastChunk> blastChunks = new List<BlastChunk>();
            HashSet<Destructible> processedDestructibles = new HashSet<Destructible>();
            HashSet<MineRock5> processedMineRock5 = new HashSet<MineRock5>();
            HashSet<MineRock> processedMineRock = new HashSet<MineRock>();
            int rocksCount = 0;

            foreach (Collider col in colliders)
            {
                if (col == null) continue;

                // Защита персонажей, мобов (в т.ч. каменных големов), животных, питомцев и игроков от любого воздействия рудного взрыва
                if (col.GetComponentInParent<Character>() != null) continue;
                if (col.GetComponentInParent<BaseAI>() != null) continue;
                if (col.GetComponentInParent<MonsterAI>() != null) continue;
                if (col.GetComponentInParent<AnimalAI>() != null) continue;
                if (col.GetComponentInParent<Tameable>() != null) continue;

                IDestructible destructible = col.GetComponentInParent<IDestructible>();
                if (destructible == null) continue;

                if (destructible is Character) continue;
                try
                {
                    if (destructible.GetDestructibleType() == DestructibleType.Character) continue;
                }
                catch { }

                GameObject rootObj = ((MonoBehaviour)destructible).gameObject;
                string prefabName = Utils.GetPrefabName(rootObj).ToLowerInvariant();

                // Фильтр растительности: кусты, трава, деревья, пни полностью игнорируются рудным взрывом
                if (IsVegetation(destructible, rootObj, prefabName))
                    continue;

                if (!CanMineOre(prefabName, skillLevel, player, destructible, out string blockedReason))
                {
                    if (col == directHitCol && !string.IsNullOrEmpty(blockedReason))
                    {
                        player.Message(MessageHud.MessageType.Center, blockedReason);
                    }
                    continue;
                }

                // 1. Большие составные скалы и жилы (MineRock5)
                if (destructible is MineRock5 mineRock5)
                {
                    if (processedMineRock5.Contains(mineRock5)) continue;
                    processedMineRock5.Add(mineRock5);
                    rocksCount++;

                    if (mineRock5.m_hitAreas != null)
                    {
                        for (int i = 0; i < mineRock5.m_hitAreas.Count; i++)
                        {
                            var area = mineRock5.GetHitArea(i);
                            if (area == null || area.m_health <= 0f) continue;

                            Collider chunkCol = area.m_collider;
                            Vector3 chunkPos = (chunkCol != null)
                                ? chunkCol.bounds.center
                                : (mineRock5.transform.position + area.m_bound.m_pos);

                            float dist = Vector3.Distance(center, chunkPos);
                            bool inRange = (dist <= radius + 0.8f) || (chunkCol != null && (chunkCol == directHitCol || colliders.Contains(chunkCol)));

                            if (inRange)
                            {
                                int chunkIndex = i;
                                Vector3 hitPos = chunkPos;
                                float chunkHealth = mineRock5.m_health > 0f ? mineRock5.m_health : 50f;
                                float chunkXp = Mathf.Max(1f, chunkHealth / GetMinPickaxeDamage(mineRock5.m_minToolTier));

                                blastChunks.Add(new BlastChunk
                                {
                                    Distance = dist,
                                    XpGain = chunkXp,
                                    DestroyAction = () =>
                                    {
                                        if (mineRock5 == null || mineRock5.m_allDestroyed) return;
                                        if (area.m_health <= 0f) return;

                                        // 1. Отображаем цифры урона
                                        DamageText.instance?.ShowText(HitData.DamageModifier.Normal, hitPos, 9999f, false);

                                        // 2. Обнуляем здоровье секции локально
                                        area.m_health = 0f;

                                        // 3. Сетевая синхронизация: захват владения ZDO, сохранение и рассылка всем игрокам
                                        if (mineRock5.m_nview != null && mineRock5.m_nview.IsValid())
                                        {
                                            mineRock5.m_nview.ClaimOwnership();
                                            mineRock5.SaveHealth();
                                            mineRock5.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_SetAreaHealth", chunkIndex, 0f);
                                        }

                                        // 4. Отключаем коллайдер разрушенной секции
                                        if (chunkCol != null)
                                        {
                                            chunkCol.gameObject.SetActive(false);
                                        }

                                        // 5. Визуальный эффект разрушения и звук камня
                                        mineRock5.m_destroyedEffect?.Create(hitPos, Quaternion.identity);

                                        // 6. Выпадение добычи (руда, камень)
                                        if (mineRock5.m_dropItems != null)
                                        {
                                            List<GameObject> dropList = mineRock5.m_dropItems.GetDropList();
                                            foreach (GameObject drop in dropList)
                                            {
                                                Vector3 dropPos = hitPos + Vector3.up * 0.2f + UnityEngine.Random.insideUnitSphere * 0.3f;
                                                ItemDrop id = UnityEngine.Object.Instantiate(drop, dropPos, Quaternion.identity)?.GetComponent<ItemDrop>();
                                                if (id != null) ItemDrop.OnCreateNew(id.gameObject, false);
                                            }
                                        }

                                        // 7. Обновляем визуальный меш скалы (удаляя разрушенный кусок)
                                        mineRock5.UpdateMesh();

                                        // 8. Проверка висящих кусков
                                        if (mineRock5.m_supportCheck)
                                        {
                                            mineRock5.CheckSupport();
                                        }

                                        // 9. Если все секции разрушены — уничтожаем объект скалы
                                        if (mineRock5.AllDestroyed())
                                        {
                                            mineRock5.m_allDestroyed = true;
                                            if (mineRock5.m_nview != null && mineRock5.m_nview.IsValid())
                                                mineRock5.m_nview.Destroy();
                                            else
                                                UnityEngine.Object.Destroy(mineRock5.gameObject);
                                        }
                                    }
                                });
                            }
                        }
                    }
                }
                // 2. Старый тип больших скал (MineRock)
                else if (destructible is MineRock mineRock)
                {
                    if (processedMineRock.Contains(mineRock)) continue;
                    processedMineRock.Add(mineRock);
                    rocksCount++;

                    if (mineRock.m_hitAreas != null)
                    {
                        for (int i = 0; i < mineRock.m_hitAreas.Length; i++)
                        {
                            Collider areaCol = mineRock.m_hitAreas[i];
                            if (areaCol == null) continue;

                            Vector3 chunkPos = areaCol.bounds.center;
                            float dist = Vector3.Distance(center, chunkPos);
                            bool inRange = (dist <= radius + 0.8f) || (areaCol == directHitCol) || colliders.Contains(areaCol);

                            if (inRange)
                            {
                                int chunkIdx = i;
                                Vector3 hitPos = chunkPos;
                                float chunkHealth = mineRock.m_health > 0f ? mineRock.m_health : 50f;
                                float chunkXp = Mathf.Max(1f, chunkHealth / GetMinPickaxeDamage(mineRock.m_minToolTier));
                                blastChunks.Add(new BlastChunk
                                {
                                    Distance = dist,
                                    XpGain = chunkXp,
                                    DestroyAction = () =>
                                    {
                                        if (mineRock == null) return;

                                        // 1. Отображаем цифры урона
                                        DamageText.instance?.ShowText(HitData.DamageModifier.Normal, hitPos, 9999f, false);

                                        // 2. Сетевая синхронизация скрытия куска (RPC_Hide) и запись в ZDO
                                        if (mineRock.m_nview != null && mineRock.m_nview.IsValid())
                                        {
                                            mineRock.m_nview.ClaimOwnership();
                                            mineRock.m_nview.GetZDO()?.Set("Health" + chunkIdx, 0f);
                                            mineRock.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_Hide", chunkIdx);
                                        }

                                        // 3. Локальное отключение коллайдера и переключение моделей
                                        if (areaCol != null) areaCol.gameObject.SetActive(false);
                                        if (mineRock.m_baseModel != null && mineRock.m_baseModel.activeSelf)
                                        {
                                            mineRock.m_baseModel.SetActive(false);
                                            if (mineRock.m_areaMeshes != null)
                                            {
                                                foreach (var mList in mineRock.m_areaMeshes)
                                                    if (mList != null)
                                                        foreach (var mr in mList)
                                                            if (mr != null) mr.enabled = true;
                                            }
                                        }

                                        // 4. Эффекты разрушения и выпадение лута
                                        mineRock.m_destroyedEffect?.Create(hitPos, Quaternion.identity);
                                        if (mineRock.m_dropItems != null)
                                        {
                                            List<GameObject> dropList = mineRock.m_dropItems.GetDropList();
                                            foreach (GameObject drop in dropList)
                                            {
                                                Vector3 dropPos = hitPos + Vector3.up * 0.2f + UnityEngine.Random.insideUnitSphere * 0.3f;
                                                ItemDrop id = UnityEngine.Object.Instantiate(drop, dropPos, Quaternion.identity)?.GetComponent<ItemDrop>();
                                                if (id != null) ItemDrop.OnCreateNew(id.gameObject, false);
                                            }
                                        }

                                        // 5. Если все разрушено
                                        if (mineRock.m_removeWhenDestroyed && mineRock.AllDestroyed())
                                        {
                                            if (mineRock.m_nview != null && mineRock.m_nview.IsValid())
                                                mineRock.m_nview.Destroy();
                                            else
                                                UnityEngine.Object.Destroy(mineRock.gameObject);
                                        }
                                    }
                                });
                            }
                        }
                    }
                }
                // 3. Обычные камни и валуны (Destructible)
                else if (destructible is Destructible dest)
                {
                    if (processedDestructibles.Contains(dest)) continue;

                    // Убеждаемся, что объект восприимчив к кирке (камни, рудные куски)
                    if (dest.m_damages.m_pickaxe == HitData.DamageModifier.Ignore || dest.m_damages.m_pickaxe == HitData.DamageModifier.Immune)
                        continue;

                    processedDestructibles.Add(dest);
                    rocksCount++;

                    float dist = Vector3.Distance(center, col.bounds.center);
                    float chunkHealth = dest.m_health > 0f ? dest.m_health : 40f;
                    float chunkXp = Mathf.Max(1f, chunkHealth / GetMinPickaxeDamage(dest.m_minToolTier));
                    blastChunks.Add(new BlastChunk
                    {
                        Distance = dist,
                        XpGain = chunkXp,
                        DestroyAction = () =>
                        {
                            if (dest == null) return;
                            if (dest.m_nview != null && dest.m_nview.IsValid())
                                dest.m_nview.ClaimOwnership();

                            HitData hit = new HitData();
                            hit.m_damage.m_pickaxe = 9999f;
                            hit.m_point = col.bounds.center;
                            hit.m_hitCollider = col;
                            hit.m_toolTier = 10;
                            hit.SetAttacker(player);

                            dest.Damage(hit);
                        }
                    });
                }
            }

            if (blastChunks.Count == 0)
            {
                if (player != null)
                {
                    player.Message(MessageHud.MessageType.Center, "В этой зоне нет доступной породы или руды!");
                }
                yield break;
            }

            if (!RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f))
            {
                yield break;
            }

            // Воспроизводим громкий и видимый взрыв строго в точке прицела
            SpawnExplosionEffect(center, radius);

            // Сортируем ВСЕ секции строго по расстоянию от точки прицела:
            // Ближайшие к точке прицеливания на поверхности разрушаются ПЕРВЫМИ,
            // а взрывная волна распространяется от центра наружу и вглубь!
            blastChunks.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            float totalMiningXp = 0f;
            int countInBatch = 0;
            foreach (var chunk in blastChunks)
            {
                chunk.DestroyAction?.Invoke();
                totalMiningXp += chunk.XpGain;
                countInBatch++;
                if (countInBatch >= 4)
                {
                    countInBatch = 0;
                    yield return new WaitForSeconds(0.02f);
                }
            }

            // Принудительно обновляем сетку всех затронутых составных скал, чтобы визуально удалить разрушенные куски
            foreach (MineRock5 mr5 in processedMineRock5)
            {
                if (mr5 != null && !mr5.m_allDestroyed)
                {
                    mr5.UpdateMesh();
                    if (mr5.m_supportCheck)
                    {
                        mr5.CheckSupport();
                    }
                }
            }

            // Начисляем опыт кирки, равный сумме ударов минимальной кирки по каждой разрушенной секции
            if (rocksCount > 0 && totalMiningXp > 0f)
            {
                player.RaiseSkill(Skills.SkillType.Pickaxes, totalMiningXp);
                player.Message(MessageHud.MessageType.Center, $"Разрушено залежей/камней: {rocksCount}");
            }
        }

        private static bool CanMineOre(string prefabName, float level, Player player, IDestructible destructible, out string reason)
        {
            reason = null;

            int minToolTier = 0;
            if (destructible is MineRock5 mr5) minToolTier = Mathf.Max(minToolTier, mr5.m_minToolTier);
            else if (destructible is MineRock mr) minToolTier = Mathf.Max(minToolTier, mr.m_minToolTier);
            else if (destructible is Destructible dest) minToolTier = Mathf.Max(minToolTier, dest.m_minToolTier);

            if (prefabName.Contains("tin") || prefabName.Contains("copper"))
            {
                if (level < 10f) { reason = "Требуется 10 ур. Шахтера для меди и олова"; return false; }
                minToolTier = Mathf.Max(minToolTier, 0);
            }
            else if (prefabName.Contains("mudpile") || prefabName.Contains("iron"))
            {
                if (level < 20f) { reason = "Требуется 20 ур. Шахтера для железа"; return false; }
                minToolTier = Mathf.Max(minToolTier, 1);
            }
            else if (prefabName.Contains("silver") || prefabName.Contains("obsidian"))
            {
                if (level < 30f) { reason = "Требуется 30 ур. Шахтера для серебра"; return false; }
                minToolTier = Mathf.Max(minToolTier, 2);
            }
            else if (prefabName.Contains("blackmarble") || prefabName.Contains("giant_") || prefabName.Contains("softtissue"))
            {
                if (level < 40f) { reason = "Требуется 40 ур. Шахтера для черного мрамора"; return false; }
                minToolTier = Mathf.Max(minToolTier, 3);
            }
            else if (prefabName.Contains("flametal") || prefabName.Contains("mine_flametal"))
            {
                if (level < 50f) { reason = "Требуется 50 ур. Шахтера для огнеметалла"; return false; }
                minToolTier = Mathf.Max(minToolTier, 4);
            }
            else if (prefabName.Contains("ice") || prefabName.Contains("bloodstone") || prefabName.Contains("bloodgold"))
            {
                if (level < 60f) { reason = "Требуется 60 ур. Шахтера для редких руд"; return false; }
                minToolTier = Mathf.Max(minToolTier, 4);
            }

            // Проверка наличия подходящей кирки нужного тира в инвентаре игрока
            if (!HasPickaxe(player, minToolTier, out string toolReason))
            {
                reason = toolReason;
                return false;
            }

            return true;
        }

        // ==========================================
        // 4. ПОСАДКИ: СБОР В РАДИУСЕ
        // ==========================================
        public static void CastAreaHarvest(Player player)
        {
            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.Farming);
            int mode = SpellModeManager.GetCurrentMode(SpellType.AreaHarvest);
            float radius = level >= 75f ? 15f : (level >= 20f ? 10f : 5f);
            float staminaCost = level >= 75f ? 5f : (level >= 35f ? 10f : 15f);
            bool autoPickup = level >= 50f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, 0f))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                AreaHarvestAtPoint(player, impactPoint, radius, autoPickup, staminaCost, mode, hitCol);
            });
        }

        private static MonoBehaviour FindGerminationComponent(GameObject go)
        {
            if (go == null) return null;
            MonoBehaviour[] mbs = go.GetComponentsInParent<MonoBehaviour>();
            foreach (var mb in mbs)
            {
                if (mb != null && mb.GetType().Name == "Germination")
                    return mb;
            }
            mbs = go.GetComponentsInChildren<MonoBehaviour>();
            foreach (var mb in mbs)
            {
                if (mb != null && mb.GetType().Name == "Germination")
                    return mb;
            }
            if (go.transform.root != null && go.transform.root.gameObject != go)
            {
                mbs = go.transform.root.GetComponentsInChildren<MonoBehaviour>();
                foreach (var mb in mbs)
                {
                    if (mb != null && mb.GetType().Name == "Germination")
                        return mb;
                }
            }
            return null;
        }

        private static bool GetSeedBedCropInfo(MonoBehaviour germination, out string seedPrefab, out string cropPrefab, out string cropSharedName)
        {
            seedPrefab = null;
            cropPrefab = null;
            cropSharedName = null;

            if (germination == null) return false;

            try
            {
                MethodInfo getContentMethod = germination.GetType().GetMethod("GetContent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getContentMethod != null)
                {
                    object res = getContentMethod.Invoke(germination, null);
                    if (res != null) seedPrefab = res.ToString();
                }
            }
            catch { }

            if (string.IsNullOrEmpty(seedPrefab))
            {
                ZNetView nv = germination.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid())
                {
                    seedPrefab = nv.GetZDO().GetString("Content", "");
                }
            }

            if (string.IsNullOrEmpty(seedPrefab)) return false;

            try
            {
                FieldInfo convField = germination.GetType().GetField("m_conversion", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (convField != null)
                {
                    System.Collections.IEnumerable list = convField.GetValue(germination) as System.Collections.IEnumerable;
                    if (list != null)
                    {
                        foreach (object item in list)
                        {
                            if (item == null) continue;
                            FieldInfo fromField = item.GetType().GetField("m_from", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            FieldInfo toField = item.GetType().GetField("m_to", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (fromField != null && toField != null)
                            {
                                ItemDrop fromDrop = fromField.GetValue(item) as ItemDrop;
                                ItemDrop toDrop = toField.GetValue(item) as ItemDrop;
                                if (fromDrop != null && toDrop != null)
                                {
                                    string fromName = Utils.GetPrefabName(fromDrop.gameObject);
                                    if (string.Equals(fromName, seedPrefab, StringComparison.OrdinalIgnoreCase) ||
                                        (fromDrop.m_itemData != null && fromDrop.m_itemData.m_shared != null && string.Equals(fromDrop.m_itemData.m_shared.m_name, seedPrefab, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        cropPrefab = Utils.GetPrefabName(toDrop.gameObject);
                                        if (toDrop.m_itemData != null && toDrop.m_itemData.m_shared != null)
                                        {
                                            cropSharedName = toDrop.m_itemData.m_shared.m_name;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // Если не нашли в m_conversion, вычисляем эвристику (CarrotSeeds -> Carrot)
            if (string.IsNullOrEmpty(cropPrefab))
            {
                string candidate = seedPrefab;
                if (candidate.EndsWith("Seeds", StringComparison.OrdinalIgnoreCase))
                    candidate = candidate.Substring(0, candidate.Length - 5);
                else if (candidate.EndsWith("Seed", StringComparison.OrdinalIgnoreCase))
                    candidate = candidate.Substring(0, candidate.Length - 4);

                if (ObjectDB.instance != null)
                {
                    GameObject go = ObjectDB.instance.GetItemPrefab(candidate);
                    if (go != null)
                    {
                        cropPrefab = candidate;
                        ItemDrop id = go.GetComponent<ItemDrop>();
                        if (id != null && id.m_itemData?.m_shared != null)
                        {
                            cropSharedName = id.m_itemData.m_shared.m_name;
                        }
                    }
                }
                if (string.IsNullOrEmpty(cropPrefab))
                {
                    cropPrefab = candidate;
                }
            }

            if (string.IsNullOrEmpty(cropSharedName) && ObjectDB.instance != null && !string.IsNullOrEmpty(seedPrefab))
            {
                GameObject seedGo = ObjectDB.instance.GetItemPrefab(seedPrefab);
                if (seedGo != null)
                {
                    ItemDrop id = seedGo.GetComponent<ItemDrop>();
                    if (id != null && id.m_itemData?.m_shared != null)
                    {
                        cropSharedName = id.m_itemData.m_shared.m_name;
                    }
                }
            }

            return true;
        }

        private static void AreaHarvestAtPoint(Player player, Vector3 center, float radius, bool autoPickup, float staminaCost, int mode = 0, Collider hitCol = null)
        {
            // Режим 1: Режим подбора (только лежащие на земле предметы, разблокируется на 50 ур.)
            if (mode == 1)
            {
                int groundCount = 0;
                if (ItemDrop.s_instances != null)
                {
                    foreach (ItemDrop idrop in ItemDrop.s_instances)
                    {
                        if (idrop == null || idrop.m_itemData == null || idrop.m_nview == null || !idrop.m_nview.IsValid())
                            continue;
                        if (IsFish(idrop))
                            continue;
                        if (Vector3.Distance(center, idrop.transform.position) <= (radius + 2f))
                        {
                            groundCount++;
                        }
                    }
                }

                if (groundCount > 0)
                {
                    RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                    player.RaiseSkill(Skills.SkillType.Farming, Mathf.Clamp(groundCount * 0.2f, 0.5f, 2.5f));
                    player.Message(MessageHud.MessageType.Center, $"Сбор предметов с земли: {groundCount}");
                    RunescapeMagicPlugin.Instance.StartCoroutine(AutoPickupRoutine(player, center, radius + 3f));
                }
                else
                {
                    player.Message(MessageHud.MessageType.Center, "На земле нет предметов для сбора!");
                }
                return;
            }

            // Режим 2: Точечный / Селективный сбор
            string filterPrefab = null;
            string filterDropPrefab = null;
            string filterItemSharedName = null;
            string filterSeedPrefab = null;

            if (mode == 2)
            {
                ItemDrop targetDrop = hitCol != null ? hitCol.GetComponentInParent<ItemDrop>() : null;
                Pickable targetPickable = hitCol != null ? hitCol.GetComponentInParent<Pickable>() : null;
                PickableItem targetPickItem = hitCol != null ? hitCol.GetComponentInParent<PickableItem>() : null;
                MonoBehaviour targetGermination = hitCol != null ? FindGerminationComponent(hitCol.gameObject) : null;

                if (targetDrop == null && targetPickable == null && targetPickItem == null && targetGermination == null && player != null)
                {
                    GameObject hover = player.GetHoverObject();
                    if (hover != null)
                    {
                        targetDrop = hover.GetComponentInParent<ItemDrop>();
                        targetPickable = hover.GetComponentInParent<Pickable>();
                        targetPickItem = hover.GetComponentInParent<PickableItem>();
                        targetGermination = FindGerminationComponent(hover);
                    }
                }

                if (targetDrop == null && targetPickable == null && targetPickItem == null && targetGermination == null)
                {
                    Collider[] near = Physics.OverlapSphere(center, 1.2f);
                    foreach (var c in near)
                    {
                        if (c == null) continue;
                        targetDrop = c.GetComponentInParent<ItemDrop>();
                        if (targetDrop != null && !IsFish(targetDrop)) break;
                        targetPickable = c.GetComponentInParent<Pickable>();
                        if (targetPickable != null) break;
                        targetPickItem = c.GetComponentInParent<PickableItem>();
                        if (targetPickItem != null) break;
                        targetGermination = FindGerminationComponent(c.gameObject);
                        if (targetGermination != null) break;
                    }
                }

                // А) Если прицелились в уже лежащий на земле предмет (ItemDrop):
                if (targetDrop != null && !IsFish(targetDrop))
                {
                    filterDropPrefab = Utils.GetPrefabName(targetDrop.gameObject);
                    filterItemSharedName = targetDrop.m_itemData?.m_shared?.m_name;

                    int matchedCount = 0;
                    if (ItemDrop.s_instances != null)
                    {
                        foreach (ItemDrop idrop in ItemDrop.s_instances)
                        {
                            if (idrop == null || idrop.m_itemData == null || idrop.m_nview == null || !idrop.m_nview.IsValid()) continue;
                            if (IsFish(idrop)) continue;
                            if (Vector3.Distance(center, idrop.transform.position) <= (radius + 2f))
                            {
                                if (Utils.GetPrefabName(idrop.gameObject) == filterDropPrefab || (filterItemSharedName != null && idrop.m_itemData?.m_shared?.m_name == filterItemSharedName))
                                {
                                    matchedCount++;
                                }
                            }
                        }
                    }

                    if (matchedCount > 0)
                    {
                        RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                        player.RaiseSkill(Skills.SkillType.Farming, Mathf.Clamp(matchedCount * 0.2f, 0.5f, 2.5f));
                        string dispName = Localization.instance != null && filterItemSharedName != null ? Localization.instance.Localize(filterItemSharedName) : filterDropPrefab;
                        player.Message(MessageHud.MessageType.Center, $"Точечный сбор с земли: {dispName} ({matchedCount})");
                        RunescapeMagicPlugin.Instance.StartCoroutine(AutoPickupRoutine(player, center, radius + 3f, filterDropPrefab, filterItemSharedName));
                        return;
                    }
                    else
                    {
                        player.Message(MessageHud.MessageType.Center, "В радиусе больше нет таких предметов!");
                        return;
                    }
                }
                // Б) Если прицелились в растущую культуру (Pickable / PickableItem):
                else if (targetPickable != null)
                {
                    filterPrefab = Utils.GetPrefabName(targetPickable.gameObject);
                    if (targetPickable.m_itemPrefab != null)
                    {
                        filterDropPrefab = Utils.GetPrefabName(targetPickable.m_itemPrefab);
                        ItemDrop id = targetPickable.m_itemPrefab.GetComponent<ItemDrop>();
                        if (id != null && id.m_itemData?.m_shared != null)
                        {
                            filterItemSharedName = id.m_itemData.m_shared.m_name;
                        }
                    }
                    if (!string.IsNullOrEmpty(filterDropPrefab))
                    {
                        filterSeedPrefab = filterDropPrefab + "Seeds";
                    }
                }
                else if (targetPickItem != null)
                {
                    filterPrefab = Utils.GetPrefabName(targetPickItem.gameObject);
                    if (targetPickItem.m_itemPrefab != null)
                    {
                        filterDropPrefab = Utils.GetPrefabName(targetPickItem.m_itemPrefab.gameObject);
                        ItemDrop id = targetPickItem.m_itemPrefab.GetComponent<ItemDrop>();
                        if (id != null && id.m_itemData?.m_shared != null)
                        {
                            filterItemSharedName = id.m_itemData.m_shared.m_name;
                        }
                    }
                }
                // В) Если прицелились в модовую рассадницу (blacks7ar-SeedBed: Germination):
                else if (targetGermination != null)
                {
                    if (GetSeedBedCropInfo(targetGermination, out string gSeed, out string gCrop, out string gName))
                    {
                        filterSeedPrefab = gSeed;
                        filterDropPrefab = gCrop;
                        filterItemSharedName = gName;
                        if (!string.IsNullOrEmpty(gCrop))
                        {
                            filterPrefab = "Pickable_" + gCrop;
                        }
                    }
                }
                else
                {
                    player.Message(MessageHud.MessageType.Center, "Прицельтесь в конкретное растение, грядку или предмет на земле для точечного сбора!");
                    return;
                }
            }

            Collider[] colliders = Physics.OverlapSphere(center, radius);
            HashSet<Pickable> picked = new HashSet<Pickable>();
            HashSet<PickableItem> pickedItems = new HashSet<PickableItem>();
            HashSet<GameObject> processedBeds = new HashSet<GameObject>();
            int collectedCount = 0;

            foreach (Collider col in colliders)
            {
                if (col == null) continue;

                // 1. Созревшие культуры, кусты ягод, грибы
                Pickable pickable = col.GetComponentInParent<Pickable>();
                if (pickable != null && col.GetComponentInParent<Fish>() == null && !picked.Contains(pickable) && pickable.CanBePicked())
                {
                    if (mode == 2)
                    {
                        string pName = Utils.GetPrefabName(pickable.gameObject);
                        string dropName = pickable.m_itemPrefab != null ? Utils.GetPrefabName(pickable.m_itemPrefab) : null;
                        string itemShared = pickable.m_itemPrefab != null ? pickable.m_itemPrefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_name : null;

                        bool match = false;
                        if (!string.IsNullOrEmpty(filterPrefab) && (string.Equals(pName, filterPrefab, StringComparison.OrdinalIgnoreCase) || string.Equals(pName, "Pickable_" + filterPrefab, StringComparison.OrdinalIgnoreCase)))
                            match = true;
                        if (!match && !string.IsNullOrEmpty(filterDropPrefab))
                        {
                            if (string.Equals(pName, "Pickable_" + filterDropPrefab, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(dropName) && string.Equals(dropName, filterDropPrefab, StringComparison.OrdinalIgnoreCase)))
                            {
                                match = true;
                            }
                        }
                        if (!match && !string.IsNullOrEmpty(filterItemSharedName) && !string.IsNullOrEmpty(itemShared) && string.Equals(itemShared, filterItemSharedName, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                        }

                        if (!match) continue;
                    }

                    picked.Add(pickable);
                    pickable.Interact(player, false, false);
                    collectedCount++;
                }

                // 2. Подбираемые предметы на земле (PickableItem: ветки, камни)
                PickableItem pickableItem = col.GetComponentInParent<PickableItem>();
                if (pickableItem != null && col.GetComponentInParent<Fish>() == null && !pickedItems.Contains(pickableItem))
                {
                    if (mode == 2)
                    {
                        string pName = Utils.GetPrefabName(pickableItem.gameObject);
                        string dropName = pickableItem.m_itemPrefab != null ? Utils.GetPrefabName(pickableItem.m_itemPrefab.gameObject) : null;
                        string itemShared = pickableItem.m_itemPrefab != null ? pickableItem.m_itemPrefab.m_itemData?.m_shared?.m_name : null;

                        bool match = false;
                        if (!string.IsNullOrEmpty(filterPrefab) && string.Equals(pName, filterPrefab, StringComparison.OrdinalIgnoreCase))
                            match = true;
                        if (!match && !string.IsNullOrEmpty(filterDropPrefab) && !string.IsNullOrEmpty(dropName) && string.Equals(dropName, filterDropPrefab, StringComparison.OrdinalIgnoreCase))
                            match = true;
                        if (!match && !string.IsNullOrEmpty(filterItemSharedName) && !string.IsNullOrEmpty(itemShared) && string.Equals(itemShared, filterItemSharedName, StringComparison.OrdinalIgnoreCase))
                            match = true;

                        if (!match) continue;
                    }

                    pickedItems.Add(pickableItem);
                    pickableItem.Interact(player, false, false);
                    collectedCount++;
                }

                // 3. Модовые рассадницы (blacks7ar-SeedBed: Germination)
                GameObject root = col.transform.root.gameObject;
                if (!processedBeds.Contains(root))
                {
                    MonoBehaviour[] scripts = root.GetComponentsInChildren<MonoBehaviour>();
                    foreach (MonoBehaviour mb in scripts)
                    {
                        if (mb != null && mb.GetType().Name == "Germination")
                        {
                            processedBeds.Add(root);

                            if (mode == 2)
                            {
                                GetSeedBedCropInfo(mb, out string bedSeed, out string bedCrop, out string bedCropName);
                                bool match = false;
                                if (!string.IsNullOrEmpty(filterSeedPrefab) && !string.IsNullOrEmpty(bedSeed) && string.Equals(bedSeed, filterSeedPrefab, StringComparison.OrdinalIgnoreCase))
                                    match = true;
                                if (!match && !string.IsNullOrEmpty(filterDropPrefab) && !string.IsNullOrEmpty(bedCrop) && string.Equals(bedCrop, filterDropPrefab, StringComparison.OrdinalIgnoreCase))
                                    match = true;
                                if (!match && !string.IsNullOrEmpty(filterItemSharedName) && !string.IsNullOrEmpty(bedCropName) && string.Equals(bedCropName, filterItemSharedName, StringComparison.OrdinalIgnoreCase))
                                    match = true;
                                if (!match && !string.IsNullOrEmpty(filterDropPrefab) && !string.IsNullOrEmpty(bedSeed) && bedSeed.IndexOf(filterDropPrefab, StringComparison.OrdinalIgnoreCase) >= 0)
                                    match = true;
                                if (!match && !string.IsNullOrEmpty(filterSeedPrefab) && !string.IsNullOrEmpty(bedCrop) && filterSeedPrefab.IndexOf(bedCrop, StringComparison.OrdinalIgnoreCase) >= 0)
                                    match = true;

                                if (!match)
                                {
                                    continue;
                                }
                            }

                            MethodInfo getStatus = mb.GetType().GetMethod("GetStatus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (getStatus != null)
                            {
                                object statusObj = getStatus.Invoke(mb, null);
                                if (statusObj != null && statusObj.ToString() == "Ready")
                                {
                                    MethodInfo interact = mb.GetType().GetMethod("Interact", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    interact?.Invoke(mb, new object[] { player, false, false });
                                    collectedCount++;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 4. На 50+ уровне проверяем наличие лежащих на земле предметов (ItemDrop), исключая рыбу
            int groundItemsCount = 0;
            if (autoPickup && mode == 0)
            {
                if (ItemDrop.s_instances != null)
                {
                    foreach (ItemDrop idrop in ItemDrop.s_instances)
                    {
                        if (idrop == null || idrop.m_itemData == null || idrop.m_nview == null || !idrop.m_nview.IsValid())
                            continue;
                        if (IsFish(idrop))
                            continue;
                        if (Vector3.Distance(center, idrop.transform.position) <= (radius + 2f))
                        {
                            groundItemsCount++;
                        }
                    }
                }
            }

            if (collectedCount > 0 || groundItemsCount > 0)
            {
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                float skillGain = collectedCount > 0 ? (collectedCount * 0.5f) : Mathf.Clamp(groundItemsCount * 0.2f, 0.5f, 2.5f);
                player.RaiseSkill(Skills.SkillType.Farming, skillGain);

                if (collectedCount > 0)
                {
                    string cropTitle = "";
                    if (mode == 2)
                    {
                        string dispName = filterItemSharedName != null && Localization.instance != null
                            ? Localization.instance.Localize(filterItemSharedName)
                            : (!string.IsNullOrEmpty(filterDropPrefab) ? filterDropPrefab : filterPrefab);
                        if (!string.IsNullOrEmpty(dispName))
                            cropTitle = $" ({dispName})";
                    }
                    player.Message(MessageHud.MessageType.Center, $"Собрано растений/ресурсов{cropTitle}: {collectedCount}");
                }
                else
                {
                    player.Message(MessageHud.MessageType.Center, $"Сбор предметов с земли: {groundItemsCount}");
                }

                // На 50+ уровне переносим выпавший лут прямо в инвентарь игрока
                if (autoPickup)
                {
                    RunescapeMagicPlugin.Instance.StartCoroutine(AutoPickupRoutine(player, center, radius + 3f, filterDropPrefab, filterItemSharedName));
                }
            }
            else
            {
                if (autoPickup)
                {
                    player.Message(MessageHud.MessageType.Center, "В радиусе нет растений или предметов для сбора!");
                }
                else
                {
                    player.Message(MessageHud.MessageType.Center, "В радиусе нет спелых растений для сбора!");
                }
            }
        }

        private static bool IsFish(ItemDrop itemDrop)
        {
            if (itemDrop == null) return false;
            if (itemDrop.GetComponentInParent<Fish>() != null || itemDrop.GetComponent<Fish>() != null)
                return true;
            if (itemDrop.m_itemData != null && itemDrop.m_itemData.m_shared != null)
            {
                if (itemDrop.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Fish)
                    return true;
            }
            return false;
        }

        private static IEnumerator AutoPickupRoutine(Player player, Vector3 center, float radius, string filterPrefab = null, string filterItemSharedName = null)
        {
            HashSet<ItemDrop> collectedDrops = new HashSet<ItemDrop>();

            // Проводим несколько последовательных итераций в течение 2.8 сек,
            // чтобы надежно захватить лут как с обычной задержкой спавна, так и модовый с задержкой (например SeedBed m_harvestDelay = 1.5s)
            for (int pass = 0; pass < 8; pass++)
            {
                yield return new WaitForSeconds(0.35f);
                if (player == null || player.GetInventory() == null) yield break;

                // 1. Поиск через активные экземпляры ItemDrop (наиболее надежный способ в Valheim)
                if (ItemDrop.s_instances != null)
                {
                    for (int i = ItemDrop.s_instances.Count - 1; i >= 0; i--)
                    {
                        if (i >= ItemDrop.s_instances.Count) continue;
                        ItemDrop itemDrop = ItemDrop.s_instances[i];
                        if (itemDrop == null || collectedDrops.Contains(itemDrop) || IsFish(itemDrop)) continue;
                        if (Vector3.Distance(center, itemDrop.transform.position) > radius) continue;

                        if (itemDrop.m_itemData == null || itemDrop.m_nview == null || !itemDrop.m_nview.IsValid())
                            continue;

                        // Если задан фильтр точечного сбора, подбираем только подходящие предметы
                        if (!string.IsNullOrEmpty(filterPrefab) || !string.IsNullOrEmpty(filterItemSharedName))
                        {
                            bool match = false;
                            if (!string.IsNullOrEmpty(filterPrefab) && Utils.GetPrefabName(itemDrop.gameObject) == filterPrefab)
                                match = true;
                            if (!string.IsNullOrEmpty(filterItemSharedName) && itemDrop.m_itemData != null && itemDrop.m_itemData.m_shared != null && itemDrop.m_itemData.m_shared.m_name == filterItemSharedName)
                                match = true;

                            if (!match) continue;
                        }

                        ItemDrop.ItemData data = itemDrop.m_itemData;
                        if (player.GetInventory().CanAddItem(data, -1))
                        {
                            collectedDrops.Add(itemDrop);
                            int stack = data.m_stack;

                            if (player.GetInventory().AddItem(data))
                            {
                                itemDrop.m_nview.ClaimOwnership();
                                ZNetScene.instance.Destroy(itemDrop.gameObject);
                                player.ShowPickupMessage(data, stack);
                            }
                        }
                    }
                }

                // 2. Дополнительный физический поиск по коллайдерам
                Collider[] drops = Physics.OverlapSphere(center, radius);
                foreach (Collider c in drops)
                {
                    if (c == null) continue;
                    ItemDrop itemDrop = c.GetComponentInParent<ItemDrop>();
                    if (itemDrop == null || collectedDrops.Contains(itemDrop) || IsFish(itemDrop)) continue;

                    if (itemDrop.m_itemData == null || itemDrop.m_nview == null || !itemDrop.m_nview.IsValid())
                        continue;

                    // Если задан фильтр точечного сбора, подбираем только подходящие предметы
                    if (!string.IsNullOrEmpty(filterPrefab) || !string.IsNullOrEmpty(filterItemSharedName))
                    {
                        bool match = false;
                        if (!string.IsNullOrEmpty(filterPrefab) && Utils.GetPrefabName(itemDrop.gameObject) == filterPrefab)
                            match = true;
                        if (!string.IsNullOrEmpty(filterItemSharedName) && itemDrop.m_itemData != null && itemDrop.m_itemData.m_shared != null && itemDrop.m_itemData.m_shared.m_name == filterItemSharedName)
                            match = true;

                        if (!match) continue;
                    }

                    ItemDrop.ItemData data = itemDrop.m_itemData;
                    if (player.GetInventory().CanAddItem(data, -1))
                    {
                        collectedDrops.Add(itemDrop);
                        int stack = data.m_stack;

                        if (player.GetInventory().AddItem(data))
                        {
                            itemDrop.m_nview.ClaimOwnership();
                            ZNetScene.instance.Destroy(itemDrop.gameObject);
                            player.ShowPickupMessage(data, stack);
                        }
                    }
                }
            }
        }

        // ==========================================
        // 5. ПОСАДКИ: МГНОВЕННОЕ ВЫРАЩИВАНИЕ
        // ==========================================
        public static void CastInstantGrow(Player player)
        {
            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.Farming);
            if (level < 25f)
            {
                player.Message(MessageHud.MessageType.Center, $"Требуется 25 ур. Земледелия (текущий: {level})");
                return;
            }

            int mode = SpellModeManager.GetCurrentMode(SpellType.InstantGrow);
            float radius = level >= 90f ? 15f : (level >= 50f ? 10f : 6f);
            float staminaCost = level >= 75f ? 15f : 30f;
            float healthCost = level >= 90f ? 0f : (level >= 75f ? 5f : 15f);

            if (mode == 1) // Компактный режим: уменьшенный вдвое радиус и расход
            {
                radius *= 0.5f;
                staminaCost *= 0.5f;
                healthCost *= 0.5f;
            }
            else if (mode == 2) // Точечный рост: расход уменьшен вдвое
            {
                staminaCost *= 0.5f;
                healthCost *= 0.5f;
            }

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                InstantGrowAtPoint(player, impactPoint, radius, level, staminaCost, healthCost, mode, hitCol);
            });
        }

        private static void InstantGrowAtPoint(Player player, Vector3 center, float radius, float level, float staminaCost, float healthCost, int mode = 0, Collider hitCol = null)
        {
            string filterPrefab = null;
            if (mode == 2)
            {
                Plant targetPlant = hitCol != null ? hitCol.GetComponentInParent<Plant>() : null;
                Pickable targetPick = hitCol != null ? hitCol.GetComponentInParent<Pickable>() : null;
                if (targetPlant == null && targetPick == null)
                {
                    Collider[] near = Physics.OverlapSphere(center, 0.8f);
                    foreach (var c in near)
                    {
                        if (c == null) continue;
                        targetPlant = c.GetComponentInParent<Plant>();
                        if (targetPlant != null) break;
                        targetPick = c.GetComponentInParent<Pickable>();
                        if (targetPick != null) break;
                    }
                }

                if (targetPlant != null)
                {
                    filterPrefab = Utils.GetPrefabName(targetPlant.gameObject);
                }
                else if (targetPick != null)
                {
                    filterPrefab = Utils.GetPrefabName(targetPick.gameObject);
                }
                else
                {
                    player.Message(MessageHud.MessageType.Center, "Прицельтесь в саженец или рассадник для точечного роста!");
                    return;
                }
            }

            Collider[] colliders = Physics.OverlapSphere(center, radius);
            HashSet<GameObject> processedRoots = new HashSet<GameObject>();
            int grownCount = 0;

            foreach (Collider col in colliders)
            {
                if (col == null) continue;

                GameObject root = col.transform.root.gameObject;
                if (processedRoots.Contains(root)) continue;

                // 1. Посадки культур и саженцев деревьев (Plant - ваниль и модовые культуры PlantEverything)
                Plant plant = col.GetComponentInParent<Plant>();
                if (plant != null && plant.m_nview != null && plant.m_nview.IsValid())
                {
                    string prefabName = Utils.GetPrefabName(plant.gameObject).ToLowerInvariant();
                    if (filterPrefab != null && Utils.GetPrefabName(plant.gameObject) != filterPrefab)
                        continue;

                    if (CanGrowPlant(prefabName, level))
                    {
                        processedRoots.Add(root);
                        plant.m_nview.ClaimOwnership();
                        // Принудительно задаем статус Healthy и сбрасываем время посадки (0L безопасно для любого возраста мира)
                        plant.m_status = Plant.Status.Healthy;
                        plant.m_nview.GetZDO().Set(ZDOVars.s_plantTime, 0L);
                        GameObject grownObj = plant.Grow();
                        if (grownObj != null || plant.m_status == Plant.Status.Healthy)
                        {
                            grownCount++;
                        }
                        continue;
                    }
                }

                // 2. Дикорастущие ягодные кусты, грибы и растения (Pickable - малина, черника, морошка и т.д.)
                Pickable pickable = col.GetComponentInParent<Pickable>();
                if (pickable != null && pickable.m_nview != null && pickable.m_nview.IsValid())
                {
                    if (filterPrefab != null && Utils.GetPrefabName(pickable.gameObject) != filterPrefab)
                        continue;

                    if (pickable.m_picked)
                    {
                        processedRoots.Add(root);
                        pickable.m_nview.ClaimOwnership();
                        pickable.m_nview.GetZDO().Set(ZDOVars.s_pickedTime, 0L);
                        pickable.SetPicked(false);
                        grownCount++;
                        continue;
                    }
                }

                // 3. Пни срубленных деревьев (Vanilla, Advize-StumpsRegrow и любые модовые)
                Destructible dest = col.GetComponentInParent<Destructible>();
                string pName = Utils.GetPrefabName(root).ToLowerInvariant();
                string destName = dest != null ? Utils.GetPrefabName(dest.gameObject).ToLowerInvariant() : "";
                if (pName.Contains("stub") || destName.Contains("stub") || (dest != null && dest.GetDestructibleType() == DestructibleType.Tree))
                {
                    string stubPrefabName = Utils.GetPrefabName(dest != null ? dest.gameObject : root);
                    GameObject treePrefab = ResolveTreePrefabForStub(stubPrefabName, ZNetScene.instance);
                    if (treePrefab != null)
                    {
                        processedRoots.Add(root);

                        Transform targetTransform = dest != null ? dest.transform : root.transform;
                        Vector3 pos = targetTransform.position;
                        Quaternion rot = targetTransform.rotation;
                        Vector3 scale = targetTransform.localScale;

                        ZNetView destNv = dest != null ? dest.m_nview : root.GetComponent<ZNetView>();
                        if (destNv != null && destNv.IsValid())
                        {
                            destNv.ClaimOwnership();
                            destNv.Destroy();
                        }
                        else
                        {
                            UnityEngine.Object.Destroy(root);
                        }

                        // Спавним взрослое полноценное дерево на месте пня
                        GameObject grownTree = UnityEngine.Object.Instantiate(treePrefab, pos, rot);
                        if (grownTree != null)
                        {
                            ZNetView treeNv = grownTree.GetComponent<ZNetView>();
                            if (treeNv != null && treeNv.IsValid())
                            {
                                treeNv.SetLocalScale(scale);
                            }
                            TreeBase tb = grownTree.GetComponent<TreeBase>();
                            if (tb != null)
                            {
                                tb.Grow();
                            }
                        }

                        grownCount++;
                        continue;
                    }
                }

                // 4. Модовые рассадницы "Бэды" (blacks7ar-SeedBed: Germination) и другие компоненты
                MonoBehaviour[] scripts = root.GetComponentsInChildren<MonoBehaviour>();
                bool handledMod = false;
                foreach (MonoBehaviour mb in scripts)
                {
                    if (mb == null) continue;
                    string tName = mb.GetType().Name;

                    if (tName == "Germination")
                    {
                        processedRoots.Add(root);

                        MethodInfo getStatus = mb.GetType().GetMethod("GetStatus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        object statusObj = getStatus?.Invoke(mb, null);
                        int statusInt = statusObj != null ? Convert.ToInt32(statusObj) : -1;

                        // Если в рассаднице посажены культуры и они еще растут (Germinating = 1)
                        if (statusInt == 1 || (statusObj != null && statusObj.ToString() == "Germinating"))
                        {
                            ZNetView nv = mb.GetComponent<ZNetView>() ?? root.GetComponent<ZNetView>();
                            if (nv != null && nv.IsValid())
                            {
                                nv.ClaimOwnership();
                                nv.GetZDO().Set("RSM_InstantGrown", true);

                                long netTicks = ZNet.instance.GetTime().Ticks;
                                long safeTicks = Math.Max(1L, netTicks - 36000000000L);
                                nv.GetZDO().Set(ZDOVars.s_startTime, safeTicks);
                            }

                            // Сбрасываем m_germinationDuration для мгновенного завершения
                            FieldInfo durField = mb.GetType().GetField("m_germinationDuration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            durField?.SetValue(mb, 0f);

                            // Принудительно вызываем обновление состояния грядки
                            MethodInfo slowUpdate = mb.GetType().GetMethod("SlowUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            slowUpdate?.Invoke(mb, null);

                            MethodInfo reapply = mb.GetType().GetMethod("ReapplyVisuals", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            reapply?.Invoke(mb, null);

                            grownCount++;
                            handledMod = true;
                            break;
                        }
                    }
                    else if (tName == "StumpGrower")
                    {
                        processedRoots.Add(root);
                        MethodInfo regrow = mb.GetType().GetMethod("RegrowStump", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        regrow?.Invoke(mb, null);
                        mb.StartCoroutine("RemoveStump");
                        grownCount++;
                        handledMod = true;
                        break;
                    }
                }
                if (handledMod) continue;
            }

            if (grownCount > 0)
            {
                // Списываем стамину и здоровье только если магия реально вырастила посадки
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost);
                player.RaiseSkill(Skills.SkillType.Farming, grownCount * 1.0f);
                player.Message(MessageHud.MessageType.Center, $"Выращено культур/растений: {grownCount}");
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, "В радиусе действия нет растущих культур, пней или рассадниц!");
            }
        }

        private static bool CanGrowPlant(string name, float level)
        {
            // Ур. 40: ячмень, лен, береза, дуб
            if (name.Contains("barley") || name.Contains("flax") || name.Contains("birch") || name.Contains("oak"))
                return level >= 40f;

            // Ур. 60: йотунские паффы, маг-грибы, лоза пеплоносца
            if (name.Contains("magecap") || name.Contains("jotunpuffs") || name.Contains("vine") || name.Contains("ashvine"))
                return level >= 60f;

            // Все базовые овощи, саженцы деревьев и любые модовые растения доступны с 25 уровня
            return level >= 25f;
        }

        // ==========================================
        // 6. РЕМЕСЛО: РЕМОНТ СНАРЯЖЕНИЯ В ИНВЕНТАРЕ
        // ==========================================
        public static void CastInventoryRepair(Player player)
        {
            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.Crafting);
            if (level < 10f)
            {
                player.Message(MessageHud.MessageType.Center, $"Требуется 10 ур. Ремесла (текущий: {level})");
                return;
            }

            int mode = SpellModeManager.GetCurrentMode(SpellType.InventoryRepair);
            float staminaCost = level >= 65f ? 15f : 30f;
            float healthCost = level >= 85f ? 0f : (level >= 65f ? 10f : 20f);

            // Процент починки от уровня Ремесла:
            // 10 ур. -> 20%
            // 20 ур. -> 40%
            // 30 ур. -> 60%
            // 40 ур. -> 80%
            // 50+ ур. -> 100%
            float repairRatio;
            if (level >= 50f) repairRatio = 1.0f;
            else if (level >= 40f) repairRatio = 0.8f;
            else if (level >= 30f) repairRatio = 0.6f;
            else if (level >= 20f) repairRatio = 0.4f;
            else repairRatio = 0.2f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            if (mode == 1) // Режим прицела: сундуки, стойки брони, подставки предметов или союзники
            {
                CastTargetedInventoryRepair(player, staminaCost, healthCost, repairRatio);
                return;
            }

            List<ItemDrop.ItemData> damagedItems = new List<ItemDrop.ItemData>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability())
                {
                    damagedItems.Add(item);
                }
            }

            if (damagedItems.Count == 0)
            {
                player.Message(MessageHud.MessageType.Center, "Все предметы в инвентаре целы!");
                return;
            }

            if (!RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost))
                return;

            foreach (var item in damagedItems)
            {
                // Ремонтируем любые предметы (включая модовые), без привязки к конкретным верстакам/кузницам
                float addDurability = item.GetMaxDurability() * repairRatio;
                item.m_durability = Mathf.Min(item.GetMaxDurability(), item.m_durability + addDurability);
            }

            PlayRepairSound(player.transform.position);
            player.RaiseSkill(Skills.SkillType.Crafting, damagedItems.Count * 0.75f);
            player.Message(MessageHud.MessageType.Center, $"Починено предметов: {damagedItems.Count} (+{(int)(repairRatio * 100)}% прочности)");
        }

        private static void CastTargetedInventoryRepair(Player player, float staminaCost, float healthCost, float repairRatio)
        {
            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                GameObject hoverObj = player.GetHoverObject();

                // 1. Другой игрок (Player)
                Player targetPlayer = hitCol != null ? hitCol.GetComponentInParent<Player>() : null;
                if ((targetPlayer == null || targetPlayer == player) && hoverObj != null)
                {
                    Player hp = hoverObj.GetComponentInParent<Player>();
                    if (hp != null && hp != player) targetPlayer = hp;
                }

                // 2. Контейнер / Сундук (Container)
                Container container = hitCol != null ? hitCol.GetComponentInParent<Container>() : null;
                if (container == null && hoverObj != null) container = hoverObj.GetComponentInParent<Container>();

                // 3. Стойка для брони (ArmorStand)
                ArmorStand armorStand = hitCol != null ? hitCol.GetComponentInParent<ArmorStand>() : null;
                if (armorStand == null && hoverObj != null) armorStand = hoverObj.GetComponentInParent<ArmorStand>();

                // 4. Подставка для предметов (ItemStand)
                ItemStand itemStand = hitCol != null ? hitCol.GetComponentInParent<ItemStand>() : null;
                if (itemStand == null && hoverObj != null) itemStand = hoverObj.GetComponentInParent<ItemStand>();

                // 5. Если прямого попадания нет, проверяем в радиусе 2.5м от точки попадания снаряда
                if (targetPlayer == null && container == null && armorStand == null && itemStand == null)
                {
                    Collider[] near = Physics.OverlapSphere(impactPoint, 2.5f);
                    foreach (Collider c in near)
                    {
                        if (c == null) continue;
                        if (targetPlayer == null)
                        {
                            Player p = c.GetComponentInParent<Player>();
                            if (p != null && p != player) targetPlayer = p;
                        }
                        if (container == null) container = c.GetComponentInParent<Container>();
                        if (armorStand == null) armorStand = c.GetComponentInParent<ArmorStand>();
                        if (itemStand == null) itemStand = c.GetComponentInParent<ItemStand>();
                    }
                }

                // Выполняем починку найденной цели
                if (targetPlayer != null && targetPlayer != player)
                {
                    if (!RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost)) return;
                    ZRoutedRpc.instance?.InvokeRoutedRPC(targetPlayer.GetZDOID().UserID, "RPC_RunescapeRepairInventory", player.GetPlayerName(), repairRatio);
                    PlayRepairSound(targetPlayer.transform.position);
                    player.RaiseSkill(Skills.SkillType.Crafting, 2.5f);
                    player.Message(MessageHud.MessageType.Center, $"Снаряжение игрока {targetPlayer.GetPlayerName()} отремонтировано!");
                    return;
                }

                if (container != null && container.GetInventory() != null)
                {
                    if (container.m_nview != null && container.m_nview.IsValid()) container.m_nview.ClaimOwnership();
                    int repaired = 0;
                    foreach (var item in container.GetInventory().GetAllItems())
                    {
                        if (item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability())
                        {
                            float maxDur = item.GetMaxDurability();
                            item.m_durability = Mathf.Min(maxDur, item.m_durability + maxDur * repairRatio);
                            repaired++;
                        }
                    }
                    if (repaired > 0)
                    {
                        if (!RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost)) return;
                        container.Save();
                        PlayRepairSound(container.transform.position);
                        player.RaiseSkill(Skills.SkillType.Crafting, repaired * 0.5f);
                        player.Message(MessageHud.MessageType.Center, $"Отремонтировано предметов в сундуке: {repaired}");
                        return;
                    }
                    else
                    {
                        player.Message(MessageHud.MessageType.Center, "В сундуке нет поврежденных предметов!");
                        return;
                    }
                }

                if (armorStand != null && armorStand.m_nview != null && armorStand.m_nview.IsValid())
                {
                    armorStand.m_nview.ClaimOwnership();
                    PlayRepairSound(armorStand.transform.position);
                    if (RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost))
                    {
                        player.RaiseSkill(Skills.SkillType.Crafting, 1.5f);
                        player.Message(MessageHud.MessageType.Center, "Снаряжение на стойке отремонтировано!");
                    }
                    return;
                }

                if (itemStand != null && itemStand.m_nview != null && itemStand.m_nview.IsValid())
                {
                    itemStand.m_nview.ClaimOwnership();
                    PlayRepairSound(itemStand.transform.position);
                    if (RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost))
                    {
                        player.RaiseSkill(Skills.SkillType.Crafting, 1f);
                        player.Message(MessageHud.MessageType.Center, "Предмет на подставке отремонтирован!");
                    }
                    return;
                }

                player.Message(MessageHud.MessageType.Center, "Прицельтесь в сундук, стойку с броней или союзника!");
            });
        }

        private static bool CanRepairItem(ItemDrop.ItemData item, float level)
        {
            if (item?.m_shared == null) return false;
            return item.m_shared.m_useDurability;
        }

        // ==========================================
        // 7. РЕМЕСЛО: РЕМОНТ ПОСТРОЕК В РАДИУСЕ
        // ==========================================
        public static void CastPieceRepair(Player player)
        {
            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.Crafting);
            int mode = SpellModeManager.GetCurrentMode(SpellType.PieceRepair);
            float radius = level >= 80f ? 25f : (level >= 25f ? 15f : 10f);
            float staminaCost = level >= 60f ? 10f : 25f;
            float healthCost = (mode == 1) ? 10f : 0f; // В режиме укрепления расходует 10 HP
            bool ignoreStation = level >= 40f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                if (mode == 1)
                {
                    OverhealPiecesAtPoint(player, impactPoint, radius, staminaCost, healthCost);
                }
                else
                {
                    RepairPiecesAtPoint(player, impactPoint, radius, ignoreStation, staminaCost);
                }
            });
        }

        private static void OverhealPiecesAtPoint(Player player, Vector3 center, float radius, float staminaCost, float healthCost)
        {
            Collider[] colliders = Physics.OverlapSphere(center, radius);
            HashSet<WearNTear> visited = new HashSet<WearNTear>();
            int fortifiedCount = 0;

            foreach (Collider col in colliders)
            {
                if (col == null) continue;
                WearNTear wnt = col.GetComponentInParent<WearNTear>();
                if (wnt != null && !visited.Contains(wnt) && wnt.m_nview != null && wnt.m_nview.IsValid())
                {
                    visited.Add(wnt);
                    float maxHealth = wnt.m_health;
                    float fortifiedHealth = maxHealth * 1.5f;
                    float curHealth = wnt.m_nview.GetZDO().GetFloat(ZDOVars.s_health, maxHealth);

                    if (curHealth < (fortifiedHealth - 0.5f))
                    {
                        // 1. Полностью восстанавливаем постройку, сбрасывая текстурный износ и повреждения
                        if (wnt.GetHealthPercentage() < 1f)
                        {
                            wnt.Repair();
                        }

                        // 2. Устанавливаем прочность на 150% (щит укрепления) и синхронизируем по сети
                        wnt.m_nview.ClaimOwnership();
                        wnt.m_nview.GetZDO().Set(ZDOVars.s_health, fortifiedHealth);
                        wnt.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_HealthChanged", fortifiedHealth);

                        // 3. Создаем визуальный эффект ремонта
                        if (wnt.m_piece != null && wnt.m_piece.m_placeEffect != null)
                        {
                            wnt.m_piece.m_placeEffect.Create(wnt.transform.position, wnt.transform.rotation, wnt.transform, 1f, -1);
                        }

                        fortifiedCount++;
                    }
                }
            }

            if (fortifiedCount > 0)
            {
                if (!RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost))
                    return;

                PlayRepairSound(center);
                player.RaiseSkill(Skills.SkillType.Crafting, fortifiedCount * 0.4f);
                player.Message(MessageHud.MessageType.Center, $"Укреплено построек: {fortifiedCount} (Прочность: 150%)");
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, "Все постройки в радиусе уже укреплены до максимума!");
            }
        }

        private static void RepairPiecesAtPoint(Player player, Vector3 center, float radius, bool ignoreStation, float staminaCost)
        {
            Collider[] colliders = Physics.OverlapSphere(center, radius);
            HashSet<WearNTear> visited = new HashSet<WearNTear>();
            List<WearNTear> toRepair = new List<WearNTear>();

            foreach (Collider col in colliders)
            {
                if (col == null) continue;
                WearNTear wnt = col.GetComponentInParent<WearNTear>();
                if (wnt != null && !visited.Contains(wnt) && wnt.GetHealthPercentage() < 1f)
                {
                    visited.Add(wnt);

                    // До 40 уровня требуется наличие соответствующего верстака поблизости
                    if (!ignoreStation && wnt.m_piece != null && wnt.m_piece.m_craftingStation != null)
                    {
                        if (!CraftingStation.HaveBuildStationInRange(wnt.m_piece.m_craftingStation.m_name, wnt.transform.position))
                            continue;
                    }

                    toRepair.Add(wnt);
                }
            }

            if (toRepair.Count > 0)
            {
                if (!RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f))
                    return;

                PlayRepairSound(center);
                RunescapeMagicPlugin.Instance.StartCoroutine(BatchedPieceRepairRoutine(player, toRepair));
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, "В месте попадания нет поврежденных построек!");
            }
        }

        private static IEnumerator BatchedPieceRepairRoutine(Player player, List<WearNTear> pieces)
        {
            int repaired = 0;
            int inBatch = 0;

            foreach (WearNTear wnt in pieces)
            {
                if (wnt != null && wnt.Repair())
                {
                    repaired++;
                    inBatch++;
                }

                if (inBatch >= 5)
                {
                    inBatch = 0;
                    yield return new WaitForSeconds(0.05f);
                }
            }

            if (repaired > 0 && player != null)
            {
                PlayRepairSound(player.transform.position);
                player.RaiseSkill(Skills.SkillType.Crafting, repaired * 0.2f);
                player.Message(MessageHud.MessageType.Center, $"Отремонтировано построек: {repaired}");
            }
        }

        // ==========================================
        // 8. РЕМЕСЛО: ОВЕРДРАЙВ
        // (Печи, плавильни, углежоги, прялки, мельницы, очистители эйтра и БРОДИЛЬНЫЕ БОЧКИ)
        // ==========================================
        public static void CastOverdrive(Player player)
        {
            float level = RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skills.SkillType.Crafting);
            if (level < 30f)
            {
                player.Message(MessageHud.MessageType.Center, $"Требуется 30 ур. Ремесла (текущий: {level})");
                return;
            }

            int mode = SpellModeManager.GetCurrentMode(SpellType.Overdrive);
            float staminaCost = level >= 75f ? 20f : 40f;
            float healthCost = level >= 90f ? 0f : (level >= 75f ? 10f : 25f);
            float radius = level >= 60f ? 15f : 10f;

            if (mode == 1) // Точечный разгон: расход ресурсов уменьшен вдвое
            {
                staminaCost *= 0.5f;
                healthCost *= 0.5f;
            }

            int maxItemsPerStation = 3;
            if (level >= 90f) maxItemsPerStation = 9999;
            else if (level >= 60f) maxItemsPerStation = 10;
            else if (level >= 45f) maxItemsPerStation = 5;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                if (mode == 1)
                {
                    OverdriveTargetedAtPoint(player, impactPoint, hitCol, maxItemsPerStation, staminaCost, healthCost);
                }
                else
                {
                    OverdriveAtPoint(player, impactPoint, radius, maxItemsPerStation, staminaCost, healthCost);
                }
            });
        }

        private static void OverdriveTargetedAtPoint(Player player, Vector3 impactPoint, Collider hitCol, int maxItemsPerStation, float staminaCost, float healthCost)
        {
            Smelter smelter = hitCol != null ? hitCol.GetComponentInParent<Smelter>() : null;
            Fermenter fermenter = hitCol != null ? hitCol.GetComponentInParent<Fermenter>() : null;
            GameObject modRoot = hitCol != null ? hitCol.transform.root.gameObject : null;

            if (smelter == null && fermenter == null)
            {
                Collider[] near = Physics.OverlapSphere(impactPoint, 1.5f);
                foreach (var c in near)
                {
                    if (c == null) continue;
                    smelter = c.GetComponentInParent<Smelter>();
                    if (smelter != null) break;
                    fermenter = c.GetComponentInParent<Fermenter>();
                    if (fermenter != null) break;
                    if (modRoot == null) modRoot = c.transform.root.gameObject;
                }
            }

            int processed = 0;
            if (smelter != null && smelter.m_nview != null && smelter.m_nview.IsValid())
            {
                smelter.m_nview.ClaimOwnership();
                int queueSize = smelter.GetQueueSize();
                if (queueSize > 0)
                {
                    bool requiresFuel = smelter.m_maxFuel > 0;
                    int fuelPerProduct = smelter.m_fuelPerProduct > 0 ? smelter.m_fuelPerProduct : 1;
                    float currentFuel = smelter.GetFuel();
                    int maxAffordable = requiresFuel ? (int)(currentFuel / fuelPerProduct) : int.MaxValue;
                    int toProcess = Mathf.Min(queueSize, Mathf.Min(maxItemsPerStation, maxAffordable));
                    for (int i = 0; i < toProcess; i++)
                    {
                        string ore = smelter.GetQueuedOre();
                        if (!string.IsNullOrEmpty(ore))
                        {
                            smelter.RemoveOneOre();
                            smelter.QueueProcessed(ore);
                            if (requiresFuel) currentFuel -= fuelPerProduct;
                            processed++;
                        }
                    }
                    if (requiresFuel) smelter.SetFuel(Mathf.Max(0f, currentFuel));
                }
            }
            else if (fermenter != null && fermenter.m_nview != null && fermenter.m_nview.IsValid())
            {
                if (fermenter.GetStatus() == Fermenter.Status.Fermenting)
                {
                    fermenter.m_nview.ClaimOwnership();
                    long netTicks = ZNet.instance.GetTime().Ticks;
                    long reqTicks = (long)((fermenter.m_fermentationDuration + 10f) * 10000000.0);
                    long safeStartTime = netTicks > reqTicks ? (netTicks - reqTicks) : 1L;
                    fermenter.m_nview.GetZDO().Set(ZDOVars.s_startTime, safeStartTime);
                    if (netTicks <= reqTicks)
                    {
                        fermenter.m_fermentationDuration = 0f;
                    }
                    processed++;
                }
            }
            else if (modRoot != null)
            {
                MonoBehaviour[] scripts = modRoot.GetComponentsInChildren<MonoBehaviour>();
                foreach (MonoBehaviour mb in scripts)
                {
                    if (mb != null && mb.GetType().Name == "FishPress")
                    {
                        ZNetView nv = mb.GetComponent<ZNetView>() ?? modRoot.GetComponent<ZNetView>();
                        if (nv != null && nv.IsValid()) nv.ClaimOwnership();
                        MethodInfo isProc = mb.GetType().GetMethod("IsProcessing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        MethodInfo finishProc = mb.GetType().GetMethod("FinishProcessing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        MethodInfo startProc = mb.GetType().GetMethod("StartProcessing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        MethodInfo getQueue = mb.GetType().GetMethod("GetQueueSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        MethodInfo updateVisuals = mb.GetType().GetMethod("UpdateStateVisuals", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                        FieldInfo reqField = mb.GetType().GetField("m_fishRequired", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        int fishReq = reqField != null ? (int)reqField.GetValue(mb) : 1;
                        if (fishReq <= 0) fishReq = 1;

                        bool currentlyProcessing = isProc != null && (bool)isProc.Invoke(mb, null);
                        if (currentlyProcessing)
                        {
                            finishProc?.Invoke(mb, null);
                            processed++;
                        }

                        int stationProcessed = 0;
                        while (stationProcessed < maxItemsPerStation && getQueue != null)
                        {
                            int queue = (int)getQueue.Invoke(mb, null);
                            if (queue < fishReq) break;
                            startProc?.Invoke(mb, null);
                            finishProc?.Invoke(mb, null);
                            stationProcessed++;
                            processed++;
                        }
                        updateVisuals?.Invoke(mb, null);
                        break;
                    }
                }
            }

            if (processed > 0)
            {
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost);
                PlayRepairSound(impactPoint);
                player.RaiseSkill(Skills.SkillType.Crafting, processed * 1.5f);
                player.Message(MessageHud.MessageType.Center, $"Точечный разгон станции завершен! (Переработано: {processed})");
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, "Под прицелом нет работающей или загруженной сырьем станции!");
            }
        }

        private static void OverdriveAtPoint(Player player, Vector3 center, float radius, int maxItemsPerStation, float staminaCost, float healthCost)
        {
            Collider[] colliders = Physics.OverlapSphere(center, radius);

            HashSet<Smelter> smelters = new HashSet<Smelter>();
            HashSet<Fermenter> fermenters = new HashSet<Fermenter>();
            HashSet<GameObject> processedModStations = new HashSet<GameObject>();
            int processedTotal = 0;
            bool lackedFuel = false;

            foreach (Collider col in colliders)
            {
                if (col == null) continue;

                // 1. Печи, углевыжигательные печи, прялки, мельницы, очистители эйтра (все они наследники Smelter)
                Smelter smelter = col.GetComponentInParent<Smelter>();
                if (smelter != null && !smelters.Contains(smelter) && smelter.m_nview != null && smelter.m_nview.IsValid())
                {
                    smelters.Add(smelter);
                    smelter.m_nview.ClaimOwnership();

                    int queueSize = smelter.GetQueueSize();
                    if (queueSize <= 0) continue;

                    bool requiresFuel = smelter.m_maxFuel > 0;
                    int fuelPerProduct = smelter.m_fuelPerProduct > 0 ? smelter.m_fuelPerProduct : 1;
                    float currentFuel = smelter.GetFuel();

                    int maxAffordable = requiresFuel ? (int)(currentFuel / fuelPerProduct) : int.MaxValue;
                    if (requiresFuel && maxAffordable <= 0)
                    {
                        lackedFuel = true;
                        continue;
                    }

                    int toProcess = Mathf.Min(queueSize, Mathf.Min(maxItemsPerStation, maxAffordable));
                    int actuallyProcessed = 0;

                    for (int i = 0; i < toProcess; i++)
                    {
                        string ore = smelter.GetQueuedOre();
                        if (!string.IsNullOrEmpty(ore))
                        {
                            smelter.RemoveOneOre();
                            smelter.QueueProcessed(ore);
                            if (requiresFuel)
                            {
                                currentFuel -= fuelPerProduct;
                            }
                            actuallyProcessed++;
                            processedTotal++;
                        }
                    }

                    if (requiresFuel)
                    {
                        smelter.SetFuel(Mathf.Max(0f, currentFuel));
                    }

                    if (actuallyProcessed > 0)
                    {
                        smelter.SpawnProcessed();
                    }
                }

                // 2. Бродильные бочки (Fermenter)
                Fermenter fermenter = col.GetComponentInParent<Fermenter>();
                if (fermenter != null && !fermenters.Contains(fermenter) && fermenter.m_nview != null && fermenter.m_nview.IsValid())
                {
                    fermenters.Add(fermenter);
                    if (fermenter.GetStatus() == Fermenter.Status.Fermenting)
                    {
                        fermenter.m_nview.ClaimOwnership();
                        // Устанавливаем время начала брожения в глубокое прошлое, переводя бочку в статус Ready (с защитой от сбоя в новых мирах)
                        long netTicks = ZNet.instance.GetTime().Ticks;
                        long reqTicks = (long)((fermenter.m_fermentationDuration + 10f) * 10000000.0);
                        long safeStartTime = netTicks > reqTicks ? (netTicks - reqTicks) : 1L;
                        fermenter.m_nview.GetZDO().Set(ZDOVars.s_startTime, safeStartTime);
                        if (netTicks <= reqTicks)
                        {
                            fermenter.m_fermentationDuration = 0f;
                        }
                        processedTotal++;
                    }
                }

                // 3. Модовые ремесленные станции (OdinShip: FishPress)
                GameObject root = col.transform.root.gameObject;
                if (!processedModStations.Contains(root))
                {
                    MonoBehaviour[] scripts = root.GetComponentsInChildren<MonoBehaviour>();
                    foreach (MonoBehaviour mb in scripts)
                    {
                        if (mb == null) continue;
                        if (mb.GetType().Name == "FishPress")
                        {
                            processedModStations.Add(root);
                            ZNetView nv = mb.GetComponent<ZNetView>() ?? root.GetComponent<ZNetView>();
                            if (nv != null && nv.IsValid())
                            {
                                nv.ClaimOwnership();
                            }

                            MethodInfo isProc = mb.GetType().GetMethod("IsProcessing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            MethodInfo finishProc = mb.GetType().GetMethod("FinishProcessing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            MethodInfo startProc = mb.GetType().GetMethod("StartProcessing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            MethodInfo getQueue = mb.GetType().GetMethod("GetQueueSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            MethodInfo updateVisuals = mb.GetType().GetMethod("UpdateStateVisuals", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                            FieldInfo reqField = mb.GetType().GetField("m_fishRequired", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            int fishReq = reqField != null ? (int)reqField.GetValue(mb) : 1;
                            if (fishReq <= 0) fishReq = 1;

                            int stationProcessed = 0;

                            // 1. Завершаем текущий процесс прессования рыбы (если идет прямо сейчас)
                            bool currentlyProcessing = isProc != null && (bool)isProc.Invoke(mb, null);
                            if (currentlyProcessing)
                            {
                                finishProc?.Invoke(mb, null);
                                stationProcessed++;
                                processedTotal++;
                            }

                            // 2. Перерабатываем рыбу из очереди до лимита овердрайва (maxItemsPerStation)
                            while (stationProcessed < maxItemsPerStation && getQueue != null)
                            {
                                int queue = (int)getQueue.Invoke(mb, null);
                                if (queue < fishReq) break;

                                startProc?.Invoke(mb, null);
                                finishProc?.Invoke(mb, null);
                                stationProcessed++;
                                processedTotal++;
                            }

                            // 3. Если после овердрайва в очереди осталась рыба, запускаем следующий обычный цикл
                            if (getQueue != null && startProc != null && isProc != null)
                            {
                                int queue = (int)getQueue.Invoke(mb, null);
                                bool isP = (bool)isProc.Invoke(mb, null);
                                if (queue >= fishReq && !isP)
                                {
                                    startProc.Invoke(mb, null);
                                }
                            }

                            updateVisuals?.Invoke(mb, null);
                            break;
                        }
                    }
                }
            }

            if (processedTotal > 0)
            {
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, healthCost);
                player.RaiseSkill(Skills.SkillType.Crafting, processedTotal * 0.5f);
                player.Message(MessageHud.MessageType.Center, $"Овердрайв обработал механизмов/ресурсов: {processedTotal}");
            }
            else
            {
                if (lackedFuel)
                {
                    player.Message(MessageHud.MessageType.Center, "В печах нет угля/топлива для переплавки!");
                }
                else
                {
                    player.Message(MessageHud.MessageType.Center, "В радиусе действия нет загруженных механизмов или бочек!");
                }
            }
        }

        public const float MaxProjectileRange = 35f;

        public static void LaunchMagicProjectile(Player player, float maxRange, Action<Vector3, Collider> onImpact)
        {
            if (player == null) return;
            float clampedRange = Mathf.Min(maxRange, MaxProjectileRange);
            RunescapeMagicPlugin.Instance.StartCoroutine(MagicProjectileRoutine(player, clampedRange, onImpact));
        }

        private static IEnumerator MagicProjectileRoutine(Player player, float maxRange, Action<Vector3, Collider> onImpact)
        {
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : player.transform;
            Vector3 camPos = cam.position;
            Vector3 camForward = cam.forward;
            Vector3 eyePos = player.GetEyePoint();

            int mask = ~LayerMask.GetMask("character", "character_net", "character_ghost", "character_trigger", "UI", "pathfinding", "smoke");

            // 1. Точно находим в игровом мире точку под перекрестием прицела камеры (до 100м)
            Vector3 aimTarget = camPos + camForward * maxRange;
            Collider targetCollider = null;

            RaycastHit[] hits = Physics.RaycastAll(camPos + camForward * 1.0f, camForward, 100f, mask);
            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider == null) continue;
                    if (hit.collider.transform.root == player.transform) continue;
                    if (hit.collider.GetComponentInParent<Player>() != null) continue;

                    Vector3 fromEye = hit.point - eyePos;
                    if (Vector3.Dot(camForward, fromEye) <= 0.1f) continue;

                    aimTarget = hit.point;
                    targetCollider = hit.collider;
                    break;
                }
            }

            // 2. Начало полета снаряда: строго перед персонажем на уровне взгляда
            Vector3 startPos = eyePos + camForward * 0.4f;
            Vector3 flyDirection = (aimTarget - startPos).normalized;
            if (flyDirection.sqrMagnitude < 0.01f) flyDirection = camForward;

            float distToTarget = Vector3.Distance(startPos, aimTarget);
            bool targetWithinRange = distToTarget <= maxRange;
            float actualMaxRange = targetWithinRange ? distToTarget : maxRange;

            // 3. Стремительный полет невидимого снаряда к цели (скорость 120 м/с, максимум 35 метров)
            float speed = 120f;
            float traveled = 0f;
            Vector3 currentPos = startPos;

            while (traveled < actualMaxRange)
            {
                float step = speed * Time.deltaTime;
                if (traveled + step > actualMaxRange)
                {
                    step = actualMaxRange - traveled;
                }

                // Физическая проверка препятствий по лучу полета (препятствует стрельбе сквозь стены, скалы и текстуры земли)
                if (Physics.Raycast(currentPos, flyDirection, out RaycastHit hit, step, mask))
                {
                    if (hit.collider != null && hit.collider.transform.root != player.transform && hit.collider.GetComponentInParent<Player>() == null)
                    {
                        onImpact?.Invoke(hit.point, hit.collider);
                        yield break;
                    }
                }

                currentPos += flyDirection * step;
                traveled += step;

                // Если цель находилась в пределах дальности и снаряд достиг цели
                if (targetWithinRange && traveled >= actualMaxRange - 0.01f)
                {
                    onImpact?.Invoke(aimTarget, targetCollider);
                    yield break;
                }

                yield return null;
            }

            // Завершение дистанции полета (не превышая максимальные 35 метров)
            if (targetWithinRange)
            {
                onImpact?.Invoke(aimTarget, targetCollider);
            }
            else
            {
                onImpact?.Invoke(currentPos, null);
            }
        }

        private static Vector3 GetAimPoint(Player player, float maxDistance)
        {
            float clampedDist = Mathf.Min(maxDistance, MaxProjectileRange);
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : player.transform;
            Vector3 camPos = cam.position;
            Vector3 camForward = cam.forward;
            Vector3 eyePos = player.GetEyePoint();

            int mask = ~LayerMask.GetMask("character", "character_net", "character_ghost", "character_trigger", "UI", "pathfinding", "smoke");
            RaycastHit[] hits = Physics.RaycastAll(camPos + camForward * 1.0f, camForward, 100f, mask);
            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider == null) continue;
                    if (hit.collider.transform.root == player.transform) continue;
                    if (hit.collider.GetComponentInParent<Player>() != null) continue;

                    Vector3 fromEye = hit.point - eyePos;
                    if (Vector3.Dot(camForward, fromEye) <= 0.1f) continue;

                    if (fromEye.magnitude <= clampedDist)
                        return hit.point;
                    else
                        return eyePos + camForward * clampedDist;
                }
            }

            return eyePos + camForward * clampedDist;
        }

        public static void PlayRepairSound(Vector3 point)
        {
            try
            {
                // Ищем ванильный молоток или верстак для воспроизведения аутентичного звука ремонта
                if (ObjectDB.instance != null)
                {
                    GameObject hammer = ObjectDB.instance.GetItemPrefab("Hammer");
                    if (hammer != null && hammer.TryGetComponent(out ItemDrop hammerDrop))
                    {
                        if (hammerDrop.m_itemData != null && hammerDrop.m_itemData.m_shared != null)
                        {
                            PieceTable pt = hammerDrop.m_itemData.m_shared.m_buildPieces;
                            if (pt != null && pt.m_pieces != null)
                            {
                                GameObject wb = pt.m_pieces.Find(p => p != null && p.name == "piece_workbench");
                                if (wb != null && wb.TryGetComponent(out CraftingStation cs) && cs.m_repairItemDoneEffects != null)
                                {
                                    cs.m_repairItemDoneEffects.Create(point, Quaternion.identity, null, 1f, -1, default);
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback без звука в случае отсутствия префаба
            }
        }
    }

    public static class SeedBedDynamicPatches
    {
        private static float GetOriginalDuration()
        {
            try
            {
                Type pluginType = AccessTools.TypeByName("SeedBed.Plugin");
                FieldInfo cfgField = pluginType?.GetField("_germinationDuration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                object cfgEntry = cfgField?.GetValue(null);
                if (cfgEntry != null)
                {
                    PropertyInfo valProp = cfgEntry.GetType().GetProperty("Value");
                    if (valProp != null)
                    {
                        return (float)valProp.GetValue(cfgEntry);
                    }
                }
            }
            catch { }
            return 4000f;
        }

        public static bool GetGerminationTimePrefix(MonoBehaviour __instance, ref double __result)
        {
            if (__instance == null) return true;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid())
            {
                if (nv.GetZDO().GetBool("RSM_InstantGrown", false))
                {
                    __result = 99999999.0;
                    return false;
                }
            }
            return true;
        }

        public static void RpcTapPrefix(MonoBehaviour __instance)
        {
            if (__instance == null) return;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid())
            {
                nv.GetZDO().Set("RSM_InstantGrown", false);
            }
            FieldInfo durField = __instance.GetType().GetField("m_germinationDuration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            durField?.SetValue(__instance, GetOriginalDuration());
        }

        public static void RpcAddItemPrefix(MonoBehaviour __instance)
        {
            if (__instance == null) return;
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid())
            {
                nv.GetZDO().Set("RSM_InstantGrown", false);
            }
            FieldInfo durField = __instance.GetType().GetField("m_germinationDuration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            durField?.SetValue(__instance, GetOriginalDuration());
        }
    }
}