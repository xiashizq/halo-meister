namespace HaloMeister.App.Services;

public enum GameSessionPhase
{
    Idle,
    WaitingForMission,
    Connected,
    Failed,
}

/// <summary>
/// Connects when Halo: Campaign Evolved is ready and drops the session when
/// the process exits. The loop wakes once a second and almost always returns
/// immediately. A process snapshot runs only while disconnected, and the
/// module walk that attaches a session slows down after the game has been
/// open without a mission for a few tries.
/// </summary>
public sealed class GameSessionWatcher : IDisposable
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WaitingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedInterval = TimeSpan.FromSeconds(10);
    private readonly RuntimeTagMemoryService _game;
    private readonly Func<bool> _connectAllowed;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1));
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateGate = new();
    private readonly Task _loop;
    private long _nextProbeTick;
    private int _probeRequested = 1;
    private int _notReadyStreak;
    private GameSessionPhase _phase = GameSessionPhase.Idle;
    private string? _detail;

    public GameSessionWatcher(RuntimeTagMemoryService game, Func<bool> connectAllowed)
    {
        _game = game;
        _connectAllowed = connectAllowed;
        Current = this;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public static GameSessionWatcher? Current { get; private set; }

    public event EventHandler? PhaseChanged;

    public GameSessionPhase Phase
    {
        get
        {
            lock (_stateGate)
                return _phase;
        }
    }

    public string? Detail
    {
        get
        {
            lock (_stateGate)
                return _detail;
        }
    }

    public void RequestProbe() => Interlocked.Exchange(ref _probeRequested, 1);

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop exits when the timer or token is cancelled.
        }

        if (ReferenceEquals(Current, this))
            Current = null;
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(token))
            {
                bool requested = Interlocked.Exchange(ref _probeRequested, 0) == 1;
                if (!requested && Environment.TickCount64 < Volatile.Read(ref _nextProbeTick))
                    continue;

                try
                {
                    Probe();
                }
                catch (Exception)
                {
                    Schedule(FailedInterval);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch (ObjectDisposedException)
        {
            // Timer disposed during shutdown.
        }
    }

    private void Probe()
    {
        if (_cts.IsCancellationRequested)
            return;

        if (!_connectAllowed())
        {
            if (_game.IsConnected)
                SetPhase(GameSessionPhase.Connected, null);
            else
                SetPhase(GameSessionPhase.Idle, null);
            Schedule(IdleInterval);
            return;
        }

        if (_game.ReleaseIfExited() && _cts.IsCancellationRequested)
            return;

        if (_game.IsConnected)
        {
            _notReadyStreak = 0;
            SetPhase(GameSessionPhase.Connected, null);
            Schedule(IdleInterval);
            return;
        }

        GameAttachResult result = _game.TryAttach(out string? error);
        if (_cts.IsCancellationRequested)
        {
            if (result == GameAttachResult.Connected)
                _game.Disconnect();
            return;
        }

        switch (result)
        {
            case GameAttachResult.Connected:
                _notReadyStreak = 0;
                SetPhase(GameSessionPhase.Connected, null);
                Schedule(IdleInterval);
                break;
            case GameAttachResult.NotRunning:
                _notReadyStreak = 0;
                SetPhase(GameSessionPhase.Idle, null);
                Schedule(IdleInterval);
                break;
            case GameAttachResult.NotReady:
                _notReadyStreak++;
                if (_notReadyStreak >= 6 && !string.IsNullOrWhiteSpace(error))
                    SetPhase(GameSessionPhase.Failed, error);
                else
                    SetPhase(GameSessionPhase.WaitingForMission, null);
                Schedule(_notReadyStreak >= 4 ? WaitingInterval : IdleInterval);
                break;
            default:
                SetPhase(GameSessionPhase.Failed, error);
                Schedule(FailedInterval);
                break;
        }
    }

    private void Schedule(TimeSpan delay)
        => Volatile.Write(ref _nextProbeTick, Environment.TickCount64 + (long)delay.TotalMilliseconds);

    private void SetPhase(GameSessionPhase phase, string? detail)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _phase != phase || !string.Equals(_detail, detail, StringComparison.Ordinal);
            _phase = phase;
            _detail = detail;
        }

        if (changed)
            PhaseChanged?.Invoke(this, EventArgs.Empty);
    }
}
