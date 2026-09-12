# Connect a phone or tablet on the local network

This guide currently applies to the **M0 connection-test PWA** in
`tools/GoldenTicket.ConnectivitySpike`. It tests local HTTPS, certificate trust, browser
installation, and pairing. The game companion, digital cards, and private player views are not
implemented in this test app. Record device results using the
[device checklist](companion-device-evidence.md).

The laptop hosts the test app. The phone reaches it over the local network. No hosted server,
subscription, cloud pairing service, or Internet connection is required for the running test.
Developer package restore may require Internet access before the test. Phone installation
behavior without Internet still needs to pass the real-device checklist.

## 1. Use the same trusted local network

Connect the laptop and phone to the same LAN. The laptop may use **Ethernet** while the phone uses
Wi-Fi on the same router. They do not both need to use Wi-Fi. Avoid an isolated guest network;
devices must be allowed to communicate with each other.

USB debugging was used during development to identify and operate the test Pixel. **ADB and a USB
cable are not required for users of the PWA.** The normal connection is through the laptop's
local address, optionally opened by scanning its QR code.

In Windows 11, the laptop's selected connection must have the **Private** network profile. A
`192.168.x.x` or other private-range IP address alone does not establish this. The test host checks
the actual Windows profile and refuses Public or unknown profiles.

For a trusted home or equivalent private LAN:

1. Open **Settings → Network & internet**.
2. For Ethernet, open **Ethernet**. For Wi-Fi, open **Wi-Fi** and the connected network's
   properties.
3. Set **Network profile type** to **Private network** for the connection used by the phone.
4. Leave unrelated connections unchanged. Do not mark a public hotspot Private to make this
   test work.

