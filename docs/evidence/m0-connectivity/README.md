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

The console draws a QR for the address the device should land on. It has structural and
in-repository decoder tests, but no recorded scan by a phone camera. Record whether the phone's own
camera app offered the address, and from how far away. The test decoder shares some encoder
metadata and does not replace an independent scanner check.

The reports here do not record the QR, because the QR contains only the landing address, which the
report already carries as `host.boundAddress`.
