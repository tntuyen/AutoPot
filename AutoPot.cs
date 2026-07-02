using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ClickableTransparentOverlay.Win32;
using ImGuiNET;
using OriathHub;
using OriathHub.RemoteEnums;
using OriathHub.RemoteObjects.Components;
using OriathHub.RemoteObjects.States.InGameStateObjects;
using OriathHub.Utils;

namespace AutoPot
{
    /// <summary>
    ///     Uses flasks automatically when a watched vital drops below a configurable
    ///     percentage.  Checks flask charges and active status before pressing
    ///     (like the Auto HotKey Trigger conditions).
    /// </summary>
    public sealed class AutoPotPlugin : OriathHub.Plugin.PluginBase
    {
        private AutoPotSettings _s = null!;
        private readonly Dictionary<VitalKind, DateTime> _lastUsed = new();

        // ── Auto-disconnect hysteresis: false = disarmed (already fired, waiting for recovery). ──
        private readonly Dictionary<VitalKind, bool> _discArmed = new();

        private const int AutoDisconnectRearmBuffer = 15;

        // Key-capture state (only one capture active at a time).
        private CaptureTarget _capturing = CaptureTarget.None;

        private enum CaptureTarget { None, Disconnect, Flask1, Flask2 }

        // Cached flask state (refreshed each frame).
        private bool[]? _flaskActive;
        private Inventory? _flaskInventory;

        private FileInfo SettingsFile => new(Path.Combine(DllDirectory, "config", "settings.json"));

        public override string Name        => "AutoPot";
        public override string Author      => "kanguru86";
        public override string Description => "Automatically uses flasks when life / ES / mana / ward drops below a threshold.";
        public override string Version     => "1.0.0";

        // ── Lifecycle ──────────────────────────────────────────────────

        public override void OnEnable(bool isGameOpened)
        {
            _s = JsonHelper.CreateOrLoadJsonFile<AutoPotSettings>(SettingsFile);
        }

        public override void OnDisable() { }

        public override void SaveSettings() => JsonHelper.SaveToFile(_s, SettingsFile);

        // ── Logic (runs every frame while in-game) ─────────────────────

        public override void DrawUI()
        {
            if (_s == null) return;
            if (Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            // Need game OR overlay in foreground for overlay/disconnect to work.
            if (!FocusHelper.IsGameOrOverlayForeground()) return;

            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null) return;

            var player = area.Player;
            if (player == null || !player.IsValid) return;
            if (!player.TryGetComponent<Life>(out var life) || life == null) return;
            if (!life.IsAlive) return;

            // Global disconnect hotkey.
            if (!IsKeyUnset(_s.DisconnectKey) &&
                HotkeyHelper.IsPressedAndNotTimeout(_s.DisconnectKey, 1000))
            {
                MiscHelper.KillTCPConnectionForProcess(Core.Process.Pid);
                return;
            }

            // Overlay
            if (_s.ShowOverlay)
            {
                DrawOverlay(life);
            }

            // Flask pressing requires the GAME itself to be foreground (keys are sent to game).
            if (!Core.Process.Foreground) return;
            if (Core.States.InGameStateObject!.GameUi.IsAnyLargePanelOpen) return;

            // Cache flask state once per frame.
            _flaskActive = null;
            _flaskInventory = null;
            if (player.TryGetComponent<Buffs>(out var buffs) && buffs != null)
            {
                _flaskActive = buffs.FlaskActive;
            }

            var serverData = area.ServerDataObject;
            if (serverData != null)
            {
                _flaskInventory = serverData.FlaskInventory;
            }

            ProcessVital(VitalKind.Life,         _s.Life,         life.Health);
            ProcessVital(VitalKind.Mana,         _s.Mana,         life.Mana);
            ProcessVital(VitalKind.EnergyShield, _s.EnergyShield, life.EnergyShield);
            ProcessVital(VitalKind.Ward,         _s.Ward,         life.Ward);
        }

        private static void DrawOverlay(Life life)
        {
            ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowBgAlpha(0.6f);
            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize |
                        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus;

            if (ImGui.Begin("AutoPot_Overlay", flags))
            {
                DrawOverlayLine("HP",   life.Health,       new Vector4(1f, 0.3f, 0.3f, 1f));
                DrawOverlayLine("MP",   life.Mana,         new Vector4(0.3f, 0.5f, 1f, 1f));
                DrawOverlayLine("ES",   life.EnergyShield, new Vector4(0.3f, 0.85f, 0.85f, 1f));
                DrawOverlayLine("Ward", life.Ward,         new Vector4(0.7f, 0.7f, 0.7f, 1f));
            }

            ImGui.End();
        }

