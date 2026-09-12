# Rules policy decisions

`rulesPolicyVersion: 1` for profile `ttr-us-classic-en-v1`.

DESIGN §6.4 requires each pathological supply state to have either an official clarification or an
**explicitly disclosed house policy**, versioned and described in local help. This file is that
disclosure. Nothing here changes ordinary play; every entry covers a boundary condition the printed
rules do not resolve.

Where a decision is a house policy rather than a publisher clarification, it says so. None of these
have been checked against an official ruling yet; that is outstanding work before a consumer release.

| Situation | Decision | Status | Where it lives |
|---|---|---|---|
| Several seats return opening tickets | All opening offers are collected first; rejected tickets are then appended under the deck in seat order (seat 1, then seat 2, …). | House policy | `GameRules.HandleTicketSelection` → `SetupReturnsRecycled` |
| Several tickets returned together | The acting seat chooses the order, and that order is preserved in the journal. With no order given, the offered order is used. | House policy | `CommitTicketSelection.ReturnOrder` |
| No second train card can be taken | Preserve the first revealed draw and pause with `RulesDecisionRequired` / `NoSelectableSecondDraw`. Do not end the turn or invent a forced pass. A reviewed continuation policy is still required. | Unresolved; safe pause | `GameRules.SecondPickPossible` |
| Market cannot stabilise below three locomotives | Replacement is bounded at 10 attempts. Beyond that the match pauses with `RulesDecisionRequired` / `MarketResetUnstable` and the exact cause, rather than looping or quietly accepting a different rule. | House policy | `GameRules.MaximumMarketResets` |
| Market cannot be refilled completely | Preserve every revealed card and pause with `PartialMarketSupply`. Do not silently skip market maintenance or complete the turn. | Unresolved; safe pause | `GameRules.MaintainMarket` |
| Available supply cannot produce fewer than three face-up locomotives | Detect the impossible supply before repeatedly reshuffling and pause with `MarketResetImpossible`. | Unresolved; safe pause | `GameRules.MaintainMarket` |
| No legal action at all | The match pauses with `RulesDecisionRequired` / `NoLegalAction` naming the seat. There is no undocumented forced pass. | House policy | `GameRules.RaiseIfNoLegalAction` |
| Official tie-breaks still leave several winners | Recorded as a shared victory, with the reason stated on the results screen. | Product clarification | `FinalScoring.ResolveWinners` |
| Longest continuous route ties | Every tied seat receives the full bonus. | Publisher rule | `FinalScoring.Compute` |
| Longest continuous route cannot be computed exactly | The calculation throws rather than returning an approximation. DESIGN §6.5 forbids a timeout approximation deciding the winner. | House policy | `LongestTrailBudgetExceededException` |

## What is deliberately *not* a policy

- **Parallel routes.** The pinned profile's own value (`parallelRouteClosedAtOrBelowPlayers: 3`)
  decides when one claim closes its twin. It is data, not code.
- **Payment choice.** The engine never picks a payment. Every legal combination is offered and the
  seat chooses, so a locomotive is never spent silently (DESIGN §4.3).
- **Scoring ladder, hand sizes, stock, market size.** All in the data manifest.

## Save, pack away and rebuild

DESIGN 19.8 defines two save paths. This build implements the **state-only** one, because a verified
board photograph needs the camera milestones.

| Aspect | This build | Design reference |
|---|---|---|
| `targetProvenance` | Always `LogicalStateOnly` | 19.8, state-only fallback |
| `photoHash` | Always null; the route list is the reconstruction record | 19.8 |
| Physical target | Committed route ownership only | 19.8 |
| A partially placed claim | **Not** part of the saved target. Its payment stays reserved and its claim stays uncommitted; after resuming, the trains are placed again and confirmed as usual. | 19.8, "a forward claim needs its trains placed again" |
| Durable boundaries | Three: request, checkpoint commit, readback verification | 19.8 step 6 |
| Safe-to-pack result | Only from a `Verified` checkpoint. A restart between commit and readback repeats the validation. | 19.8 step 6 |
| Resume gate | Operator whole-target attestation, re-checked at the moment Resume is pressed | 19.8, guided reconstruction |
| Retention | The journal is never pruned, so a checkpoint's source history is pinned by construction | 19.7 |

The camera milestones add the verified photograph, the pending-placement mask and image readback to
this same protocol; the transaction boundaries and the resume gate do not change.
