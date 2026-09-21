# Contributing

GoldenTicket is preparing for an open-source release. Begin with the implemented
[architecture](docs/architecture.md), [README](README.md) and [remaining work](TODO.md).
[DESIGN.md](DESIGN.md) includes planned behaviour as well as implemented decisions.

## Build and checks

Use the .NET SDK in `global.json`. Core changes can be developed without Windows:

```sh
dotnet restore tests/GoldenTicket.Core.Tests/GoldenTicket.Core.Tests.csproj --locked-mode --configfile NuGet.config
dotnet test tests/GoldenTicket.Core.Tests/GoldenTicket.Core.Tests.csproj --no-restore
```

Windows 11 is required for desktop/vision integration and WPF rendering:

```sh
dotnet restore GoldenTicket.sln --locked-mode --configfile NuGet.config
dotnet build GoldenTicket.sln --no-restore
dotnet test GoldenTicket.sln --no-build --no-restore
dotnet run --project tools/GoldenTicket.Simulator --no-build -- simulate --games 20 --seats 4
dotnet run --project tools/GoldenTicket.UiSmoke --no-build -- artifacts/ui-smoke
```

See [build and CI](docs/build-and-ci.md) for companion browser checks. Run checks appropriate to the
changed boundary; command, storage or shared-projection changes warrant replay, failure and
integration coverage. UI rendering must remain free of binding errors. Automated camera fixtures
cannot replace real-device acceptance.

## Change boundaries

- Put rules in Domain, coordination in Application, and device/storage/transport details in their
  adapters. Production code must not depend on `tools/` or `tests/`.
- Keep authoritative changes on the command/event path. Read-only collections must not expose
  mutable backing objects. Use public/seat projections rather than sharing referee state.
- Preserve private-hand boundaries. AI may infer from public play but never inspect an opponent's
  actual tickets, hand or the hidden deck order.
- Use capture/storage fakes for lifecycle and failure tests. Do not drive the user's webcam, saved
  games or presentation preferences as a side effect of automated testing.
- Retain operation identity and cancellation checks in asynchronous work. A stale result must not
  become a current action; a failed storage acknowledgement requires recovery.
- Preserve event formats and hashes unless a migration is deliberately designed and tested. Commit
  package locks with intentional dependency changes.
- Keep refactors reviewable. Explain the concrete coupling or defect, resulting ownership and
  regression evidence. New abstractions should remove a real dependency or duplicated policy.

Update affected documentation with behaviour changes. Before producing a release, reconcile README,
TODO and DESIGN with the final code and include those updates in the release source commit. Do not
commit local saves, private diagnostics or raw training captures.
