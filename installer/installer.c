/*
 * Sarah's Toolkit - Installer
 *
 * A tiny single-file Windows installer for the self-contained app:
 *   1. Extracts the embedded application files (self-contained publish
 *      output: the app plus its private .NET runtime — no SDK, no build,
 *      no downloads needed on the user's PC).
 *   2. Installs them to %ProgramFiles%\SarahsToolkit.
 *   3. Creates Start Menu + Desktop shortcuts and an Add/Remove Programs
 *      entry. The installed copy doubles as the uninstaller.
 *
 * The published app is appended to this exe by build_installer.py:
 *   [installer.exe][zip payload][u64 LE zip size]["STKINSTL"]
 *
 * Build (Linux cross-compile):
 *   zig cc -target x86_64-windows-gnu -O2 installer.c -o installer.exe \
 *       -lole32 -lshell32 -luuid
 */

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <conio.h>
#include <shlobj.h>

#define APP_DISPLAY_NAME    "Sarah's Toolkit"
#define APP_DIR_NAME        "SarahsToolkit"
#define APP_EXE_NAME        "SarahsToolkit.exe"
#ifndef APP_VERSION
#define APP_VERSION         "1.0.0"
#endif
#define APP_PUBLISHER       "Sarah"
#define INSTALLER_SELF_NAME "SarahsToolkitInstaller_x64.exe"
#define UNINSTALL_KEY       "SarahsToolkit"

#define PAYLOAD_MAGIC       "STKINSTL"   /* exactly 8 bytes */
#define PAYLOAD_MAGIC_LEN   8
#define PAYLOAD_FOOTER_LEN  16          /* u64 LE size + magic */

static int g_noPause = 0;

static void PauseExit(int code) {
    if (!g_noPause) {
        printf("\nPress any key to exit...\n");
        _getch();
    }
    ExitProcess(code);
}

static void Fail(const char *msg) {
    printf("\nERROR: %s\n", msg);
    PauseExit(1);
}

/* ------------------------------------------------------------------ */
/* OS + elevation                                                      */
/* ------------------------------------------------------------------ */

typedef LONG (WINAPI *RtlGetVersionFn)(PRTL_OSVERSIONINFOW);

static int WindowsMajorVersion(void) {
    HMODULE ntdll = GetModuleHandleA("ntdll.dll");
    RtlGetVersionFn pFn;
    RTL_OSVERSIONINFOW vi;
    if (!ntdll) return 0;
    pFn = (RtlGetVersionFn)GetProcAddress(ntdll, "RtlGetVersion");
    if (!pFn) return 0;
    vi.dwOSVersionInfoSize = sizeof(vi);
    if (pFn(&vi) != 0) return 0;
    return (int)vi.dwMajorVersion;
}

static int IsElevated(void) {
    BOOL elevated = FALSE;
    HANDLE hToken = NULL;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken)) {
        TOKEN_ELEVATION te;
        DWORD len = sizeof(te);
        if (GetTokenInformation(hToken, TokenElevation, &te, sizeof(te), &len))
            elevated = te.TokenIsElevated;
        CloseHandle(hToken);
    }
    return elevated ? 1 : 0;
}

static void RelaunchElevated(const char *selfPath, const char *args) {
    SHELLEXECUTEINFOA sei;
    memset(&sei, 0, sizeof(sei));
    sei.cbSize = sizeof(sei);
    sei.lpVerb = "runas";
    sei.lpFile = selfPath;
    sei.lpParameters = args;
    sei.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExA(&sei)) {
        DWORD err = GetLastError();
        if (err == ERROR_CANCELLED)
            printf("Administrator privileges are required. Setup cancelled.\n");
        else
            printf("Could not elevate (error %lu).\n", (unsigned long)err);
        PauseExit(1);
    }
    ExitProcess(0);
}

/* ------------------------------------------------------------------ */
/* Paths                                                               */
/* ------------------------------------------------------------------ */

