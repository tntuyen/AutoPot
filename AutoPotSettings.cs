using System.Collections.Generic;
using ClickableTransparentOverlay.Win32;
using OriathHub.RemoteEnums;

namespace AutoPot
{
    /// <summary>Which vital pool.</summary>
    public enum VitalKind
    {
        Life,
        Mana,
        EnergyShield,
        Ward,
    }

    /// <summary>Per-vital configuration.</summary>
    public sealed class VitalRule
    {
        /// <summary>Whether this vital is monitored.</summary>
        public bool Enabled = true;

        /// <summary>
        ///     Which flask slot this vital triggers (0 = None/display-only, 1 = Flask 1, 2 = Flask 2).
        ///     The actual key comes from <see cref="AutoPotSettings.Flask1Key"/> / <see cref="AutoPotSettings.Flask2Key"/>.
        /// </summary>
        public int FlaskSlot = 0;

        /// <summary>Trigger when the vital drops to or below this percentage.</summary>
        public int TriggerPercent = 50;

        /// <summary>Minimum milliseconds between flask uses.</summary>
        public int RepeatDelayMs = 250;

        /// <summary>Whether to auto-disconnect (kill TCP) when the vital is critically low.</summary>
        public bool AutoDisconnect = false;

        /// <summary>Auto-disconnect threshold percentage.</summary>
        public int AutoDisconnectPercent = 10;
    }

    /// <summary>
    ///     "Surrounded" condition: press a flask when at least <see cref="MinCount"/> monsters of
    ///     rarity &gt;= <see cref="MinRarity"/> are within range of the player (range is a single
    ///     global setting shared by all conditions — see <see cref="AutoPotSettings.MonsterTriggerRange"/>).
    ///     Idea from Murdoc (Discord): "If [number] monster's [Normal/Magic/Rare/Unique]+ rarity in
    ///     [number] range".
    /// </summary>
    public sealed class MonsterTriggerRule
    {
        /// <summary>User-editable label shown in place of "Enabled" so multiple conditions are easy to tell apart.</summary>
        public string Name = "Condition";

        /// <summary>Whether this condition is monitored.</summary>
        public bool Enabled = false;

        /// <summary>Minimum monster rarity to count (monster.Rarity &gt;= this).</summary>
        public Rarity MinRarity = Rarity.Rare;

        /// <summary>How many qualifying monsters must be in range to trigger.</summary>
        public int MinCount = 3;

        /// <summary>Which flask slot to press (0 = None, 1 = Flask 1, 2 = Flask 2).</summary>
        public int FlaskSlot = 0;
    }

    /// <summary>Persisted AutoPot settings.</summary>
    public sealed class AutoPotSettings
    {
        /// <summary>Show vitals overlay on screen while playing.</summary>
        public bool ShowOverlay = false;

        /// <summary>Global hotkey to instantly disconnect (kill TCP). Unset = disabled.</summary>
        public VK DisconnectKey = VK.NONAME;

        /// <summary>Key bound to flask slot 1. Unset = disabled.</summary>
        public VK Flask1Key = VK.KEY_1;

        /// <summary>Key bound to flask slot 2. Unset = disabled.</summary>
        public VK Flask2Key = VK.KEY_2;

        /// <summary>Range in grid units (same unit as Entity.DistanceFrom), shared by every Monster proximity condition.</summary>
        public int MonsterTriggerRange = 40;

        /// <summary>Draw a circle around the player at MonsterTriggerRange, to visually test the setting.</summary>
        public bool ShowMonsterRangeCircle = false;

        public VitalRule Life         { get; set; } = new() { FlaskSlot = 1, TriggerPercent = 76 };
        public VitalRule Mana         { get; set; } = new() { FlaskSlot = 2, TriggerPercent = 50 };
        public VitalRule EnergyShield { get; set; } = new() { TriggerPercent = 50 };
        public VitalRule Ward         { get; set; } = new() { TriggerPercent = 50 };

        public List<MonsterTriggerRule> MonsterTriggers { get; set; } = new();
    }
}
