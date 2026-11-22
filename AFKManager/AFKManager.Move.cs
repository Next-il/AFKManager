using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;

namespace AFKManager;

/// <summary>
/// The move to spectators (AfkPunishment 1) on its own clock: AfkMoveAfterSeconds standing still, then an
/// AfkMoveCountdownSeconds countdown on the AFK card, then the move. On when both keys are above 0 (40 and
/// 10 by default); either at 0 brings back the AfkWarnInterval warnings for it exactly as they were. Kill
/// and kick, the spectator kick and anti-camp stay on the timer.
///
/// <para><b>Why a clock of its own.</b> The timer adds Timer seconds every Timer seconds and warns once
/// AfkWarnInterval is reached, so the wait before the first warning is one step and the countdown is
/// AfkPunishAfterWarnings steps of the same size: the countdown can never be shorter than the wait. This
/// clock looks ten times a second and counts real seconds.</para>
///
/// <para><b>What counts.</b> The timer's test (view X/Y and position X/Y), against the sample from the last
/// look. As on the timer, nothing is counted while the player is dead or spectating, controlling a bot, in
/// freeze time, in a skipped warmup, off the ground or frozen: the count waits there, it does not start
/// over, and a respawn is not movement. Also not on the halftime and end-of-match screens. Moving at any
/// time (looking around in freeze time included), a team change and the spectator commands start it over.
/// Players with AfkSkipFlag are never moved. The countdown card goes while the count waits and comes back
/// with what is left.</para>
///
/// <para><b>Chat.</b> With the HUD on the card is the warning and nothing goes to chat. With it off, or when
/// the card can't reach the player, the old warning line comes once, as the countdown starts, with the
/// real countdown in it. The move line is the timer's (MoveToSpectators).</para>
/// </summary>
public partial class AFKManager
{
    /// <summary>The move to spectators runs on this clock, not on the timer's warnings.</summary>
    private bool MoveClockOn => Config.AfkPunishment == 1 && Config.AfkPunishAfterWarnings != 0
                                && Config.AfkMoveAfterSeconds > 0 && Config.AfkMoveCountdownSeconds > 0;

    /// <summary>When MoveTick last ran.</summary>
    private float _moveTickAt;

    /// <summary>
    /// The card's end is moved to the clock's only when it is this far out. While the count waits with the
    /// card up (off the ground) the end creeps on every look; following each step would leave the bar's
    /// drain running to the old end, while a jump past this restarts the bar where it should be.
    /// </summary>
    private const float MoveDeadlineSlack = 0.5f;

    /// <summary>Ten times a second: the move clock, then the HUD, so the HUD draws what the clock just decided.</summary>
    private void FastTick()
    {
        MoveTick();
        HudTick();
    }