static void GetTempDir(char *out, int outLen) {
    DWORD n = GetTempPathA(outLen, out);
    if (n == 0 || n >= (DWORD)outLen) {
        strncpy(out, "C:\\Windows\\Temp\\", outLen - 1);
        out[outLen - 1] = 0;
    }
}

static void GetInstallDir(char *out, int outLen) {
    char pf[MAX_PATH] = "C:\\Program Files";
    SHGetFolderPathA(NULL, CSIDL_PROGRAM_FILES, NULL, 0, pf);
    _snprintf(out, outLen, "%s\\%s", pf, APP_DIR_NAME);
    out[outLen - 1] = 0;
}

static int RunHidden(const char *cmdline, DWORD timeoutMs, DWORD *exitCodeOut) {
    char buf[4096];
    STARTUPINFOA si;
    PROCESS_INFORMATION pi;
    DWORD code = 0;
    strncpy(buf, cmdline, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = 0;
    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    memset(&pi, 0, sizeof(pi));
    if (!CreateProcessA(NULL, buf, NULL, NULL, FALSE, CREATE_NO_WINDOW,
                        NULL, NULL, &si, &pi))
        return 0;
    if (WaitForSingleObject(pi.hProcess, timeoutMs) == WAIT_TIMEOUT)
        TerminateProcess(pi.hProcess, 1);
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    if (exitCodeOut) *exitCodeOut = code;
    return 1;
}
/* ------------------------------------------------------------------ */
/* Embedded payload extraction                                         */
/* ------------------------------------------------------------------ */

static int ExtractPayload(const char *selfPath, const char *destDir,
                          const char *zipOut) {
    HANDLE h, out;
    LARGE_INTEGER fsize, off;
    unsigned char footer[PAYLOAD_FOOTER_LEN];
    unsigned long long zsize = 0, left;
    DWORD rd = 0;
    char buf[65536];
    char cmd[2048];
    int ok = 1;
    DWORD rc;

    h = CreateFileA(selfPath, GENERIC_READ, FILE_SHARE_READ, NULL,
                    OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        printf("  Could not open installer file.\n");
        return 0;
    }
    GetFileSizeEx(h, &fsize);
    if (fsize.QuadPart < PAYLOAD_FOOTER_LEN) {
        printf("  Installer payload is missing.\n");
        CloseHandle(h);
        return 0;
    }
    off.QuadPart = fsize.QuadPart - PAYLOAD_FOOTER_LEN;
    SetFilePointerEx(h, off, NULL, FILE_BEGIN);
    if (!ReadFile(h, footer, PAYLOAD_FOOTER_LEN, &rd, NULL) ||
        rd != PAYLOAD_FOOTER_LEN ||
        memcmp(footer + 8, PAYLOAD_MAGIC, PAYLOAD_MAGIC_LEN) != 0) {
        printf("  Installer payload is corrupt.\n");
        CloseHandle(h);
        return 0;
    }
    memcpy(&zsize, footer, 8);   /* little-endian */
    if (zsize == 0 || zsize > (unsigned long long)(fsize.QuadPart)) {
        printf("  Installer payload size is invalid.\n");
        CloseHandle(h);
        return 0;
    }

    off.QuadPart = fsize.QuadPart - PAYLOAD_FOOTER_LEN - (LONGLONG)zsize;
    SetFilePointerEx(h, off, NULL, FILE_BEGIN);

    out = CreateFileA(zipOut, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (out == INVALID_HANDLE_VALUE) {
        printf("  Could not write temporary file.\n");
        CloseHandle(h);
        return 0;
    }
    left = zsize;
    while (left > 0) {
        DWORD want = left > sizeof(buf) ? sizeof(buf) : (DWORD)left;
        DWORD got = 0, wrote = 0;
        if (!ReadFile(h, buf, want, &got, NULL) || got == 0) { ok = 0; break; }
        if (!WriteFile(out, buf, got, &wrote, NULL) || wrote != got) { ok = 0; break; }
        left -= got;
    }
    CloseHandle(out);
    CloseHandle(h);
    if (!ok) {
        printf("  Failed to extract setup files.\n");
        return 0;
    }

    /* Expand with PowerShell (built into Windows 10/11). */
    _snprintf(cmd, sizeof(cmd),
              "powershell -NoProfile -ExecutionPolicy Bypass -Command "
              "\"Expand-Archive -LiteralPath '%s' -DestinationPath '%s' -Force\"",
              zipOut, destDir);
    cmd[sizeof(cmd) - 1] = 0;
    rc = 0;
    if (!RunHidden(cmd, 120000, &rc) || rc != 0) {
        printf("  Failed to expand setup files.\n");
        return 0;
    }
    return 1;
}

/* ------------------------------------------------------------------ */
/* Shortcuts + Add/Remove Programs entry                               */
/* ------------------------------------------------------------------ */

static int CreateShortcut(const char *target, const char *workDir,
                          const char *linkPath, const char *desc) {
    IShellLinkA *psl = NULL;
    IPersistFile *ppf = NULL;
    wchar_t wlink[MAX_PATH];
    int ok = 0;
    CoInitialize(NULL);
    if (SUCCEEDED(CoCreateInstance(&CLSID_ShellLink, NULL, CLSCTX_INPROC_SERVER,
                                   &IID_IShellLinkA, (void **)&psl))) {
        psl->lpVtbl->SetPath(psl, target);
        psl->lpVtbl->SetWorkingDirectory(psl, workDir);
        psl->lpVtbl->SetDescription(psl, desc);
        psl->lpVtbl->SetIconLocation(psl, target, 0);
        if (SUCCEEDED(psl->lpVtbl->QueryInterface(psl, &IID_IPersistFile,
                                                  (void **)&ppf))) {
            MultiByteToWideChar(CP_ACP, 0, linkPath, -1, wlink, MAX_PATH);
            if (SUCCEEDED(ppf->lpVtbl->Save(ppf, wlink, TRUE)))
                ok = 1;
            ppf->lpVtbl->Release(ppf);
        }
        psl->lpVtbl->Release(psl);
    }
    CoUninitialize();
    return ok;
}

static void WriteUninstallKey(const char *installDir, const char *selfCopy) {
    HKEY hKey;
    char sub[256];
    char uninstallStr[MAX_PATH + 16];
    char iconPath[MAX_PATH];
    DWORD dw = 1;
    _snprintf(sub, sizeof(sub),
              "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\%s",
              UNINSTALL_KEY);
    sub[sizeof(sub) - 1] = 0;
    if (RegCreateKeyExA(HKEY_LOCAL_MACHINE, sub, 0, NULL, 0,
                        KEY_WRITE, NULL, &hKey, NULL) != ERROR_SUCCESS)
        return;
    _snprintf(uninstallStr, sizeof(uninstallStr), "\"%s\" /uninstall", selfCopy);
    uninstallStr[sizeof(uninstallStr) - 1] = 0;
    _snprintf(iconPath, sizeof(iconPath), "%s\\%s", installDir, APP_EXE_NAME);
    iconPath[sizeof(iconPath) - 1] = 0;
    RegSetValueExA(hKey, "DisplayName", 0, REG_SZ,
                   (const BYTE *)APP_DISPLAY_NAME,
                   (DWORD)strlen(APP_DISPLAY_NAME) + 1);
    RegSetValueExA(hKey, "DisplayVersion", 0, REG_SZ,
                   (const BYTE *)APP_VERSION,
                   (DWORD)strlen(APP_VERSION) + 1);
    RegSetValueExA(hKey, "Publisher", 0, REG_SZ,
                   (const BYTE *)APP_PUBLISHER,
                   (DWORD)strlen(APP_PUBLISHER) + 1);
    RegSetValueExA(hKey, "InstallLocation", 0, REG_SZ,
                   (const BYTE *)installDir, (DWORD)strlen(installDir) + 1);
    RegSetValueExA(hKey, "UninstallString", 0, REG_SZ,
                   (const BYTE *)uninstallStr, (DWORD)strlen(uninstallStr) + 1);
    RegSetValueExA(hKey, "DisplayIcon", 0, REG_SZ,
                   (const BYTE *)iconPath, (DWORD)strlen(iconPath) + 1);
    RegSetValueExA(hKey, "NoModify", 0, REG_DWORD,
                   (const BYTE *)&dw, sizeof(dw));
    RegSetValueExA(hKey, "NoRepair", 0, REG_DWORD,
                   (const BYTE *)&dw, sizeof(dw));
    RegCloseKey(hKey);
}

/* ------------------------------------------------------------------ */
/* Uninstall                                                           */
/* ------------------------------------------------------------------ */

static void DirNameOf(const char *path, char *out, int outLen) {
    strncpy(out, path, outLen - 1);
    out[outLen - 1] = 0;
    {
        char *p = strrchr(out, '\\');
        if (p) *p = 0;
    }
}

static int StrCaseEq(const char *a, const char *b) {
    while (*a && *b) {
        char ca = *a, cb = *b;
        if (ca >= 'A' && ca <= 'Z') ca += 32;
        if (cb >= 'A' && cb <= 'Z') cb += 32;
        if (ca != cb) return 0;
        a++; b++;
    }
    return *a == *b;
}

static void DoUninstallGo(const char *selfPath) {
    char installDir[MAX_PATH], selfDir[MAX_PATH];
    char startMenu[MAX_PATH], desktop[MAX_PATH], link[MAX_PATH];
    char sub[256], cmd[2048];
    GetInstallDir(installDir, sizeof(installDir));
    DirNameOf(selfPath, selfDir, sizeof(selfDir));

    printf("Uninstalling %s...\n", APP_DISPLAY_NAME);

    /* Shortcuts */
    if (SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_COMMON_PROGRAMS, NULL, 0, startMenu))) {
        _snprintf(link, sizeof(link), "%s\\%s.lnk", startMenu, APP_DISPLAY_NAME);
        link[sizeof(link) - 1] = 0;
        DeleteFileA(link);
    }
    if (SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_COMMON_DESKTOPDIRECTORY, NULL, 0, desktop))) {
        _snprintf(link, sizeof(link), "%s\\%s.lnk", desktop, APP_DISPLAY_NAME);
        link[sizeof(link) - 1] = 0;
        DeleteFileA(link);
    }

    /* Registry */
    _snprintf(sub, sizeof(sub),
              "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\%s",
              UNINSTALL_KEY);
    sub[sizeof(sub) - 1] = 0;
    RegDeleteKeyA(HKEY_LOCAL_MACHINE, sub);

    /* Install directory (only if we are NOT running from inside it) */
    if (!StrCaseEq(selfDir, installDir)) {
        _snprintf(cmd, sizeof(cmd), "cmd.exe /c rmdir /s /q \"%s\"", installDir);
        cmd[sizeof(cmd) - 1] = 0;
        RunHidden(cmd, 60000, NULL);
    } else {
        printf("  (running from the install folder; files left in place)\n");
    }

    printf("Done.\n");

    /* Delete this temp copy of the uninstaller after exit. */
    {
        char args[2048];
        _snprintf(args, sizeof(args),
                  "/c ping -n 3 127.0.0.1 >nul & del \"%s\"", selfPath);
        args[sizeof(args) - 1] = 0;
        ShellExecuteA(NULL, "open", "cmd.exe", args, NULL, SW_HIDE);
    }
    ExitProcess(0);
}

