; Installeur unique : Aion 2 Builder + AION2 Pet Meter, pour l'utilisateur seul (sans droits administrateur).
; Compilé par .github/workflows/installer.yml avec les dernières versions publiées des deux applis :
;   ISCC.exe /DBuilderVersion=1.1.N /DPetMeterVersion=0.21.N installer\aion2.iss
; Les deux applis se mettent ensuite à jour toutes seules, à leur place. La désinstallation retire les programmes et
; les raccourcis, jamais tes données (réglages, combats, récolte : %APPDATA%\Aion2Builder, %LOCALAPPDATA%\AION2-pet-meter,
; et l'état du Pet Meter rangé à côté de son exe).

#ifndef BuilderVersion
  #define BuilderVersion "0.0.0"
#endif
#ifndef PetMeterVersion
  #define PetMeterVersion "0.0.0"
#endif

[Setup]
AppId={{8C1B2E57-3F0A-4C7E-9B3D-AE2A2E7C4D21}
AppName=Aion 2 Builder et Pet Meter
AppVersion={#BuilderVersion}
AppVerName=Aion 2 Builder {#BuilderVersion} et Pet Meter {#PetMeterVersion}
AppPublisher=Tomtoxi44
AppPublisherURL=https://github.com/Tomtoxi44/aion2-builder
DefaultDirName={localappdata}\Programs\Aion2
DefaultGroupName=Aion 2
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=Aion2-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Aion 2 Builder et Pet Meter
UninstallDisplayIcon={app}\Builder\Aion2Builder.exe

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktop"; Description: "Raccourcis sur le bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "..\dist\Aion2Builder.exe"; DestDir: "{app}\Builder"; Flags: ignoreversion
Source: "..\dist\aion2-pet-meter.exe"; DestDir: "{app}\PetMeter"; Flags: ignoreversion

[Icons]
Name: "{group}\Aion 2 Builder"; Filename: "{app}\Builder\Aion2Builder.exe"; WorkingDir: "{app}\Builder"
Name: "{group}\AION2 Pet Meter"; Filename: "{app}\PetMeter\aion2-pet-meter.exe"; WorkingDir: "{app}\PetMeter"
Name: "{group}\Désinstaller Aion 2 Builder et Pet Meter"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Aion 2 Builder"; Filename: "{app}\Builder\Aion2Builder.exe"; WorkingDir: "{app}\Builder"; Tasks: desktop
Name: "{userdesktop}\AION2 Pet Meter"; Filename: "{app}\PetMeter\aion2-pet-meter.exe"; WorkingDir: "{app}\PetMeter"; Tasks: desktop

[Run]
Filename: "{app}\PetMeter\aion2-pet-meter.exe"; WorkingDir: "{app}\PetMeter"; Description: "Lancer le Pet Meter"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Builder\Aion2Builder.exe"; WorkingDir: "{app}\Builder"; Description: "Lancer Aion 2 Builder"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Restes des mises à jour automatiques (anciens exe), pas les données.
Type: files; Name: "{app}\Builder\Aion2Builder.ancien-*.exe"
Type: files; Name: "{app}\Builder\Aion2Builder.nouveau.exe"
Type: files; Name: "{app}\PetMeter\aion2-pet-meter.exe.old"
Type: files; Name: "{app}\PetMeter\aion2-pet-meter.new.exe"
Type: files; Name: "{app}\PetMeter\aion2-pet-meter.new.txt"

[Code]
{ Le Pet Meter lit le trafic du jeu avec Npcap, que l'installeur ne peut pas embarquer (licence) : on prévient s'il manque. }
function NpcapInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Npcap') or RegKeyExists(HKLM32, 'SOFTWARE\Npcap') or
            FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent()) and (not NpcapInstalled()) then
    if MsgBox('Le Pet Meter a besoin de Npcap (gratuit) pour lire le trafic du jeu, et il n''est pas installé sur ce PC.' + #13#10 +
              'Ouvrir la page de téléchargement de Npcap ? (pendant son installation, coche « WinPcap API-compatible Mode »)',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://npcap.com/#download', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;
