using System;
using System.Collections.Generic;

namespace RunescapeMagicValheim
{
    public struct SpellModeInfo
    {
        public int ModeIndex;
        public string Name;
        public string Description;
        public float ReqLevel;
        public Skills.SkillType Skill;

        public SpellModeInfo(int modeIndex, string name, string description, float reqLevel = 0f, Skills.SkillType skill = Skills.SkillType.None)
        {
            ModeIndex = modeIndex;
            Name = name;
            Description = description;
            ReqLevel = reqLevel;
            Skill = skill;
        }

        public bool IsUnlocked(Player player)
        {
            if (ReqLevel <= 0f || Skill == Skills.SkillType.None) return true;
            return RunescapeMagicPlugin.GetEffectiveSkillLevel(player, Skill) >= ReqLevel;
        }
    }

    public static class SpellModeManager
    {
        private static readonly Dictionary<SpellType, int> _activeModes = new Dictionary<SpellType, int>();

        public static readonly Dictionary<SpellType, SpellModeInfo[]> Modes = new Dictionary<SpellType, SpellModeInfo[]>
        {
            {
                SpellType.SpectralAxe, new[]
                {
                    new SpellModeInfo(0, "Режим лесоруба", "Срубает только целые стоячие деревья"),
                    new SpellModeInfo(1, "Режим кустореза", "Срубает кустарники, молодняк и старые декоративные стволы (не трогает деревья и пни)")
                }
            },
            {
                SpellType.Splinter, new[]
                {
                    new SpellModeInfo(0, "Стандартный режим", "Расщепляет бревна в полном радиусе действия"),
                    new SpellModeInfo(1, "Точечный режим", "Расщепляет выбранное бревно или пенёк под прицелом (без затрат здоровья)")
                }
            },
            {
                SpellType.OreBlast, new[]
                {
                    new SpellModeInfo(0, "Стандартный режим", "Подрывает рудные жилы и валуны в полном радиусе"),
                    new SpellModeInfo(1, "Компактный режим", "Уменьшенный вдвое радиус взрыва и сниженный вдвое расход выносливости"),
                    new SpellModeInfo(2, "Режим кирки", "Точечный удар по жиле под прицелом (15 выносливости на старте, 5 на 55 ур.)")
                }
            },
            {
                SpellType.AreaHarvest, new[]
                {
                    new SpellModeInfo(0, "Стандартный режим", "Собирает все спелые культуры, ягоды и грибы в радиусе"),
                    new SpellModeInfo(1, "Режим подбора", "Собирает только лежащие на земле ресурсы прямо в инвентарь", 50f, Skills.SkillType.Farming),
                    new SpellModeInfo(2, "Точечный сбор", "Собирает только урожай или лежащие предметы того вида, на который наведен прицел")
                }
            },
            {
                SpellType.InstantGrow, new[]
                {
                    new SpellModeInfo(0, "Стандартный режим", "Мгновенно ускоряет рост всех культур и саженцев в радиусе"),
                    new SpellModeInfo(1, "Компактный режим", "Уменьшенный вдвое радиус роста (расход ресурсов уменьшен вдвое)"),
                    new SpellModeInfo(2, "Точечный рост", "Ускоряет рост только саженцев того вида, на который наведен прицел (расход уменьшен вдвое)")
                }
            },
            {
                SpellType.InventoryRepair, new[]
                {
                    new SpellModeInfo(0, "Свой инвентарь", "Чинит экипировку и инструменты в вашем инвентаре"),
                    new SpellModeInfo(1, "Режим прицела", "Чинит вещи в сундуке, на стойке для брони/предметов или у союзника под прицелом")
                }
            },
            {
                SpellType.PieceRepair, new[]
                {
                    new SpellModeInfo(0, "Стандартный ремонт", "Восстанавливает прочность поврежденных построек до 100%"),
                    new SpellModeInfo(1, "Режим укрепления", "Временно поднимает прочность построек до 150% (+50% щит, расход 10 HP)", 50f, Skills.SkillType.Crafting)
                }
            },
            {
                SpellType.Overdrive, new[]
                {
                    new SpellModeInfo(0, "Стандартный режим", "Перегружает все печи и механизмы в радиусе действия"),
                    new SpellModeInfo(1, "Точечный разгон", "Перегружает только ту станцию, на которую направлен прицел (расход уменьшен вдвое)")
                }
            }
        };

        public static int GetCurrentMode(SpellType spell)
        {
            if (_activeModes.TryGetValue(spell, out int mode))
                return mode;
            return 0;
        }

        public static SpellModeInfo GetCurrentModeInfo(Player player, SpellType spell)
        {
            if (!Modes.TryGetValue(spell, out SpellModeInfo[] list) || list.Length == 0)
                return new SpellModeInfo(0, "Стандартный режим", "");

            int cur = GetCurrentMode(spell);
            if (cur < 0 || cur >= list.Length) cur = 0;
            return list[cur];
        }

        public static void CycleMode(Player player, SpellType spell, int direction)
        {
            if (!Modes.TryGetValue(spell, out SpellModeInfo[] list) || list.Length <= 1)
                return;

            int cur = GetCurrentMode(spell);
            int next = cur;
            int count = list.Length;

            for (int i = 0; i < count; i++)
            {
                next = (next + direction + count) % count;
                if (list[next].IsUnlocked(player))
                {
                    _activeModes[spell] = next;
                    TopModePlateUI.Instance?.OnModeChanged();
                    return;
                }
            }

            // Если доступен только один режим, сообщим игроку о требуемом уровне для заблокированного
            for (int i = 0; i < count; i++)
            {
                int checkIdx = (cur + direction + count) % count;
                if (!list[checkIdx].IsUnlocked(player))
                {
                    player.Message(MessageHud.MessageType.Center, $"{list[checkIdx].Name} открывается на {list[checkIdx].ReqLevel} уровне навыка!");
                    break;
                }
            }
        }
    }
}
