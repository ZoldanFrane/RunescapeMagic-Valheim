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
        public const string PluginVersion = "1.0.2";

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
                Log.LogInfo($"{PluginName} v{PluginVersion} успешно загружен!");
            }
            catch (Exception ex)
            {
                Log.LogError($"[RunescapeMagic] Ошибка при инициализации Harmony: {ex}");
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
            HashSet<TreeBase> hitTrees = new HashSet<TreeBase>();
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
                    // Только TreeBase (стоящие не срубленные деревья; бревна TreeLog и пеньки Destructible игнорируются)
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

                        tree.Damage(hit);
                        player.RaiseSkill(Skills.SkillType.WoodCutting, 1.2f);
                    }
                }

                yield return null;
            }

            if (visualAxe != null)
                UnityEngine.Object.Destroy(visualAxe);

            if (hitTrees.Count == 0 && player != null)
            {
                player.Message(MessageHud.MessageType.Center, "Призрачный топор не поразил ни одного дерева!");
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

            float radius = level >= 90f ? 25f : (level >= 35f ? 15f : 10f);
            float staminaCost = level >= 75f ? 15f : 25f;
            float healthCost = level >= 55f ? 0f : 10f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                RunescapeMagicPlugin.Instance.StartCoroutine(SplinterRoutine(player, impactPoint, radius, staminaCost, healthCost));
            });
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

                    log.Damage(hit);
                    player.RaiseSkill(Skills.SkillType.WoodCutting, 1.0f);
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
            float radius = level >= 75f ? 6f : (level >= 35f ? 4f : 2f);
            float staminaCost = level >= 55f ? 10f : 25f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, 0f))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                RunescapeMagicPlugin.Instance.StartCoroutine(OreBlastWaveRoutine(player, impactPoint, radius, level, hitCol, staminaCost));
            });
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

                                blastChunks.Add(new BlastChunk
                                {
                                    Distance = dist,
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
                                blastChunks.Add(new BlastChunk
                                {
                                    Distance = dist,
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
                    blastChunks.Add(new BlastChunk
                    {
                        Distance = dist,
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

            int countInBatch = 0;
            foreach (var chunk in blastChunks)
            {
                chunk.DestroyAction?.Invoke();
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

            // Начисляем опыт кирки ОДИН раз за каст, пропорционально количеству разрушенных объектов (не более 1.5-2.0 за раз)
            if (rocksCount > 0)
            {
                float xpGain = Mathf.Clamp(rocksCount * 0.5f, 0.5f, 2.0f);
                player.RaiseSkill(Skills.SkillType.Pickaxes, xpGain);
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
            float radius = level >= 75f ? 15f : (level >= 20f ? 10f : 5f);
            float staminaCost = level >= 75f ? 5f : (level >= 35f ? 10f : 15f);
            bool autoPickup = level >= 50f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, 0f))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                AreaHarvestAtPoint(player, impactPoint, radius, autoPickup, staminaCost);
            });
        }

        private static void AreaHarvestAtPoint(Player player, Vector3 center, float radius, bool autoPickup, float staminaCost)
        {
            Collider[] colliders = Physics.OverlapSphere(center, radius);
            HashSet<Pickable> picked = new HashSet<Pickable>();
            HashSet<PickableItem> pickedItems = new HashSet<PickableItem>();
            int collectedCount = 0;

            foreach (Collider col in colliders)
            {
                if (col == null) continue;

                // 1. Созревшие культуры, кусты ягод, грибы
                Pickable pickable = col.GetComponentInParent<Pickable>();
                if (pickable != null && !picked.Contains(pickable) && pickable.CanBePicked())
                {
                    picked.Add(pickable);
                    pickable.Interact(player, false, false);
                    collectedCount++;
                }

                // 2. Подбираемые предметы на земле (PickableItem: ветки, камни)
                PickableItem pickableItem = col.GetComponentInParent<PickableItem>();
                if (pickableItem != null && !pickedItems.Contains(pickableItem))
                {
                    pickedItems.Add(pickableItem);
                    pickableItem.Interact(player, false, false);
                    collectedCount++;
                }
            }

            if (collectedCount > 0)
            {
                RunescapeMagicPlugin.TryConsumeResources(player, staminaCost, 0f);
                player.RaiseSkill(Skills.SkillType.Farming, collectedCount * 0.5f);
                player.Message(MessageHud.MessageType.Center, $"Собрано растений/ресурсов: {collectedCount}");

                // На 50+ уровне переносим выпавший лут прямо в инвентарь игрока
                if (autoPickup)
                {
                    RunescapeMagicPlugin.Instance.StartCoroutine(AutoPickupRoutine(player, center, radius + 2f));
                }
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, "В радиусе нет спелых растений для сбора!");
            }
        }

        private static IEnumerator AutoPickupRoutine(Player player, Vector3 center, float radius)
        {
            yield return new WaitForSeconds(0.2f);
            if (player == null || player.GetInventory() == null) yield break;

            Collider[] drops = Physics.OverlapSphere(center, radius);
            foreach (Collider c in drops)
            {
                if (c == null) continue;
                ItemDrop itemDrop = c.GetComponentInParent<ItemDrop>();
                if (itemDrop != null && itemDrop.CanPickup() && itemDrop.m_itemData != null)
                {
                    if (player.GetInventory().CanAddItem(itemDrop.m_itemData, -1))
                    {
                        if (player.GetInventory().AddItem(itemDrop.m_itemData))
                        {
                            if (itemDrop.m_nview != null && itemDrop.m_nview.IsValid())
                            {
                                itemDrop.m_nview.ClaimOwnership();
                                itemDrop.m_nview.Destroy();
                            }
                            else
                            {
                                UnityEngine.Object.Destroy(itemDrop.gameObject);
                            }
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

            float radius = level >= 90f ? 15f : (level >= 50f ? 10f : 6f);
            float staminaCost = level >= 75f ? 15f : 30f;
            float healthCost = level >= 90f ? 0f : (level >= 75f ? 5f : 15f);

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                InstantGrowAtPoint(player, impactPoint, radius, level, staminaCost, healthCost);
            });
        }

        private static void InstantGrowAtPoint(Player player, Vector3 center, float radius, float level, float staminaCost, float healthCost)
        {
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
                    if (CanGrowPlant(prefabName, level))
                    {
                        processedRoots.Add(root);
                        plant.m_nview.ClaimOwnership();
                        // Принудительно задаем статус Healthy и перематываем время посадки в прошлое
                        plant.m_status = Plant.Status.Healthy;
                        plant.m_nview.GetZDO().Set(ZDOVars.s_plantTime, (ZNet.instance.GetTime() - TimeSpan.FromDays(5)).Ticks);
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
                MonoBehaviour[] scripts = col.GetComponentsInParent<MonoBehaviour>();
                bool handledMod = false;
                foreach (MonoBehaviour mb in scripts)
                {
                    if (mb == null) continue;
                    string tName = mb.GetType().Name;

                    if (tName == "Germination")
                    {
                        processedRoots.Add(root);
                        ZNetView nv = mb.GetComponent<ZNetView>();
                        if (nv != null && nv.IsValid())
                        {
                            nv.ClaimOwnership();
                            nv.GetZDO().Set(ZDOVars.s_startTime, (ZNet.instance.GetTime() - TimeSpan.FromHours(24)).Ticks);
                        }

                        MethodInfo slowUpdate = mb.GetType().GetMethod("SlowUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        slowUpdate?.Invoke(mb, null);
                        grownCount++;
                        handledMod = true;
                        break;
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
            float radius = level >= 80f ? 25f : (level >= 25f ? 15f : 10f);
            float staminaCost = level >= 60f ? 10f : 25f;
            bool ignoreStation = level >= 40f;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, 0f))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                RepairPiecesAtPoint(player, impactPoint, radius, ignoreStation, staminaCost);
            });
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

            float staminaCost = level >= 75f ? 20f : 40f;
            float healthCost = level >= 90f ? 0f : (level >= 75f ? 10f : 25f);
            float radius = level >= 60f ? 15f : 10f;

            int maxItemsPerStation = 3;
            if (level >= 90f) maxItemsPerStation = 9999;
            else if (level >= 60f) maxItemsPerStation = 10;
            else if (level >= 45f) maxItemsPerStation = 5;

            if (!RunescapeMagicPlugin.HasResources(player, staminaCost, healthCost))
                return;

            LaunchMagicProjectile(player, 35f, (impactPoint, hitCol) =>
            {
                OverdriveAtPoint(player, impactPoint, radius, maxItemsPerStation, staminaCost, healthCost);
            });
        }

        private static void OverdriveAtPoint(Player player, Vector3 center, float radius, int maxItemsPerStation, float staminaCost, float healthCost)
        {
            Collider[] colliders = Physics.OverlapSphere(center, radius);

            HashSet<Smelter> smelters = new HashSet<Smelter>();
            HashSet<Fermenter> fermenters = new HashSet<Fermenter>();
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
                        // Устанавливаем время начала брожения в глубокое прошлое, переводя бочку в статус Ready
                        fermenter.m_nview.GetZDO().Set(ZDOVars.s_startTime, (ZNet.instance.GetTime() - TimeSpan.FromSeconds(fermenter.m_fermentationDuration + 10f)).Ticks);
                        processedTotal++;
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
}