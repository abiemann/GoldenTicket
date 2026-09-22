# AI policy comparisons

`benchmark-ai` compares the current AI with a preserved `GoldenTicket.AI.dll`. Preserve the old assembly and its source hashes **before changing the policy**, and use the same build configuration and runtime for both versions.

Run these commands from the repository root:

```powershell
dotnet build tools/GoldenTicket.Simulator/GoldenTicket.Simulator.csproj -c Debug --no-restore -warnaserror
dotnet tools/GoldenTicket.Simulator/bin/Debug/net10.0/GoldenTicket.Simulator.dll benchmark-ai --baseline artifacts/baseline/assembly/GoldenTicket.AI.dll --output artifacts/baseline-check.json --seeds 1 --seed 101 --seats 3,5 --difficulties Standard,Aggressive --self-check
dotnet tools/GoldenTicket.Simulator/bin/Debug/net10.0/GoldenTicket.Simulator.dll benchmark-ai --baseline artifacts/baseline/assembly/GoldenTicket.AI.dll --output artifacts/comparison.json --seeds 10 --seed 1 --seats 3,5 --difficulties Standard,Aggressive --max-seconds 180
```

The self-check runs the old policy on both sides and requires identical complete outcome fingerprints. Each comparison then changes only the focal policy, retaining the deck seed, per-seat random streams, and old Standard rivals. Every seat position is tested, and execution order alternates between old-first and new-first. An adjacent `baseline/baseline.json`, when present, is embedded in the report for source provenance; the actual assembly SHA-256 hashes are always recorded.

The simulator still drives computer seats, but each policy receives public seat labels that identify its opponents as humans. This exercises Aggressive blocking behavior, which an ordinary all-computer match would bypass. Policies receive only their normal seat projection; opponents' hands, tickets and future deck order remain unavailable.

Reports include final score, completed/incomplete tickets, ticket penalties, wins, decision latency samples, fallbacks, rejected commands, invariant checks and journal replay. Each completed pair is saved immediately, and the time bound preserves a partial report if necessary. Keep tuning seeds separate from held-out validation seeds.

These comparisons measure performance against a fixed synthetic opponent, not playing strength against people. Seat rotations within one seed are related observations; avoid treating them as independent statistical trials. Physical camera recognition, tablet UI and real-world decision latency require separate checks.
