#ifndef PackageDir
  #error PackageDir must name a verified self-contained GoldenTicket package.
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion must be supplied by Build-WindowsInstaller.ps1.
#endif

[Setup]
AppId={{9743bdfd-4680-465d-acfd-a25616b492da}
AppName=GoldenTicket
AppVersion={#ReleaseVersion}
AppPublisher=GoldenTicket
AppPublisherURL=https://github.com/abiemann/GoldenTicket
AppSupportURL=https://github.com/abiemann/GoldenTicket/issues
DefaultDirName={userpf}\GoldenTicket
DefaultGroupName=GoldenTicket
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
MinVersion=10.0.22000
OutputBaseFilename=GoldenTicket-Setup-{#ReleaseVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#PackageDir}\LICENSE
UninstallDisplayIcon={app}\GoldenTicket.exe

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\GoldenTicket"; Filename: "{app}\GoldenTicket.exe"
Name: "{group}\Uninstall GoldenTicket"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\GoldenTicket.exe"; Description: "Launch GoldenTicket"; Flags: nowait postinstall skipifsilent
