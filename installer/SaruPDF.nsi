; -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "Sections.nsh"
!include "x64.nsh"

!ifndef PRODUCT_VERSION
  !error "Build using scripts/Build-Installer.ps1"
!endif
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SaruPDF"
!define PRODUCT_ID "SaruPDF-Local-Install-v1"
Name "SaruPDF ${PRODUCT_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\SaruPDF"
InstallDirRegKey HKCU "Software\SaruPDF" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID zlib
SetOverwrite on
BrandingText "SaruPDF"
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1041 "ProductName" "SaruPDF"
VIAddVersionKey /LANG=1041 "FileDescription" "SaruPDF Windows x64 セットアップ"
VIAddVersionKey /LANG=1041 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1041 "LegalCopyright" "SaruPDF"

!define MUI_ABORTWARNING
!define MUI_ICON "../assets/SaruPDF.ico"
!define MUI_UNICON "../assets/SaruPDF.ico"
!define MUI_WELCOMEPAGE_TEXT "SaruPDF を現在のユーザー用にインストールします。$\r$\n$\r$\nPDF を開くアプリへの登録を選択できます。既定の PDF アプリにする場合は、インストール後に Windows の設定画面で SaruPDF を選択してください。$\r$\n$\r$\n更新する場合は、起動中の SaruPDF の全ウィンドウを閉じてください。"
!insertmacro MUI_PAGE_WELCOME
!define MUI_LICENSEPAGE_CHECKBOX
!define MUI_LICENSEPAGE_TEXT_TOP "SaruPDF に同梱する Windows SDK コンポーネントの利用条件を確認してください。"
!insertmacro MUI_PAGE_LICENSE "../assets/licenses/Windows-SDK-LICENSE.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\SaruPDF.exe"
!define MUI_FINISHPAGE_RUN_TEXT "SaruPDF を起動する"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "PDF の関連付け設定を開く"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION ShowAssociationSettings
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Japanese"

Function ValidateInstallDirectory
  ; NSIS GetFullPathName requires the parent directory to exist. The Win32
  ; function also normalizes a brand-new install destination.
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
  ${If} $1 == 0
  ${OrIf} $1 >= ${NSIS_MAX_STRLEN}
    Goto bad_directory
  ${EndIf}
  StrCpy $INSTDIR $0
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == $0
  ${OrIf} $INSTDIR == "$0\"
    Goto bad_directory
  ${EndIf}
  IfFileExists "$INSTDIR\*" 0 done_directory
  ReadINIStr $0 "$INSTDIR\SaruPDF.install.ini" "Install" "Product"
  ${If} $0 != "${PRODUCT_ID}"
    ClearErrors
    ${GetSize} "$INSTDIR" "/G=0" $0 $1 $2
    ${IfNot} ${Errors}
    ${AndIf} $1 = 0
    ${AndIf} $2 = 0
      Goto done_directory
    ${EndIf}
    Goto bad_directory
  ${EndIf}
  IfFileExists "$INSTDIR\SaruPDF.exe" 0 done_directory
  ClearErrors
  FileOpen $0 "$INSTDIR\SaruPDF.exe" a
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "起動中の SaruPDF をすべて閉じてから、セットアップを再実行してください。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FileClose $0
  Goto done_directory
bad_directory:
  MessageBox MB_OK|MB_ICONSTOP "空のフォルダー、または SaruPDF をインストール済みのフォルダーを選択してください。" /SD IDOK
  SetErrorLevel 2
  Abort
done_directory:
FunctionEnd

