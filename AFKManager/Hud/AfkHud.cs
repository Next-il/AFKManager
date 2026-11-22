using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;

namespace AFKManager;

/// <summary>
/// The AFK card: the private center panel that replaces the "You're Idle/AFK. Move or you'll be moved
/// to SPEC in N seconds" chat line. The layout is hud/panorama/layout/custom_game/afk_hud.xml, shipped
/// in the workshop addon.
///
/// <para><b>One layout, opened once per player and left open.</b> Its root is an invisible full-screen
/// panel; the one card inside carries its own "show". The card is "hidden" (collapsed) while not in use
/// and un-collapsed a moment before it shows, so it always has a resting place to slide in from.</para>
///
/// <para><b>Everything is drawn from state.</b> PanoramaManager rebuilds a player's panel by itself
/// after a respawn, a round restart or a map change, and drops every per-viewer variable and class on
/// the way (PanelAction.Restored). Each viewer's card is a plain record here and <see cref="Restore"/>
/// writes it back. Every timed step (the entry, the bar start, the end of "Back") is a time in that
/// record, run by <see cref="Tick"/>, so nothing is left behind in a timer closure.</para>
///
/// <para>This class only draws. Whether a player has a countdown, and until when, is the plugin's
/// call (AFKManager.Hud.cs).</para>
/// </summary>
internal sealed class AfkHud : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/afk_hud.vxml_c";

    private const string CardId = "afk_card";
    private const string NumberId = "afk_n";
    private const string BarId = "afk_bar";

    /// <summary>Un-collapse first, then "show": a transition needs the client to have seen the card at
    /// its resting offset (ToastService found one server frame is not enough).</summary>
    private const float EntrySeconds = 0.06f;

    /// <summary>The exit transition, before the card collapses or comes back as another state.</summary>
    private const float ExitSeconds = 0.35f;

    /// <summary>"run" off, wait, "run" on. A restart of the same class was measured to need 0.3s or more.</summary>
    private const float BarStartSeconds = 0.35f;

    private const float BackSeconds = 1.5f;
    private const float MovedSeconds = 4.0f;
    private const int LastSeconds = 3;
    private const int BarSteps = 20;

    /// <summary>The dur-N classes afk_hud.css defines: every second to 60, then every 5 to 180.</summary>
    private static readonly int[] BarDurations =
        Enumerable.Range(1, 60).Concat(Enumerable.Range(13, 24).Select(i => i * 5)).ToArray();

    private static readonly int MaxBarSeconds = BarDurations[^1];

    private enum Kind { None, Afk, Back, Moved }

    private static readonly string[] States = ["afk", "last", "ok", "moved"];

    private sealed class Viewer
    {
        public Kind Kind;

        /// <summary>"show" is on.</summary>
        public bool Shown;

        /// <summary>When to write the card and turn "show" on; -1 when nothing is pending.</summary>
        public float ShowAt = -1;

        /// <summary>When to collapse a card that has finished leaving; -1 when nothing is pending.</summary>
        public float CollapseAt = -1;

        /// <summary>Back and Moved: when the card goes.</summary>
        public float Until;

        // countdown
        public float Deadline;
        public float Total = 1;
        public string Sub = "";
        public int Number = -1;
        public bool PopA;

        // bar: the classes on afk_bar as last written, so the next write can take them off again
        public int BarAt = -1;
        public int BarDur = -1;
        public bool BarStarted;
        public float BarRunAt = -1;

        /// <summary>The bar is known to sit full and still on the client (reset while the card was
        /// collapsed), so a new countdown can start draining the moment it shows.</summary>
        public bool BarFull;
        public float BarResetAt = -1000;
    }

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Viewer> _viewers = [];
    private PanelHandle? _panel;

    public AfkHud(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    public bool Enabled => _panel != null;

    /// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
    public void Init()
    {
        if (_panel != null)
            return;

        try
        {
            Panorama.Init(_plugin);
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                // Unique server-wide: dialog variables are keyed by panel id
                RootPanelId = "AfkRoot",
                RevealClass = "show",
                RowCount = 0,
                CaptureInput = false,
                HideHud = HideHudFlags.None,
                SharedText = false,
            });
            _panel.OnEvent += OnPanelEvent;
            _plugin.Logger.LogInformation("AFK HUD ready");
        }
        catch (Exception ex)
        {
            // The plugin works without its HUD: every caller falls back to chat when Enabled is false
            _plugin.Logger.LogWarning("AFK HUD failed to start, using chat instead: {Message}", ex.Message);
            _panel = null;
        }
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _panel = null;
        _viewers.Clear();
    }

    private void OnPanelEvent(PanelEvent e)
    {
        if (e.Action == PanelAction.Restored && e.Player.IsValid)
            Restore(e.Player);
    }

    // ------------------------------------------------------------------ viewers

    private static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;

    private Viewer ViewerOf(CCSPlayerController player)
    {
        if (!_viewers.TryGetValue(player.Slot, out var viewer))
        {
            viewer = new Viewer();
            _viewers[player.Slot] = viewer;
        }

        return viewer;
    }

    /// <summary>Opens the player's session if it is not open yet, which also redraws what they should see.</summary>
    private bool EnsureOpen(CCSPlayerController player)
    {
        if (_panel == null || !IsHuman(player))
            return false;

        if (!_panel.IsOpenFor(player))
        {
            _panel.Open(player);
            Restore(player);
        }

        return _panel.IsOpenFor(player);
    }

    public bool HasCountdown(int slot) => _viewers.TryGetValue(slot, out var viewer) && viewer.Kind == Kind.Afk;

    public void OnDisconnect(int slot)
    {
        _viewers.Remove(slot);
        _panel?.Close(slot);
    }

    /// <summary>Map end: the clock restarts on the next map, so every stored time is meaningless now.</summary>
    public void Clear()
    {
        foreach (var viewer in _viewers.Values)
        {
            viewer.Kind = Kind.None;
            viewer.Shown = false;
            viewer.ShowAt = -1;
            viewer.CollapseAt = -1;
            viewer.BarStarted = false;
            viewer.BarRunAt = -1;
            viewer.BarFull = false;
            viewer.BarResetAt = -1000;
        }
    }

    /// <summary>
    /// Writes the card back for one player, from state. Called on open and after the library rebuilt the
    /// panel. Every class it may have lost is written again; the bar starts over from where it should be.
    /// </summary>
    public void Restore(CCSPlayerController player)
    {
        if (_panel == null || !_panel.IsOpenFor(player))
            return;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;

        if (viewer.Kind is Kind.Back or Kind.Moved && now >= viewer.Until)
            viewer.Kind = Kind.None;

        viewer.Shown = false;
        viewer.Number = -1;
        viewer.BarStarted = false;
        viewer.BarRunAt = -1;
        // Not trusted after a rebuild: the bar takes the slow, explicit reset
        viewer.BarFull = false;
        viewer.BarResetAt = -1000;
        Cls(player, CardId, "show", false);

        if (viewer.Kind == Kind.None)
        {
            viewer.ShowAt = -1;
            viewer.CollapseAt = -1;
            Cls(player, CardId, "hidden", true);
            return;
        }

        Cls(player, CardId, "hidden", false);
        viewer.CollapseAt = -1;
        viewer.ShowAt = now + EntrySeconds;
    }

    // ------------------------------------------------------------------ the three cards

    /// <summary>
    /// Shows or updates the player's countdown. <paramref name="deadline"/> is the server time the plugin
    /// will move them, <paramref name="total"/> the whole countdown from its first warning (what a full
    /// bar stands for). <paramref name="idleSeconds"/> rewrites the "You haven't moved" line; null keeps it.
    /// Returns false when the card cannot be shown to this player, so the caller prints its chat line.
    /// </summary>
    public bool Countdown(CCSPlayerController player, float deadline, float total, int? idleSeconds)
    {
        if (!EnsureOpen(player))
            return false;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;

        if (idleSeconds is { } idle)
            viewer.Sub = Text("Hud.Afk.Sub", "You haven't moved for {0} seconds.", idle);

        // A countdown that moved by more than a moment (it was picked up again after a pause) gets a new bar
        if (viewer.Kind == Kind.Afk && Math.Abs(viewer.Deadline - deadline) > 0.5f)
            viewer.BarStarted = false;

        viewer.Deadline = deadline;
        viewer.Total = Math.Max(total, 0.1f);

        if (viewer.Kind != Kind.Afk)
        {
            viewer.Kind = Kind.Afk;
            viewer.Number = -1;
            viewer.BarStarted = false;
            viewer.BarRunAt = -1;
            Reveal(player, viewer, now);
        }
        else if (idleSeconds != null && viewer.Shown)
        {
            Set(player, "afk_sub", viewer.Sub);
        }

        return true;
    }

    /// <summary>The player moved in time: the countdown turns into "Back" for a moment, then goes.</summary>
    public void Back(CCSPlayerController player)
    {
        if (_panel == null || !_viewers.TryGetValue(player.Slot, out var viewer) || viewer.Kind != Kind.Afk)
            return;

        viewer.Kind = Kind.Back;
        viewer.Until = Server.CurrentTime + BackSeconds;

        // In place, like the preview. A card still waiting for its entry is written when it shows.
        if (viewer.Shown && _panel.IsOpenFor(player))
            Draw(player, viewer);
    }

    /// <summary>
    /// The player was moved to spectators: "Moved to spectators / You were AFK / Press M to pick a team".
    /// A countdown still on screen leaves first and the card comes back as the new state. Returns false
    /// when the card cannot be shown to this player.
    /// </summary>
    public bool Moved(CCSPlayerController player)
    {
        if (!EnsureOpen(player))
            return false;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;
        var wasShown = viewer.Shown;

        viewer.Kind = Kind.Moved;

        if (wasShown)
        {
            Cls(player, CardId, "show", false);
            viewer.Shown = false;
            viewer.CollapseAt = -1;
            viewer.ShowAt = now + ExitSeconds;
        }
        else
        {
            Reveal(player, viewer, now);
        }

        viewer.Until = viewer.ShowAt + MovedSeconds;
        return true;
    }

    /// <summary>Takes a countdown away without a "Back" (the player died, changed team, or the round paused it).</summary>
    public void EndCountdown(CCSPlayerController player)
    {
        if (_viewers.TryGetValue(player.Slot, out var viewer) && viewer.Kind == Kind.Afk)
            Hide(player, viewer, Server.CurrentTime);
    }

    private void Reveal(CCSPlayerController player, Viewer viewer, float now)
    {
        Cls(player, CardId, "hidden", false);
        viewer.CollapseAt = -1;

        if (viewer.Shown)
        {
            Draw(player, viewer);
            return;
        }

        viewer.ShowAt = now + EntrySeconds;
    }

    private void Hide(CCSPlayerController player, Viewer viewer, float now)
    {
        viewer.Kind = Kind.None;
        viewer.ShowAt = -1;
        viewer.BarRunAt = -1;

        if (_panel == null || !_panel.IsOpenFor(player))
        {
            viewer.Shown = false;
            return;
        }

        // The content stays as it is, so the card fades out showing what it showed
        Cls(player, CardId, "show", false);
        viewer.Shown = false;
        viewer.CollapseAt = now + ExitSeconds;
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>Runs every timed step. The plugin calls this ten times a second while the HUD is on.</summary>
    public void Tick()
    {
        if (_panel == null || _viewers.Count == 0)
            return;

        var now = Server.CurrentTime;

        foreach (var (slot, viewer) in _viewers)
        {
            if (viewer.Kind == Kind.None && viewer.CollapseAt < 0)
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || !_panel.IsOpenFor(player))
                continue;

            if (viewer.CollapseAt >= 0 && now >= viewer.CollapseAt)
            {
                viewer.CollapseAt = -1;
                if (viewer.Kind == Kind.None)
                {
                    Cls(player, CardId, "hidden", true);
                    // Out of sight now, so the bar can go back to full for the next countdown
                    BarSnapTo(player, viewer, 1f, now);
                }
            }

            if (viewer.ShowAt >= 0 && now >= viewer.ShowAt && viewer.Kind != Kind.None)
            {
                viewer.ShowAt = -1;
                Draw(player, viewer);
                Cls(player, CardId, "show", true);
                viewer.Shown = true;
            }

            switch (viewer.Kind)
            {
                case Kind.Afk:
                    if (viewer.Shown)
                        TickCountdown(player, viewer, now);
                    break;

                case Kind.Back:
                case Kind.Moved:
                    if (now >= viewer.Until && viewer.ShowAt < 0)
                        Hide(player, viewer, now);
                    break;
            }
        }
    }

    private void TickCountdown(CCSPlayerController player, Viewer viewer, float now)
    {
        var remaining = viewer.Deadline - now;
        var number = Math.Max(0, (int)Math.Ceiling(remaining - 0.05f));

        if (number != viewer.Number)
        {
            viewer.Number = number;
            Set(player, "afk_n", number.ToString());
            OneOf(player, CardId, "st-", number <= LastSeconds ? "last" : "afk", States);

            if (number <= LastSeconds)
            {
                // Ping-pong: the only restart a class diff can carry
                viewer.PopA = !viewer.PopA;
                Cls(player, NumberId, "pop-a", viewer.PopA);
                Cls(player, NumberId, "pop-b", !viewer.PopA);
            }
        }

        // A countdown longer than the longest dur-N class keeps a full bar until it fits
        if (!viewer.BarStarted && remaining <= MaxBarSeconds + 0.5f)
        {
            viewer.BarStarted = true;
            var fraction = remaining / viewer.Total;

            if (viewer.BarFull && now - viewer.BarResetAt >= BarStartSeconds && fraction >= 0.97f)
            {
                // A fresh countdown on a bar that has sat full long enough: drain from here
                BarRun(player, viewer, remaining);
            }
            else
            {
                // Picked up halfway, or the bar's state is not known: snap it, then drain after the wait
                BarSnapTo(player, viewer, fraction, now);
                viewer.BarRunAt = now + BarStartSeconds;
            }
        }

        if (viewer.BarRunAt >= 0 && now >= viewer.BarRunAt)
        {
            viewer.BarRunAt = -1;
            BarRun(player, viewer, viewer.Deadline - now);
        }
    }

    // ------------------------------------------------------------------ the bar

    /// <summary>
    /// Puts the bar at a fraction with no transition. The duration comes off before "run" so the reset
    /// snaps; with a dur-N class still on, dropping "run" would animate the bar back to full over it.
    /// </summary>
    private void BarSnapTo(CCSPlayerController player, Viewer viewer, float fraction, float now)
    {
        if (viewer.BarDur >= 0)
            Cls(player, BarId, $"dur-{viewer.BarDur}", false);
        viewer.BarDur = -1;
        Cls(player, BarId, "run", false);

        var at = Math.Clamp((int)Math.Round(fraction * BarSteps), 0, BarSteps);
        if (viewer.BarAt >= 0 && viewer.BarAt != at)
            Cls(player, BarId, $"at-{viewer.BarAt}", false);
        Cls(player, BarId, $"at-{at}", true);
        viewer.BarAt = at;

        viewer.BarFull = at == BarSteps;
        viewer.BarResetAt = now;
    }

    /// <summary>Starts the drain: one linear clip transition to 0 over the time that is left.</summary>
    private void BarRun(CCSPlayerController player, Viewer viewer, float seconds)
    {
        var dur = BarDurations.OrderBy(d => Math.Abs(d - seconds)).First();
        Cls(player, BarId, $"dur-{dur}", true);
        viewer.BarDur = dur;
        Cls(player, BarId, "run", true);
        viewer.BarFull = false;
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Writes the card's text and state for what the viewer holds now.</summary>
    private void Draw(CCSPlayerController player, Viewer viewer)
    {
        switch (viewer.Kind)
        {
            case Kind.Afk:
                Set(player, "afk_eyebrow", Text("Hud.Afk.Eyebrow", "AFK"));
                Set(player, "afk_title", Text("Hud.Afk.Title", "Move or go to spectators"));
                Set(player, "afk_sub", viewer.Sub);
                // The number and its state come from the clock, at once
                viewer.Number = -1;
                TickCountdown(player, viewer, Server.CurrentTime);
                break;

            case Kind.Back:
                Set(player, "afk_eyebrow", Text("Hud.Back.Eyebrow", "Back"));
                Set(player, "afk_title", Text("Hud.Back.Title", "You're back in the game"));
                Set(player, "afk_sub", "");
                OneOf(player, CardId, "st-", "ok", States);
                break;

            case Kind.Moved:
                Set(player, "afk_eyebrow", Text("Hud.Moved.Eyebrow", "Moved to spectators"));
                Set(player, "afk_title", Text("Hud.Moved.Title", "You were AFK"));
                Set(player, "afk_sub", "");
                Set(player, "afk_keys_a", Text("Hud.Moved.KeysBefore", "Press"));
                Set(player, "afk_keys_b", Text("Hud.Moved.KeysAfter", "to pick a team."));
                OneOf(player, CardId, "st-", "moved", States);
                break;
        }
    }

    private void Set(CCSPlayerController player, string name, string value) => _panel!.SetVariableFor(player, name, value);

    private void Cls(CCSPlayerController player, string id, string cls, bool on) => _panel!.SetClassFor(player, id, cls, on);

    /// <summary>Exactly one class of a group on.</summary>
    private void OneOf(CCSPlayerController player, string id, string prefix, string value, string[] all)
    {
        foreach (var candidate in all)
            Cls(player, id, prefix + candidate, candidate == value);
    }

    /// <summary>A phrase from lang/, or the English fallback when the server's lang files don't have it yet.</summary>
    private string Text(string key, string fallback, params object[] args)
    {
        var text = _plugin.Localizer[key, args];
        return text.ResourceNotFound ? string.Format(fallback, args) : text.Value;
    }
}
