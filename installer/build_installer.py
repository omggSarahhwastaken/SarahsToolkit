#!/usr/bin/env python3
"""Build SarahsToolkitInstaller_x64.exe.

1. Zips the Sarah's Toolkit source tree (payload).
2. Cross-compiles installer.c to a Windows x64 exe with zig.
3. Appends: [exe][zip payload][u64 LE zip size][b"STKINSTL"].

Usage:
    python3 build_installer.py <repo_root> <zig_binary> <out_exe>
"""
import os
import struct
import subprocess
import sys
import zipfile

MAGIC = b"STKINSTL"


def make_payload(repo_root, skip_files=()):
    names = []
    for dirpath, dirnames, filenames in os.walk(repo_root):
        dirnames[:] = [d for d in dirnames if d not in (".git", "bin", "obj")]
        for fn in filenames:
            # Never embed build leftovers or the installer output itself.
            if fn in skip_files or fn.endswith(".pdb"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, repo_root)
            if rel in skip_files or rel.replace(os.sep, "/") in skip_files:
                continue
            names.append((full, rel))
    names.sort(key=lambda t: t[1])
    buf = bytearray()
    import io
    bio = io.BytesIO()
    with zipfile.ZipFile(bio, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for full, rel in names:
            z.write(full, rel.replace(os.sep, "/"))
    return bio.getvalue(), [r for _, r in names]


def main(argv):
    if len(argv) != 4:
        print(__doc__.strip().splitlines()[-3])
        return 2
    repo_root, zig, out_exe = argv[1], argv[2], argv[3]
    here = os.path.dirname(os.path.abspath(__file__))
    src_c = os.path.join(here, "installer.c")
    exe_tmp = os.path.join(here, "_installer_tmp.exe")

    payload, names = make_payload(repo_root, skip_files={
        "_installer_tmp.exe", "_installer_tmp.pdb",
        os.path.basename(out_exe), "SarahsToolkitInstaller_x64.exe",
    })
    print(f"payload: {len(payload)} bytes, {len(names)} files")
    assert any(n == "SarahsToolkit.sln" for n in names), "solution missing from payload!"
    assert any(n == "src/SarahsToolkit/SarahsToolkit.csproj" for n in names), "csproj missing!"

    cmd = [zig, "cc", "-target", "x86_64-windows-gnu", "-O2",
           src_c, "-o", exe_tmp,
           "-lurlmon", "-lwintrust", "-lcrypt32", "-lole32", "-lshell32", "-luuid"]
    print("compiling:", " ".join(cmd))
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout)
        print(r.stderr)
        return 1
    if r.stderr.strip():
        print("compiler warnings:\n" + r.stderr)

    with open(exe_tmp, "rb") as f:
        exe = f.read()
    os.remove(exe_tmp)
    # zig also emits a .pdb next to the exe; remove it so it never gets
    # committed or embedded.
    pdb_tmp = os.path.splitext(exe_tmp)[0] + ".pdb"
    if os.path.exists(pdb_tmp):
        os.remove(pdb_tmp)

    with open(out_exe, "wb") as f:
        f.write(exe)
        f.write(payload)
        f.write(struct.pack("<Q", len(payload)))
        f.write(MAGIC)

    # verify footer
    with open(out_exe, "rb") as f:
        f.seek(-16, os.SEEK_END)
        footer = f.read(16)
    size = struct.unpack("<Q", footer[:8])[0]
    assert footer[8:] == MAGIC, "magic mismatch"
    assert size == len(payload), "size mismatch"
    total = os.path.getsize(out_exe)
    assert total == len(exe) + len(payload) + 16
    print(f"OK: {out_exe} ({total} bytes, exe={len(exe)} payload={len(payload)})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
