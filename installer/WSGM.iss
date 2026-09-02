; WSGM installer — elevated (admin) because the logon service is machine-wide:
; the service binary lives in Program Files (a SYSTEM service exe must never be
; user-writable) and stopping/registering it needs the SCM. The app itself stays
; per-user in %LOCALAPPDATA%\WSGM\bin. Consequence: run this setup from the
; handheld's (typically sole) admin account — {localappdata}/HKCU below belong
; to the ELEVATING user.
; Build via build.ps1 (publishes the app first, then compiles this).

#define AppName "WSGM - Windows Steam Game Mode"
; Version comes from the csproj <Version> via build.ps1 (/DAppVersion=...); the
; fallback below only applies when ISCC is invoked directly.
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#define AppPublisher "NightHammer1000"
#define AppURL "https://github.com/NightHammer1000/WSGM"
#define PublishRoot "..\publish"
#define AppPublishDir "..\publish\App"
#define DevicePackagesPublishDir "..\publish\Packages"
#define DeviceToolsPublishDir "..\publish\Tools"

[Setup]
; New product identity (renamed from OpenFSE) — a fresh AppId so the old OpenFSE
; install is not silently upgraded; uninstall OpenFSE separately.
AppId={{E4C7A9D2-58F1-4B36-A2C4-7D9E31B0F5C8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppURL}
; Per-user app layout (%LOCALAPPDATA%\WSGM\bin) written from an elevated setup —
; deliberate single-user-device design, see header. UsedUserAreasWarning quiets
; the compiler's warning about exactly that combination.
DefaultDirName={localappdata}\WSGM\bin
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
OutputDir={#PublishRoot}
OutputBaseFilename=WSGM-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\WSGM.exe
SetupIconFile=..\src\WSGM\Assets\wsgm.ico
CloseApplications=no
; Restart Manager must not close the fixed-purpose recovery owner before [Code] observes its
; recovery acknowledgement. The installer retires this one image itself after that boundary.
CloseApplicationsFilterExcludes=WSGM.ShellAnchor.exe
; win-x64-only binary: refuse ARM64 (x64os, not x64compatible) — an emulated
; shell replacement is an untested configuration. Needs Inno Setup 6.3+.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; WSGM reconstructs SteamOS Game Mode on Windows 11 (the per-user shell and
; game-mode scaling paths are only exercised there).
MinVersion=10.0.22000

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Types]
; Core-only is first and therefore the unattended/default choice. Installing the
; Device Integration bytes are inert until the user explicitly enables the feature.
Name: "core"; Description: "Core WSGM"
Name: "full"; Description: "Core WSGM + Device Integration"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "core"; Description: "Core WSGM"; Types: core full custom; Flags: fixed
Name: "device"; Description: "Device Integration runtime and one installed device package (remains disabled until enabled in WSGM Settings)"; Types: full
Name: "devicelab"; Description: "Device Lab and offline device-development tools"; Types: custom
Name: "controller"; Description: "Virtual controller support (requires the USBIP driver; remains disabled until enabled in WSGM Settings)"; Types: full

[Tasks]
; The one place the USB/IP driver may be installed from. It is a separate, visibly ticked line
; rather than a silent consequence of the component because it installs a signed third-party kernel
; driver, restarts every USB 3.0 hub while it runs, and needs a reboot afterwards. Unticking it
; installs WSGM's controller bytes and leaves controller management reporting itself unavailable,
; which is a supported state — so the choice is real in both directions.
; It is never offered outside setup (INV-020): the hub restart drops the built-in controller, the
; touch digitiser and the keyboard, which underneath a running game mode would leave the user with
; no input and no way back.
Name: "usbipdriver"; Description: "Install the USB/IP and HidHide controller drivers (USB devices restart briefly and a reboot is required)"; GroupDescription: "Virtual controller"; Components: controller

[CustomMessages]
english.SteamMissing=Steam was not found on this PC.%n%nWSGM is Steam-exclusive and boots straight into Steam Big Picture. Install Steam from steampowered.com, sign in once, and then run this setup again.
german.SteamMissing=Steam wurde auf diesem PC nicht gefunden.%n%nWSGM funktioniert ausschließlich mit Steam und startet direkt in Steam Big Picture. Installiere Steam von steampowered.com, melde dich einmal an und führe dieses Setup danach erneut aus.

[Files]
; WSGM is self-contained; Device Lab and plugin packages stay in sibling component trees.
Source: "{#AppPublishDir}\WSGM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\WSGM.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\WSGM.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
; The fixed-purpose Explorer recovery owner is the same payload under a distinct process image.
; Installer force fallback can therefore end WSGM.exe without killing the owner that must restore
; Explorer. If its bounded recovery acknowledgement is unavailable, an interactive operation may
; defer replacement/deletion to reboot; a silent update keeps the old image and never auto-reboots.
Source: "{#AppPublishDir}\WSGM.exe"; DestDir: "{app}"; DestName: "WSGM.ShellAnchor.exe"; Flags: ignoreversion restartreplace uninsrestartdelete; Check: CanInstallShellAnchor
Source: "{#AppPublishDir}\WSGM.Launch.exe"; DestDir: "{app}"; Flags: ignoreversion
; SYSTEM service binary: Program Files only (admin-writable), never {app}. It
; launches the per-user WSGM.exe via the boot manifest — as that user, which is
; why the user-writable app path is not an escalation.
Source: "{#AppPublishDir}\WSGM.LogonService.exe"; DestDir: "{autopf}\WSGM"; Flags: ignoreversion
Source: "{#AppPublishDir}\*.dll"; DestDir: "{app}"; Excludes: "libviiper.dll"; Flags: ignoreversion
Source: "{#AppPublishDir}\SteamInputLease-*.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\SteamInputLease-*.md"; DestDir: "{app}"; Flags: ignoreversion
; WSGM's authoritative GPL-3.0-or-later license, staged from the repository root.
Source: "{#AppPublishDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; Third-party license texts for managed packages (src\WSGM\Licenses\).
Source: "{#AppPublishDir}\LoadingIndicators.Avalonia-UNLICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; VIIPER creates the virtual controller. The release build requires and validates the library,
; header, license, and notice before invoking setup; missing component bytes fail the build.
Source: "{#AppPublishDir}\libviiper.dll"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
Source: "{#AppPublishDir}\libviiper.h"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
Source: "{#AppPublishDir}\VIIPER-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
Source: "{#AppPublishDir}\VIIPER-NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
; The driver step's script. It ships unconditionally rather than under the component so that a user
; who adds the controller component to an existing install, or who needs to re-run the step after a
; failure, already has it on disk. It does nothing unless invoked.
Source: "Install-UsbipDriver.ps1"; DestDir: "{app}"; Flags: ignoreversion
; The USB/IP driver installer itself, already verified against the reviewed digest and signer on the
; release machine and verified again by the script before it is run. Carrying both installers means
; a freshly imaged handheld can install the controller stack before its Wi-Fi is configured. The
; release build requires them; they ship under the controller component only.
Source: "{#AppPublishDir}\USBip-0.9.7.7-x64.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
Source: "{#AppPublishDir}\HidHide_1.5.230_x64.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
Source: "..\third_party\controller\licenses\usbip-win2-BSD-2-Clause.txt"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
Source: "..\third_party\controller\licenses\HidHide-MIT.txt"; DestDir: "{app}"; Flags: ignoreversion; Components: controller
; The one plugin package is administrator-protected and never loads from a user-writable path.
Source: "{#DevicePackagesPublishDir}\*"; DestDir: "{autopf}\WSGM\DevicePlugins\.staging"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device
; Device Lab never owns the production cycle and remains an explicit custom
; component. Its attended hardware actions still require interactive consent;
; its tool tree carries the exact self-contained .NET runtime license/notices.
Source: "{#DeviceToolsPublishDir}\*"; DestDir: "{app}\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: devicelab

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\WSGM.exe"; Comment: "WSGM settings"

[Run]
; --setup: install per-user files, migrate OFF a legacy shell registration
; (restores the snapshotted previous shell), apply the Xbox-FSE guard, write the
; boot manifest. Runs elevated (whole setup is) — same single-user profile.
Filename: "{app}\WSGM.exe"; Parameters: "--setup"; Flags: runhidden
; Register + start the logon service (create-or-reconfigure also adopts an
; abandoned preview registration of the same name; PrepareToInstall already
; stopped it so [Files] could overwrite the binary).
Filename: "{autopf}\WSGM\WSGM.LogonService.exe"; Parameters: "--install"; Flags: runhidden waituntilterminated
; The USB/IP driver, only when its task was ticked, and deliberately BEFORE the restart entries
; below: installing it restarts every USB 3.0 hub, so it has to finish while nothing of WSGM's is
; running and the user is still looking at setup. The script exits 0 even when it fails — a machine
; without the driver is a supported state where controller management reports itself unavailable —
; so this can never strand a WSGM install on a driver problem.
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Install-UsbipDriver.ps1"" -StatusPath ""{commonappdata}\WSGM\usbip-install-status.ini"""; StatusMsg: "Installing the USB/IP driver for virtual controller support..."; Tasks: usbipdriver; Flags: runhidden waituntilterminated; BeforeInstall: PrepareUsbipInstallOutcome; AfterInstall: ReportUsbipInstallOutcome
Filename: "{app}\HidHide_1.5.230_x64.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP-"; StatusMsg: "Installing HidHide for physical controller isolation..."; Tasks: usbipdriver; Flags: runhidden waituntilterminated skipifdoesntexist
; Update restart: if the shell was running it comes back as the shell; a plain
; settings instance comes back as settings (no args = DecideMode).
Filename: "{app}\WSGM.exe"; Parameters: "--shell"; Flags: nowait; Check: WasShellRunning
Filename: "{app}\WSGM.exe"; Flags: nowait; Check: WasSettingsRunning
Filename: "{app}\WSGM.exe"; Description: "Open WSGM settings"; Flags: nowait postinstall skipifsilent; Check: WasNothingRunning

[UninstallRun]
; Remove the Steam Input shim from STEAM's directory before anything else — it is
; the only file WSGM puts outside its own install, it needs {app}\WSGM.exe to
; still exist, and only WSGM can tell its own copy from a same-named file another
; tool (ValvePlug, Special K) owns. [UninstallDelete] deliberately cannot do this.
Filename: "{app}\WSGM.exe"; Parameters: "--remove-steam-input-shim"; RunOnceId: "RemoveSteamInputShim"; Flags: runhidden skipifdoesntexist
; Stop + delete the logon service FIRST — after files are gone the SCM would
; point at a missing binary and the next boot would log service-start failures.
Filename: "{autopf}\WSGM\WSGM.LogonService.exe"; Parameters: "--uninstall"; RunOnceId: "UninstallService"; Flags: runhidden skipifdoesntexist
; Legacy: restore a pre-service Winlogon shell registration BEFORE files are
; removed — otherwise the next logon would point at a deleted exe. Self-guarding
; no-op on service-boot installs. Quiet: no explorer start, no UI.
Filename: "{app}\WSGM.exe"; Parameters: "--unregister-shell"; RunOnceId: "UnregisterShell"; Flags: runhidden skipifdoesntexist
; Restore machine settings (UAC, lock-on-wake, ...) from the config snapshots
; while config.json still exists — [UninstallDelete] removes it afterwards.
Filename: "{app}\WSGM.exe"; Parameters: "--uninstall-restore"; RunOnceId: "UninstallRestore"; Flags: runhidden skipifdoesntexist

[UninstallDelete]
; Config/logs live one level up; remove them with the app (per-user data only).
Type: filesandordirs; Name: "{localappdata}\WSGM"
; Service binary + service log directory (machine-wide pieces).
Type: filesandordirs; Name: "{autopf}\WSGM"
Type: filesandordirs; Name: "{commonappdata}\WSGM"

[InstallDelete]
; Self-contained component publishes must replace their complete previous closure.
; Overlaying would retain deleted assemblies and the retired CommandLine/ProbeHost tools.
; Remove the pre-collapse DeviceHost tree when updating a development installation.
Type: filesandordirs; Name: "{autopf}\WSGM\DeviceHost"
; Core Audio now runs through managed COM interop. Remove the retired native helper on upgrade.
Type: files; Name: "{app}\WSGM.VolumeControl.dll"
Type: files; Name: "{app}\WSGM.Radio.dll"
Type: files; Name: "{app}\WSGM.RadioProbe.exe"
Type: filesandordirs; Name: "{app}\Tools"
; Component deselection must remove optional controller payloads left by an earlier full install.
; Selected components copy them back during [Files].
Type: files; Name: "{app}\libviiper.dll"
Type: files; Name: "{app}\libviiper.h"
Type: files; Name: "{app}\VIIPER-LICENSE.txt"
Type: files; Name: "{app}\VIIPER-NOTICE.md"
Type: files; Name: "{app}\USBip-0.9.7.7-x64.exe"
Type: files; Name: "{app}\HidHide_1.5.230_x64.exe"
Type: files; Name: "{app}\usbip-win2-BSD-2-Clause.txt"
Type: files; Name: "{app}\HidHide-MIT.txt"
; Package bytes stage outside runtime discovery. CurStepChanged swaps this whole
; sibling into the sole installed slot after every file has landed successfully.
Type: filesandordirs; Name: "{autopf}\WSGM\DevicePlugins\.staging"
; Remove the per-user staging helper left by service-based preview builds.
Type: files; Name: "{app}\WSGM.LogonService.exe"
; The two wrappers WSGM.Launch.exe replaces. Deleting them is deliberate: a stale
; helper would keep an old pasted launch option working, so the two would drift
; apart silently. Gone, the option fails visibly and the release note explains it.
Type: files; Name: "{app}\WSGM.Deelevate.exe"
Type: files; Name: "{app}\steam-input-lease.exe"

[Code]
const
  ErrorAlreadyExists = 183;
  ErrorFileNotFound = 2;
  ErrorNoMoreFiles = 18;
  ErrorPathNotFound = 3;
  ErrorServiceDoesNotExist = 1060;
  FileAttributeDirectory = $00000010;
  FileAttributeReparsePoint = $00000400;
  InvalidFileAttributes = $FFFFFFFF;
  InvalidHandleValue = $FFFFFFFF;
  ScManagerConnect = $0001;
  ServiceQueryStatus = $0004;
  ServiceRunning = $00000004;
  ServiceStopped = $00000001;
  WaitAbandoned = $00000080;
  WaitObject0 = $00000000;

type
  TShutdownHandoffResult = (
    shrLegacy,
    shrCompleted,
    shrTimedOut,
    shrFailed);

  TWin32FindData = record
    FileAttributes: LongWord;
    CreationTimeLow: LongWord;
    CreationTimeHigh: LongWord;
    LastAccessTimeLow: LongWord;
    LastAccessTimeHigh: LongWord;
    LastWriteTimeLow: LongWord;
    LastWriteTimeHigh: LongWord;
    FileSizeHigh: LongWord;
    FileSizeLow: LongWord;
    Reserved0: LongWord;
    Reserved1: LongWord;
    FileName: array[0..259] of Char;
    AlternateFileName: array[0..13] of Char;
  end;

  TServiceStatus = record
    ServiceType: LongWord;
    CurrentState: LongWord;
    ControlsAccepted: LongWord;
    Win32ExitCode: LongWord;
    ServiceSpecificExitCode: LongWord;
    CheckPoint: LongWord;
    WaitHint: LongWord;
  end;

var
  DeviceOwnerHandle: THandle;
  DevicePackageGateHandle: THandle;
  DevicePackageGateOwned: Boolean;
  ShellAnchorReplacementSafe: Boolean;
  SetupPostInstallCompleted: Boolean;
  SetupRuntimeClassificationCaptured: Boolean;
  SetupServiceExisted: Boolean;
  SetupServiceStateCaptured: Boolean;
  SetupServiceWasRunning: Boolean;
  SetupShutdownApplied: Boolean;
  UsbipOutcomeReported: Boolean;
  UsbipRebootRequired: Boolean;
  UsbipStatusPrepared: Boolean;
  UninstallMutationStarted: Boolean;
  UninstallServiceExisted: Boolean;
  UninstallServiceWasRunning: Boolean;
  UninstallShutdownApplied: Boolean;
  UninstallWasRunning: Boolean;
  UninstallWasShell: Boolean;
  WasShell: Boolean;
  WasRunning: Boolean;

// Mirrors Core\Steam.cs detection: HKCU SteamExe (stored with forward slashes),
// then the machine-wide install dir. Detection only — the path is never stored.
function SteamInstalled(): Boolean;
var
  Exe, Dir: String;
begin
  Result := False;
  if RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamExe', Exe) then
  begin
    StringChangeEx(Exe, '/', '\', True);
    if (Exe <> '') and FileExists(Exe) then
    begin
      Result := True;
      Exit;
    end;
  end;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Valve\Steam', 'InstallPath', Dir) then
    if (Dir <> '') and FileExists(AddBackslash(Dir) + 'steam.exe') then
      Result := True;
end;

// Steam is WSGM's only application prerequisite; the .NET runtime ships with WSGM.
// Without it an installed WSGM can only show its "install Steam" warning, so
// block setup up front and tell the user what to do instead.
function InitializeSetup(): Boolean;
begin
  DeviceOwnerHandle := 0;
  DevicePackageGateHandle := 0;
  DevicePackageGateOwned := False;
  ShellAnchorReplacementSafe := True;
  SetupPostInstallCompleted := False;
  SetupRuntimeClassificationCaptured := False;
  SetupServiceExisted := False;
  SetupServiceStateCaptured := False;
  SetupServiceWasRunning := False;
  SetupShutdownApplied := False;
  UsbipOutcomeReported := False;
  UsbipRebootRequired := False;
  UsbipStatusPrepared := False;
  Result := SteamInstalled();
  if not Result then
    MsgBox(CustomMessage('SteamMissing'), mbCriticalError, MB_OK);
end;

// Only a newly installed USB/IP driver requires a routine reboot. An incomplete
// outcome stays conservative, while an already-present or failed optional driver
// no longer makes every ordinary WSGM update advertise a needless restart. Never
// mark silent setup for restart: /VERYSILENT could reboot automatically.
function NeedRestart(): Boolean;
begin
  Result := WizardIsTaskSelected('usbipdriver') and
    (UsbipRebootRequired or not UsbipOutcomeReported) and not WizardSilent();
end;

function UsbipInstallStatusPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\WSGM\usbip-install-status.ini');
end;

procedure PrepareUsbipInstallOutcome();
var
  StatusPath: String;
begin
  UsbipOutcomeReported := False;
  UsbipRebootRequired := False;
  StatusPath := UsbipInstallStatusPath();
  UsbipStatusPrepared := not FileExists(StatusPath) or DeleteFile(StatusPath);
  if not UsbipStatusPrepared then
    Log('USB/IP: the previous outcome marker could not be removed; stale data will not be trusted');
end;

procedure WarnUsbipInstallOutcome(const Detail: String);
begin
  Log('USB/IP: ' + Detail);
  if not WizardSilent() then
    MsgBox(Detail + #13#10 + #13#10 +
      'WSGM was installed, but controller management may remain unavailable.',
      mbInformation, MB_OK);
end;

procedure ReportUsbipInstallOutcome();
var
  StatusPath, SchemaVersion, Outcome, RequiredVersion, ObservedVersion,
    DriverRegistered, RebootRequired, MessageText, Detail: String;
begin
  StatusPath := UsbipInstallStatusPath();
  if not UsbipStatusPrepared then
  begin
    WarnUsbipInstallOutcome('The USB/IP driver result could not be verified.');
    Exit;
  end;
  if not FileExists(StatusPath) then
  begin
    WarnUsbipInstallOutcome('The USB/IP driver did not publish a result.');
    Exit;
  end;

  SchemaVersion := GetIniString('usbip', 'schemaVersion', '', StatusPath);
  Outcome := GetIniString('usbip', 'outcome', '', StatusPath);
  RequiredVersion := GetIniString('usbip', 'requiredVersion', '', StatusPath);
  ObservedVersion := GetIniString('usbip', 'observedVersion', '', StatusPath);
  DriverRegistered := GetIniString('usbip', 'driverRegistered', 'unknown', StatusPath);
  RebootRequired := GetIniString('usbip', 'rebootRequired', 'false', StatusPath);
  MessageText := Copy(GetIniString('usbip', 'message', '', StatusPath), 1, 512);
  if SchemaVersion <> '1' then
  begin
    WarnUsbipInstallOutcome('The USB/IP driver returned an unsupported status format.');
    Exit;
  end;

  UsbipRebootRequired := CompareText(RebootRequired, 'true') = 0;
  Detail := 'outcome=' + Outcome + ', required=' + RequiredVersion +
    ', observed=' + ObservedVersion + ', registered=' + DriverRegistered +
    ', reboot=' + RebootRequired;
  if (Outcome = 'installed') or (Outcome = 'already-present') then
  begin
    UsbipOutcomeReported := True;
    Log('USB/IP: ' + Detail);
    Exit;
  end;
  if (Outcome = 'failed') or (Outcome = 'blocked-newer-version') then
  begin
    UsbipOutcomeReported := True;
    if MessageText = '' then
      MessageText := 'The optional USB/IP driver was not made available.';
    WarnUsbipInstallOutcome(MessageText + ' (' + Detail + ')');
    Exit;
  end;

  WarnUsbipInstallOutcome(
    'The USB/IP driver returned an incomplete result (' + Detail + ').');
end;

function WasShellRunning(): Boolean;
begin
  Result := WasShell;
end;

function WasSettingsRunning(): Boolean;
begin
  Result := WasRunning and not WasShell;
end;

function WasNothingRunning(): Boolean;
begin
  Result := not WasRunning;
end;

function OpenEventW(dwDesiredAccess: LongWord; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function CreateFileW(lpFileName: String; dwDesiredAccess, dwShareMode: LongWord;
  lpSecurityAttributes: LongWord; dwCreationDisposition, dwFlagsAndAttributes: LongWord;
  hTemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function OpenSCManagerW(lpMachineName, lpDatabaseName,
  dwDesiredAccess: LongWord): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenServiceW(hSCManager: THandle; lpServiceName: String;
  dwDesiredAccess: LongWord): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function CreateEventW(lpEventAttributes: LongWord; bManualReset, bInitialState: BOOL;
  lpName: String): THandle;
  external 'CreateEventW@kernel32.dll stdcall';
function CreateMutexW(lpMutexAttributes: LongWord; bInitialOwner: BOOL;
  lpName: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function ReleaseMutexK(hMutex: THandle): BOOL;
  external 'ReleaseMutex@kernel32.dll stdcall';
function GetLastErrorK(): LongWord;
  external 'GetLastError@kernel32.dll stdcall';
function GetFileAttributesW(lpFileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function FindFirstFileW(lpFileName: String;
  var FindFileData: TWin32FindData): THandle;
  external 'FindFirstFileW@kernel32.dll stdcall';
function FindNextFileW(hFindFile: THandle;
  var FindFileData: TWin32FindData): BOOL;
  external 'FindNextFileW@kernel32.dll stdcall';
function FindCloseK(hFindFile: THandle): BOOL;
  external 'FindClose@kernel32.dll stdcall';
function QueryServiceStatusK(hService: THandle;
  var ServiceStatus: TServiceStatus): BOOL;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function ResetEventK(hEvent: THandle): BOOL;
  external 'ResetEvent@kernel32.dll stdcall';
function WaitForSingleObjectK(hHandle: THandle; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandleK(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function CloseServiceHandleK(hSCObject: THandle): BOOL;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function GetCurrentProcessIdK(): LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function ProcessIdToSessionIdK(dwProcessId: LongWord;
  var pSessionId: LongWord): BOOL;
  external 'ProcessIdToSessionId@kernel32.dll stdcall';

function AcquireDevicePackageSlotGate(): Boolean;
var
  WaitResult: LongWord;
begin
  if DevicePackageGateHandle <> 0 then
  begin
    Result := DevicePackageGateOwned;
    Exit;
  end;

  Result := False;
  DevicePackageGateHandle := CreateMutexW(
    0, False, 'Global\WSGM.DevicePackageSlot');
  if DevicePackageGateHandle = 0 then
  begin
    Log('Could not open the machine-wide Device Plugin package-slot gate');
    Exit;
  end;

  // Setup and uninstall lifecycle hooks execute on their main script thread. Keep mutex ownership
  // there from the stop/recheck boundary through package mutation; the matching deinitializer
  // closes an abandoned handle on every early-exit path.
  WaitResult := WaitForSingleObjectK(DevicePackageGateHandle, 5000);
  if (WaitResult = WaitObject0) or (WaitResult = WaitAbandoned) then
  begin
    DevicePackageGateOwned := True;
    Result := True;
    Exit;
  end;

  Log('The machine-wide Device Plugin package-slot gate remained busy or inaccessible');
  CloseHandleK(DevicePackageGateHandle);
  DevicePackageGateHandle := 0;
end;

procedure ReleaseDeviceOwnerReservation();
begin
  if DeviceOwnerHandle <> 0 then
  begin
    CloseHandleK(DeviceOwnerHandle);
    DeviceOwnerHandle := 0;
  end;
end;

procedure ReleaseDevicePackageGateReservation();
begin
  if DevicePackageGateHandle <> 0 then
  begin
    if DevicePackageGateOwned then
    begin
      if not ReleaseMutexK(DevicePackageGateHandle) then
        Log('Could not release the Device Plugin package-slot mutex cleanly');
      DevicePackageGateOwned := False;
    end;
    CloseHandleK(DevicePackageGateHandle);
    DevicePackageGateHandle := 0;
  end;
end;

procedure ReleaseDevicePublicationReservations();
begin
  // Close the unowned marker first. A new WSGM coordinator may reserve it, but it still has to
  // wait for the package gate until the completed publication is visible.
  ReleaseDeviceOwnerReservation();
  ReleaseDevicePackageGateReservation();
end;

function ReserveDeviceOwner(): Boolean;
var
  CreationError: LongWord;
begin
  if DeviceOwnerHandle <> 0 then
  begin
    Result := True;
    Exit;
  end;

  Result := False;
  DeviceOwnerHandle := CreateMutexW(0, False, 'Global\WSGM.DeviceOwner');
  CreationError := GetLastErrorK();
  if DeviceOwnerHandle = 0 then
  begin
    Log('Could not create the machine-wide Device Plugin owner marker');
    Exit;
  end;
  if CreationError = ErrorAlreadyExists then
  begin
    Log('A machine-wide WSGM or Device Lab owner is still active');
    CloseHandleK(DeviceOwnerHandle);
    DeviceOwnerHandle := 0;
    Exit;
  end;

  // Runtime ownership is elected by object creation rather than mutex acquisition. Holding the
  // new object prevents WSGM or Device Lab from opening the plugin while package files move.
  Result := True;
end;

function InspectDeviceDirectory(const Path, Description: String;
  var Exists: Boolean): Boolean;
var
  Attributes, InspectionError: LongWord;
begin
  Result := False;
  Exists := False;
  Attributes := GetFileAttributesW(Path);
  if Attributes = InvalidFileAttributes then
  begin
    InspectionError := GetLastErrorK();
    if (InspectionError = ErrorFileNotFound) or
      (InspectionError = ErrorPathNotFound) then
    begin
      Result := True;
      Exit;
    end;
    Log(Description + ' could not be inspected; error=' + IntToStr(InspectionError));
    Exit;
  end;

  if (Attributes and FileAttributeDirectory) = 0 then
  begin
    Log(Description + ' is not a directory: ' + Path);
    Exit;
  end;
  if (Attributes and FileAttributeReparsePoint) <> 0 then
  begin
    Log(Description + ' is a link/reparse point and will not be traversed: ' + Path);
    Exit;
  end;

  Exists := True;
  Result := True;
end;

function DeleteInspectedDeviceDirectory(const Path, Description: String;
  Exists: Boolean): Boolean;
begin
  Result := True;
  if not Exists then Exit;
  Result := DelTree(Path, True, True, True);
  if not Result then
    Log(Description + ' could not be removed: ' + Path);
end;

function FindDataName(const Data: TWin32FindData): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to 259 do
  begin
    if Data.FileName[I] = #0 then Exit;
    Result := Result + Data.FileName[I];
  end;
end;

procedure AppendPath(var Paths: TArrayOfString; const Path: String);
var
  Count: Integer;
begin
  Count := GetArrayLength(Paths);
  SetArrayLength(Paths, Count + 1);
  Paths[Count] := Path;
end;

function CleanupStaleDevicePluginStaging(): Boolean;
var
  EnumerationError: LongWord;
  FindData: TWin32FindData;
  FindHandle: THandle;
  FixedStaging, LegacyStaging, Root: String;
  FixedStagingExists, LegacyStagingExists, RootExists: Boolean;
  Index: Integer;
  LegacyStagingPaths: TArrayOfString;
begin
  Result := False;
  Root := ExpandConstant('{autopf}\WSGM\DevicePlugins');
  FixedStaging := AddBackslash(Root) + '.staging';
  if not InspectDeviceDirectory(
    Root, 'Device Plugin slot parent', RootExists) or
    not InspectDeviceDirectory(
      FixedStaging, 'Device Plugin staging root', FixedStagingExists) then Exit;

  if RootExists then
  begin
    FindHandle := FindFirstFileW(
      AddBackslash(Root) + '.installed.staging-*', FindData);
    if FindHandle = InvalidHandleValue then
    begin
      EnumerationError := GetLastErrorK();
      if (EnumerationError <> ErrorFileNotFound) and
        (EnumerationError <> ErrorPathNotFound) then
      begin
        Log('Legacy Device Plugin staging roots could not be enumerated; error=' +
          IntToStr(EnumerationError));
        Exit;
      end;
    end
    else
    begin
      try
        repeat
          LegacyStaging := AddBackslash(Root) + FindDataName(FindData);
          if not InspectDeviceDirectory(
            LegacyStaging, 'Legacy Device Plugin staging root',
            LegacyStagingExists) then Exit;
          if LegacyStagingExists then
            AppendPath(LegacyStagingPaths, LegacyStaging);
        until not FindNextFileW(FindHandle, FindData);
        EnumerationError := GetLastErrorK();
        if EnumerationError <> ErrorNoMoreFiles then
        begin
          Log('Legacy Device Plugin staging enumeration ended with error=' +
            IntToStr(EnumerationError));
          Exit;
        end;
      finally
        FindCloseK(FindHandle);
      end;
    end;
  end;

  // Every cleanup target and the parent were inspected before the first delete. Never let an
  // inaccessible later match turn a partially completed cleanup into a successful preflight.
  if not DeleteInspectedDeviceDirectory(
    FixedStaging, 'Device Plugin staging root', FixedStagingExists) then Exit;
  for Index := 0 to GetArrayLength(LegacyStagingPaths) - 1 do
    if not DeleteInspectedDeviceDirectory(
      LegacyStagingPaths[Index], 'Legacy Device Plugin staging root', True) then Exit;
  Result := True;
end;

procedure ReplaceDevicePluginSlot();
var
  HadInstalled: Boolean;
  Installed, LegacyPrevious, LegacyReviewed, Previous, Root, Staging: String;
  InstalledExists, LegacyPreviousExists, LegacyReviewedExists, PreviousExists,
    RootExists, StagingExists: Boolean;
begin
  Root := ExpandConstant('{autopf}\WSGM\DevicePlugins');
  Installed := AddBackslash(Root) + 'installed';
  Staging := AddBackslash(Root) + '.staging';
  Previous := AddBackslash(Root) + '.previous';
  LegacyPrevious := AddBackslash(Root) + '.installed.previous';
  LegacyReviewed := AddBackslash(Root) + 'reviewed';

  // Validate every move/delete target before changing any slot state. A top-level reparse point
  // is never followed by installer cleanup, even though the parent is administrator-protected.
  if not InspectDeviceDirectory(Root, 'Device Plugin slot parent', RootExists) or
    not InspectDeviceDirectory(Installed, 'Installed Device Plugin slot', InstalledExists) or
    not InspectDeviceDirectory(Staging, 'Device Plugin staging root', StagingExists) or
    not InspectDeviceDirectory(Previous, 'Device Plugin recovery root', PreviousExists) or
    not InspectDeviceDirectory(
      LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) or
    not InspectDeviceDirectory(
      LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists) then
    RaiseException('The Device Plugin slot contains an unsafe or inaccessible path.');
  if not RootExists and (InstalledExists or StagingExists or PreviousExists or
    LegacyPreviousExists or LegacyReviewedExists) then
    RaiseException('Device Plugin slot children exist without a valid parent directory.');

  if not WizardIsComponentSelected('device') then
  begin
    // Component removal is authoritative: remove both recovery namespaces as well as the live
    // slot so no interrupted-update backup can resurrect Device Integration on next startup.
    // The live slot is deliberately last: a failed backup cleanup leaves the current package
    // installed instead of turning the surviving recovery directory into the next active slot.
    if not DeleteInspectedDeviceDirectory(
      Staging, 'Device Plugin staging root', StagingExists) or
      not DeleteInspectedDeviceDirectory(
        Previous, 'Device Plugin recovery root', PreviousExists) or
      not DeleteInspectedDeviceDirectory(
        LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) or
      not DeleteInspectedDeviceDirectory(
        LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists) or
      not DeleteInspectedDeviceDirectory(
        Installed, 'Installed Device Plugin slot', InstalledExists) then
      RaiseException('Could not remove every Device Plugin package and recovery root.');
    Exit;
  end;

  if not StagingExists then
    RaiseException('The one Device Plugin package was not staged.');

  if InstalledExists then
  begin
    // A live directory proves publication completed. Any recovery sibling is stale and may be
    // retired only now, after the replacement staging tree is complete.
    if not DeleteInspectedDeviceDirectory(
      Previous, 'Device Plugin recovery root', PreviousExists) or
      not DeleteInspectedDeviceDirectory(
        LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) then
      RaiseException('Could not retire stale Device Plugin recovery state.');
    PreviousExists := False;
    LegacyPreviousExists := False;
  end
  else
  begin
    if PreviousExists and LegacyPreviousExists then
      RaiseException('Both current and legacy Device Plugin recovery roots exist.');
    if LegacyPreviousExists then
    begin
      if not RenameFile(LegacyPrevious, Previous) then
        RaiseException('Could not migrate the legacy Device Plugin recovery root.');
      PreviousExists := True;
      LegacyPreviousExists := False;
    end;
  end;

  HadInstalled := InstalledExists;
  if HadInstalled and not RenameFile(Installed, Previous) then
    RaiseException('Could not move the previous Device Plugin outside the active slot.');
  if HadInstalled then
    PreviousExists := True;

  if not RenameFile(Staging, Installed) then
  begin
    if HadInstalled then
    begin
      if not RenameFile(Previous, Installed) then
        RaiseException('Could not install the replacement Device Plugin or restore the previous slot. ' +
          'The previous package remains in the recovery directory.');
      Log('Replacement Device Plugin publication failed; restored the previous active slot');
    end;
    RaiseException('Could not atomically install the replacement Device Plugin.');
  end;

  if not DeleteInspectedDeviceDirectory(
    Previous, 'Device Plugin recovery root', PreviousExists) or
    not DeleteInspectedDeviceDirectory(
      LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) or
    not DeleteInspectedDeviceDirectory(
      LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists) then
    RaiseException('The Device Plugin was published but stale package state could not be retired.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    try
      ReplaceDevicePluginSlot();
      // [Run] now owns service/runtime restart. Do not let DeinitializeSetup launch a second copy.
      // Set only after publication actually succeeded: ReplaceDevicePluginSlot raises on a failure
      // it cannot make safe, and clearing this first would tell DeinitializeSetup that [Run] was
      // going to restart a runtime that setup had already abandoned.
      SetupShutdownApplied := False;
      SetupPostInstallCompleted := True;
    finally
      ReleaseDevicePublicationReservations();
    end;
  end;
end;

// WSGM is almost certainly running during an update (it IS the shell), and it
// may be ELEVATED. This setup is itself elevated (PrivilegesRequired=admin), so
// its token carries BUILTIN\Administrators and this user's SID — both of which
// the event's DACL grants EVENT_MODIFY_STATE. WSGM listens on a named
// MANUAL-RESET event (one SetEvent releases every waiting instance, elevated or
// not) and exits itself gracefully (which also asks Steam to exit and releases
// the injected Steam Input payload). taskkill remains as fallback for any
// leftovers. New builds expose one optional completion event and write the compact
// clean/unverified/timed-out/failed outcome to wsgm.log. Its absence identifies an
// old build and preserves the original bounded wait/taskkill fallback.
function RequestRunningInstancesExit(const EventName: String;
  GraceIterations: Integer;
  var HandoffResult: TShutdownHandoffResult): Boolean;
var
  I: Integer;
  H, CompletionEvent: THandle;
  HasHandoffChannel: Boolean;
  CompletionObserved: Boolean;
begin
  Result := False;
  HandoffResult := shrLegacy;
  CompletionObserved := False;
  CompletionEvent := OpenEventW(
    $00100002 { SYNCHRONIZE | EVENT_MODIFY_STATE },
    False, EventName + '.Completed');
  HasHandoffChannel := CompletionEvent <> 0;
  try
    if HasHandoffChannel then
      ResetEventK(CompletionEvent);

    H := OpenEventW($0002 { EVENT_MODIFY_STATE }, False, EventName);
    if H <> 0 then
    begin
      Result := True;
      if not SetEvent(H) then
      begin
        if HasHandoffChannel then
          HandoffResult := shrFailed;
      end
      else
      begin
        // Wait for both the shell owner to release its mutex and a new build to
        // publish completion. A settings-only instance has no shell mutex, so the
        // completion event prevents setup from force-stopping it immediately.
        for I := 1 to GraceIterations do
        begin
          if HasHandoffChannel and
            (WaitForSingleObjectK(CompletionEvent, 0) = 0) then
            CompletionObserved := True;
          if (not CheckForMutexes('WSGM.Shell')) and
            ((not HasHandoffChannel) or CompletionObserved) then Break;
          Sleep(500);
        end;
        Sleep(500);

        if HasHandoffChannel then
          if CompletionObserved and not CheckForMutexes('WSGM.Shell') then
            HandoffResult := shrCompleted
          else
            HandoffResult := shrTimedOut;
      end;
      CloseHandleK(H);
    end;
  finally
    if CompletionEvent <> 0 then CloseHandleK(CompletionEvent);
  end;
end;

procedure ForceStopCurrentSessionImage(const ImageName: String);
var
  Args: String;
  R: Integer;
  SessionId: LongWord;
begin
  // Every shutdown/recovery object above is Local to this Terminal Services session. Match that
  // ownership boundary when applying the force fallback: an image-only taskkill would also end
  // another logged-on user's primary and the anchor that is still restoring that user's desktop.
  if not ProcessIdToSessionIdK(GetCurrentProcessIdK(), SessionId) then
  begin
    Log('Could not resolve the installer session; refusing a cross-session force stop for ' +
      ImageName);
    Exit;
  end;
  Args := '/FI "SESSION eq ' + IntToStr(SessionId) + '" /IM "' + ImageName + '" /F';
  if not Exec(ExpandConstant('{sys}\taskkill.exe'), Args, '', SW_HIDE,
    ewWaitUntilTerminated, R) then
    Log('Could not start the current-session force stop for ' + ImageName);
end;

function CanInstallShellAnchor(): Boolean;
begin
  // restartreplace is an interactive fallback only. A silent update must never turn an
  // unacknowledged recovery owner into Inno's automatic reboot path.
  Result := ShellAnchorReplacementSafe or not WizardSilent();
end;

procedure LogShutdownHandoff(const Operation: String;
  HandoffResult: TShutdownHandoffResult);
begin
  case HandoffResult of
    shrCompleted:
      Log(Operation + ' shutdown handoff: completed; compact outcome is in wsgm.log');
    shrTimedOut:
      Log(Operation + ' shutdown handoff: timed out; applying bounded force-stop fallback');
    shrFailed:
      Log(Operation + ' shutdown handoff: failed; applying bounded force-stop fallback');
    shrLegacy:
      Log(Operation + ' shutdown handoff: legacy build without result channel; preserving fallback');
  end;
end;

procedure ForceStopRunningInstances();
begin
  // Fallback / leftovers (unelevated instances only — elevated ones should
  // already have exited through their bounded graceful path). Never cross the
  // Local event/mutex session boundary while stopping an identically named run.
  ForceStopCurrentSessionImage('WSGM.exe');
end;

procedure WaitForShellAnchorRecovery();
var
  AnchorPath: String;
  H, Probe: THandle;
  WaitResult: LongWord;
begin
  AnchorPath := ExpandConstant('{app}\WSGM.ShellAnchor.exe');
  // The companion signals only after an explicit stop or after owner-loss recovery has made its
  // preserve/restore decision. Never image-kill it before that boundary: it may be the only process
  // capable of restoring Explorer after the primary force fallback. Keep this event handle open
  // through retirement so a concurrent replacement anchor cannot reuse the name and enter this
  // session's image-name scope. A missing/late acknowledgement leaves the file to interactive restartreplace,
  // silent-update preservation, or uninsrestartdelete so installer completion remains bounded.
  H := OpenEventW(
    $00100000 { SYNCHRONIZE }, False,
    'Local\WSGM.ShellAnchor.RecoverySettled');
  if H = 0 then
  begin
    // Keep the installed image intact so a later preflight refusal or setup rollback can restart
    // the old build. A zero-access, zero-share open checks replaceability without mutating it.
    if FileExists(AnchorPath) then
    begin
      Probe := CreateFileW(AnchorPath, 0, 0, 0, 3 { OPEN_EXISTING }, $80, 0);
      if Probe = InvalidHandleValue then
      begin
        ShellAnchorReplacementSafe := False;
        Log('Shell-anchor image remained locked without a recovery event; silent update will preserve it');
      end
      else
        CloseHandleK(Probe);
    end;
    Exit;
  end;
  WaitResult := WaitForSingleObjectK(H, 5000);
  if WaitResult = 0 then
  begin
    // The acknowledgement is published from the companion's final exit path. Give natural process
    // exit a short head start, then retire only this session's companion before replacement.
    Sleep(250);
    ForceStopCurrentSessionImage('WSGM.ShellAnchor.exe');
    if FileExists(AnchorPath) then
    begin
      Probe := CreateFileW(AnchorPath, 0, 0, 0, 3 { OPEN_EXISTING }, $80, 0);
      if Probe = InvalidHandleValue then
      begin
        ShellAnchorReplacementSafe := False;
        Log('Acknowledged shell-anchor image remained locked; silent update will preserve it');
      end
      else
        CloseHandleK(Probe);
    end;
  end
  else
  begin
    ShellAnchorReplacementSafe := False;
    Log('Shell-anchor recovery acknowledgement timed out; leaving the companion alive for ' +
      'deferred replacement/deletion');
  end;
  CloseHandleK(H);
end;

function StopRunningInstances(): Boolean;
var
  HandoffResult: TShutdownHandoffResult;
begin
  // WSGM first has a bounded 10-second Steam/wrapper pre-stop, then its own
  // 10-second update cleanup. Forty-four half-second iterations plus the final
  // settle leave margin for dispatcher handoff before the force-stop fallback.
  Result := RequestRunningInstancesExit(
    'Local\WSGM.ExitForUpdate', 44, HandoffResult);
  if Result then
    LogShutdownHandoff('Update', HandoffResult);
  ForceStopRunningInstances();
  WaitForShellAnchorRecovery();
end;

function StopRunningInstancesForUninstall(): Boolean;
var
  HandoffResult: TShutdownHandoffResult;
begin
  Result := False;
  // New builds expose a distinct 20-second uninstall budget. When removing an
  // older build, fall back to its cross-version update event and preserve enough
  // grace for that build's Steam pre-stop before applying the force fallback.
  if RequestRunningInstancesExit(
    'Local\WSGM.ExitForUninstall', 40, HandoffResult) then
  begin
    Result := True;
    LogShutdownHandoff('Uninstall', HandoffResult);
  end
  else if RequestRunningInstancesExit(
    'Local\WSGM.ExitForUpdate', 44, HandoffResult) then
  begin
    Result := True;
    LogShutdownHandoff('Uninstall through legacy update event', HandoffResult);
  end;
  ForceStopRunningInstances();
  WaitForShellAnchorRecovery();
end;

function ReplacementBlockersPresent(IncludeSteam: Boolean): Boolean;
var
  R: Integer;
  Args, Script: String;
begin
  // The Local shutdown event and wrapper ownership are session scoped. Inspect that exact session
  // and fail closed if inspection itself is unavailable. Setup never terminates Steam or a launch
  // wrapper: either can own a running game tree that needs its normal save/exit path.
  if IncludeSteam then
    Script := '$names=@(''steam'',''WSGM.Launch'',''WSGM.Deelevate'',''steam-input-lease''); '
  else
    Script := '$names=@(''WSGM.Launch'',''WSGM.Deelevate'',''steam-input-lease''); ';
  Script := '$session=(Get-Process -Id $PID).SessionId; ' + Script +
    '$blocked=@(Get-Process -ErrorAction SilentlyContinue | Where-Object { ' +
    '$_.SessionId -eq $session -and $names -contains $_.ProcessName }); ' +
    'if($blocked.Count -gt 0){exit 75}; exit 0';
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"';
  if not Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Args, '', SW_HIDE, ewWaitUntilTerminated, R) then
  begin
    Log('Could not start the current-session update blocker inspection; refusing replacement');
    Result := True;
    Exit;
  end;

  Result := R <> 0;
  if Result then
    Log('Current-session Steam or launch wrapper blocks safe replacement (inspection code ' +
      IntToStr(R) + ')');
end;

function InspectLogonServiceState(var Exists, Running: Boolean): Boolean;
var
  Manager, Service: THandle;
  OpenError: LongWord;
  Status: TServiceStatus;
begin
  Result := False;
  Exists := False;
  Running := False;
  Manager := OpenSCManagerW(0, 0, ScManagerConnect);
  if Manager = 0 then
  begin
    Log('Could not open the Service Control Manager to inspect WSGMLogonService');
    Exit;
  end;

  try
    Service := OpenServiceW(Manager, 'WSGMLogonService', ServiceQueryStatus);
    if Service = 0 then
    begin
      OpenError := GetLastErrorK();
      if OpenError = ErrorServiceDoesNotExist then
        Result := True
      else
        Log('Could not inspect WSGMLogonService; error=' + IntToStr(OpenError));
      Exit;
    end;

    try
      Exists := True;
      if not QueryServiceStatusK(Service, Status) then
      begin
        Log('Could not query WSGMLogonService state; error=' +
          IntToStr(GetLastErrorK()));
        Exit;
      end;
      if Status.CurrentState = ServiceStopped then
      begin
        Result := True;
        Exit;
      end;
      if Status.CurrentState = ServiceRunning then
      begin
        Running := True;
        Result := True;
        Exit;
      end;

      // Pending or unsupported service states cannot be reproduced exactly after refusal. Stop no
      // process and fail before the runtime or protected files are touched.
      Log('WSGMLogonService is in an unverified transitional state: ' +
        IntToStr(Status.CurrentState));
    finally
      CloseServiceHandleK(Service);
    end;
  finally
    CloseServiceHandleK(Manager);
  end;
end;

// Stop the logon service BEFORE stopping WSGM. Ordering is load-bearing: with
// the service alive, a killed WSGM trips its watchdog, which starts explorer
// mid-update and flips the post-update restart into desktop mode. Also frees
// the service binary for [Files] (covers the abandoned preview too — same
// service name). Delete is not needed on updates; --install reconfigures.
function StopLogonService(): Boolean;
var
  I, R: Integer;
  Exists, Running: Boolean;
begin
  Result := False;
  if not InspectLogonServiceState(Exists, Running) then
  begin
    Log('Could not inspect WSGMLogonService before requesting stop');
    Exit;
  end;
  if not Exists or not Running then
  begin
    Result := True;
    Exit;
  end;

  if not Exec(
    ExpandConstant('{sys}\sc.exe'),
    'stop WSGMLogonService',
    '', SW_HIDE, ewWaitUntilTerminated, R) then
  begin
    Log('Could not start the WSGMLogonService stop request');
    Exit;
  end;
  if R <> 0 then
  begin
    // A concurrent service stop can race the request. Accept only a fresh
    // observation of the stopped/missing state; otherwise fail before files move.
    if InspectLogonServiceState(Exists, Running) and (not Exists or not Running) then
    begin
      Result := True;
      Exit;
    end;
    Log('WSGMLogonService stop request exited with code ' + IntToStr(R));
    Exit;
  end;

  for I := 1 to 40 do
  begin
    if InspectLogonServiceState(Exists, Running) and (not Exists or not Running) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
  Log('WSGMLogonService did not reach the stopped state within ten seconds');
end;

procedure RestoreStoppedServiceAndRuntime(const Operation: String;
  ServiceExisted, ServiceWasRunning, RuntimeWasShell, RuntimeWasRunning: Boolean);
var
  Arguments, RuntimePath, ServicePath: String;
  R: Integer;
begin
  if ServiceExisted and ServiceWasRunning then
  begin
    // Use the service's installer path rather than `sc start`: --install tags the SCM start so a
    // recent autologon cannot be mistaken for a missed logon and trigger a second --boot takeover.
    ServicePath := ExpandConstant('{autopf}\WSGM\WSGM.LogonService.exe');
    if not FileExists(ServicePath) then
      Log(Operation + ': the previous logon-service executable is absent; service restart was skipped')
    else if not Exec(ServicePath, '--install', '', SW_HIDE, ewWaitUntilTerminated, R) then
      Log(Operation + ': could not run the logon-service recovery command')
    else if R <> 0 then
      Log(Operation + ': logon-service recovery exited with code ' + IntToStr(R));
  end;

  if not RuntimeWasRunning then Exit;
  RuntimePath := ExpandConstant('{app}\WSGM.exe');
  if not FileExists(RuntimePath) then
  begin
    Log(Operation + ': the previous WSGM executable is absent; runtime restart was skipped');
    Exit;
  end;
  if RuntimeWasShell then
  begin
    Arguments := '--shell';
  end
  else
    // Do not re-run legacy auto-mode classification during rollback. The initially observed
    // settings process must come back as settings even if Explorer recovery is still settling.
    Arguments := '--settings';

  if not Exec(RuntimePath, Arguments, '', SW_SHOWNORMAL, ewNoWait, R) then
    Log(Operation + ': could not restart the previous WSGM runtime');
end;

procedure RestoreStoppedSetupRuntime();
begin
  if not SetupShutdownApplied then Exit;
  // Clear first so a failed best-effort launch cannot be duplicated by DeinitializeSetup.
  SetupShutdownApplied := False;
  RestoreStoppedServiceAndRuntime(
    'Setup rollback', SetupServiceExisted, SetupServiceWasRunning,
    WasShell, WasRunning);
end;

procedure RestoreStoppedUninstallRuntime();
begin
  if not UninstallShutdownApplied then Exit;
  UninstallShutdownApplied := False;
  RestoreStoppedServiceAndRuntime(
    'Uninstall rollback', UninstallServiceExisted, UninstallServiceWasRunning,
    UninstallWasShell, UninstallWasRunning);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not AcquireDevicePackageSlotGate() then
  begin
    Result := 'The protected Device Plugin slot is busy or could not be verified. ' +
      'Close WSGM maintenance and try setup again.';
    Exit;
  end;

  if not SetupServiceStateCaptured then
  begin
    if not InspectLogonServiceState(SetupServiceExisted, SetupServiceWasRunning) then
    begin
      ReleaseDevicePackageGateReservation();
      Result := 'The WSGM logon-service state could not be verified. ' +
        'Setup refused to stop the current session.';
      Exit;
    end;
    SetupServiceStateCaptured := True;
  end;
  if SetupServiceWasRunning and not StopLogonService() then
  begin
    ReleaseDevicePackageGateReservation();
    Result := 'The WSGM logon service did not stop cleanly. Setup made no file changes.';
    Exit;
  end;
  // Capture the initial mode exactly once. A post-shutdown refusal restores that mode, and a retry
  // stops it again without overwriting the classification with the temporary stopped state.
  if not SetupRuntimeClassificationCaptured then
  begin
    // Only the shell-mode instance holds this mutex (session namespace). taskkill's exit code is
    // deliberately NOT part of WasRunning: image-name fallback also sees unrelated portable runs.
    WasShell := CheckForMutexes('WSGM.Shell');
    WasRunning := StopRunningInstances() or WasShell;
    SetupRuntimeClassificationCaptured := True;
  end
  else if StopRunningInstances() then
    Log('Setup retry stopped the previously restored WSGM runtime');
  if WasRunning then
    Sleep(500);
  SetupShutdownApplied := True;

  // An existing installation may have its injected payload loaded in Steam or a launch wrapper
  // holding the game tree. Defer the update instead of killing either. Fresh installs do not own
  // those images and therefore do not block merely because Steam is open.
  if FileExists(ExpandConstant('{app}\WSGM.exe')) and ReplacementBlockersPresent(True) then
  begin
    ReleaseDevicePublicationReservations();
    RestoreStoppedSetupRuntime();
    Result := 'Steam or a game launched through WSGM is still running. Close it normally, ' +
      'then retry setup. No process was terminated.';
    Exit;
  end;

  // WSGM now loads its one plugin in-process. The package gate prevents new package maintenance,
  // and the owner marker proves neither WSGM nor Device Lab can have plugin code loaded while the
  // protected slot moves. Both handles stay live through staging and publication.
  if not ReserveDeviceOwner() then
  begin
    ReleaseDevicePublicationReservations();
    RestoreStoppedSetupRuntime();
    Result := 'A WSGM or Device Lab hardware owner is still active on this machine. ' +
      'Close it and try setup again.';
    Exit;
  end;
  if not CleanupStaleDevicePluginStaging() then
  begin
    ReleaseDevicePublicationReservations();
    RestoreStoppedSetupRuntime();
    Result := 'Stale Device Plugin staging could not be removed safely. ' +
      'Setup refused to replace the protected slot.';
    Exit;
  end;
end;

procedure DeinitializeSetup();
begin
  // Covers cancellation and every failure that does not end in a completed post-install. Closing an
  // owned gate handle after a release failure makes it abandoned rather than leaving replacement
  // blocked indefinitely.
  ReleaseDevicePublicationReservations();
  // Gated on the post-install having actually completed, not merely on installation having begun.
  // The [Run] restart entries execute only after a successful setup, so a failure during [Files]
  // or a RaiseException out of ReplaceDevicePluginSlot used to reach here with the shell, Settings
  // and the logon service stopped and nothing left to start them again.
  if not SetupPostInstallCompleted then
    RestoreStoppedSetupRuntime();
end;

// The uninstaller must stop a running WSGM too (in desktop mode — the only
// place Settings > Apps > Uninstall is reachable — WSGM stays resident), or
// WSGM.exe stays locked, file removal leaves 'could not be removed' leftovers
// and a zombie ex-shell process keeps running.
function InitializeUninstall(): Boolean;
begin
  DeviceOwnerHandle := 0;
  DevicePackageGateHandle := 0;
  DevicePackageGateOwned := False;
  UninstallMutationStarted := False;
  UninstallServiceExisted := False;
  UninstallServiceWasRunning := False;
  UninstallShutdownApplied := False;
  Result := False;
  if not AcquireDevicePackageSlotGate() then
  begin
    MsgBox('The protected Device Plugin slot is busy or could not be verified. ' +
      'Close WSGM maintenance and try uninstall again.', mbCriticalError, MB_OK);
    Exit;
  end;

  if not InspectLogonServiceState(
    UninstallServiceExisted, UninstallServiceWasRunning) then
  begin
    ReleaseDevicePackageGateReservation();
    MsgBox('The WSGM logon-service state could not be verified. ' +
      'Uninstall refused to stop the current session.', mbCriticalError, MB_OK);
    Exit;
  end;
  if UninstallServiceWasRunning and not StopLogonService() then
  begin
    ReleaseDevicePackageGateReservation();
    MsgBox('The WSGM logon service did not stop cleanly. Uninstall made no file changes.',
      mbCriticalError, MB_OK);
    Exit;
  end;
  UninstallWasShell := CheckForMutexes('WSGM.Shell');
  UninstallWasRunning := StopRunningInstancesForUninstall() or UninstallWasShell;
  UninstallShutdownApplied := True;
  if ReplacementBlockersPresent(False) then
  begin
    ReleaseDevicePublicationReservations();
    RestoreStoppedUninstallRuntime();
    MsgBox('A game launched through WSGM is still running. Close it normally, then retry ' +
      'uninstall. No process was terminated.', mbCriticalError, MB_OK);
    Exit;
  end;
  if not ReserveDeviceOwner() then
  begin
    ReleaseDevicePublicationReservations();
    RestoreStoppedUninstallRuntime();
    MsgBox('A WSGM or Device Lab hardware owner is still active on this machine. ' +
      'Close it and try uninstall again.', mbCriticalError, MB_OK);
    Exit;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    UninstallMutationStarted := True;
end;

procedure DeinitializeUninstall();
begin
  // [UninstallRun] and [UninstallDelete] complete before this callback. Keep the exact global gate
  // and owner marker live through both, then release on the same script thread that acquired them.
  ReleaseDevicePublicationReservations();
  if not UninstallMutationStarted then
    RestoreStoppedUninstallRuntime();
end;
