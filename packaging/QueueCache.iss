#ifndef PackageDir
  #error PackageDir must identify a signed lab package
#endif
#ifndef BuildVersion
  #define BuildVersion "0.3.0.0"
#endif
[Setup]
AppId={{D664F471-5707-420A-ABAB-9EDD5217B771}
AppName=QueueCache
AppVersion={#BuildVersion}
DefaultDirName={autopf}\QueueCache
DefaultGroupName=QueueCache
OutputBaseFilename=QueueCache-{#BuildVersion}-setup
OutputDir=..\artifacts\installers
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
ChangesEnvironment=yes
Compression=lzma2
SolidCompression=yes
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\desktop\QueueCache.Desktop.exe
SetupIconFile=..\assets\branding\queuecache.ico

[Files]
Source: "{#PackageDir}\controller\*"; DestDir: "{tmp}\QueueCachePreflight"; Flags: dontcopy recursesubdirs createallsubdirs
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Driver.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "Recover-Registration.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "Update-QueueCache.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "Update QueueCache.cmd"; DestDir: "{app}\setup"; Flags: ignoreversion
; Desktop copies only when missing or changed: replacing an identical file moves its icon.
Source: "Update-QueueCache.ps1"; DestDir: "{commondesktop}"; Flags: ignoreversion; Check: DesktopFileStale('Update-QueueCache.ps1')
Source: "Update QueueCache.cmd"; DestDir: "{commondesktop}"; Flags: ignoreversion; Check: DesktopFileStale('Update QueueCache.cmd')

[Icons]
Name: "{group}\QueueCache"; Filename: "{app}\desktop\QueueCache.Desktop.exe"
Name: "{commondesktop}\QueueCache"; Filename: "{app}\desktop\QueueCache.Desktop.exe"; Check: DesktopShortcutMissing

[UninstallDelete]
; A later install that skipped an unchanged desktop file did not log it.
Type: files; Name: "{commondesktop}\Update-QueueCache.ps1"
Type: files; Name: "{commondesktop}\Update QueueCache.cmd"
Type: files; Name: "{commondesktop}\QueueCache.lnk"

[InstallDelete]
Type: files; Name: "{app}\tests\*.exe"
Type: files; Name: "{app}\tests\*.dll"
Type: files; Name: "{app}\tests\*.pdb"
Type: files; Name: "{app}\tests\qcache-*.deps.json"
Type: files; Name: "{app}\tests\qcache-*.runtimeconfig.json"
Type: dirifempty; Name: "{app}\tests"
Type: files; Name: "{app}\write-tests\*.exe"
Type: files; Name: "{app}\write-tests\*.dll"
Type: files; Name: "{app}\write-tests\*.pdb"
Type: files; Name: "{app}\write-tests\qcache-*.deps.json"
Type: files; Name: "{app}\write-tests\qcache-*.runtimeconfig.json"
Type: dirifempty; Name: "{app}\write-tests"
Type: files; Name: "{app}\file-tests\*.exe"
Type: files; Name: "{app}\file-tests\*.dll"
Type: files; Name: "{app}\file-tests\*.pdb"
Type: files; Name: "{app}\file-tests\qcache-*.deps.json"
Type: files; Name: "{app}\file-tests\qcache-*.runtimeconfig.json"
Type: dirifempty; Name: "{app}\file-tests"
Type: files; Name: "{app}\lab\Manage-Lab.ps1"
Type: files; Name: "{app}\lab\Test-CacheFaults.ps1"
Type: files; Name: "{app}\lab\Test-LabReads.ps1"
Type: dirifempty; Name: "{app}\lab"
Type: files; Name: "{group}\Driver setup or resume after reboot.lnk"
Type: files; Name: "{app}\setup\Install-Interactive.ps1"

[Code]
// The desktop app runs in the notification area from sign-in. It holds no cache or disk
// state (the driver and the service do), so closing it is safe; it returns at the next sign-in.
procedure CloseDesktopApp();
var Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM QueueCache.Desktop.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer; Controller: String;
begin
  Result := '';
  // Use the bundled guard before replacing any installed recovery tools. An
  // older CLI may be unable to distinguish an unbound interrupted installation.
  ExtractTemporaryFiles('{tmp}\QueueCachePreflight\*');
  Controller := ExpandConstant('{tmp}\QueueCachePreflight\qcache.exe');
  if (not Exec(Controller, '--managed-update-preflight', '', SW_HIDE, ewWaitUntilTerminated, Code)) or (Code <> 0) then
    Result := 'QueueCache update preflight failed. Stop or recover every managed disk first, then retry. RAM, images and recovery tools have been retained.';
  if Result = '' then
    CloseDesktopApp();
end;

// The bundled copy in {app}\setup is installed by an earlier [Files] entry of this run.
function DesktopFileStale(Name: String): Boolean;
var Desktop, Bundled: String;
begin
  Desktop := ExpandConstant('{commondesktop}\' + Name);
  Bundled := ExpandConstant('{app}\setup\' + Name);
  Result := (not FileExists(Desktop)) or (not FileExists(Bundled)) or
    (GetSHA256OfFile(Desktop) <> GetSHA256OfFile(Bundled));
end;

// The shortcut's target never moves, so an existing shortcut stays where the user put it.
function DesktopShortcutMissing(): Boolean;
begin
  Result := not FileExists(ExpandConstant('{commondesktop}\QueueCache.lnk'));
end;

// Install-Driver.ps1 exit codes: 0/3010 done; 1 failed with the driver registration unchanged or
// restored; 5 failed and could not restore it.
function RunDriverSetup(Uninstall: Boolean): Integer;
var Code: Integer; Parameters: String;
begin
  Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup\Install-Driver.ps1') + '"';
  if Uninstall then Parameters := Parameters + ' -Uninstall';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Code := RunDriverSetup(False);
    if Code = 5 then
      MsgBox('Driver setup failed and could not restore the previous driver registration. Do not restart yet: see C:\ProgramData\QueueCache\Logs, then rerun setup or run Recover-Registration.ps1 with the backup the log names.', mbCriticalError, MB_OK)
    else if (Code <> 0) and (Code <> 3010) then
      MsgBox('Driver setup did not complete, so the next restart loads the same storage filter as before. See C:\ProgramData\QueueCache\Logs for the error, fix it and run setup again. Applications are installed, but do not enable caching until setup succeeds.', mbError, MB_OK);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := True;
end;

function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  CloseDesktopApp();
  Code := RunDriverSetup(True);
  Result := (Code = 0) or (Code = 3010);
  if not Result then MsgBox('Driver detach/drain failed. Application removal has been stopped to preserve recovery tools.', mbError, MB_OK);
end;
