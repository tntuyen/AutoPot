using ClickableTransparentOverlay.Win32;

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

        public VitalRule Life         { get; set; } = new() { FlaskSlot = 1, TriggerPercent = 76 };
        public VitalRule Mana         { get; set; } = new() { FlaskSlot = 2, TriggerPercent = 50 };
        public VitalRule EnergyShield { get; set; } = new() { TriggerPercent = 50 };
        public VitalRule Ward         { get; set; } = new() { TriggerPercent = 50 };
    }
}
