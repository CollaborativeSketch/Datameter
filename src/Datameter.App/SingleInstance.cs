using System.Threading;

namespace Datameter.App;

/// <summary>
/// Keeps one Datameter per user, and hands a second launch back to the first.
///
/// Two copies would be two of everything visible — two floating meters, two notification-area
/// icons — and two writers against one SQLite file. Launching again is nearly always someone
/// asking for the window they have already got, so the second copy wakes the first and leaves.
///
/// Both names are per-user rather than global: two people signed in at once each get their own
/// Datameter, reading their own history, which is the same boundary the database already has.
/// Debug builds take their own names, so a dev build never talks to an installed one.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _held;
    private static EventWaitHandle? _wakeUp;
    private static Thread? _listener;

    private static string MutexName => $@"Local\Datameter.Instance.{AppInfo.DisplayName}";
    private static string EventName => $@"Local\Datameter.Activate.{AppInfo.DisplayName}";

    /// <summary>
    /// True if this process is the one that should run. False means another copy already owns
    /// the name and has been asked to come forward, and this one should exit without drawing
    /// anything.
    /// </summary>
    public static bool Claim()
    {
        try
        {
            _held = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

            if (isFirst) return true;

            // Someone is already running. Ask them to show themselves, then step aside.
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var existing))
                {
                    using (existing) existing.Set();
                }
            }
            catch
            {
                // Waking the other copy is a courtesy. Failing it is not a reason to start a
                // second one.
            }

            _held.Dispose();
            _held = null;
            return false;
        }
        catch
        {
            // If the name cannot be claimed at all, run: one window the user did not expect is
            // a smaller failure than no window at all.
            return true;
        }
    }

    /// <summary>
    /// Starts listening for later launches. <paramref name="activate"/> is raised on a
    /// background thread, so it must marshal to the UI itself.
    /// </summary>
    public static void ListenForOtherLaunches(Action activate)
    {
        try
        {
            _wakeUp = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch
        {
            return;
        }

        _listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_wakeUp.WaitOne()) return;
                    activate();
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Datameter second-launch listener"
        };

        _listener.Start();
    }
}