Section "SaruPDF 本体（必須）" SEC_APP
  SectionIn RO
  Call ValidateInstallDirectory
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD_DIR}/*"
  WriteINIStr "$INSTDIR\SaruPDF.install.ini" "Install" "Product" "${PRODUCT_ID}"
  WriteINIStr "$INSTDIR\SaruPDF.install.ini" "Install" "Version" "${PRODUCT_VERSION}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\SaruPDF" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "SaruPDF"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "SaruPDF"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" '"$INSTDIR\SaruPDF.exe",0'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
  CreateDirectory "$SMPROGRAMS\SaruPDF"
  CreateShortcut "$SMPROGRAMS\SaruPDF\SaruPDF.lnk" "$INSTDIR\SaruPDF.exe"
  CreateShortcut "$SMPROGRAMS\SaruPDF\PDF の関連付け.lnk" "$INSTDIR\SaruPDF.exe" "--pdf-association"
  CreateShortcut "$SMPROGRAMS\SaruPDF\アンインストール.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "PDF を開くアプリの候補に登録" SEC_ASSOC
  ClearErrors
  ExecWait '"$INSTDIR\SaruPDF.exe" --register-pdf --quiet' $0
  ${If} ${Errors}
  ${OrIf} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "PDF の候補登録に失敗しました。SaruPDF の「設定 → PDF の関連付け」から再実行できます。" /SD IDOK
    SetErrorLevel 3
  ${EndIf}
SectionEnd

Section /o "デスクトップにショートカットを作成" SEC_DESKTOP
  CreateShortcut "$DESKTOP\SaruPDF.lnk" "$INSTDIR\SaruPDF.exe"
  WriteINIStr "$INSTDIR\SaruPDF.install.ini" "Install" "DesktopShortcut" "1"
SectionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "SaruPDF には 64 ビットの Windows が必要です。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/NoAssociation" $1
  ${IfNot} ${Errors}
    SectionSetFlags ${SEC_ASSOC} 0
  ${EndIf}
FunctionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} "PDF Viewer と .NET Runtime をインストールします。WebView2 Runtime は別途必要です。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_ASSOC} "現在のユーザーの「プログラムから開く」と既定アプリの候補に SaruPDF を登録します。既定アプリは Windows の画面で選択します。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "現在のユーザーのデスクトップに SaruPDF のショートカットを作成します。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function ShowAssociationSettings
  Exec '"$INSTDIR\SaruPDF.exe" --pdf-association'
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadINIStr $0 "$INSTDIR\SaruPDF.install.ini" "Install" "Product"
  ${If} $0 != "${PRODUCT_ID}"
    MessageBox MB_OK|MB_ICONSTOP "SaruPDF のインストール先を確認できません。削除を中止します。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  IfFileExists "$INSTDIR\SaruPDF.exe" 0 done_check
  ClearErrors
  FileOpen $0 "$INSTDIR\SaruPDF.exe" a
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "SaruPDF の全ウィンドウを閉じてから、アンインストールを再実行してください。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FileClose $0
done_check:
FunctionEnd

Section "Uninstall"
  IfFileExists "$INSTDIR\SaruPDF.exe" 0 association_done
  ClearErrors
  ExecWait '"$INSTDIR\SaruPDF.exe" --unregister-pdf --quiet' $0
  ${If} ${Errors}
  ${OrIf} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "PDF の候補登録を削除できませんでした。アンインストールを中止します。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
association_done:
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    Delete "$SMPROGRAMS\SaruPDF\SaruPDF.lnk"
    Delete "$SMPROGRAMS\SaruPDF\PDF の関連付け.lnk"
    Delete "$SMPROGRAMS\SaruPDF\アンインストール.lnk"
    RMDir "$SMPROGRAMS\SaruPDF"
    ReadINIStr $1 "$INSTDIR\SaruPDF.install.ini" "Install" "DesktopShortcut"
    ${If} $1 == "1"
      Delete "$DESKTOP\SaruPDF.lnk"
    ${EndIf}
  ${EndIf}
  ReadRegStr $0 HKCU "Software\SaruPDF" "InstallDir"
  ${If} $0 == $INSTDIR
    DeleteRegValue HKCU "Software\SaruPDF" "InstallDir"
    DeleteRegKey /ifempty HKCU "Software\SaruPDF"
  ${EndIf}
  !include "${UNINSTALL_MANIFEST}"
  Delete "$INSTDIR\SaruPDF.install.ini"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
