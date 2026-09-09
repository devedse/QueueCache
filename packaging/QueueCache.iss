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
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Driver.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "Update-QueueCache.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "Update-QueueCache.ps1"; DestDir: "{commondesktop}"; Flags: ignoreversion
Source: "Update QueueCache.cmd"; DestDir: "{commondesktop}"; Flags: ignoreversion

[Icons]
Name: "{group}\QueueCache"; Filename: "{app}\desktop\QueueCache.Desktop.exe"
Name: "{commondesktop}\QueueCache"; Filename: "{app}\desktop\QueueCache.Desktop.exe"

[InstallDelete]
Type: files; Name: "{group}\Driver setup or resume after reboot.lnk"
Type: files; Name: "{app}\setup\Install-Interactive.ps1"

[Code]
function RunDriverSetup(Uninstall: Boolean): Boolean;
var Code: Integer; Parameters: String;
begin
  Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup\Install-Driver.ps1') + '"';
  if Uninstall then Parameters := Parameters + ' -Uninstall';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and ((Code = 0) or (Code = 3010));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not RunDriverSetup(False) then
      MsgBox('Driver setup did not complete. See C:\ProgramData\QueueCache\Logs for the error. Applications are installed, but do not enable caching until setup succeeds.', mbError, MB_OK);
end;

function NeedRestart(): Boolean;
begin
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  Result := RunDriverSetup(True);
  if not Result then MsgBox('Driver detach/drain failed. Application removal has been stopped to preserve recovery tools.', mbError, MB_OK);
end;
