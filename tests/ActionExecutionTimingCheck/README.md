# Action execution timing check

Run from the repository root:

```powershell
dotnet run --project tests/ActionExecutionTimingCheck -c Release
```

The harness follows `TargetingDecisionCheck`: SDK-bundled Roslyn compiles the actual production `CanDoAnAction`, `DoAction`, `CanDoAction`, `PlayerHasLockActions`, `IsPlayerOccupied`, and (when present) `CanQueueGcd` method bodies against narrow platform stubs. It does not reimplement the scheduler policy. The source hashes identify the tested inputs.

The frame scenario invokes admission followed by execution. It checks early GCD evaluation, the native queue submission boundary, press cadence after native success/failure, the end-of-cast queue window, manual queue ownership, and existing status/utility lockouts. Roslyn substitutes only `DateTime.Now` platform reads with a controlled `Clock.Now`; fixed click bounds and exact clock advances verify the 99ms/100ms cadence boundary without sleeps, random verdicts, or host scheduling assumptions.

To reproduce the pre-fix result against a Git revision:

```powershell
dotnet run --project tests/ActionExecutionTimingCheck -c Release -- --baseline-ref 713233f0
```

Before the scheduler fix, the harness reports nine failing checks (26/35 pass): early evaluations consume cadence, first queueable GCD admission is delayed, GCDs are throttled, short-lock GCD submission is blocked (including both inclusive queue boundaries), rejected native actions consume cadence and block immediate retries, and casting uses total rather than remaining time.

The native game engine, latency, targeting, and native action success are external boundaries. Passing these checks proves scheduler admission and protected lockouts, not a live GCD/DPS improvement.