        private static void DrawOverlayLine(string tag, VitalInfo vital, Vector4 color)
        {
            if (vital.Total <= 0) return;
            float pct = vital.CurrentInPercent();
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(pct / 100f, new Vector2(140, 14), $"{tag} {pct:0}%");
            ImGui.PopStyleColor();
        }

        private void ProcessVital(VitalKind kind, VitalRule rule, VitalInfo vital)
        {
            if (!rule.Enabled || vital.Total <= 0) return;

            float pct = vital.CurrentInPercent();

            // ── Auto-disconnect ──
            if (rule.AutoDisconnect)
            {
                bool armed = !_discArmed.TryGetValue(kind, out var a) || a; // default: armed

                if (pct > 0 && pct <= rule.AutoDisconnectPercent)
                {
                    if (armed)
                    {
                        MiscHelper.KillTCPConnectionForProcess(Core.Process.Pid);
                        _discArmed[kind] = false; // disarm: won't fire again until it recovers
                        return;
                    }
                    // Fix reconnect loop: ensures no infinite loop occurs if Vital has not recovered in time.
                }
                else if (pct >= rule.AutoDisconnectPercent + AutoDisconnectRearmBuffer)
                {
                    _discArmed[kind] = true; // recovered enough — re-arm for next time
                }
            }

            // ── Flask press ──
            int slot = rule.FlaskSlot;
            if (slot != 1 && slot != 2) return; // 0 = None / display-only

            VK key = slot == 1 ? _s.Flask1Key : _s.Flask2Key;
            if (IsKeyUnset(key)) return;

            if (pct > rule.TriggerPercent) return;

            // Skip if flask effect is already active.
            if (_flaskActive != null && slot - 1 < _flaskActive.Length && _flaskActive[slot - 1])
            {
                return;
            }

            // Skip if flask has no charges.
            if (_flaskInventory != null)
            {
                var flaskItem = _flaskInventory[0, slot - 1];
                if (flaskItem != null && flaskItem.Address != IntPtr.Zero &&
                    flaskItem.TryGetComponent<Charges>(out var charges) && charges != null)
                {
                    if (charges.Current < charges.PerUseCharge)
                    {
                        return;
                    }
                }
            }

            _lastUsed.TryGetValue(kind, out var last);
            var now = DateTime.Now;
            if ((now - last).TotalMilliseconds < rule.RepeatDelayMs) return;

            if (MiscHelper.KeyUp(key))
            {
                _lastUsed[kind] = now;
            }
        }

        // ── Settings UI ────────────────────────────────────────────────

        public override void DrawSettings()
        {
            if (_s == null) return;

            // ── Warning ──
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.15f, 1f),
                "Auto-disconnect only works when OriathHub is run as administrator.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // ── Current Vitals bars ──
            ImGui.Text("Current Vitals");
            ImGui.Spacing();

            Life? lifeComp = null;
            var player = Core.States.GameCurrentState == GameStateTypes.InGameState
                ? Core.States.InGameStateObject?.CurrentAreaInstance?.Player
                : null;
            if (player != null && player.IsValid)
                player.TryGetComponent<Life>(out lifeComp);

            const float barWidth = 260f;
            DrawVitalBar(lifeComp?.Health,       new Vector4(0.90f, 0.20f, 0.20f, 1f), barWidth);
            DrawVitalBar(lifeComp?.Mana,         new Vector4(0.20f, 0.50f, 0.90f, 1f), barWidth);
            DrawVitalBar(lifeComp?.EnergyShield, new Vector4(0.30f, 0.85f, 0.85f, 1f), barWidth);
            DrawVitalBar(lifeComp?.Ward,         new Vector4(0.70f, 0.70f, 0.70f, 1f), barWidth);

            ImGui.Spacing();

            bool changed = false;

            // ── Show overlay checkbox ──
            changed |= ImGui.Checkbox("Show overlay", ref _s.ShowOverlay);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // ── Disconnect key (global) ──
            ImGui.Text("Disconnect key");
            ImGui.SameLine(120);
            changed |= DrawKeyButton("disckey", CaptureTarget.Disconnect, () => _s.DisconnectKey, v => _s.DisconnectKey = v);