static void DoUninstall(const char *selfPath) {
    char installDir[MAX_PATH], selfDir[MAX_PATH];
    char tmp[MAX_PATH], tmpCopy[MAX_PATH], cmd[2048];
    if (!IsElevated())
        RelaunchElevated(selfPath, "/uninstall");
    GetInstallDir(installDir, sizeof(installDir));
    DirNameOf(selfPath, selfDir, sizeof(selfDir));
    if (StrCaseEq(selfDir, installDir)) {
        /* Running from the install dir: copy to temp and relaunch. */
        GetTempDir(tmp, sizeof(tmp));
        _snprintf(tmpCopy, sizeof(tmpCopy), "%sSarahsToolkitUninstall.exe", tmp);
        tmpCopy[sizeof(tmpCopy) - 1] = 0;
        if (!CopyFileA(selfPath, tmpCopy, FALSE))
            Fail("Could not stage the uninstaller.");
        _snprintf(cmd, sizeof(cmd), "\"%s\" /uninstall-go", tmpCopy);
        cmd[sizeof(cmd) - 1] = 0;
        RunHidden(cmd, INFINITE, NULL);
        ExitProcess(0);
    }
    g_noPause = 1;
    DoUninstallGo(selfPath);
}

/* ------------------------------------------------------------------ */
/* Install                                                             */
/* ------------------------------------------------------------------ */

