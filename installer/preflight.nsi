; Preflight Windows installer (NSIS 3). Per-user install, no administrator rights needed.
; Build: installer/build-windows-installer.sh  (or: makensis -DVERSION=x.y.z installer/preflight.nsi)

Unicode true
SetCompressor /SOLID lzma
ManifestDPIAware true
RequestExecutionLevel user

!ifndef VERSION
  !define VERSION "0.1.0"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\artifacts\publish\win-x64"
!endif
!ifndef OUT_DIR
  !define OUT_DIR "..\artifacts"
!endif

!define APP      "Preflight"
!define EXE      "Preflight.exe"
!define PUBLISHER "JW-1980"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Preflight"

Name "${APP} ${VERSION}"
OutFile "${OUT_DIR}\Preflight-${VERSION}-win-x64-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP}"
InstallDirRegKey HKCU "Software\${APP}" "InstallDir"
BrandingText "${APP} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP} installer"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 ${PUBLISHER}, MIT License"

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "LogicLib.nsh"

!define MUI_ICON "..\src\Preflight.App\Assets\preflight.ico"
!define MUI_UNICON "..\src\Preflight.App\Assets\preflight.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start Preflight"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Preflight requires 64-bit Windows (x64)."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "Preflight requires Windows 10 or later."
    Abort
  ${EndIf}
FunctionEnd

Section "Preflight (required)" SecMain
  SectionIn RO
  ; Close a running copy so files can be replaced on upgrade.
  nsExec::Exec 'taskkill /IM "${EXE}" /F'
  SetOutPath "$INSTDIR"
  ; On upgrade, replace the old app files, but only in a folder that really holds Preflight
  ; (user data lives in %APPDATA%\Preflight, not here).
  ${If} ${FileExists} "$INSTDIR\${EXE}"
  ${AndIf} ${FileExists} "$INSTDIR\Preflight.dll"
    RMDir /r "$INSTDIR"
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "${PUBLISH_DIR}\*.*"
  File "..\LICENSE"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\${APP}"
  CreateShortCut "$SMPROGRAMS\${APP}\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP}\Uninstall ${APP}.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\${APP}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "https://github.com/JW-1980/Preflight"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortCut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /IM "${EXE}" /F'
  Delete "$DESKTOP\${APP}.lnk"
  RMDir /r "$SMPROGRAMS\${APP}"
  ; Only wipe the folder if it is a Preflight install; otherwise remove just what we put there.
  ${If} ${FileExists} "$INSTDIR\Preflight.dll"
    RMDir /r "$INSTDIR"
  ${Else}
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
  ${EndIf}
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APP}"
  ; Linked sites (%APPDATA%\Preflight\sites.json) are kept on purpose.
SectionEnd