            // ── Flask keys (Currently only 2 flask slots) ──
            ImGui.Text("Flask 1 key");
            ImGui.SameLine(120);
            changed |= DrawKeyButton("flask1key", CaptureTarget.Flask1, () => _s.Flask1Key, v => _s.Flask1Key = v);

            ImGui.Text("Flask 2 key");
            ImGui.SameLine(120);
            changed |= DrawKeyButton("flask2key", CaptureTarget.Flask2, () => _s.Flask2Key, v => _s.Flask2Key = v);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // ── Vitals ──
            ImGui.Text("Vitals");
            ImGui.Spacing();
            changed |= DrawVitalSection("Life",   VitalKind.Life,         _s.Life,         lifeComp?.Health,       new Vector4(0.90f, 0.20f, 0.20f, 1f));
            changed |= DrawVitalSection("Mana",   VitalKind.Mana,        _s.Mana,         lifeComp?.Mana,         new Vector4(0.20f, 0.50f, 0.90f, 1f));
            changed |= DrawVitalSection("Shield", VitalKind.EnergyShield,_s.EnergyShield,  lifeComp?.EnergyShield, new Vector4(0.30f, 0.85f, 0.85f, 1f));
            changed |= DrawVitalSection("Ward",   VitalKind.Ward,        _s.Ward,         lifeComp?.Ward,         new Vector4(0.70f, 0.70f, 0.70f, 1f));

            if (changed) SaveSettings();
        }

        // ── Progress bar ───────────────────────────────────────────────

        private static void DrawVitalBar(VitalInfo? vital, Vector4 color, float width = 260f)
        {
            float fraction = 0f;
            string overlay = "N/A";

            if (vital is { } v && v.Total > 0)
            {
                fraction = v.CurrentInPercent() / 100f;
                overlay  = $"{fraction * 100f:0}%";
            }

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar(fraction, new Vector2(width, 18), overlay);
            ImGui.PopStyleColor();
        }

        // ── Per-vital section ──────────────────────────────────────────

