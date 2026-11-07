using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RunescapeMagicValheim
{
    [HarmonyPatch]
    public static class MagicWandItem
    {
        private static GameObject _wandPrefab;
        private static GameObject _prefabContainer;

        /// <summary>
        /// 1. Создаем префаб палочки и регистрируем его в ZNetScene при загрузке мира,
        /// чтобы предмет корректно спавнился на земле и синхронизировался в мультиплеере.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        [HarmonyPostfix]
        public static void OnZNetSceneAwakePostfix(ZNetScene __instance)
        {
            if (__instance == null) return;

            try
            {
                if (_wandPrefab == null)
                {
                    _wandPrefab = CreateWandPrefab(__instance);
                }

                if (_wandPrefab != null && !__instance.m_prefabs.Contains(_wandPrefab))
                {
                    __instance.m_prefabs.Add(_wandPrefab);
                    __instance.m_namedPrefabs[_wandPrefab.name.GetStableHashCode()] = _wandPrefab;
                }

                // Индексируем соответствие всех пней и деревьев в мире для быстророста
                SpellMechanics.IndexTreeStubs(__instance);
            }
            catch (System.Exception ex)
            {
                RunescapeMagicPlugin.Log.LogError($"[MagicWandItem] Ошибка инициализации префаба в ZNetScene: {ex}");
            }
        }

        /// <summary>
        /// 2. Регистрируем сам предмет и его рецепт на верстаке в ObjectDB.
        /// Вызываем и в Awake, и в CopyOtherDB, чтобы рецепт не пропадал при перезаходе в главное меню.
        /// </summary>
        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
        [HarmonyPostfix]
        public static void OnObjectDBAwakePostfix(ObjectDB __instance)
        {
            try
            {
                RegisterItemAndRecipe(__instance);
            }
            catch (System.Exception ex)
            {
                RunescapeMagicPlugin.Log.LogError($"[MagicWandItem] Ошибка регистрации в ObjectDB.Awake: {ex}");
            }
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        [HarmonyPostfix]
        public static void OnObjectDBCopyPostfix(ObjectDB __instance)
        {
            try
            {
                RegisterItemAndRecipe(__instance);
            }
            catch (System.Exception ex)
            {
                RunescapeMagicPlugin.Log.LogError($"[MagicWandItem] Ошибка регистрации в ObjectDB.CopyOtherDB: {ex}");
            }
        }

        // Автоматически открываем рецепт палочки локальному игроку при заходе в мир
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        [HarmonyPostfix]
        public static void OnPlayerSpawnedPostfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || ObjectDB.instance == null) return;

            string recipeName = "Recipe_" + RunescapeMagicPlugin.WandPrefabName;
            Recipe wandRecipe = ObjectDB.instance.m_recipes?.Find(r => r != null && r.name == recipeName);
            if (wandRecipe != null && !__instance.IsRecipeKnown(wandRecipe.m_item.m_itemData.m_shared.m_name))
            {
                __instance.AddKnownRecipe(wandRecipe);
            }
        }

        private static GameObject CreateWandPrefab(ZNetScene znet)
        {
            // Берем стандартную дубинку как донор анимаций, коллайдера и точки хвата руки (attach)
            GameObject baseClub = znet.GetPrefab("Club");
            if (baseClub == null) return null;

            // Создаем скрытый контейнер, чтобы наш клон не валялся прямо на сцене в координатах (0,0,0)
            if (_prefabContainer == null)
            {
                _prefabContainer = new GameObject("RunescapeMagicPrefabContainer");
                Object.DontDestroyOnLoad(_prefabContainer);
                _prefabContainer.SetActive(false);
            }

            GameObject wand = Object.Instantiate(baseClub, _prefabContainer.transform);
            wand.name = RunescapeMagicPlugin.WandPrefabName; // "RunescapeMagicWand"

            // --- ВИЗУАЛ: Превращаем грубую дубинку в аккуратную волшебную палочку ---
            Material clubMat = null;
            MeshRenderer wandMr = wand.GetComponentInChildren<MeshRenderer>(true);
            if (wandMr != null)
            {
                // Исходная ориентация
                wandMr.transform.localRotation = Quaternion.identity;
                // Пропорции палочки: умеренная длина (~40-45 см)
                wandMr.transform.localScale = new Vector3(0.40f, 0.40f, 0.50f);

                // Клонируем и разворачиваем меш "вверх ногами" (на 180°), чтобы толстая часть дубинки
                // была рукоятью в ладони, а к кончику шло красивое утоньшение
                try
                {
                    MeshFilter mf = wandMr.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        Mesh mesh = Object.Instantiate(mf.sharedMesh);
                        Vector3[] vertices = mesh.vertices;
                        Vector3[] normals = mesh.normals;

                        if (vertices != null && vertices.Length > 0)
                        {
                            Vector3 min = vertices[0];
                            Vector3 max = vertices[0];
                            for (int i = 1; i < vertices.Length; i++)
                            {
                                min = Vector3.Min(min, vertices[i]);
                                max = Vector3.Max(max, vertices[i]);
                            }

                            Vector3 span = max - min;

                            if (span.z >= span.x && span.z >= span.y)
                            {
                                float sumZ = min.z + max.z;
                                float sumX = min.x + max.x;
                                for (int i = 0; i < vertices.Length; i++)
                                {
                                    vertices[i].z = sumZ - vertices[i].z;
                                    vertices[i].x = sumX - vertices[i].x;
                                    if (normals != null && i < normals.Length)
                                    {
                                        normals[i].z = -normals[i].z;
                                        normals[i].x = -normals[i].x;
                                    }
                                }
                            }
                            else if (span.y >= span.x && span.y >= span.z)
                            {
                                float sumY = min.y + max.y;
                                float sumX = min.x + max.x;
                                for (int i = 0; i < vertices.Length; i++)
                                {
                                    vertices[i].y = sumY - vertices[i].y;
                                    vertices[i].x = sumX - vertices[i].x;
                                    if (normals != null && i < normals.Length)
                                    {
                                        normals[i].y = -normals[i].y;
                                        normals[i].x = -normals[i].x;
                                    }
                                }
                            }
                            else
                            {
                                float sumX = min.x + max.x;
                                float sumZ = min.z + max.z;
                                for (int i = 0; i < vertices.Length; i++)
                                {
                                    vertices[i].x = sumX - vertices[i].x;
                                    vertices[i].z = sumZ - vertices[i].z;
                                    if (normals != null && i < normals.Length)
                                    {
                                        normals[i].x = -normals[i].x;
                                        normals[i].z = -normals[i].z;
                                    }
                                }
                            }

                            mesh.vertices = vertices;
                            if (normals != null && normals.Length == vertices.Length)
                            {
                                mesh.normals = normals;
                            }
                            mesh.RecalculateBounds();
                            mf.sharedMesh = mesh;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    RunescapeMagicPlugin.Log.LogWarning($"[MagicWandItem] Ошибка разворота меша: {ex}");
                }

                if (wandMr.materials != null && wandMr.materials.Length > 0)
                {
                    clubMat = wandMr.materials[0];
                    foreach (Material mat in wandMr.materials)
                    {
                        if (mat != null) mat.color = new Color(0.24f, 0.18f, 0.32f);
                    }
                }
            }

            // Добавляем светящееся магическое ядро строго на дальний тонкий рабочий кончик палочки
            AddMagicalTipEffect(wand.transform, wandMr, clubMat);

            // --- ХАРАКТЕРИСТИКИ ПРЕДМЕТА: Одноручный магический проводник ---
            ItemDrop itemDrop = wand.GetComponent<ItemDrop>();
            if (itemDrop != null && itemDrop.m_itemData != null && itemDrop.m_itemData.m_shared != null)
            {
                ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;

                shared.m_itemType = ItemDrop.ItemData.ItemType.OneHandedWeapon;
                shared.m_name = "Волшебная палочка";
                shared.m_description = "<color=#ffd700>Бытовая магия</color>\nПКМ — выбор заклинания\nЛКМ — сотворение магии";
                shared.m_weight = 0.5f;
                shared.m_maxQuality = 1;
                shared.m_useDurability = false;
                shared.m_damages = new HitData.DamageTypes(); // Обнуляем боевой урон
                shared.m_timedBlockBonus = 0f;
                shared.m_deflectionForce = 0f;
                shared.m_blockPower = 0f;
                if (shared.m_attack != null)
                {
                    shared.m_attack.m_attackStamina = 0f;
                }

                // Иконка предмета: кастомная magic_wand.png (или факел/дубинка в качестве резерва)
                Sprite wandSprite = SpellRadialMenu.GetCustomSprite("magic_wand.png");
                if (wandSprite != null)
                {
                    shared.m_icons = new Sprite[] { wandSprite };
                }
                else
                {
                    GameObject iconDonor = znet.GetPrefab("Torch") ?? znet.GetPrefab("Club");
                    if (iconDonor != null && iconDonor.TryGetComponent(out ItemDrop donorDrop))
                    {
                        shared.m_icons = donorDrop.m_itemData.m_shared.m_icons;
                    }
                }
            }

            return wand;
        }

        /// <summary>
        /// Создает светящуюся сферу строго на кончике палочки (используя вершины меша модели).
        /// </summary>
        private static void AddMagicalTipEffect(Transform wandRoot, MeshRenderer wandMr, Material baseMat)
        {
            Transform attach = wandRoot.Find("attach") ?? wandRoot;

            GameObject orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orb.name = "MagicCoreOrb";
            Object.DestroyImmediate(orb.GetComponent<Collider>()); // Убираем коллайдер

            orb.transform.SetParent(attach, false);

            // Вычисляем точную позицию кончика палочки в пространстве attach
            Vector3 tipPos = new Vector3(0f, 0f, 0.48f); // Безопасное значение по умолчанию
            if (wandMr != null)
            {
                MeshFilter mf = wandMr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    // Находим вершину модели, максимально удаленную от точки хвата в ладони (0,0,0)
                    float maxDistSqr = 0f;
                    Vector3[] vertices = mf.sharedMesh.vertices;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Vector3 worldV = wandMr.transform.TransformPoint(vertices[i]);
                        Vector3 attachV = attach.InverseTransformPoint(worldV);
                        float distSqr = attachV.sqrMagnitude;
                        if (distSqr > maxDistSqr)
                        {
                            maxDistSqr = distSqr;
                            tipPos = attachV;
                        }
                    }
                }
            }

            orb.transform.localPosition = tipPos;
            orb.transform.localScale = new Vector3(0.08f, 0.08f, 0.08f);

            MeshRenderer orbRenderer = orb.GetComponent<MeshRenderer>();
            if (orbRenderer != null)
            {
                Material orbMat = baseMat != null ? new Material(baseMat) : new Material(orbRenderer.material);
                orbMat.color = new Color(0.2f, 1f, 0.85f);
                if (orbMat.HasProperty("_EmissionColor"))
                {
                    orbMat.EnableKeyword("_EMISSION");
                    orbMat.SetColor("_EmissionColor", new Color(0.2f, 1f, 0.85f) * 2.5f);
                }
                orbRenderer.material = orbMat;
            }

            // Мягкий бирюзовый свет вокруг рабочего кончика палочки
            Light magicLight = orb.AddComponent<Light>();
            magicLight.type = LightType.Point;
            magicLight.color = new Color(0.2f, 0.95f, 0.8f);
            magicLight.range = 3.0f;
            magicLight.intensity = 1.3f;
            magicLight.shadows = LightShadows.None;
        }

        private static void RegisterItemAndRecipe(ObjectDB odb)
        {
            if (odb == null || odb.m_items == null || odb.m_items.Count == 0 || _wandPrefab == null) return;

            // Регистрируем сам предмет в базе ObjectDB
            if (odb.GetItemPrefab(RunescapeMagicPlugin.WandPrefabName) == null)
            {
                odb.m_items.Add(_wandPrefab);
                if (odb.m_itemByHash != null)
                {
                    odb.m_itemByHash[_wandPrefab.name.GetStableHashCode()] = _wandPrefab;
                }
            }

            // Проверяем, не добавлен ли уже рецепт
            string recipeName = "Recipe_" + RunescapeMagicPlugin.WandPrefabName;
            if (odb.m_recipes != null && odb.m_recipes.Exists(r => r != null && r.name == recipeName))
                return;

            ItemDrop wandItem = _wandPrefab.GetComponent<ItemDrop>();
            if (wandItem == null) return;

            // Гарантируем кастомную иконку палочки
            Sprite customWandIcon = SpellRadialMenu.GetCustomSprite("magic_wand.png");
            if (customWandIcon != null && wandItem.m_itemData?.m_shared != null)
            {
                wandItem.m_itemData.m_shared.m_icons = new Sprite[] { customWandIcon };
            }

            // Собираем рецепт для обычного Верстака (piece_workbench)
            Recipe wandRecipe = ScriptableObject.CreateInstance<Recipe>();
            wandRecipe.name = recipeName;
            wandRecipe.m_item = wandItem;
            wandRecipe.m_amount = 1;
            wandRecipe.m_enabled = true;
            wandRecipe.m_minStationLevel = 1;

            // Находим станцию верстака (piece_workbench)
            if (wandRecipe.m_craftingStation == null)
            {
                // Вариант 1: Берем ссылку на станцию из любого ванильного рецепта верстака (например, деревянный щит или лук)
                Recipe donorRecipe = odb.m_recipes?.Find(r => r != null && r.m_craftingStation != null && r.m_craftingStation.name == "piece_workbench");
                if (donorRecipe != null)
                {
                    wandRecipe.m_craftingStation = donorRecipe.m_craftingStation;
                }
                else
                {
                    // Вариант 2: Ищем в строительной таблице молотка
                    GameObject hammerPrefab = odb.GetItemPrefab("Hammer");
                    if (hammerPrefab != null && hammerPrefab.TryGetComponent(out ItemDrop hammerDrop))
                    {
                        if (hammerDrop.m_itemData != null && hammerDrop.m_itemData.m_shared != null)
                        {
                            PieceTable buildTable = hammerDrop.m_itemData.m_shared.m_buildPieces;
                            if (buildTable != null && buildTable.m_pieces != null)
                            {
                                GameObject workbenchObj = buildTable.m_pieces.Find(p => p != null && p.name == "piece_workbench");
                                if (workbenchObj != null)
                                {
                                    wandRecipe.m_craftingStation = workbenchObj.GetComponent<CraftingStation>();
                                }
                            }
                        }
                    }
                }
            }

            // Задаем стоимость крафта: 10 Дерева, 4 Смолы и 1 Глаз грейдворфа
            List<Piece.Requirement> reqs = new List<Piece.Requirement>();
            AddRequirement(odb, reqs, "Wood", 10);
            AddRequirement(odb, reqs, "Resin", 4);
            AddRequirement(odb, reqs, "GreydwarfEye", 1);

            wandRecipe.m_resources = reqs.ToArray();
            if (odb.m_recipes != null)
            {
                odb.m_recipes.Add(wandRecipe);
            }

            RunescapeMagicPlugin.Log.LogInfo("Волшебная палочка и рецепт на верстаке успешно зарегистрированы! Стоимость: 10 Дерева, 4 Смолы, 1 Глаз грейдворфа.");
        }

        private static void AddRequirement(ObjectDB odb, List<Piece.Requirement> list, string resName, int amount)
        {
            GameObject resPrefab = odb.GetItemPrefab(resName);
            if (resPrefab != null && resPrefab.TryGetComponent(out ItemDrop resItem))
            {
                list.Add(new Piece.Requirement
                {
                    m_resItem = resItem,
                    m_amount = amount,
                    m_amountPerLevel = amount,
                    m_recover = true
                });
            }
        }

        // Заменяем громоздкую боевую подсказку оружия на элегантное описание бытовой магии
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new System.Type[] { typeof(int) })]
        [HarmonyPostfix]
        public static void OnItemGetTooltipPostfix(ItemDrop.ItemData __instance, ref string __result)
        {
            if (__instance != null && __instance.m_shared != null && (__instance.m_shared.m_name == "Волшебная палочка" || __instance.m_dropPrefab?.name == RunescapeMagicPlugin.WandPrefabName))
            {
                __result = $"<color=#ffd700>Бытовая магия</color>\n\n<color=#ffffff>ПКМ</color> — выбор заклинания\n<color=#ffffff>ЛКМ</color> — сотворение магии\n\nВес: {__instance.m_shared.m_weight:0.0}";
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new System.Type[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        public static void OnStaticGetTooltipPostfix(ItemDrop.ItemData item, ref string __result)
        {
            if (item != null && item.m_shared != null && (item.m_shared.m_name == "Волшебная палочка" || item.m_dropPrefab?.name == RunescapeMagicPlugin.WandPrefabName))
            {
                __result = $"<color=#ffd700>Бытовая магия</color>\n\n<color=#ffffff>ПКМ</color> — выбор заклинания\n<color=#ffffff>ЛКМ</color> — сотворение магии\n\nВес: {item.m_shared.m_weight:0.0}";
            }
        }
    }
}
