using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace AFKManager;

/// <summary>"Hud" in AFKManager.json. Both keys are optional: a config without them gets these defaults.</summary>
public class AFKManagerHudConfig
{
    /// <summary>The AFK card and the toasts. False brings every old chat line back unchanged.</summary>
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>"X moved to spectators" as a toast to everyone else. False shows them nothing at all.</summary>
    [JsonPropertyName("OthersToast")]
    public bool OthersToast { get; set; } = true;
}

/// <summary>
/// The HUD side of the plugin: decides from the AFK state who has a countdown and until when, and hands
/// that to <see cref="AfkHud"/>, which only draws. Covers the move-to-spectators punishment
/// (AfkPunishment 1); the kill and kick punishments, the spectator kick and anti-camp keep their chat lines.
///
/// <para><b>The countdown is the real one.</b> The chat line said "in {AfkPunishAfterWarnings x
/// AfkWarnInterval - warnings x AfkWarnInterval} seconds", which is only true while Timer equals
/// AfkWarnInterval: the warnings come every Timer seconds, so with Timer 20 and AfkWarnInterval 5 the line
/// said 10 and the move came 40 seconds later. The card counts to the tick that will actually move the
/// player, worked out the way the timer counts (<see cref="AfkDeadline"/>).</para>
///
/// <para><b>Moving is noticed at once.</b> The timer only looks every Timer seconds, so a player who moves
/// right after a warning would watch the countdown carry on for up to that long. The HUD compares the
/// player's position and view with the timer's last sample ten times a second, the same X/Y test the timer
/// makes, and shows "Back" as soon as they differ. The timer's own state is never touched: it resets the
/// warnings at its next tick exactly as before.</para>
///
/// <para>All of the above is the warning ladder. With AfkMoveAfterSeconds and AfkMoveCountdownSeconds
/// (the default) the move has its own clock in AFKManager.Move.cs, which drives the same card itself.</para>
/// </summary>
public partial class AFKManager
{
    private AfkHud? _hud;

    /// <summary>When AfkTimer_Callback last ran, freeze time included, so the next tick is known.</summary>
    private float _lastTickAt;

    private bool HudOn => Config.Hud.Enabled && _hud is { Enabled: true };

    /// <summary>
    /// The card replaces the warnings only for the move-to-spectators punishment, and only while that runs
    /// on the warnings. On the move clock (MoveClockOn) the clock feeds the card instead.
    /// </summary>
    private bool HudCountdowns => HudOn && Config.AfkPunishment == 1 && Config.AfkPunishAfterWarnings > 0 && !MoveClockOn;

    /// <summary>A respawned player's sample is taken 0.2s after the spawn; until then it is the old position.
    /// The move clock uses the same window: a spawn puts the pawn somewhere new, which is not the player moving.</summary>
    private const float SpawnSampleSeconds = 0.3f;

    private void HudLoad()
    {
        if (!Config.Hud.Enabled)
            return;

        // ToastService can be (re)loaded at any time; one probe per map keeps the answer fresh
        RegisterListener<Listeners.OnMapStart>(_ => ToastBridge.Resolve());

        _hud = new AfkHud(this);
        _hud.Init();
        // HudTick runs from FastTick (AFKManager.Move.cs), registered in Load
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        // Not in Load: plugin load order is undefined, so ToastService may not have registered yet
        ToastBridge.Resolve();
    }

    public override void Unload(bool hotReload)
    {
        _hud?.Dispose();
        _hud = null;
    }

    // ------------------------------------------------------------------ the clock

    private void HudTick()
    {
        if (_hud is not { Enabled: true })
            return;

        if (HudCountdowns)
        {
            var now = Server.CurrentTime;

            foreach (var (index, data) in _gPlayerInfo)
            {
                if (data.AfkWarningCount == 0 && !_hud.HasCountdown((int)index - 1))
                    continue;

                var player = Utilities.GetPlayerFromIndex((int)index);
                if (player is not { IsValid: true, IsBot: false })
                    continue;

                HudSync(player, data, now);
            }
        }

        _hud.Tick();
    }

    /// <summary>Brings one player's card in line with the AFK state.</summary>
    private void HudSync(CCSPlayerController player, PlayerInfo data, float now)
    {
        if (!CountdownApplies(player, data))
        {
            _hud!.EndCountdown(player);
            return;
        }

        // Just respawned: the sample is still the old position, so a comparison means nothing yet
        if (now - data.SpawnedAt < SpawnSampleSeconds)
            return;

        if (MovedSinceSample(player, data))
        {
            _hud!.Back(player);
            return;
        }

        var (deadline, total) = AfkDeadline(data.AfkWarningCount, data.AfkTime, _lastTickAt + Config.Timer);
        _hud!.Countdown(player, deadline, total, _hud.HasCountdown(player.Slot) ? null : IdleSeconds(data, now));
    }

    /// <summary>
    /// Whether the timer is counting this player towards a move right now. Off while it would skip them -
    /// freeze time, a skipped warmup, frozen (a pause), dead, spectating, controlling a bot - since the
    /// time it would move them can't be known then. The card comes back when the count goes on.
    /// </summary>
    private bool CountdownApplies(CCSPlayerController player, PlayerInfo data)
    {
        if (data.AfkWarningCount == 0 || player.ControllingBot)
            return false;

        if (_gGameRulesProxy == null || _gGameRulesProxy.FreezePeriod || (Config.SkipWarmup && _gGameRulesProxy.WarmupPeriod))
            return false;

        if (player is not { LifeState: (byte)LifeState_t.LIFE_ALIVE, Team: CsTeam.Terrorist or CsTeam.CounterTerrorist })
            return false;

        var flags = player.Pawn.Value?.Flags ?? 0;
        return (flags & (uint)PlayerFlags.FL_FROZEN) == 0;
    }

