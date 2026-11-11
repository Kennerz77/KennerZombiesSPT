using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies mode's own controls, only in a zombies match (EFT's key settings are never touched; normal raids are
    /// as they were). A short list of its own, set on the Esc menu's "ZOMBIES CONTROLS" page (or in F12):
    /// move (WASD), sprint (Shift, held), jump, crouch, prone, grenade (G, thrown at once), melee (V: a knife stab at
    /// once, EFT's quick knife), reload, use (F: doors, barricades, revives, buying), and the mouse wheel for the next /
    /// the gun before (primary, secondary, then the Mule Kick third).
    /// How: in a match, EFT's own key presses for these actions are blocked (PlayerOwner.TranslateCommand), and these
    /// keys send EFT's own commands for them instead (so EFT's sprint, jump, knife and grenade code runs as normal);
    /// walking takes its direction from these keys (MoveInputTranslator.TranslateAxes). The walking speed is held at its
    /// top (the wheel no longer slows you; only a fall's knock does, a moment).
    /// </summary>
    internal static class ZombiesControls
    {
        public enum Act { Forward, Back, Left, Right, Sprint, Jump, Crouch, Prone, Grenade, Tactical, Melee, Reload, Use, Scoreboard, NextGun, GunBefore, Secondary, VoteYes, VoteNo }

        private static readonly (Act act, string label, KeyCode key)[] Defaults =
        {
            (Act.Forward, "Move forward", KeyCode.W), (Act.Back, "Move back", KeyCode.S), (Act.Left, "Move left", KeyCode.A),
            (Act.Right, "Move right", KeyCode.D), (Act.Sprint, "Sprint", KeyCode.LeftShift), (Act.Jump, "Jump", KeyCode.Space),
            (Act.Crouch, "Crouch", KeyCode.C), (Act.Prone, "Prone", KeyCode.X), (Act.Grenade, "Throw lethal", KeyCode.G), (Act.Tactical, "Throw tactical", KeyCode.Q),
            (Act.Melee, "Melee (knife)", KeyCode.V), (Act.Reload, "Reload", KeyCode.R), (Act.Use, "Use / buy / rebuild", KeyCode.F), (Act.Scoreboard, "Scoreboard (hold)", KeyCode.Tab),
            (Act.NextGun, "Next gun (also wheel down)", KeyCode.None), (Act.GunBefore, "Gun before (also wheel up)", KeyCode.None),
            // The gun's underbarrel launcher and back (the PaP M16's M203; other guns' second fire later)
            (Act.Secondary, "Secondary fire (launcher)", KeyCode.B),
            // The team's exfil vote (once the lever's thrown, user 2026-11-09)
            (Act.VoteYes, "Exfil vote: yes", KeyCode.F1), (Act.VoteNo, "Exfil vote: no", KeyCode.F2),
        };

        private static readonly Dictionary<Act, ConfigEntry<KeyCode>> Keys = new Dictionary<Act, ConfigEntry<KeyCode>>();

        // The controls EFT lets you hold or toggle (as in its own settings): sprint and crouch toggled by default.
        // No leaning in zombies: EFT's lean keys (and its slow lean) do nothing in a match
        private static readonly (Act act, bool holdByDefault)[] Pressable = { (Act.Sprint, false), (Act.Crouch, false) };
        private static readonly Dictionary<Act, ConfigEntry<bool>> Hold = new Dictionary<Act, ConfigEntry<bool>>();

        private static bool Held(Act a) => Hold.TryGetValue(a, out ConfigEntry<bool> e) ? e.Value : Pressable.First(p => p.act == a).holdByDefault;

        // EFT's commands these controls take over: EFT's own keys for them do nothing in a zombies match
        private static readonly HashSet<ECommand> Taken = new HashSet<ECommand>
        {
            ECommand.Jump, ECommand.ToggleDuck, ECommand.ToggleProne, ECommand.ToggleSprinting, ECommand.EndSprinting,
            ECommand.ThrowGrenade, ECommand.PressThrowGrenade, ECommand.QuickKnifeKick, ECommand.ReloadWeapon,
            ECommand.QuickReloadWeapon, ECommand.BeginInteracting, ECommand.EndInteracting, ECommand.ScrollNext, ECommand.ScrollPrevious,
            ECommand.RestorePose, ECommand.ToggleLeanLeft, ECommand.ToggleLeanRight, ECommand.EndLeanLeft, ECommand.EndLeanRight, // no leaning
            // No voice phrases or communication wheel (EFT's Y: the character coughed "hurt", double Y opened the wheel; the user 2026-11-08)
            ECommand.QuickMumbleStart, ECommand.MumbleToggle, ECommand.MumbleEnd, ECommand.ToggleMumbleDropdown, ECommand.ToggleTacticalInteraction,
            // Lasers and lights: Pack-a-Punched guns keep theirs on (ZombiesLasers), nobody switches them
            ECommand.ToggleTacticalDevice, ECommand.NextTacticalDevice,
        };

        private static readonly EquipmentSlot[] Order = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };
        private static float _lastSwap;
        private static bool _sending;
        private static GamePlayerOwner _owner;
        private static Player _ownerOf;

        /// <summary>Walking slowly until then (a fall's knock).</summary>
        public static float SlowUntil;

        /// <summary>A key's name as the prompts show it (F, Shift, Mouse 4, Space...).</summary>
        public static string KeyName(Act a)
        {
            KeyCode k = Key(a);
            switch (k)
            {
                case KeyCode.None: return "-";
                case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
                case KeyCode.Mouse0: return "Left click";
                case KeyCode.Mouse1: return "Right click";
                case KeyCode.Mouse2: return "Middle click";
                case KeyCode.Mouse3: return "Mouse 4";
                case KeyCode.Mouse4: return "Mouse 5";
                case KeyCode.Return: return "Enter";
            }
            string name = k.ToString();
            if (name.StartsWith("Alpha"))
                return name.Substring(5);
            if (name.StartsWith("Keypad"))
                return "Num " + name.Substring(6);
            return name;
        }

        /// <summary>The key for an action (the use key also rebuilds barricades and revives).</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// Is this key held right now. The mouse's side buttons (Mouse3/Mouse4) are read from Windows itself: Unity's
        /// old input reported them late (held grenades read as taps, and a release fired a second press).
        /// </summary>
        public static bool IsDown(KeyCode key)
        {
            if ((key == KeyCode.Mouse3 || key == KeyCode.Mouse4) && Application.isFocused)
            {
                try { return (GetAsyncKeyState(key == KeyCode.Mouse3 ? 0x05 : 0x06) & 0x8000) != 0; }
                catch { }
            }
            return Input.GetKey(key);
        }

        private static readonly System.Collections.Generic.Dictionary<KeyCode, bool> WasDown = new System.Collections.Generic.Dictionary<KeyCode, bool>();
        private static readonly System.Collections.Generic.Dictionary<KeyCode, int> DownFrame = new System.Collections.Generic.Dictionary<KeyCode, int>();

        /// <summary>Went down this frame (side buttons from Windows, see <see cref="IsDown"/>).</summary>
        public static bool WentDown(KeyCode key)
        {
            if (key != KeyCode.Mouse3 && key != KeyCode.Mouse4)
                return Input.GetKeyDown(key);
            if (DownFrame.TryGetValue(key, out int f) && f == Time.frameCount)
                return true;
            bool now = IsDown(key);
            bool was = WasDown.TryGetValue(key, out bool w) && w;
            WasDown[key] = now;
            if (now && !was)
            {
                DownFrame[key] = Time.frameCount;
                return true;
            }
            return false;
        }

        public static KeyCode Key(Act a) => Keys.TryGetValue(a, out ConfigEntry<KeyCode> e) ? e.Value : Defaults.First(d => d.act == a).key;

        /// <summary>"Press [R] to reload" under the crosshair when the mag runs low (zombies settings, F12).</summary>
        public static ConfigEntry<bool> ReloadPrompt;

        public static void Bind(ConfigFile config)
        {
            const string section = "3. Zombies controls";
            int order = 100;
            ReloadPrompt = config.Bind(section, "Reload reminder", true, new ConfigDescription(
                "Press (key) to reload shows under the crosshair once the mag is down to a quarter.", null,
                new ZombiesPlugin.ConfigurationManagerAttributes { Order = 101 }));
            foreach (var d in Defaults)
                Keys[d.act] = config.Bind(section, d.label.Replace("/", "or").Replace("(", "").Replace(")", ""), d.key,
                    new ConfigDescription($"{d.label} in a zombies match (also on the Esc menu's ZOMBIES CONTROLS page).", null,
                        new ZombiesPlugin.ConfigurationManagerAttributes { Order = order-- }));
            foreach (var p in Pressable)
            {
                string label = Defaults.First(d => d.act == p.act).label;
                Hold[p.act] = config.Bind(section, label + " held", p.holdByDefault,
                    new ConfigDescription("On: hold the key to " + label.ToLower() + "; off: press once to start, again to stop.", null,
                        new ZombiesPlugin.ConfigurationManagerAttributes { Order = order-- }));
            }
        }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MoveInputTranslator), nameof(MoveInputTranslator.TranslateCommand)),
                prefix: new HarmonyMethod(typeof(ZombiesControls), nameof(BlockSpeedWheel)));
            harmony.Patch(AccessTools.Method(typeof(MoveInputTranslator), nameof(MoveInputTranslator.TranslateAxes)),
                prefix: new HarmonyMethod(typeof(ZombiesControls), nameof(Axes)));
            harmony.Patch(AccessTools.Method(typeof(PlayerOwner), nameof(PlayerOwner.TranslateCommand)),
                prefix: new HarmonyMethod(typeof(ZombiesControls), nameof(Command)));
            var panel = AccessTools.Method(typeof(EFT.UI.ActionPanel), nameof(EFT.UI.ActionPanel.TranslateCommand));
            if (panel != null)
                harmony.Patch(panel, prefix: new HarmonyMethod(typeof(ZombiesControls), nameof(PanelCommand)));
        }

        // In a match, with nothing else (a menu, the editor, our controls page) taking the keys
        private static bool Using => ZombiesMatch.Active && ZombiesMatch.LoadedIn && !MapEditor.IsOpen && !_menuOpen && !ZombiesPause.Paused
                                     && !ZombiesPause.IsMenuOpen() && !ZombiesGameOver.Active;

        // EFT's wheel slows your walking: never in a zombies match (the wheel swaps guns, read in Tick)
        private static bool BlockSpeedWheel(ECommand command, ref InputNode.ETranslateResult __result)
        {
            if (!ZombiesMatch.Active || (command != ECommand.ScrollNext && command != ECommand.ScrollPrevious))
                return true;
            __result = InputNode.ETranslateResult.Block;
            return false;
        }

        // EFT's own keys for the actions we took over do nothing in a match; our own sends go through
        private static bool Command(ECommand command, ref InputNode.ETranslateResult __result)
        {
            // Paused (the menu up or the countdown after it): the player does nothing; Esc still works
            if (!_sending && ZombiesMatch.Active && ZombiesPause.Paused && command != ECommand.Escape)
            {
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            // One of our zombies keys pressed or let go this frame: whatever EFT has on the same key is ignored (the
            // user's Mouse4 is EFT's swap hands and the zombies grenade: it swapped hands with every throw)
            if (!_sending && ZombiesMatch.Active && OurKeyEdge())
            {
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            // Reviving a teammate (gun put away): no firing or drawing. A left click drew the gun with empty hands, then
            // our draw after the revive hit it mid-draw and the gun came out and vanished (user, 2026-11-10)
            if (!_sending && ZombiesMatch.Active && ZombiesDowned.GunIsAway && HandsCommand(command))
            {
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            if (_sending || !ZombiesMatch.Active || !Taken.Contains(command))
                return true;
            __result = InputNode.ETranslateResult.Block;
            return false;
        }

        private static bool HandsCommand(ECommand c)
        {
            string n = c.ToString();
            return n.Contains("Shoot") || n.Contains("Fire") || n.StartsWith("SelectWeapon") || n.StartsWith("Select") && n.Contains("Weapon")
                   || n.Contains("Scroll") || n == "QuickKnifeKick" || n.Contains("Grenade") || n.Contains("Reload") || n.Contains("NextWeapon")
                   || n.Contains("PreviousWeapon") || n.Contains("Aim");
        }

        private static int _edgeFrame = -1;
        private static bool _edge;

        // Any of our action keys (not the walking ones, not the mouse buttons EFT fires with) went down or up this frame
        private static bool OurKeyEdge()
        {
            if (_edgeFrame == Time.frameCount)
                return _edge;
            _edgeFrame = Time.frameCount;
            _edge = false;
            foreach (var kv in Keys)
            {
                KeyCode k = kv.Value.Value;
                if (k == KeyCode.None || k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || kv.Key <= Act.Right)
                    continue;
                if (Input.GetKeyDown(k) || Input.GetKeyUp(k))
                {
                    _edge = true;
                    break;
                }
            }
            return _edge;
        }

        // Walking: the direction from our keys (EFT's own move keys ignored)
        private static void Axes(MoveInputTranslator __instance, ref float[] axes)
        {
            if (!ZombiesMatch.Active || axes == null || axes.Length < 2 || !ZombiesInertia.Tweaks)
                return;
            if (!Using)
            {
                axes[0] = 0f;
                axes[1] = 0f;
                // Frozen through the pause's countdown: no looking round either
                if (ZombiesPause.Paused && axes.Length > 5)
                {
                    axes[4] = 0f;
                    axes[5] = 0f;
                }
                return;
            }
            // No slow lean either
            if (axes.Length > 6)
                axes[6] = 0f;
            axes[0] = (Input.GetKey(Key(Act.Right)) ? 1f : 0f) - (Input.GetKey(Key(Act.Left)) ? 1f : 0f);
            axes[1] = (Input.GetKey(Key(Act.Forward)) ? 1f : 0f) - (Input.GetKey(Key(Act.Back)) ? 1f : 0f);
        }

        private static bool RunInteraction(Player me)
        {
            try
            {
                if (_ownerOf != me || _owner == null)
                {
                    _owner = ((Component)me).GetComponent<GamePlayerOwner>();
                    _ownerOf = me;
                }
                var state = _owner?.AvailableInteractionState?.Value;
                if (state?.SelectedAction == null)
                    return false;
                state.SelectedAction.Action?.Invoke();
                return true;
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"controls: interaction failed ({e.Message})");
                return false;
            }
        }

        // EFT's interaction menu takes its own interact key and the wheel before the player does: in a match the use
        // key runs it instead (RunInteraction) and the wheel swaps guns
        private static bool PanelCommand(ECommand command, ref InputNode.ETranslateResult __result)
        {
            if (!ZombiesMatch.Active || _sending
                || command != ECommand.BeginInteracting && command != ECommand.ScrollNext && command != ECommand.ScrollPrevious)
                return true;
            __result = InputNode.ETranslateResult.Block;
            return false;
        }

        /// <summary>One of EFT's commands as if its key were pressed (our own sends get past the block).</summary>
        public static void SendCommand(Player me, ECommand command) => Send(me, command);

        private static void Send(Player me, ECommand command)
        {
            if (_ownerOf != me || _owner == null)
            {
                _owner = ((Component)me).GetComponent<GamePlayerOwner>();
                _ownerOf = me;
            }
            if (_owner == null || !_owner.enabled)
                return;
            _sending = true;
            try
            {
                _owner.TranslateCommand(command);
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"controls: {command} failed ({e.Message})");
            }
            finally
            {
                _sending = false;
            }
        }

        private static void Swap(Player me, int step)
        {
            // One swap per notch, not a burst of them from a fast spin
            if (Time.time - _lastSwap < 0.25f)
                return;
            var guns = Order.Select(s => me.InventoryController.Inventory.Equipment.GetSlot(s).ContainedItem as Weapon).Where(w => w != null).ToList();
            if (guns.Count < 2)
                return;
            int now = guns.IndexOf(me.HandsController?.Item as Weapon);
            Weapon next = guns[((now < 0 ? 0 : now + step) % guns.Count + guns.Count) % guns.Count];
            if (next == me.HandsController?.Item || me.HandsController != null && me.HandsController.InCanNotBeInterruptedOperation())
                return;
            _lastSwap = Time.time;
            ZombiesDebug.Log($"controls: swap to {next.ShortName.Localized()} ({(step > 0 ? "next" : "before")})");
            _swapWanted = next;
            _swapAt = Time.time;
            me.SetInHands(next, result =>
            {
                if (result.Failed)
                    ZombiesPlugin.Log.LogWarning($"Zombies controls: the swap to the {next.LocalizedName()} failed: {result.Error}");
            });
        }

        // ---------- A swap that never happens (user, 2026-11-10: after a box gun, nothing worked on that player) ----------
        // A Fika client's hands can be left waiting on the host for good: swaps asked, nothing ever changes. A swap still
        // not done 3 s on (and the hands not busy reloading, drinking or the like) puts the hands away by force and draws
        // the wanted gun again; at most once every 10 s
        private static Weapon _swapWanted;
        private static float _swapAt, _swapRescuedAt = -100f;

        private static void WatchSwap(Player me)
        {
            if (_swapWanted == null)
                return;
            if (me.HandsController?.Item == _swapWanted || !ZombiesMatch.Active || ZombiesDowned.Mine != ZombiesDowned.State.Up)
            {
                _swapWanted = null;
                return;
            }
            if (Time.time - _swapAt < 3f || ZombiesPerks.Drinking || ZombiesDowned.GunIsAway || ZombiesGameOver.Active)
                return;
            Weapon want = _swapWanted;
            _swapWanted = null;
            if (Time.time - _swapRescuedAt < 10f || want.CurrentAddress == null)
                return;
            _swapRescuedAt = Time.time;
            ZombiesPlugin.Log.LogWarning($"Zombies controls: the swap to the {want.LocalizedName()} never happened in 3 s (hands: {me.HandsController?.GetType().Name} {me.HandsController?.Item?.LocalizedName()}): hands put away by force, drawn again");
            ZombiesPlugin.Instance.StartCoroutine(ZombiesReload.ForceDraw(me, want));
        }

        /// <summary>Each frame of a match: full walking speed, then our keys.</summary>
        private static bool _slowed;
        private static float _speedBefore = 1f;

        public static void Tick(Player me)
        {
            MovementContext m = me?.MovementContext;
            if (m == null)
                return;
            WatchSwap(me);
            if (ZombiesDowned.Mine == ZombiesDowned.State.Up)
            {
                // Only the slowdown (a slap) is ours: speed set to 0.45 while it lasts, EFT's own speed put back once after.
                // Forcing full speed all the time fought EFT's (0.69 held) and the player lurched in little jumps
                bool slow = Time.time < SlowUntil;
                if (slow && !_slowed)
                {
                    _slowed = true;
                    _speedBefore = m.CharacterMovementSpeed;
                    me.ChangeSpeed(0.45f - m.CharacterMovementSpeed);
                }
                else if (!slow && _slowed)
                {
                    _slowed = false;
                    me.ChangeSpeed(_speedBefore - m.CharacterMovementSpeed);
                }
            }
            if (!Using)
                return;
            bool up = ZombiesDowned.Mine == ZombiesDowned.State.Up;
            // Sprint: held (on while down) or toggled (each press)
            if (Input.GetKeyDown(Key(Act.Sprint)))
                Send(me, ECommand.ToggleSprinting);
            if (Held(Act.Sprint) && Input.GetKeyUp(Key(Act.Sprint)))
                Send(me, ECommand.EndSprinting);
            // Use: the selected option of EFT's interaction menu (doors, debris, buying), run here so the zombies key
            // works and EFT's own interact key doesn't (ActionPanel is blocked in a match); else EFT's interact
            if (Input.GetKeyDown(Key(Act.Use)) && !RunInteraction(me))
                Send(me, ECommand.BeginInteracting);
            if (Input.GetKeyUp(Key(Act.Use)))
                Send(me, ECommand.EndInteracting);
            if (Input.GetKeyDown(Key(Act.Reload)))
            {
                string why = ZombiesAmmo.NoReload(me);
                if (why == null)
                    Send(me, ECommand.ReloadWeapon);
                else
                    ZombiesDebug.Log($"reload: not now ({why})");
            }
            if (!up)
                return;
            if (Input.GetKeyDown(Key(Act.Jump)))
                Send(me, ECommand.Jump);
            if (Input.GetKeyDown(Key(Act.Crouch)))
                Send(me, ECommand.ToggleDuck);
            if (Held(Act.Crouch) && Input.GetKeyUp(Key(Act.Crouch)))
                Send(me, ECommand.RestorePose);
            if (Input.GetKeyDown(Key(Act.Prone)))
                Send(me, ECommand.ToggleProne);
            // The original's grenade: out quick, pin pulled, held while the key's down, thrown on release (ZombiesQuickGrenade)
            if (WentDown(Key(Act.Grenade)))
                ZombiesQuickGrenade.Press(me, lethal: true);
            if (ZombiesExfil.Voting)
            {
                if (WentDown(Key(Act.VoteYes)))
                    ZombiesExfil.Vote(true);
                else if (WentDown(Key(Act.VoteNo)))
                    ZombiesExfil.Vote(false);
            }
            if (WentDown(Key(Act.Secondary)))
                ZombiesUbgl.Toggle(me);
            if (WentDown(Key(Act.Tactical)))
            {
                ZombiesDebug.Log($"controls: tactical key ({Key(Act.Tactical)}), {ZombiesMonkey.Count(me)} monkey(s)");
                ZombiesQuickGrenade.Press(me, lethal: false);
            }
            if (Input.GetKeyDown(Key(Act.Melee)))
            {
                // The original's knife: one swing at a time, the hit on the swing, EFT's quick knife as the swing you see
                if (ZombiesSwipe.TrySwing(me))
                {
                    Send(me, ECommand.QuickKnifeKick);
                    ZombiesKnife.Asked(me);
                }
            }
            float wheel = Input.mouseScrollDelta.y;
            bool next = wheel < -0.01f || Input.GetKeyDown(Key(Act.NextGun)), before = wheel > 0.01f || Input.GetKeyDown(Key(Act.GunBefore));
            // The original: with the underbarrel launcher up, the first scroll only puts it away (back to the rifle); the next swaps
            // A wheel notch fires over several frames: after a launcher toggle, no swap or second toggle for a moment
            // (swapping mid-toggle left the hands half in launcher mode)
            if ((next || before) && Time.time < _launcherLockUntil)
                return;
            if ((next || before) && ZombiesUbgl.LauncherUp(me))
            {
                ZombiesUbgl.Toggle(me);
                _launcherLockUntil = Time.time + 0.7f;
                return;
            }
            if (next)
                Swap(me, 1);
            else if (before)
                Swap(me, -1);
        }

        // ---------- The Esc menu's ZOMBIES CONTROLS page ----------

        private static float _launcherLockUntil;
        private static bool _menuOpen;
        private static Act? _waiting;
        private static GUIStyle _button, _title, _label;

        private static bool _selectOpen;
        private static int _tab; // the settings page: 0 keybinds, 1 sound

        /// <summary>Drawn over EFT's Esc menu in a zombies match: a button that opens the page of zombies controls.</summary>
        public static void DrawMenu()
        {
            if (!ZombiesMatch.Active || !ZombiesPause.IsMenuOpen())
            {
                _menuOpen = false;
                _waiting = null;
                return;
            }
            float s = Screen.height / 1080f;
            Styles(s);
            GUI.depth = -900;
            GUI.color = Color.white;
            if (GUI.Button(new Rect(40 * s, 40 * s, 260 * s, 48 * s), _menuOpen ? "CLOSE CONTROLS" : "ZOMBIES CONTROLS", _button))
            {
                _menuOpen = !_menuOpen;
                _waiting = null;
            }
            if (_menuOpen)
                DrawPanel(new Vector2(40 * s, 100 * s), s);
        }

        /// <summary>The zombies settings (controls) page is open over the zombies menu.</summary>
        public static bool PageOpen => _selectOpen;

        /// <summary>The zombies menu's and lobby's ZOMBIES SETTINGS: the controls page, centred over everything.</summary>
        public static void OpenPage()
        {
            _selectOpen = true;
            _waiting = null;
            ZombiesUi.Sound("open");
        }

        private static void ClosePage()
        {
            _selectOpen = false;
            _waiting = null;
            ZombiesUi.Sound("back");
        }

        /// <summary>
        /// The page (user, 2026-11-10): centre screen, on top of everything, on the menus' glass panel; EXIT or Esc goes
        /// back (Esc while a key is being picked only cancels that). Closed when the menus are left.
        /// </summary>
        public static void DrawPage()
        {
            if (!_selectOpen)
                return;
            // At the menus, or over the zombies match's own Esc menu
            bool menus = ZombiesPlugin.AtMenus && !ZombiesMatch.Active && !ZombiesReport.Waiting;
            if (!menus && !ZombiesFrontEnd.InMatchMenu)
            {
                _selectOpen = false;
                _waiting = null;
                return;
            }
            Event ev = Event.current;
            if (_waiting == null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                ev.Use();
                ZombiesUi.EscUsedAt = Time.unscaledTime;
                ClosePage();
                return;
            }
            float s = Screen.height / 1080f;
            float k = Mathf.Min(1f, (Screen.height - 60f * s) / (PanelHeight(s) + 70f * s));
            Styles(s * k);
            GUI.depth = -1100;
            GUI.color = Color.white;
            ZombiesUi.ImguiDim(0.6f);
            float sk = s * k;
            float w = 760 * sk, h = PanelHeight(sk) + 70 * sk;
            var at = new Vector2((Screen.width - w) / 2f, (Screen.height - h) / 2f);
            if (_tab == 0)
                DrawPanel(at, sk);
            else
            {
                // SOUND: every volume of the zombies mode (ZombiesSoundPage)
                var panel = new Rect(at.x, at.y, w, h);
                ZombiesUi.ImguiPanel(panel);
                GUI.Label(new Rect(panel.x + 20 * sk, panel.y + 10 * sk, w, 44 * sk), "ZOMBIES SETTINGS", _title);
                GUI.color = new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.7f);
                GUI.DrawTexture(new Rect(panel.x + 16 * sk, panel.y + 54 * sk, w - 32 * sk, 1.5f * sk), ZombiesUi.Tex(ZombiesUi.Divider));
                GUI.color = Color.white;
                ZombiesSoundPage.Draw(new Rect(panel.x + 20 * sk, panel.y + 66 * sk, w - 40 * sk, h - 66 * sk - 74 * sk), sk);
            }
            // The tabs, top right (user, 2026-11-11): KEYBINDS and SOUND
            string[] tabs = { "KEYBINDS", "SOUND" };
            for (int t = 0; t < tabs.Length; t++)
            {
                var tr = new Rect(at.x + w - 20 * sk - (tabs.Length - t) * 150 * sk, at.y + 12 * sk, 140 * sk, 36 * sk);
                if (_tab == t && Event.current.type == EventType.Repaint)
                    GUI.Box(new Rect(tr.x - 2 * sk, tr.y - 2 * sk, tr.width + 4 * sk, tr.height + 4 * sk), GUIContent.none, ZombiesUi.Style(ZombiesUi.Flat(ZombiesUi.Red, ZombiesUi.RedDeep)));
                if (GUI.Button(tr, tabs[t], _button) && _tab != t)
                {
                    _tab = t;
                    _waiting = null;
                    ZombiesUi.Sound("toggle");
                }
            }
            if (GUI.Button(new Rect(at.x + w - 20 * s * k - 200 * s * k, at.y + h - 58 * s * k, 200 * s * k, 42 * s * k), "EXIT", _button))
                ClosePage();
            Styles(s);
        }

        private static bool OnScreen(EFT.UI.Screens.EEftScreenType type)
        {
            try
            {
                return Equals(Comfort.Common.Singleton<EFT.UI.Screens.IScreenManager>.Instance?.CurrentScreenType, type);
            }
            catch
            {
                return false;
            }
        }

        private static float PanelHeight(float s) => (Defaults.Length + Pressable.Length + 3 + (_selectOpen ? 2 : 0)) * 40 * s + 70 * s;

        private static void Styles(float s)
        {
            if (_button == null)
            {
                _button = ZombiesUi.ImguiButton(18);
                _title = ZombiesUi.ImguiLabel(26, ZombiesUi.Red, TextAnchor.MiddleLeft, true);
                _label = ZombiesUi.ImguiLabel(18, ZombiesUi.Ink);
                _label.wordWrap = false;
            }
            _button.fontSize = Mathf.RoundToInt(18 * s);
            _title.fontSize = Mathf.RoundToInt(26 * s);
            _label.fontSize = Mathf.RoundToInt(18 * s);
        }

        // The keys page itself: a key per action (click, then press the new key) and hold / toggle where EFT has it
        private static void DrawPanel(Vector2 at, float s)
        {

            // Waiting for a key: the next key pressed is the new one (Esc cancels, Backspace clears)
            Event ev = Event.current;
            if (_waiting != null && ev.isKey && ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None)
            {
                if (ev.keyCode != KeyCode.Escape)
                    Keys[_waiting.Value].Value = ev.keyCode == KeyCode.Backspace ? KeyCode.None : ev.keyCode;
                else
                    ZombiesUi.EscUsedAt = Time.unscaledTime;
                _waiting = null;
                ev.Use();
            }
            else if (_waiting != null && ev.type == EventType.MouseDown && ev.button >= 2)
            {
                Keys[_waiting.Value].Value = ev.button == 2 ? KeyCode.Mouse2 : ev.button == 3 ? KeyCode.Mouse3 : KeyCode.Mouse4;
                _waiting = null;
                ev.Use();
            }
            else if (_waiting != null && ev.shift && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.None)
            {
                Keys[_waiting.Value].Value = KeyCode.LeftShift;
                _waiting = null;
                ev.Use();
            }

            float rowH = 40 * s, w = (_selectOpen ? 760 : 560) * s;
            var panel = new Rect(at.x, at.y, w, PanelHeight(s) + (_selectOpen ? 70 * s : 0f));
            ZombiesUi.ImguiPanel(panel);
            GUI.color = Color.white;
            GUI.Label(new Rect(panel.x + 20 * s, panel.y + 10 * s, w, 44 * s), _selectOpen ? "ZOMBIES SETTINGS" : "ZOMBIES CONTROLS", _title);
            GUI.color = new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.7f);
            GUI.DrawTexture(new Rect(panel.x + 16 * s, panel.y + 54 * s, w - 32 * s, 1.5f * s), ZombiesUi.Tex(ZombiesUi.Divider));
            GUI.color = Color.white;
            float y = panel.y + 60 * s;
            foreach (var d in Defaults)
            {
                GUI.Label(new Rect(panel.x + 20 * s, y, 300 * s, rowH), d.label, _label);
                string shown = _waiting == d.act ? "press a key..." : Keys[d.act].Value == KeyCode.None ? "-" : Keys[d.act].Value.ToString();
                if (GUI.Button(new Rect(panel.x + 330 * s, y + 4 * s, 210 * s, rowH - 8 * s), shown, _button))
                    _waiting = d.act;
                y += rowH;
            }
            foreach (var p in Pressable)
            {
                string label = Defaults.First(d => d.act == p.act).label;
                GUI.Label(new Rect(panel.x + 20 * s, y, 300 * s, rowH), label + ": hold or toggle", _label);
                if (GUI.Button(new Rect(panel.x + 330 * s, y + 4 * s, 210 * s, rowH - 8 * s), Held(p.act) ? "HOLD" : "TOGGLE", _button))
                    Hold[p.act].Value = !Held(p.act);
                y += rowH;
            }
            // Invites from other players (user, 2026-11-10): off, anyone who tries is told "X has invites off!"; on again,
            // they work again within a second or two
            if (_selectOpen)
            {
                GUI.Label(new Rect(panel.x + 20 * s, y, 300 * s, rowH), "Invites from other players", _label);
                if (GUI.Button(new Rect(panel.x + 330 * s, y + 4 * s, 210 * s, rowH - 8 * s), ZombiesParty.InvitesOn ? "ON" : "OFF", _button))
                {
                    ZombiesParty.InvitesOn = !ZombiesParty.InvitesOn;
                    ZombiesUi.Sound("toggle");
                }
                y += rowH;
            }
            // The reload reminder under the crosshair (user, 2026-11-11)
            if (_selectOpen && ReloadPrompt != null)
            {
                GUI.Label(new Rect(panel.x + 20 * s, y, 300 * s, rowH), "Reload reminder", _label);
                if (GUI.Button(new Rect(panel.x + 330 * s, y + 4 * s, 210 * s, rowH - 8 * s), ReloadPrompt.Value ? "ON" : "OFF", _button))
                {
                    ReloadPrompt.Value = !ReloadPrompt.Value;
                    ZombiesUi.Sound("toggle");
                }
                y += rowH;
            }
            GUI.Label(new Rect(panel.x + 20 * s, y, w - 40 * s, rowH), "Mouse wheel: next gun / gun before. Esc: cancel, Backspace: none.", _label);
            y += rowH;
            if (GUI.Button(new Rect(panel.x + 20 * s, y + 6 * s, 220 * s, rowH), "Back to defaults", _button))
            {
                foreach (var d in Defaults)
                    Keys[d.act].Value = d.key;
                foreach (var p in Pressable)
                    Hold[p.act].Value = p.holdByDefault;
            }
        }
    }
}
