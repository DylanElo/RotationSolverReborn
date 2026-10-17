Runs the complete production registration/dispatcher and state/special-command implementations, linked directly from source. Command and targeting enums are also production source. External Dalamud/ECommons services, player presence, config, queued actions, IPC and UI are in-memory boundaries; no installed settings or game state are changed.

```powershell
dotnet run --project tests/CommandHandlerCheck -c Release
```

Checks include first startup cleanup, repeated enable/disable, help refresh, foreign command ownership, aliases, existing macro strings, configuration fallback, mixed-case state commands, targeting, cancellation cleanup, missing player and special/other dispatch. The other-command branch records dispatch and payload at its external boundary; it does not execute settings mutations.

To verify regression sensitivity without replacing working source, set `-p:CommandRegistrationSource=<absolute-path-to-old-RSCommands_BasicInfo.cs>`. The original implementation fails the first-startup cleanup assertion.
