# Companion browser UI evidence, September 12, 2026

The shipped companion HTML, CSS, JavaScript, manifest, icons and service worker were exercised in actual headless Chrome 152.0.7977.83 on Windows. All **21 scenarios passed**, seven scenario groups at each of **448×900**, **320×740**, and **768×1024** CSS pixels. [Machine-readable results](browser-ui-results.json) record durations and browser identity.

The test used a temporary browser profile and an HTTP loopback fixture. Chromium treats loopback as a secure context, allowing the real service worker to install without a certificate exception. API fixtures were exported from the real `CoordinatorCompanionBridge` using synthetic two-human games and seed 91. No personal save was loaded. This validates browser rendering and client behavior. It does **not** establish real-phone TLS trust, LAN reachability, iOS installation, Android app installation, OS task-switcher snapshot protection, or full desktop integration. Actual HTTPS transport and authorization have separate .NET integration tests with explicit test-root chain validation.

## Scenarios

At each viewport the browser completed:

1. Full seven-asset shell caching, final browser-context selection, pairing request and simulated laptop approval.
2. Opening destination selection, minimum-keep gate, exact submitted keep/return IDs and covered handoff to the second human.
3. Blind first and second train-card draws, no selectable second face-up locomotive, and the next human's curtain.
4. Destination-ticket offers, selected keep set, and reversible return order.
5. Exact route/payment authorization, reserved-placement instructions, and absence of any phone control to verify physical placement.
6. Explicit Hide, simulated focus/background lifecycle signals, and rejection of a delayed private response after either local Hide or a newer laptop revocation received through polling.
7. Connection-loss cover, cached shell reload while offline, zero API cache entries, and no localStorage/sessionStorage entries.

No horizontal overflow or JavaScript page errors were observed. The persistent Hide button remains in the viewport while scrolling long private choices. Private DOM content was empty after every privacy transition. Browser lifecycle events were deliberately injected for repeatability; this is separate from physical Android/iOS lifecycle acceptance.

The final run uses shell version 2. Both the service-worker cache and the authenticated server asset version were advanced after the privacy race fixes, so an older cached script is refused until reloaded. Separate behavioral regressions verify monotonic handoff generations, re-pairing after a server restart, and removal of the old shell cache while preserving other applications' caches.

Visual inspection covered the phone connection screen, private turn, opening tickets, tablet curtain and disconnected state. The review led to keeping Hide visible while scrolling, separating the bottom Hide button from action buttons, wrapping long player text and replacing browser-specific fetch errors with actionable connection guidance. A wrapping route/payment review line ensures the full endpoints, lane and expenditure remain readable when a narrow native dropdown truncates its selected option.

## Screenshots

All screenshots contain synthetic player data. Representative artifacts:

- [Pixel-sized connection screen](pixel-connect.png)
- [Pixel-sized private turn](pixel-private-turn.png)
- [Small-phone opening ticket selection](small-phone-opening-tickets.png)
- [Small-phone route/payment view](small-phone-route-payment.png)
- [Tablet handoff curtain](tablet-curtain.png)
- [Pixel-sized disconnected curtain](pixel-disconnected.png)

Full-page screenshots begin at the document top. A separate scrolled-layout assertion verifies that the sticky Hide control stays visible.

## Reproduce

Build the test project, then export fresh synthetic fixtures:

```powershell
$env:GOLDENTICKET_COMPANION_FIXTURE_DIRECTORY = "$PWD/artifacts/companion-browser"
dotnet test tests/GoldenTicket.Domain.Tests/GoldenTicket.Domain.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~CompanionHostBrowserFixtures
```

Run [the browser test](../../../tests/GoldenTicket.Domain.Tests/CompanionHostBrowserUi.cjs) using Node with the existing Playwright installation available through `NODE_PATH`:

```powershell
node tests/GoldenTicket.Domain.Tests/CompanionHostBrowserUi.cjs
```

The script uses installed Chrome or Edge before falling back to Playwright's browser. It downloads nothing. Optional overrides are `GOLDENTICKET_BROWSER_EXECUTABLE`, `GOLDENTICKET_COMPANION_FIXTURES` and `GOLDENTICKET_BROWSER_EVIDENCE`. On this machine, the Windows security sandbox could not initialize Chrome's temporary-profile encryption; the successful run used the normal signed-in Windows profile, still with isolated temporary browser data. No OS trust store, firewall rule or normal browser profile was changed by this test.
