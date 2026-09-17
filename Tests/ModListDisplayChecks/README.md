# Mod list display checks

Run from the repository root:

```
dotnet run --project Tests/ModListDisplayChecks/ModListDisplayChecks.csproj -p:SelfContained=false -p:PublishSingleFile=false -p:UsedAvaloniaProducts=
```

Checks all sort directions, stable ties, missing timestamps, regex matching by name/ID/path, invalid patterns and timeouts, reset, read-only behavior and metadata refresh. Source collection notifications and serialized order must remain unchanged during every display-only operation. No game files or Steam access are needed.