        private bool DrawVitalSection(string label, VitalKind kind, VitalRule rule, VitalInfo? vital, Vector4 dotColor)
        {
            bool changed = false;
            ImGui.PushID(label);

            // ── Header: checkbox + colored dot + label + live values ──
            changed |= ImGui.Checkbox("##en", ref rule.Enabled);
            ImGui.SameLine();

            // Colored dot
            var cursorPos = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            dl.AddCircleFilled(
                new Vector2(cursorPos.X + 6, cursorPos.Y + ImGui.GetTextLineHeight() / 2f),
                5f,
                ImGuiHelper.Color(
                    (byte)(dotColor.X * 255), (byte)(dotColor.Y * 255),
                    (byte)(dotColor.Z * 255), (byte)(dotColor.W * 255)));
            ImGui.Dummy(new Vector2(16, 0));
            ImGui.SameLine();

            if (vital is { } v && v.Total > 0)
            {
                ImGui.TextColored(dotColor, label);
                ImGui.SameLine();
                ImGui.Text($"{v.Current} / {v.Total}");
            }
            else
            {
                ImGui.TextColored(dotColor, label);
                ImGui.SameLine();
                ImGui.TextDisabled("(not available)");
            }

            // Only draw controls if enabled.
            if (rule.Enabled)
            {
                ImGui.Indent(28);

                // ── Trigger row: threshold slider + flask dropdown ──
                int total = vital is { } vt ? vt.Total : 0;
                string trigFmt = total > 0
                    ? $"%d%% ({(int)Math.Round(rule.TriggerPercent / 100f * total)})"
                    : "%d%%";

                ImGui.Text("Trigger");
                ImGui.SameLine(90);
                ImGui.SetNextItemWidth(360);
                changed |= ImGui.SliderInt("##trig", ref rule.TriggerPercent, 1, 99, trigFmt);

                ImGui.SameLine();
                ImGui.SetNextItemWidth(110);
                string[] flaskOptions = { "None", "Flask 1", "Flask 2" };
                int flaskIdx = rule.FlaskSlot; // 0,1,2 map directly to option index
                if (ImGui.Combo("##flask", ref flaskIdx, flaskOptions, flaskOptions.Length))
                {
                    rule.FlaskSlot = flaskIdx;
                    changed = true;
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Which flask this vital triggers.\nKeys are configured once at the top (Flask 1 key / Flask 2 key).");

                // ── Repeat delay row ──
                ImGui.Text("Delay");
                ImGui.SameLine(90);
                ImGui.SetNextItemWidth(180);
                if (ImGui.SliderInt("##delay", ref rule.RepeatDelayMs, 50, 2000, "%d ms"))
                {
                    changed = true;
                }

                if (rule.RepeatDelayMs < 50) rule.RepeatDelayMs = 50;

                // ── Auto-disconnect row ──
                string discFmt = total > 0
                    ? $"%d%% ({(int)Math.Round(rule.AutoDisconnectPercent / 100f * total)})"
                    : "%d%%";

                ImGui.Spacing();
                ImGui.Indent(16);
                changed |= ImGui.Checkbox("Auto-disconnect", ref rule.AutoDisconnect);
                ImGui.SameLine(220);
                ImGui.SetNextItemWidth(300);
                if (rule.AutoDisconnect)
                {
                    changed |= ImGui.SliderInt("##disc", ref rule.AutoDisconnectPercent, 1, 99, discFmt);
                }
                else
                {
                    // Show disabled slider
                    ImGui.BeginDisabled();
                    ImGui.SliderInt("##disc", ref rule.AutoDisconnectPercent, 1, 99, discFmt);
                    ImGui.EndDisabled();
                }

                ImGui.Unindent(16);
                ImGui.Unindent(28);
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PopID();
            return changed;
        }

        /// <summary>Draws a "Set key" capture button + clear "x" button for a single key binding.</summary>
        private bool DrawKeyButton(string id, CaptureTarget target, Func<VK> getter, Action<VK> setter)
        {
            bool changed = false;
            bool isCapturing = _capturing == target;
            VK current = getter();

            string label = isCapturing
                ? "press..."
                : IsKeyUnset(current) ? "Set key" : HotkeyHelper.DisplayName(current);

            if (ImGui.Button($"{label}##{id}", new Vector2(90, 0)))
            {
                _capturing = target;
            }

            if (isCapturing)
            {
                if (TryCaptureKey(out var vk))
                {
                    setter(vk); _capturing = CaptureTarget.None; changed = true;
                }
                else if (HotkeyHelper.IsPressed(VK.ESCAPE))
                {
                    _capturing = CaptureTarget.None;
                }
            }

            if (!IsKeyUnset(current))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"x##{id}clr"))
                {
                    setter(VK.NONAME);
                    changed = true;
                }
            }

            return changed;
        }

        // ── Helpers ────────────────────────────────────────────────────

        /// <summary>VK.NONAME or whatever HotkeyHelper considers unset.</summary>
        private static bool IsKeyUnset(VK key)
            => key == VK.NONAME || HotkeyHelper.IsUnset(key);

        /// <summary>Scan common key ranges + mouse buttons for a press. Returns true + the key if found.</summary>
        private static bool TryCaptureKey(out VK result)
        {
            // Mouse buttons (skip LBUTTON).
            if (HotkeyHelper.IsPressed(VK.RBUTTON))   { result = VK.RBUTTON;   return true; }
            if (HotkeyHelper.IsPressed(VK.MBUTTON))   { result = VK.MBUTTON;   return true; }
            if (HotkeyHelper.IsPressed(VK.XBUTTON1))  { result = VK.XBUTTON1;  return true; }
            if (HotkeyHelper.IsPressed(VK.XBUTTON2))  { result = VK.XBUTTON2;  return true; }

            // Keyboard
            for (var vk = VK.KEY_0; vk <= VK.KEY_9; vk++)
                if (HotkeyHelper.IsPressed(vk)) { result = vk; return true; }
            for (var vk = VK.KEY_A; vk <= VK.KEY_Z; vk++)
                if (HotkeyHelper.IsPressed(vk)) { result = vk; return true; }
            for (var vk = VK.F1; vk <= VK.F12; vk++)
                if (HotkeyHelper.IsPressed(vk)) { result = vk; return true; }
            for (var vk = VK.NUMPAD0; vk <= VK.NUMPAD9; vk++)
                if (HotkeyHelper.IsPressed(vk)) { result = vk; return true; }

            result = VK.NONAME;
            return false;
        }
    }
}
