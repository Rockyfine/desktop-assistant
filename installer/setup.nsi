Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetOverwrite on
ManifestDPIAware true

!ifndef APP_ID
!define APP_ID "DesktopAssistant.56ec080e"
!endif
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
!include "MUI2.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

Name "${PRODUCT_NAME}"
OutFile "${OUTPUT_EXE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_ID}"
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" ""

!define MUI_ICON "${BRAND_ICON}"
!define MUI_UNICON "${BRAND_ICON}"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Var Updating
Var Maintenance

!macro AcquireMaintenance
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\${APP_ID}.maintenance") p .r0 ?e'
    Pop $1
    StrCpy $Maintenance $0
    ${If} $0 == 0
    ${OrIf} $1 == 183
        MessageBox MB_OK|MB_ICONEXCLAMATION "另一个安装或更新操作正在运行。"
        SetErrorLevel 5
        Abort
    ${EndIf}
!macroend

!macro RequireClosed
    StrCpy $2 240
    ${Do}
        System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\${APP_ID}.running") p .r0'
        ${IfThen} $0 == 0 ${|} ${ExitDo} ${|}
        System::Call 'kernel32::CloseHandle(p r0)'
        ${If} $Updating != 1
        ${OrIf} $2 == 0
            MessageBox MB_OK|MB_ICONEXCLAMATION "请先从托盘退出 ${PRODUCT_NAME}，然后重新运行安装程序。" /SD IDOK
            SetErrorLevel 5
            Abort
        ${EndIf}
        Sleep 250
        IntOp $2 $2 - 1
    ${Loop}
!macroend

Function .onInit
    ${IfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "此版本需要 64 位 Windows。" /SD IDOK
        SetErrorLevel 5
        Abort
    ${EndIf}
    ${GetParameters} $0
    ClearErrors
    ${GetOptions} $0 "/UPDATE" $1
    ${IfNot} ${Errors}
        StrCpy $Updating 1
        SetSilent silent
    ${EndIf}
    !insertmacro AcquireMaintenance
    !insertmacro RequireClosed
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 != ""
        StrCpy $INSTDIR $0
    ${ElseIf} $Updating == 1
        MessageBox MB_OK|MB_ICONSTOP "找不到已注册的安装。请从发布页运行普通安装。"
        SetErrorLevel 5
        Abort
    ${EndIf}
FunctionEnd

Function .onInstSuccess
    ${If} $Updating == 1
        System::Call 'kernel32::CloseHandle(p $Maintenance)'
        Exec '"$INSTDIR\app\DesktopAssistant.exe"'
    ${EndIf}
FunctionEnd

Function un.onInit
    !insertmacro AcquireMaintenance
    !insertmacro RequireClosed
FunctionEnd
Section "安装"
    SetOutPath "$INSTDIR"
    ; Recover the previous app if an earlier run stopped between the two moves.
    IfFileExists "$INSTDIR\app\*.*" recover_done
    IfFileExists "$INSTDIR\app.previous\*.*" 0 recover_done
    ClearErrors
    Rename "$INSTDIR\app.previous" "$INSTDIR\app"
    IfErrors install_failed
recover_done:
    RMDir /r "$INSTDIR\app.incoming"
    RMDir /r "$INSTDIR\app.previous"
    IfFileExists "$INSTDIR\app.incoming\*.*" install_failed
    IfFileExists "$INSTDIR\app.previous\*.*" install_failed

    ClearErrors
    SetOutPath "$INSTDIR\app.incoming"
    File /r "${PAYLOAD}\*"
    IfErrors install_failed
    IfFileExists "$INSTDIR\app.incoming\DesktopAssistant.exe" 0 install_failed
    SetOutPath "$INSTDIR"
    IfFileExists "$INSTDIR\app\*.*" 0 activate
    ClearErrors
    Rename "$INSTDIR\app" "$INSTDIR\app.previous"
    IfErrors install_failed
activate:
    ClearErrors
    Rename "$INSTDIR\app.incoming" "$INSTDIR\app"
    IfErrors restore_previous
    WriteUninstaller "$INSTDIR\Uninstall.exe"

    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "Shortcut"
    StrCmp $0 "" +2
    Delete "$SMPROGRAMS\$0.lnk"
    CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}.lnk" "$INSTDIR\app\DesktopAssistant.exe" "" "$INSTDIR\app\assistant.ico"

    WriteRegStr HKCU "${UNINSTALL_KEY}" "Shortcut" "${PRODUCT_NAME}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_NAME}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\app\assistant.ico"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    RMDir /r "$INSTDIR\app.previous"
    Goto install_done
restore_previous:
    Rename "$INSTDIR\app.previous" "$INSTDIR\app"
install_failed:
    SetOutPath "$INSTDIR"
    RMDir /r "$INSTDIR\app.incoming"
    MessageBox MB_OK|MB_ICONSTOP "无法替换程序文件。请关闭其他占用程序，再从发布页重试。"
    SetErrorLevel 10
    Abort
install_done:
SectionEnd

Section "Uninstall"
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "Shortcut"
    StrCmp $0 "" +2
    Delete "$SMPROGRAMS\$0.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    !include "${UNINSTALL_FILES}"
    RMDir /r "$INSTDIR\app.incoming"
    RMDir /r "$INSTDIR\app.previous"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
SectionEnd