    /// <summary>The timer's own test (AfkTimer_Callback): view X/Y and position X/Y against its last sample.</summary>
    private static bool MovedSinceSample(CCSPlayerController player, PlayerInfo data)
    {
        var pawn = player.PlayerPawn.Value;
        var angles = pawn?.EyeAngles;
        var origin = pawn?.CBodyComponent?.SceneNode?.AbsOrigin;

        if (angles == null || origin == null || data.Angles == null || data.Origin == null)
            return false;

        return data.Angles.X != angles.X || data.Angles.Y != angles.Y
            || data.Origin.X != origin.X || data.Origin.Y != origin.Y;
    }

    /// <summary>
    /// When the timer will move a player who stays put, and how long the whole countdown is (what a full
    /// bar stands for). It mirrors AfkTimer_Callback: every Timer seconds AfkTime grows by Timer; once it
    /// reaches AfkWarnInterval the player is warned (one more warning, AfkTime back to 0), or moved when the
    /// warnings already number AfkPunishAfterWarnings.
    /// </summary>
    /// <param name="warnings">Warnings given so far.</param>
    /// <param name="afkTime">AfkTime now.</param>
    /// <param name="nextTick">Server time of the timer's next run.</param>
    private (float Deadline, float Total) AfkDeadline(int warnings, float afkTime, float nextTick)
    {
        var step = TicksToWarn(0f) * Config.Timer;
        var deadline = nextTick
                       + (TicksToWarn(afkTime) - 1) * Config.Timer
                       + Math.Max(0, Config.AfkPunishAfterWarnings - warnings) * step;

        return (deadline, Config.AfkPunishAfterWarnings * step);
    }

    /// <summary>Timer runs until AfkTime reaches AfkWarnInterval, counted in the same float steps the timer adds.</summary>
    private int TicksToWarn(float afkTime)
    {
        var ticks = 0;
        var t = afkTime;

        do
        {
            t += Config.Timer;
            ticks++;
        }
        while (t < Config.AfkWarnInterval && ticks < 10000);

        return ticks;
    }

    /// <summary>
    /// How long the player has stood still, at least: since the timer last saw them in a different place,
    /// or since they spawned.
    /// </summary>
    private static int IdleSeconds(PlayerInfo data, float now) => Math.Max(0, (int)Math.Round(now - data.IdleSince));

    // ------------------------------------------------------------------ called from AfkTimer_Callback

    /// <summary>
    /// A warning for the move-to-spectators punishment, before the warning is counted. Shows or updates the
    /// card; false when it could not be shown, so the caller prints the chat line.
    /// </summary>
    private bool HudWarn(CCSPlayerController player, PlayerInfo data)
    {
        if (!HudCountdowns)
            return false;

        var (deadline, total) = AfkDeadline(data.AfkWarningCount + 1, 0f, _lastTickAt + Config.Timer);
        return _hud!.Countdown(player, deadline, total, IdleSeconds(data, Server.CurrentTime));
    }

    /// <summary>The timer saw the player move.</summary>
    private void HudBack(CCSPlayerController player)
    {
        if (HudOn)
            _hud!.Back(player);
    }

    /// <summary>
    /// The player was just moved to spectators: their own card, and one toast to everyone else instead of
    /// the chat line to all. Anyone the card or toast can't reach gets <paramref name="chatLine"/>, the line
    /// they would have had before.
    /// </summary>
    private void HudMoved(CCSPlayerController player, string chatLine, int idleSeconds)
    {
        if (!_hud!.Moved(player))
            player.PrintToChat(chatLine);

        if (!Config.Hud.OthersToast)
            return;

        var title = HudText("Hud.Toast.Moved.Title", "{0} moved to spectators", player.PlayerName);
        var description = HudText("Hud.Toast.Moved.Description", "AFK for {0} seconds", idleSeconds);

        foreach (var other in Humans())
        {
            if (other.Slot == player.Slot)
                continue;

            if (!ToastBridge.TryNotify(other, title, description))
                other.PrintToChat(chatLine);
        }
    }

    /// <summary>
    /// The bomb was passed on from an AFK carrier: a toast to the Terrorists only. False when ToastService
    /// is not there (or the HUD is off), so the caller prints the old line to everyone.
    /// </summary>
    private bool HudBombToast(CCSPlayerController afk, CCSPlayerController target, string chatLine)
    {
        if (!Config.Hud.Enabled || !ToastBridge.Available)
            return false;

        var title = HudText("Hud.Toast.Bomb.Title", "Bomb passed to {0}", target.PlayerName);
        var description = HudText("Hud.Toast.Bomb.Description", "{0} is AFK", afk.PlayerName);

        foreach (var terrorist in Humans().Where(p => p.Team == CsTeam.Terrorist))
        {
            if (!ToastBridge.TryNotify(terrorist, title, description, ToastBridge.ColorWarn, ToastBridge.IconAlert))
                terrorist.PrintToChat(chatLine);
        }

        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<CCSPlayerController> Humans() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false } && p.Connected == PlayerConnectedState.Connected);

    /// <summary>A phrase from lang/, or the English fallback when the server's lang files don't have it yet.</summary>
    private string HudText(string key, string fallback, params object[] args)
    {
        var text = Localizer[key, args];
        return text.ResourceNotFound ? string.Format(fallback, args) : text.Value;
    }
}
