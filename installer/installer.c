/*
 * Sarah's Toolkit - Installer
 *
 * A tiny single-file Windows installer:
 *   1. Detects an installed .NET 8 SDK (registry + `dotnet --list-sdks`).
 *      Skips the SDK install when one is already present.
 *   2. Otherwise downloads the .NET 8 SDK (x64) from Microsoft, verifies
 *      the Authenticode signature (must be "Microsoft Corporation"),
 *      and installs it silently.
 *   3. Extracts the embedded Sarah's Toolkit source, builds it with
 *      `dotnet build`, and installs it to %ProgramFiles%\SarahsToolkit.
 *   4. Creates Start Menu + Desktop shortcuts and an Add/Remove Programs
 *      entry. The installed copy doubles as the uninstaller.
 *
 * The app source is appended to this exe by build_installer.py:
 *   [installer.exe][zip payload][u64 LE zip size]["STKINSTL"]
 *
 * Build (Linux cross-compile):
 *   zig cc -target x86_64-windows-gnu -O2 installer.c -o installer.exe \
 *       -lurlmon -lwintrust -lcrypt32 -lole32 -lshell32 -luuid
 */

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <conio.h>
#include <shlobj.h>
#include <urlmon.h>
#include <wintrust.h>
#include <softpub.h>
#include <wincrypt.h>

#ifndef CERT_NAME_DN_TYPE
#define CERT_NAME_DN_TYPE 6
#endif

#define APP_DISPLAY_NAME    "Sarah's Toolkit"
#define APP_DIR_NAME        "SarahsToolkit"
#define APP_EXE_NAME        "SarahsToolkit.exe"
#define APP_VERSION         "1.0.0"
#define APP_PUBLISHER       "Sarah"
#define INSTALLER_SELF_NAME "SarahsToolkitInstaller_x64.exe"
#define UNINSTALL_KEY       "SarahsToolkit"
#define SDK_DOWNLOAD_URL    "https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe"
#define SDK_TEMP_NAME       "dotnet-sdk-8.0-win-x64.exe"

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

static int FindDotnetExe(char *out, int outLen) {
    char pf[MAX_PATH];
    if (SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_PROGRAM_FILES, NULL, 0, pf))) {
        _snprintf(out, outLen, "%s\\dotnet\\dotnet.exe", pf);
        out[outLen - 1] = 0;
        if (GetFileAttributesA(out) != INVALID_FILE_ATTRIBUTES)
            return 1;
    }
    if (SearchPathA(NULL, "dotnet.exe", NULL, outLen, out, NULL))
        return 1;
    return 0;
}

/* ------------------------------------------------------------------ */
/* .NET 8 SDK detection                                                */
/* ------------------------------------------------------------------ */

static char g_sdkVersion[64] = "";