static void DoInstall(const char *selfPath) {
    char tmp[MAX_PATH];
    char appDir[MAX_PATH], zipPath[MAX_PATH];
    char installDir[MAX_PATH];
    char cmd[4096], selfCopy[MAX_PATH];
    char startMenu[MAX_PATH], desktop[MAX_PATH], link[MAX_PATH], target[MAX_PATH];
    DWORD rc;

    SetConsoleTitleA(APP_DISPLAY_NAME " Setup");
    printf("==============================================================\n");
    printf("  %s Setup\n", APP_DISPLAY_NAME);
    printf("==============================================================\n\n");

    if (WindowsMajorVersion() < 10)
        Fail("Windows 10 or later is required.");

    if (!IsElevated()) {
        printf("Requesting administrator privileges...\n\n");
        RelaunchElevated(selfPath, NULL);
    }

    GetTempDir(tmp, sizeof(tmp));
    GetInstallDir(installDir, sizeof(installDir));

    /* ---- Step 1: extract ---------------------------------------- */
    printf("[1/2] Extracting application files...\n");
    _snprintf(appDir, sizeof(appDir), "%sstkapp", tmp);
    appDir[sizeof(appDir) - 1] = 0;
    _snprintf(zipPath, sizeof(zipPath), "%sstkapp.zip", tmp);
    zipPath[sizeof(zipPath) - 1] = 0;
    if (!ExtractPayload(selfPath, appDir, zipPath))
        Fail("Could not extract the application files.");
    _snprintf(target, sizeof(target), "%s\\%s", appDir, APP_EXE_NAME);
    target[sizeof(target) - 1] = 0;
    if (GetFileAttributesA(target) == INVALID_FILE_ATTRIBUTES)
        Fail("Setup files are incomplete (application not found).");
    printf("  Done.\n\n");

    /* ---- Step 2: install ---------------------------------------- */
    printf("[2/2] Installing to %s...\n", installDir);
    /* Clean-install semantics: remove the old install dir first so an
     * update can never leave stale files behind. Never wipe the folder
     * we are currently running from. */
    {
        char selfDir[MAX_PATH];
        DirNameOf(selfPath, selfDir, sizeof(selfDir));
        if (!StrCaseEq(selfDir, installDir)) {
            _snprintf(cmd, sizeof(cmd),
                      "cmd.exe /c rmdir /s /q \"%s\"", installDir);
            cmd[sizeof(cmd) - 1] = 0;
            RunHidden(cmd, 60000, NULL);
        }
    }
    CreateDirectoryA(installDir, NULL);
    _snprintf(cmd, sizeof(cmd),
              "robocopy \"%s\" \"%s\" /E /NFL /NDL /NJH /NJS /NC /NS /R:2 /W:2 /XF *.pdb",
              appDir, installDir);
    cmd[sizeof(cmd) - 1] = 0;
    if (!RunHidden(cmd, 300000, &rc) || rc >= 8) {
        printf("  File copy failed (robocopy code %lu).\n", (unsigned long)rc);
        Fail("Could not copy the application files.");
    }

    /* Keep a copy of the installer for Add/Remove Programs. */
    _snprintf(selfCopy, sizeof(selfCopy), "%s\\%s", installDir, INSTALLER_SELF_NAME);
    selfCopy[sizeof(selfCopy) - 1] = 0;
    CopyFileA(selfPath, selfCopy, FALSE);

    /* Shortcuts */
    _snprintf(target, sizeof(target), "%s\\%s", installDir, APP_EXE_NAME);
    target[sizeof(target) - 1] = 0;
    if (SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_COMMON_PROGRAMS, NULL, 0, startMenu))) {
        _snprintf(link, sizeof(link), "%s\\%s.lnk", startMenu, APP_DISPLAY_NAME);
        link[sizeof(link) - 1] = 0;
        if (CreateShortcut(target, installDir, link, APP_DISPLAY_NAME))
            printf("  Start Menu shortcut created.\n");
    }
    if (SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_COMMON_DESKTOPDIRECTORY, NULL, 0, desktop))) {
        _snprintf(link, sizeof(link), "%s\\%s.lnk", desktop, APP_DISPLAY_NAME);
        link[sizeof(link) - 1] = 0;
        if (CreateShortcut(target, installDir, link, APP_DISPLAY_NAME))
            printf("  Desktop shortcut created.\n");
    }

    WriteUninstallKey(installDir, selfCopy);
    printf("  Registered in Add/Remove Programs.\n\n");

    /* ---- Cleanup -------------------------------------------------- */
    DeleteFileA(zipPath);
    _snprintf(cmd, sizeof(cmd), "cmd.exe /c rmdir /s /q \"%s\"", appDir);
    cmd[sizeof(cmd) - 1] = 0;
    RunHidden(cmd, 60000, NULL);

    printf("==============================================================\n");
    printf("  %s installed successfully!\n", APP_DISPLAY_NAME);
    printf("  Installed to: %s\n", installDir);
    printf("==============================================================\n");
    /* Auto-launch the app so an update flows straight back into it. */
    printf("  Launching %s...\n", APP_DISPLAY_NAME);
    ShellExecuteA(NULL, "open", target, NULL, installDir, SW_SHOWNORMAL);
    PauseExit(0);
}

/* ------------------------------------------------------------------ */

int main(int argc, char **argv) {
    char selfPath[MAX_PATH];
    GetModuleFileNameA(NULL, selfPath, sizeof(selfPath));
    /* Update mode: the app's updater saves the installer as
     * SarahsToolkitSetup_update.exe. In that case, launch the app when
     * done and close this window automatically (no "press any key"). */
    {
        char lower[MAX_PATH];
        strncpy(lower, selfPath, sizeof(lower) - 1);
        lower[sizeof(lower) - 1] = 0;
        for (char *p = lower; *p; p++)
            if (*p >= 'A' && *p <= 'Z') *p += 32;
        if (strstr(lower, "_update") != NULL)
            g_noPause = 1;
    }
    if (argc > 1 && (strcmp(argv[1], "/uninstall") == 0 ||
                     strcmp(argv[1], "-uninstall") == 0)) {
        DoUninstall(selfPath);
        return 0;
    }
    if (argc > 1 && strcmp(argv[1], "/uninstall-go") == 0) {
        if (!IsElevated())
            RelaunchElevated(selfPath, "/uninstall-go");
        g_noPause = 1;
        DoUninstallGo(selfPath);
        return 0;
    }
    DoInstall(selfPath);
    return 0;
}