Microsoft documents these Ethernet/Wi-Fi profile controls in
[Essential network settings and tasks in Windows](https://support.microsoft.com/en-us/windows/experience/connectivity-networking/essential-network-settings-and-tasks-in-windows).

An administrator can also inspect the exact connection in PowerShell:

```powershell
Get-NetConnectionProfile | Format-Table InterfaceIndex, InterfaceAlias, NetworkCategory
```

After choosing the correct interface, run this in **PowerShell as Administrator**, replacing `12`
with its actual index:

```powershell
Set-NetConnectionProfile -InterfaceIndex 12 -NetworkCategory Private
Get-NetConnectionProfile -InterfaceIndex 12
```

An unelevated command can fail even when the signed-in account is an administrator. Open an
elevated terminal and approve the Windows UAC prompt, then verify the resulting profile. The
application does not change this setting automatically. Managed Windows policies may prevent a
user from changing it.

## 2. Start the laptop host

From the repository directory, using the laptop's actual LAN IPv4 address:

```powershell
dotnet run --project tools/GoldenTicket.ConnectivitySpike -- --address <laptop LAN IPv4>
```

The host prints the HTTPS address, a temporary certificate-download address, the CA certificate's
SHA-256 fingerprint, a single-use pairing code, and a QR code. Keep the host running while testing.

If the phone cannot reach it, first confirm the selected Windows connection is still Private and
that guest-network isolation is not separating the devices. If Windows Firewall needs an inbound
rule, use the **exact scoped rule printed by the running host** in an elevated terminal. The
default rule permits TCP ports **8080 and 8443**, only for the Private profile, the selected laptop
IP, the host executable, and peers on the local subnet. Use the printed values if custom ports or
a different executable are in use.

Do not disable Windows Firewall or create a rule for all profiles or all remote addresses. No
router port forwarding, public DNS, or incoming Internet access is needed. The temporary HTTP
port serves only certificate bootstrap content; pairing uses HTTPS. If `.local` discovery fails,
test the printed IP-based HTTPS address and record that fallback.

## 3. Trust this laptop's certificate on the phone

1. Scan the laptop's QR with the phone camera, or type the printed bootstrap address. Record
   which method actually worked; opening the URL through ADB does not validate the QR scanner.
2. Download the **public CA certificate**. Inspect the downloaded certificate's SHA-256
   fingerprint and compare it with the value on the laptop screen. The fingerprint printed on
   the HTTP download page alone is not an independent check.
3. On Android, install it as a **CA certificate** through the phone's security settings. On iOS
   or iPadOS, install the profile and explicitly enable full trust under **Settings → General →
   About → Certificate Trust Settings**. Device and OS menus vary; record the actual path tested.
4. Open the printed HTTPS address in Chrome on Android or Safari on iOS. Certificate trust must
   succeed without a warning bypass. If there is a certificate warning, resolve it before
   continuing.
5. Press `b` in the laptop console after certificate transfer to close the temporary bootstrap.

Chrome on the test Pixel displayed **“File can’t be downloaded securely”** when downloading the
certificate from the HTTP bootstrap, with **Discard** and **Keep** choices. This is a download
transport warning, before the HTTPS trust check. The bootstrap flow must explain this case rather
than assume an uninterrupted download. For this development test, the browser download was
discarded and the same public certificate was copied over the already authorized USB connection.
The transferred file's SHA-256 hash matched the laptop copy before installation, as recorded
below. The reusable alternative
requires a verified file transfer; USB/ADB is optional for provisioning and is not the PWA's
runtime transport. Never copy the laptop's private keys.

Google's [Pixel certificate guide](https://support.google.com/pixelphone/answer/2844832?hl=en)
documents the security settings entry point and removal of individual user credentials. Its
installation example is for a Wi-Fi certificate; GoldenTicket's local HTTPS authority needs the
**CA certificate** installation option. Record the actual CA path offered on the test phone.

On the tested Pixel with Android 17, the observed path was **Settings → Security & privacy → More
security & privacy → Encryption & credentials → Install a certificate → CA certificate**. Android
then displayed **“Your data won’t be private”**, with **Install anyway** and **Don't install**.
This is the operating system's CA-trust confirmation, separate from Chrome's download warning.
The device owner must decide whether to grant that trust and complete any device authentication.
If approved, select the verified public CA file from Downloads. The app's onboarding should
explain the scope of installing a local CA, identify the certificate and fingerprint, and pause
for this device confirmation; it must not treat file transfer as completed certificate trust.

Trust is specific to this laptop installation's CA. Only its public certificate is copied to the
phone; private keys remain on the laptop. Installing a CA changes the phone's trust settings and
requires an explicit device confirmation. Remove this test CA from the phone's user certificates
when the test installation is no longer needed.

## 4. Install, pair, and record the outcome

Use the page's installation guidance, then open the resulting home-screen app and pair **inside
that launched context** with the laptop's current code. If a browser offers only a shortcut,
record it as a shortcut; do not report it as verified standalone PWA installation. The QR contains
only the landing address, never the pairing code.

Use the [Android or iOS checklist](companion-device-evidence.md) to check secure context, service
worker caching, installation, reconnection, and name resolution. Disconnect the router's WAN for
the offline steps while keeping the LAN working. Record whether mobile data or other Internet
access remained available; an Internet-connected run does not prove offline behavior. Stopping
the laptop tests the cached reconnect screen, not offline game play without the host.

Press `s` in the laptop console to save the report and `q` to quit. The spike writes reports under
`docs/evidence/m0-connectivity/`. Include the Windows network profile and any firewall changes in
the session notes so setup costs are visible to future users. The spike has no game state or
private-view grants, so those product checklist steps remain pending.

## Setup event: 2026-09-12, Pixel 8 Pro

| Item | Observed state |
|---|---|
| Device | Pixel 8 Pro, Android 17, SDK 37; ADB authorized. |
| Browser | Chrome `151.0.7922.108`, confirmed from the phone's Chrome DevTools Protocol version response. |
| LAN | Phone `192.168.1.15`, laptop Ethernet `192.168.1.11`, same `/24` subnet. These are this session's addresses, not defaults for other users. |
| Initial blocker | The laptop's Ethernet connection was classified **Public** by Windows, so the host correctly refused it. |
| User authorization | User explicitly approved marking this trusted connection Private and, if necessary, opening only the local-subnet application ports `8080`/`8443`. |
| First profile-change attempt | Failed from an unelevated token. This exposed the need to explain Administrator/UAC access in onboarding. |
| Confirmed profile after change | At `2026-09-12T01:55:42-07:00`, the user accepted UAC for an elevated helper. Ethernet interface index `4` was changed **Public → Private** and the resulting profile was verified. |
| Firewall change | Created `GoldenTicket-M0-Private-LAN-TCP-20260912`: inbound allow, Private profile, TCP `8080,8443`, local `192.168.1.11`, remote `192.168.1.0/24`, interface `Ethernet`, and the exact built host executable. Existing Public-profile block rules were left intact. |
| Host startup | Announced `https://gt-b2261472.local:8443/` and `https://192.168.1.11:8443/`, with temporary bootstrap at `http://192.168.1.11:8080/`. Phone HTTPS reachability and name resolution remain pending. |
| Phone bootstrap reachability | **Passed:** the Pixel loaded `http://192.168.1.11:8080/` over the actual Wi-Fi LAN after the profile and scoped firewall changes. |
| HTTP certificate download | Chrome displayed **“File can’t be downloaded securely”**, offering Discard/Keep after **Download certificate** was tapped. **Discard** was selected. This is a provisioning UX finding, not an HTTPS trust error; the HTTP download warning was not bypassed. |
| Public CA transfer | **Verified:** only `authority.crt` was copied over authorized USB to `/sdcard/Download/GoldenTicket-laptop-CA.crt`. Phone `sha256sum` and laptop `Get-FileHash` produced the identical SHA-256 value recorded below. No private key was transferred. |
| CA installation path | Observed **Settings → Security & privacy → More security & privacy → Encryption & credentials → Install a certificate → CA certificate**. |
| CA trust confirmation | **Waiting for the user:** the phone is at **“Your data won’t be private”**, with Install anyway/Don't install. Neither option was selected by the agent. The user must approve trust, authenticate as requested, and choose `Downloads/GoldenTicket-laptop-CA.crt` to continue. |
| Temporary bootstrap | **Closed after certificate transfer:** the host confirmed shutdown after `b`; a listener check showed only HTTPS `8443` on the selected LAN IP and loopback, with no `8080` listener. The test host was subsequently stopped while the game companion integration is implemented. |
| Phone HTTPS, installed PWA, and pairing | Pending. Verified public-certificate transfer does not prove installed CA trust, trusted HTTPS, PWA installation, or pairing. |
| Offline acceptance | Pending; no WAN-disconnected result recorded. |

The firewall rule's executable scope for this development build is
`D:\Projects\GoldenTicket\tools\GoldenTicket.ConnectivitySpike\bin\Release\net10.0-windows10.0.26100.0\GoldenTicket.ConnectivitySpike.exe`.
Rebuilding to another location or switching to a published executable requires inspecting and
updating that scope; this developer path is not a user installation path.

The laptop-announced public CA SHA-256 fingerprint for this session is
`B13F550E4C9CF62FC56A612DE482E4839657A2EBCD28DF08A7427C5E451CC343`.
The same value was independently computed for the public certificate file on both the laptop and
phone after USB transfer. This verifies the transferred bytes; Android CA installation and browser
trust remain unconfirmed.

For the eventual game companion, this event establishes an onboarding requirement: inspect the
selected connection, explain a Public-profile block, and offer instructions for changing only a
trusted connection with the user's consent. Show any required firewall scope before applying it,
verify the result, and record failure details. This guide documents that requirement; it does not
claim the product has implemented an automatic setup workflow. Certificate onboarding must also
cover the observed HTTP download warning and a verified public-certificate transfer alternative
without treating a browser warning bypass as successful HTTPS trust.
