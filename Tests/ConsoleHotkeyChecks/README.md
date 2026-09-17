# Console hotkey checks

Run `dotnet run --project Tests/ConsoleHotkeyChecks/ConsoleHotkeyChecks.csproj` from the repository root.
Optionally pass an Input.ini path after `--` to check that an existing Insert binding needs no changes. The supplied file is only read.

These checks cover profile persistence, disabled behavior, all three game editions, array directives, idempotence, and preservation of mappings, history, encoding and modification time. File tests use a temporary directory and never launch the game.
