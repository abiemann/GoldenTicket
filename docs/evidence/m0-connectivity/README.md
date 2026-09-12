# M0 connectivity evidence

Each run of the spike writes `run-<timestamp>.json` here on exit.

**Nothing in this folder yet is device evidence.** A file only counts when a real phone or tablet
went through the checklist in [../../companion-device-evidence.md](../../companion-device-evidence.md)
and the device submitted its own observation from the page. Laptop-side smoke tests are deleted
rather than kept, because a report with a fabricated user agent looks exactly like a real one.

A run is evidence when it records:

- the device model, OS version and browser version,
- that the WAN was disconnected for the offline steps,
- a device observation submitted from the device itself, not from a script.

Run it with:

```powershell
dotnet run --project tools/GoldenTicket.ConnectivitySpike -- --address <laptop private IP>
```

The console draws a QR for the address the device should land on. It has been checked against the
standard and read back by an independent decoder, but **a run is the first time a camera has ever
seen it** — record whether the phone's own camera app offered the address, and from how far away.

The reports here do not record the QR, because the QR contains only the landing address, which the
report already carries as `host.boundAddress`.