    private void MoveTick()
    {
        var now = Server.CurrentTime;
        // Real seconds since the last look. 0 across a map change (the server clock starts over), capped after a hitch.
        var dt = Math.Clamp(now - _moveTickAt, 0f, 1f);
        _moveTickAt = now;

        if (!MoveClockOn)
            return;

        // What the timer skips as a whole (AfkTimer_Callback), and the halftime / match-ended phases
        var rules = _gGameRulesProxy;
        var paused = rules == null || rules.FreezePeriod || (Config.SkipWarmup && rules.WarmupPeriod) || rules.GamePhase is 4 or 5;

        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, IsBot: false, IsHLTV: false, Connected: PlayerConnectedState.Connected }
                || !_gPlayerInfo.TryGetValue(player.Index, out var data))
                continue;

            MoveStep(player, data, now, dt, paused);
        }
    }

    private void MoveStep(CCSPlayerController player, PlayerInfo data, float now, float dt, bool paused)
    {
        // Taking over a bot is playing
        if (player.ControllingBot)
        {
            data.StillSampled = false;
            MoveBack(player, data);
            return;
        }

        var pawn = player is { LifeState: (byte)LifeState_t.LIFE_ALIVE, Team: CsTeam.Terrorist or CsTeam.CounterTerrorist }
            ? player.PlayerPawn.Value
            : null;
        var angles = pawn?.EyeAngles;
        var origin = pawn?.CBodyComponent?.SceneNode?.AbsOrigin;

        if (pawn == null || angles == null || origin == null)
        {
            // Dead or spectating: nothing to compare and nothing counted. The count waits for the next life.
            data.StillSampled = false;
            MoveHud(player, data, false, now);
            return;
        }

        // The first look in this life, or just spawned: the pawn is being put at its spawn point
        if (!data.StillSampled || now - data.SpawnedAt < SpawnSampleSeconds)
        {
            StillSample(data, angles, origin);
            MoveHud(player, data, false, now);
            return;
        }

        if (data.StillAngX != angles.X || data.StillAngY != angles.Y
            || data.StillOrgX != origin.X || data.StillOrgY != origin.Y)
        {
            StillSample(data, angles, origin);
            MoveBack(player, data);
            return;
        }

        var flags = player.Pawn.Value?.Flags ?? 0;
        var frozen = (flags & (uint)PlayerFlags.FL_FROZEN) != 0;
        var onGround = (flags & (uint)PlayerFlags.FL_ONGROUND) != 0;

        // The timer's per-player skip: off the ground or frozen
        if (paused || frozen || !onGround)
        {
            MoveHud(player, data, !paused && !frozen, now);
            return;
        }

        data.StillSeconds += dt;

        if (!data.MoveCountdown)
        {
            if (data.StillSeconds < Config.AfkMoveAfterSeconds)
                return;

            // Looked up only here, not ten times a second
            if (Config.AfkSkipFlag.Count >= 1 && AdminManager.PlayerHasPermissions(player, Config.AfkSkipFlag.ToArray()))
            {
                data.StillSeconds = 0;
                return;
            }

            MoveCountdownStart(player, pawn, data, now);
            return;
        }

        if (data.StillSeconds >= Config.AfkMoveAfterSeconds + Config.AfkMoveCountdownSeconds)
        {
            var idleSeconds = (int)Math.Round(data.StillSeconds);
            MoveClockReset(data);
            data.StillSampled = false;
            MoveToSpectators(player, data, pawn, idleSeconds);
            return;
        }

        MoveHud(player, data, true, now);
    }

    /// <summary>The countdown starts: the bomb is passed on, then the card, or the chat line, and the sound.</summary>
    private void MoveCountdownStart(CCSPlayerController player, CCSPlayerPawn pawn, PlayerInfo data, float now)
    {
        data.MoveCountdown = true;
        data.MoveDeadline = now + MoveRemaining(data);

        // The countdown is the one warning here, so a non-zero AfkTransferC4AfterWarnings means "at its start"
        if (Config.AfkTransferC4AfterWarnings != 0 && player.TeamNum == 2 && HasC4(player))
            TransferC4(player, pawn);

        // The card when the HUD is on; the chat line otherwise, or when the card can't reach them
        if (!HudOn || !_hud!.Countdown(player, data.MoveDeadline, Config.AfkMoveCountdownSeconds, (int)Math.Round(data.StillSeconds)))
            player.PrintToChat(ReplaceVars(player, Localizer["ChatWarningMoveMessage"].Value, Config.AfkMoveCountdownSeconds));

        if (!string.IsNullOrEmpty(Config.PlaySoundName))
            player.ExecuteClientCommand($"play {Config.PlaySoundName}");
    }

    /// <summary>
    /// Brings the card in line with the clock. <paramref name="visible"/> is false while the count waits for
    /// something the player can't help (dead, freeze time, frozen); the card goes then and comes back after.
    /// </summary>
    private void MoveHud(CCSPlayerController player, PlayerInfo data, bool visible, float now)
    {
        if (!HudOn)
            return;

        if (!data.MoveCountdown || !visible)
        {
            if (_hud!.HasCountdown(player.Slot))
                _hud.EndCountdown(player);
            return;
        }

        var deadline = now + MoveRemaining(data);
        if (Math.Abs(deadline - data.MoveDeadline) > MoveDeadlineSlack)
            data.MoveDeadline = deadline;

        _hud!.Countdown(player, data.MoveDeadline, Config.AfkMoveCountdownSeconds,
            _hud.HasCountdown(player.Slot) ? null : (int)Math.Round(data.StillSeconds));
    }

    /// <summary>Counted seconds left before the move.</summary>
    private float MoveRemaining(PlayerInfo data) =>
        Math.Max(0f, Config.AfkMoveAfterSeconds + Config.AfkMoveCountdownSeconds - data.StillSeconds);

    /// <summary>The player moved: the count starts over, and a countdown on screen turns into "Back".</summary>
    private void MoveBack(CCSPlayerController player, PlayerInfo data)
    {
        var hadCountdown = data.MoveCountdown;
        MoveClockReset(data);

        if (hadCountdown && HudOn)
            _hud!.Back(player);
    }

    /// <summary>The count starts over. A card still up goes at the next look (MoveHud).</summary>
    private static void MoveClockReset(PlayerInfo data)
    {
        data.StillSeconds = 0;
        data.MoveCountdown = false;
    }

    private static void StillSample(PlayerInfo data, QAngle angles, Vector origin)
    {
        data.StillAngX = angles.X;
        data.StillAngY = angles.Y;
        data.StillOrgX = origin.X;
        data.StillOrgY = origin.Y;
        data.StillSampled = true;
    }
}
