; AI Usage for StreamDock - Windows installer (NSIS 3)
;
; Per-user install: no administrator rights, no UAC prompt.
;   plugin files -> %APPDATA%\HotSpot\StreamDock\plugins\com.saeedsh.aiusage.sdPlugin
;   uninstaller  -> %LOCALAPPDATA%\Programs\AI Usage for StreamDock\Uninstall.exe
;   Apps & features entry under HKCU, publisher SaeedSH.
; The setup file itself can be deleted after installing.
;
; Build:  makensis /DVERSION=1.0.0 /DSRC=..\com.saeedsh.aiusage.sdPlugin /DOUT=..\dist installer\AIUsage.nsi

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel user
ManifestDPIAware true

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef SRC
  !define SRC "..\com.saeedsh.aiusage.sdPlugin"
!endif
!ifndef OUT
  !define OUT "..\dist"
!endif

!define PRODUCT     "AI Usage for StreamDock"
!define PUBLISHER   "SaeedSH"
!define PLUGIN_ID   "com.saeedsh.aiusage.sdPlugin"
!define OLD_ID      "com.sherafat.claudeusage.sdPlugin"
!define EXE         "AIUsage.exe"
!define UNINST_KEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsageStreamDock"
!define APP_KEY     "Software\SaeedSH\AIUsageStreamDock"
!define URL         "https://github.com/saeedsh70/ai-usage-streamdock"

Name "${PRODUCT}"
OutFile "${OUT}\AIUsage-Setup-${VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT}"
BrandingText "${PRODUCT} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName"     "${PRODUCT}"
VIAddVersionKey "CompanyName"     "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${PRODUCT} Setup"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "LegalCopyright"  "Copyright (c) 2026 ${PUBLISHER}. MIT License."

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define MUI_ICON   "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "sidebar.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "sidebar.bmp"
!define MUI_ABORTWARNING

!define MUI_WELCOMEPAGE_TITLE "Install ${PRODUCT}"
!define MUI_WELCOMEPAGE_TEXT "This puts the AI Usage plugin into your stream deck software (Redragon SS-550 and other MiraBox StreamDock devices).$\r$\n$\r$\nIt shows the usage limits of Claude, ChatGPT (Codex) and Google Antigravity on your keys.$\r$\n$\r$\nThe stream deck software will be closed for a moment and started again.$\r$\n$\r$\nNo administrator rights are needed. After installing you can delete this setup file."
!define MUI_FINISHPAGE_TITLE "Installed"
!define MUI_FINISHPAGE_TEXT "In the stream deck software, find the AI Usage category and drag the AI Usage action onto a key. Choose the provider and what to show in the key settings.$\r$\n$\r$\nYou can delete this setup file now. To remove the plugin, use Settings > Apps > Installed apps."
!define MUI_FINISHPAGE_LINK "Help and documentation"
!define MUI_FINISHPAGE_LINK_LOCATION "${URL}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Var DeckPlugins

; Finds the StreamDock plugins folder: HotSpot\StreamDock first, then any HotSpot\<app>\plugins.
Function FindPluginsDir
  StrCpy $DeckPlugins ""
  ${If} ${FileExists} "$APPDATA\HotSpot\StreamDock\*.*"
    StrCpy $DeckPlugins "$APPDATA\HotSpot\StreamDock\plugins"
    Return
  ${EndIf}
  ClearErrors
  FindFirst $0 $1 "$APPDATA\HotSpot\*.*"
  ${DoUntil} ${Errors}
    ${If} $1 != "."
    ${AndIf} $1 != ".."
    ${AndIf} ${FileExists} "$APPDATA\HotSpot\$1\plugins\*.*"
      StrCpy $DeckPlugins "$APPDATA\HotSpot\$1\plugins"
      ${Break}
    ${EndIf}
    FindNext $0 $1
  ${Loop}
  FindClose $0
FunctionEnd

Section "Install"
  Call FindPluginsDir
  ${If} $DeckPlugins == ""
    MessageBox MB_ICONEXCLAMATION|MB_OK "The stream deck software was not found.$\r$\n$\r$\nInstall the Redragon (or MiraBox StreamDock) software, open it once, then run this setup again."
    Abort "Stream deck software not found."
  ${EndIf}
  DetailPrint "Plugins folder: $DeckPlugins"

  ; Close the stream deck software so its plugin files are not locked; remember what to restart.
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "${SRC}\${EXE}"
  DetailPrint "Closing the stream deck software..."
  ExecWait '"$PLUGINSDIR\${EXE}" --close-host "$PLUGINSDIR\host.txt"'

  ; Replace any earlier version (also the old "Claude Usage" plugin).
  RMDir /r "$DeckPlugins\${OLD_ID}"
  RMDir /r "$DeckPlugins\${PLUGIN_ID}"
  SetOutPath "$DeckPlugins\${PLUGIN_ID}"
  File /r "${SRC}\*.*"

  ; Uninstaller and Apps & features entry (current user only).
  CreateDirectory "$INSTDIR"
  SetOutPath "$INSTDIR"
  File "app.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APP_KEY}" "PluginDir" "$DeckPlugins\${PLUGIN_ID}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\app.ico"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$DeckPlugins\${PLUGIN_ID}"
  WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "${URL}"
  WriteRegStr HKCU "${UNINST_KEY}" "HelpLink" "${URL}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$DeckPlugins\${PLUGIN_ID}" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"

  DetailPrint "Starting the stream deck software..."
  ExecWait '"$PLUGINSDIR\${EXE}" --start-host "$PLUGINSDIR\host.txt"'
SectionEnd

Section "Uninstall"
  ReadRegStr $0 HKCU "${APP_KEY}" "PluginDir"
  InitPluginsDir
  ${If} $0 != ""
  ${AndIf} ${FileExists} "$0\${EXE}"
    CopyFiles /SILENT "$0\${EXE}" "$PLUGINSDIR\${EXE}"
    ExecWait '"$PLUGINSDIR\${EXE}" --close-host "$PLUGINSDIR\host.txt"'
    RMDir /r "$0"
    ExecWait '"$PLUGINSDIR\${EXE}" --start-host "$PLUGINSDIR\host.txt"'
  ${ElseIf} $0 != ""
    RMDir /r "$0"
  ${EndIf}

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "${APP_KEY}"
  DeleteRegKey /ifempty HKCU "Software\SaeedSH"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\app.ico"
  RMDir "$INSTDIR"
  ; Settings and log in %APPDATA%\AIUsageDeck are small and kept, in case you reinstall.
SectionEnd
