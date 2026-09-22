# Computer strategy validation — 2026-09-21

## Observed failure

The reported match ended with the Aggressive computer at −5: 41 route points, no completed
destinations, and 46 points of ticket penalties. The saved seat was Aggressive; this was not a lost
style setting. It kept Los Angeles–New York, Boston–Miami and Calgary–Phoenix. Eight of its claims
used 14 trains without reducing any destination's minimum remaining distance.

From turn 51, Charleston–Miami was the sole missing connection for Boston–Miami. The computer
repeatedly chose unrelated colors instead of collecting pink cards or useful locomotives. At turn
108 it spent its only pink on Seattle–Vancouver. The old evaluator overvalued short links near human
networks, and summing card demand across those possible blocks overwhelmed destination needs.

## Implemented behavior

- Compare required ticket bundles using shared routes and card costs; accept extra commitments
  only when they are cheap, feasible extensions.
- Rank reachable destinations and fund a concrete next connection. Use current cards, claim turns,
  available trains and public game tempo; reevaluate after board and hand changes.
- Build an affordable link for the primary destination before unrelated interference. Reserve the
  remaining destination's trains and cards when evaluating a detour, including alternative payments
  for grey routes. Useful blocks remain possible with spare resources and enough time.
- Keep reachable objectives useful when another ticket is blocked. Treat estimated game tempo as
  a soft signal and the actual final round as a hard limit. Score available points on the last turn
  instead of drawing cards that cannot be played.

The owning seat projection and public board remain the only policy inputs. This is bounded
heuristic planning, not lookahead through the real deck or access to human destinations.

## Exact saved-position checks

The exported journal was replayed locally without modifying the original save. The new policy:

- Keeps two tickets from that opening offer instead of all three.
- At turn 54, takes the visible pink card toward Charleston–Miami.
- At turn 87, takes the useful visible locomotive.
- At turn 108, draws instead of spending its only pink on Seattle–Vancouver.

Each is a decision from the original position, not a claim that the original full match would
otherwise have ended with a particular score. The policy RNG was fixed for these diagnostic checks.
Local details: `artifacts/ai-improvement-20260921/actual-position-decisions.json` and `replay-probe/`.

## Automated checks

Warnings-as-errors builds pass for the core and Windows integration test projects. All 343 core
tests pass, including 12 new planning cases and 11 decision cases. Eleven targeted integration
checks cover complete games, invariants, replay determinism, reshuffling and desktop AI styles.
The decision tests explicitly check both destination protection and a useful Aggressive block
paid with spare cards; encountering opponents in a benchmark alone would not establish that.

Test logs are under `artifacts/ai-improvement-20260921/` (ignored local evidence).

## Paired comparison method

The baseline is the unchanged AI source from commit `08cc2fc778ce62bb4386fd0f7d87a7c5dc09db6c`.
Its source and compiled assembly were preserved before edits. Baseline DLL SHA-256:
`99405024E30855CDBA6367D0C6F36ECD6FFCF5098247E8B603D9E7BA24E39E4B`.

Each pair uses identical deck and per-seat random seeds, changing only one focal policy. Rivals
use the frozen Standard policy in both games. Every seat position is covered in three- and
five-player games, for Standard and Aggressive. Public rival labels appear human to the policy,
so Aggressive actually evaluates human networks. An old-versus-old smoke check produced identical
outcome fingerprints across 16 pairs.

Seeds 1–10 were used for screening. A dispatched 1001–1010 run belongs to an intermediate policy;
its results were withheld while independently identified regressions were fixed. Final validation
uses untouched seeds 2001–2010, with no subsequent tuning. Each cohort contains 160 pairs / 320 games.
See the [benchmark guide](../tools/GoldenTicket.Simulator/README.md) for reproduction commands.

## Final held-out results

| Style / seats | Mean score, old → new | Mean completed tickets, old → new | Win rate, old → new |
|---|---:|---:|---:|
| Standard / 3 | 66.20 → 98.63 | 2.03 → 2.90 | 33.3% → 73.3% |
| Standard / 5 | 59.62 → 100.64 | 1.78 → 2.68 | 20.0% → 70.0% |
| Aggressive / 3 | 13.80 → 75.13 | 0.40 → 2.47 | 0.0% → 50.0% |
| Aggressive / 5 | 12.08 → 80.62 | 0.46 → 2.42 | 2.0% → 34.0% |

All 320 held-out games completed, with zero fallback decisions, rejected commands, invariant
problems or replay mismatches. The final screening cohort also completed all 320 games cleanly.
Final-policy decision p95 ranged from 8.38 to 12.64 ms across the held-out groups; the maximum was
53.75 ms. A separate stress probe covered 48 decisions with 8–12 held tickets, 38 held cards and
up to 100 legal routes / 1,018 payment options: no illegal decisions or two-second deadline
cancellations, with a 1.10-second cold maximum and 565 ms warm maximum on this machine.

Final DLL SHA-256: `ECD9504B6303CF2433425DC9A3AC271399716BBC62E5BCF3EC36F6C49DA48893`.
Local reports:

- `artifacts/ai-policy-benchmark-20260921/validated-screening-seeds-1-10.json`
- `artifacts/ai-policy-benchmark-20260921/heldout-seeds-2001-2010.json`
- `artifacts/ai-improvement-20260921/rich-hand-probe/final-results.json`

The final held-out run used:

```powershell
dotnet artifacts/ai-improvement-20260921/integration-bin/GoldenTicket.Simulator.dll benchmark-ai --baseline artifacts/ai-policy-benchmark-20260921/baseline/assembly/GoldenTicket.AI.dll --output artifacts/ai-policy-benchmark-20260921/heldout-seeds-2001-2010.json --seeds 10 --seed 2001 --seats 3,5 --difficulties Standard,Aggressive --max-seconds 180
```

Seat rotations within a seed are related observations. These results measure performance against
a fixed synthetic opponent, not a human skill rating or a guarantee of wins against people.