static int DetectSdkViaRegistry(void) {
    HKEY hKey;
    LONG rc = RegOpenKeyExA(HKEY_LOCAL_MACHINE,
                            "SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\sdk",
                            0, KEY_READ, &hKey);
    DWORD idx;
    char name[256];
    if (rc != ERROR_SUCCESS)
        return 0;
    idx = 0;
    while (1) {
        DWORD nameLen = sizeof(name);
        rc = RegEnumValueA(hKey, idx, name, &nameLen, NULL, NULL, NULL, NULL);
        if (rc != ERROR_SUCCESS)
            break;
        idx++;
        if (nameLen >= 2 && name[0] == '8' && name[1] == '.') {
            strncpy(g_sdkVersion, name, sizeof(g_sdkVersion) - 1);
            g_sdkVersion[sizeof(g_sdkVersion) - 1] = 0;
            RegCloseKey(hKey);
            return 1;
        }
    }
    RegCloseKey(hKey);
    return 0;
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

static int DetectSdkViaCli(void) {
    char dotnet[MAX_PATH], tmp[MAX_PATH], listFile[MAX_PATH], cmd[2048];
    FILE *f;
    char line[256];
    int found = 0;
    if (!FindDotnetExe(dotnet, sizeof(dotnet)))
        return 0;
    GetTempDir(tmp, sizeof(tmp));
    _snprintf(listFile, sizeof(listFile), "%sstk_sdks.txt", tmp);
    listFile[sizeof(listFile) - 1] = 0;
    _snprintf(cmd, sizeof(cmd), "cmd.exe /c \"\"%s\" --list-sdks > \"%s\" 2>&1\"",
              dotnet, listFile);
    cmd[sizeof(cmd) - 1] = 0;
    if (!RunHidden(cmd, 30000, NULL))
        return 0;
    f = fopen(listFile, "r");
    if (!f)
        return 0;
    while (fgets(line, sizeof(line), f)) {
        if (line[0] == '8' && line[1] == '.') {
            char *sp = strchr(line, ' ');
            if (sp) *sp = 0;
            line[strcspn(line, "\r\n")] = 0;
            strncpy(g_sdkVersion, line, sizeof(g_sdkVersion) - 1);
            g_sdkVersion[sizeof(g_sdkVersion) - 1] = 0;
            found = 1;
            break;
        }
    }
    fclose(f);
    DeleteFileA(listFile);
    return found;
}

static int HasDotnet8Sdk(void) {
    g_sdkVersion[0] = 0;
    if (DetectSdkViaRegistry())
        return 1;
    return DetectSdkViaCli();
}

/* ------------------------------------------------------------------ */
/* Download with progress (IBindStatusCallback)                        */
/* ------------------------------------------------------------------ */

typedef struct {
    IBindStatusCallbackVtbl *lpVtbl;
    LONG ref;
} DlCb;

static HRESULT STDMETHODCALLTYPE Dl_QueryInterface(IBindStatusCallback *This,
                                                   REFIID riid, void **ppv) {
    if (IsEqualIID(riid, &IID_IUnknown) ||
        IsEqualIID(riid, &IID_IBindStatusCallback)) {
        *ppv = This;
        This->lpVtbl->AddRef(This);
        return S_OK;
    }
    *ppv = NULL;
    return E_NOINTERFACE;
}

static ULONG STDMETHODCALLTYPE Dl_AddRef(IBindStatusCallback *This) {
    DlCb *cb = (DlCb *)This;
    return (ULONG)InterlockedIncrement(&cb->ref);
}

static ULONG STDMETHODCALLTYPE Dl_Release(IBindStatusCallback *This) {
    DlCb *cb = (DlCb *)This;
    LONG c = InterlockedDecrement(&cb->ref);
    if (c == 0)
        free(cb);
    return (ULONG)c;
}

static HRESULT STDMETHODCALLTYPE Dl_OnStartBinding(IBindStatusCallback *This,
                                                   DWORD dwReserved,
                                                   IBinding *pib) {
    (void)This; (void)dwReserved; (void)pib;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Dl_GetPriority(IBindStatusCallback *This,
                                                LONG *pnPriority) {
    (void)This; (void)pnPriority;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE Dl_OnLowResource(IBindStatusCallback *This,
                                                  DWORD reserved) {
    (void)This; (void)reserved;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Dl_OnProgress(IBindStatusCallback *This,
                                               ULONG ulProgress,
                                               ULONG ulProgressMax,
                                               ULONG ulStatusCode,
                                               LPCWSTR szStatusText) {
    (void)This; (void)ulStatusCode; (void)szStatusText;
    if (ulProgressMax > 0) {
        int pct = (int)((ulProgress * 100ULL) / ulProgressMax);
        printf("\r  Downloading .NET 8 SDK... %3d%%  (%lu / %lu MB)   ",
               pct,
               (unsigned long)(ulProgress / 1048576ULL),
               (unsigned long)(ulProgressMax / 1048576ULL));
        fflush(stdout);
    }
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Dl_OnStopBinding(IBindStatusCallback *This,
                                                  HRESULT hresult,
                                                  LPCWSTR szError) {
    (void)This; (void)hresult; (void)szError;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Dl_GetBindInfo(IBindStatusCallback *This,
                                                DWORD *grfBINDF,
                                                BINDINFO *pbindinfo) {
    (void)This; (void)grfBINDF; (void)pbindinfo;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE Dl_OnDataAvailable(IBindStatusCallback *This,
                                                    DWORD grfBSCF,
                                                    DWORD dwSize,
                                                    FORMATETC *pformatetc,
                                                    STGMEDIUM *pstgmed) {
    (void)This; (void)grfBSCF; (void)dwSize;
    (void)pformatetc; (void)pstgmed;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE Dl_OnObjectAvailable(IBindStatusCallback *This,
                                                      REFIID riid,
                                                      IUnknown *punk) {
    (void)This; (void)riid; (void)punk;
    return E_NOTIMPL;
}

static IBindStatusCallbackVtbl g_dlVtbl = {
    Dl_QueryInterface,
    Dl_AddRef,
    Dl_Release,
    Dl_OnStartBinding,
    Dl_GetPriority,
    Dl_OnLowResource,
    Dl_OnProgress,
    Dl_OnStopBinding,
    Dl_GetBindInfo,
    Dl_OnDataAvailable,
    Dl_OnObjectAvailable
};

static int DownloadFile(const char *url, const char *dest) {
    DlCb *cb = (DlCb *)calloc(1, sizeof(DlCb));
    HRESULT hr;
    HANDLE h;
    LARGE_INTEGER sz;
    if (!cb)
        return 0;
    cb->lpVtbl = &g_dlVtbl;
    cb->ref = 1;
    printf("  Downloading .NET 8 SDK (about 216 MB, this may take a few minutes)...\n");
    hr = URLDownloadToFileA(NULL, url, dest, 0, (IBindStatusCallback *)cb);
    printf("\n");
    ((IBindStatusCallback *)cb)->lpVtbl->Release((IBindStatusCallback *)cb);
    if (FAILED(hr)) {
        printf("  Download failed (0x%08lx). Check your internet connection.\n",
               (unsigned long)hr);
        return 0;
    }
    h = CreateFileA(dest, GENERIC_READ, FILE_SHARE_READ, NULL,
                    OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        printf("  Download failed: file not found after download.\n");
        return 0;
    }
    GetFileSizeEx(h, &sz);
    CloseHandle(h);
    if (sz.QuadPart < 50LL * 1024 * 1024) {
        printf("  Download failed: file is too small (%lld bytes), likely incomplete.\n",
               (long long)sz.QuadPart);
        return 0;
    }
    return 1;
}

/* ------------------------------------------------------------------ */
/* Authenticode signature check: must be signed by Microsoft            */
/* ------------------------------------------------------------------ */

static int VerifyMicrosoftSignature(const char *path) {
    wchar_t wpath[MAX_PATH];
    WINTRUST_FILE_INFO fi;
    WINTRUST_DATA wd;
    GUID act = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    LONG st;
    HCERTSTORE hStore = NULL;
    HCRYPTMSG hMsg = NULL;
    DWORD enc = 0, cont = 0, fmt = 0;
    DWORD len = 0;
    PCMSG_SIGNER_INFO psi = NULL;
    CERT_INFO ci;
    PCCERT_CONTEXT cc = NULL;
    char name[256] = { 0 };
    char subjDN[512] = { 0 };
    char issuerDN[512] = { 0 };
    int ok = 0;

    if (!MultiByteToWideChar(CP_ACP, 0, path, -1, wpath, MAX_PATH))
        return 0;

    memset(&fi, 0, sizeof(fi));
    fi.cbStruct = sizeof(fi);
    fi.pcwszFilePath = wpath;

    memset(&wd, 0, sizeof(wd));
    wd.cbStruct = sizeof(wd);
    wd.dwUIChoice = WTD_UI_NONE;
    wd.fdwRevocationChecks = WTD_REVOKE_NONE;
    wd.dwUnionChoice = WTD_CHOICE_FILE;
    wd.pFile = &fi;

    st = WinVerifyTrust(NULL, &act, &wd);
    wd.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(NULL, &act, &wd);
    if (st != ERROR_SUCCESS) {
        printf("  Signature validation failed (0x%08lx).\n", (unsigned long)st);
        return 0;
    }

    if (!CryptQueryObject(CERT_QUERY_OBJECT_FILE, wpath,
                          CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
                          CERT_QUERY_FORMAT_FLAG_BINARY, 0,
                          &enc, &cont, &fmt, &hStore, &hMsg, NULL)) {
        printf("  Could not read the file signature.\n");
        return 0;
    }

    if (CryptMsgGetParam(hMsg, CMSG_SIGNER_INFO_PARAM, 0, NULL, &len) && len) {
        psi = (PCMSG_SIGNER_INFO)malloc(len);
        if (psi && CryptMsgGetParam(hMsg, CMSG_SIGNER_INFO_PARAM, 0, psi, &len)) {
            memset(&ci, 0, sizeof(ci));
            ci.Issuer = psi->Issuer;
            ci.SerialNumber = psi->SerialNumber;
            cc = CertFindCertificateInStore(hStore,
                                            X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                                            0, CERT_FIND_SUBJECT_CERT, &ci, NULL);
            if (cc) {
                /* Microsoft signs the .NET SDK with a cert whose simple
                 * display name is just ".NET", so check the full subject
                 * and issuer for Microsoft instead of the display name. */
                if (CertGetNameStringA(cc, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0,
                                       NULL, name, sizeof(name)) > 1) {
                    printf("  Signed by: %s\n", name);
                }
                CertGetNameStringA(cc, CERT_NAME_DN_TYPE, 0,
                                   NULL, subjDN, sizeof(subjDN));
                CertGetNameStringA(cc, CERT_NAME_DN_TYPE, CERT_NAME_ISSUER_FLAG,
                                   NULL, issuerDN, sizeof(issuerDN));
                /* Microsoft signs the .NET SDK with a cert whose subject is
                 * just ".NET"; the meaningful check is that it was issued
                 * by Microsoft's own code signing CA (which never issues
                 * to third parties). */
                ok = (strstr(issuerDN, "Microsoft Code Signing") != NULL) ||
                     (strstr(subjDN, "Microsoft") != NULL);
                CertFreeCertificateContext(cc);
            }
        }
        free(psi);
    }
    if (hMsg) CryptMsgClose(hMsg);
    if (hStore) CertCloseStore(hStore, 0);

    if (!ok) {
        printf("  Subject: %s\n", subjDN[0] ? subjDN : "(unknown)");
        printf("  Issuer:  %s\n", issuerDN[0] ? issuerDN : "(unknown)");
        printf("  Signer is not Microsoft. Aborting for safety.\n");
    }
    return ok;
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
    char tmp[MAX_PATH], sdkExe[MAX_PATH];
    char srcDir[MAX_PATH], zipPath[MAX_PATH];
    char dotnet[MAX_PATH], installDir[MAX_PATH];
    char buildOut[MAX_PATH], slnPath[MAX_PATH];
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

    /* ---- Step 1: .NET 8 SDK ------------------------------------- */
    printf("[1/4] Checking for .NET 8 SDK...\n");
    if (HasDotnet8Sdk()) {
        printf("  Found .NET SDK %s - skipping SDK installation.\n\n", g_sdkVersion);
    } else {
        printf("  No .NET 8 SDK found.\n");
        _snprintf(sdkExe, sizeof(sdkExe), "%s%s", tmp, SDK_TEMP_NAME);
        sdkExe[sizeof(sdkExe) - 1] = 0;
        if (!DownloadFile(SDK_DOWNLOAD_URL, sdkExe))
            Fail("Could not download the .NET 8 SDK.");
        printf("  Verifying digital signature...\n");
        if (!VerifyMicrosoftSignature(sdkExe)) {
            DeleteFileA(sdkExe);
            Fail("The downloaded .NET 8 SDK failed signature verification.");
        }
        printf("  Signature OK.\n");
        printf("  Installing .NET 8 SDK (this may take a few minutes)...\n");
        _snprintf(cmd, sizeof(cmd),
                  "\"%s\" /install /quiet /norestart", sdkExe);
        cmd[sizeof(cmd) - 1] = 0;
        if (!RunHidden(cmd, INFINITE, &rc) || rc != 0) {
            DeleteFileA(sdkExe);
            printf("  SDK installer exited with code %lu.\n", (unsigned long)rc);
            Fail("The .NET 8 SDK installation failed.");
        }
        DeleteFileA(sdkExe);
        if (!HasDotnet8Sdk())
            Fail("The .NET 8 SDK was installed but could not be detected.");
        printf("  .NET SDK %s installed successfully.\n\n", g_sdkVersion);
    }

    if (!FindDotnetExe(dotnet, sizeof(dotnet)))
        Fail("Could not locate dotnet.exe after the SDK check.");

    /* ---- Step 2: extract ---------------------------------------- */
    printf("[2/4] Extracting setup files...\n");
    _snprintf(srcDir, sizeof(srcDir), "%sstksrc", tmp);
    srcDir[sizeof(srcDir) - 1] = 0;
    _snprintf(zipPath, sizeof(zipPath), "%sstksrc.zip", tmp);
    zipPath[sizeof(zipPath) - 1] = 0;
    if (!ExtractPayload(selfPath, srcDir, zipPath))
        Fail("Could not extract the setup files.");
    _snprintf(slnPath, sizeof(slnPath), "%s\\SarahsToolkit.sln", srcDir);
    slnPath[sizeof(slnPath) - 1] = 0;
    if (GetFileAttributesA(slnPath) == INVALID_FILE_ATTRIBUTES)
        Fail("Setup files are incomplete (solution not found).");
    printf("  Done.\n\n");

    /* ---- Step 3: build ------------------------------------------ */
    printf("[3/4] Building %s (first build may take a minute)...\n", APP_DISPLAY_NAME);
    _snprintf(cmd, sizeof(cmd),
              "\"%s\" build \"%s\" -c Release --nologo -v minimal",
              dotnet, slnPath);
    cmd[sizeof(cmd) - 1] = 0;
    {
        /* Inherit the console so build progress is visible. */
        char buf[4096];
        STARTUPINFOA si;
        PROCESS_INFORMATION pi;
        DWORD code = 1;
        strncpy(buf, cmd, sizeof(buf) - 1);
        buf[sizeof(buf) - 1] = 0;
        memset(&si, 0, sizeof(si));
        si.cb = sizeof(si);
        memset(&pi, 0, sizeof(pi));
        /* Keep first-run .NET chatter (welcome banner, telemetry notice,
         * dev-cert message) out of the setup screen. These only affect
         * the build process itself, not the user's system settings. */
        SetEnvironmentVariableA("DOTNET_NOLOGO", "1");
        SetEnvironmentVariableA("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
        SetEnvironmentVariableA("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        if (CreateProcessA(NULL, buf, NULL, NULL, TRUE, 0,
                           NULL, NULL, &si, &pi)) {
            WaitForSingleObject(pi.hProcess, INFINITE);
            GetExitCodeProcess(pi.hProcess, &code);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        } else {
            printf("  Could not start the build (%lu).\n",
                   (unsigned long)GetLastError());
        }
        SetEnvironmentVariableA("DOTNET_NOLOGO", NULL);
        SetEnvironmentVariableA("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", NULL);
        SetEnvironmentVariableA("DOTNET_CLI_TELEMETRY_OPTOUT", NULL);
        if (code != 0)
            Fail("The build failed. See the output above for details.");
    }
    _snprintf(buildOut, sizeof(buildOut),
              "%s\\src\\SarahsToolkit\\bin\\Release\\net8.0-windows", srcDir);
    buildOut[sizeof(buildOut) - 1] = 0;
    _snprintf(target, sizeof(target), "%s\\%s", buildOut, APP_EXE_NAME);
    target[sizeof(target) - 1] = 0;
    if (GetFileAttributesA(target) == INVALID_FILE_ATTRIBUTES)
        Fail("Build finished but the app executable was not produced.");
    printf("  Build succeeded.\n\n");

    /* ---- Step 4: install ---------------------------------------- */
    printf("[4/4] Installing to %s...\n", installDir);
    CreateDirectoryA(installDir, NULL);
    _snprintf(cmd, sizeof(cmd),
              "robocopy \"%s\" \"%s\" /E /NFL /NDL /NJH /NJS /NC /NS /R:2 /W:2",
              buildOut, installDir);
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
    _snprintf(cmd, sizeof(cmd), "cmd.exe /c rmdir /s /q \"%s\"", srcDir);
    cmd[sizeof(cmd) - 1] = 0;
    RunHidden(cmd, 60000, NULL);

    printf("==============================================================\n");
    printf("  %s installed successfully!\n", APP_DISPLAY_NAME);
    printf("  Installed to: %s\n", installDir);
    printf("==============================================================\n");
    PauseExit(0);
}

/* ------------------------------------------------------------------ */

int main(int argc, char **argv) {
    char selfPath[MAX_PATH];
    GetModuleFileNameA(NULL, selfPath, sizeof(selfPath));
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
