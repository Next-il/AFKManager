# [CS2] AFK Manager
[![Downloads](https://img.shields.io/github/downloads/NiGHT757/AFKManager/total.svg)](https://github.com/NiGHT757/AFKManager/releases)
[![License](https://img.shields.io/github/license/NiGHT757/AFKManager.svg)](https://github.com/NiGHT757/AFKManager/blob/main/LICENSE)

AFK Manager plugin for CS2 based on [player-checker by sazonische from CS:GO](https://github.com/sazonische/player-checker/blob/master/addons/sourcemod/scripting/player_checker.sp)

# Features:
Config File located in **/addons/counterstrikesharp/configs/plugins/AFKManager** with Settings:
  -	AfkPunishAfterWarnings: Number of warnings to issue before Punishment type (set to 0 to disable AFK feature).
  - AfkPunishment: Punishment type (0 - kill, 1 - kill + move to spectator, 2 - kick).
  - AfkWarnInterval: Issue a warning every X seconds for AFK.
  - AfkMoveAfterSeconds: With AfkPunishment 1, seconds standing still before the move countdown starts (default = 40). Counted in real seconds, not in Timer steps.
  - AfkMoveCountdownSeconds: With AfkPunishment 1, the countdown before the player is moved to spectators (default = 10). Set either key to 0 to use the AfkWarnInterval warnings for the move instead.
  - AfkTransferC4AfterWarnings: number of AFK warnings before transferring the C4 (default = 1,   0 = disabled)
  - AfkTransferC4OnlyFromBuyZone: C4 can only be transferred if the AFK player is in the Buy Zone. (default = true)
  - SpecWarnInterval: Issue a warning every X seconds for AFK at SPEC.
  - SpecKickAfterWarnings: Kick the player after X warnings are issued (set to 0 to disable).
  - SpecKickMinPlayers: Minimum number of players required to kick.
  - SpecKickOnlyMovedByPlugin: Only check players in spectator mode who were moved by AFK Manager.
  - SpecSkipFlag: Skip players in spectator mode with this flag during AFK verification.
  - AfkSkipFlag: Skip players with this flag during AFK verification.
  - AntiCampSkipFlag: Skip players with this flag during AntiCamp verification.
  - PlaySoundName: Play a sound after a warning is issued (leave empty to disable).
  - SkipWarmup: Skip checks during warmup.
  - AntiCampRadius: Distance check in units.
  - AntiCampPunishment: Punishment type for camping (0 - slay, 1 - slap).
  - AntiCampSlapDamage: Damage dealt by slap (set to 0 for no damage).
  - AntiCampWarnInterval: Issue a warning every X seconds for camping.
  - AntiCampPunishAfterWarnings: Punish the player after X warnings for camping (set to 0 to disable AntiCamp feature).
  - AntiCampSkipBombPlanted: Skip camping checks if the bomb is planted (set to true to enable).
  - AntiCampSkipTeam: Skip camping checks for a specific team (2 - Terrorists, 3 - Counter-Terrorists).
  - Timer: Adjust the timer for player checks.
  - Hud.Enabled: Show the AFK warning as a HUD card instead of chat (default = true). false brings every old chat line back.
  - Hud.OthersToast: Tell the other players "X moved to spectators" with a toast (default = true). false shows them nothing.

# HUD (Next fork):
With AfkPunishment 1 (move to spectator), the AFK player gets a private card in the lower middle of the screen
instead of the warning in chat: a countdown to the real move time, a bar that drains over it, "Back" when they
move, and "Moved to spectators" after. Everyone else gets one toast through ToastService instead of the chat
line, and the bomb transfer line becomes a toast to the Terrorists only.

The layout is `hud/panorama/layout/custom_game/afk_hud.xml` with `afk_hud.css` and `hud_shared.css`. It has to
be in the workshop addon the clients mount; without it the card is invisible, so ship the addon first or set
Hud.Enabled to false. The kill and kick punishments, the spectator kick and anti-camp keep their chat lines.

With AfkMoveAfterSeconds and AfkMoveCountdownSeconds above 0 (40 and 10 by default) the move has its own clock: the card
comes after 40 seconds standing still and counts the real 10 seconds down; nothing goes to chat while the HUD is on, and
with it off the warning line comes once, when the countdown starts. Dead, freeze time, a skipped warmup, halftime and
the end-of-match screen don't count, and the count goes on after them rather than starting over.

These keys and both Hud keys are optional - a config file without them gets the defaults, and the plugin never writes the file:

```json
"Hud": { "Enabled": true, "OthersToast": true }
```

# Requirements:
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 1.0.374 or newer. PanoramaManager.dll ships beside the plugin.
Optional: ToastService (with `shared/PanoramaManager.Toasts.Api/`) for the toasts; without it those lines stay in chat.

# Credits:
[K4ryuu](https://github.com/K4ryuu) - Helping with config file and other things before CSS's config implementation.

xstage - Helping with the "changeTeam" function before CSS version ~90 in order to properly move players to SPEC.

sazonische - [player-checker](https://github.com/sazonische/player-checker/blob/master/addons/sourcemod/scripting/player_checker.sp)

B3none - Plugin ideas.
