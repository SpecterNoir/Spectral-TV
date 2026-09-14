#!/usr/bin/env python3
"""Validate a Spectral TV Jellyfin plugin archive before it can be published.

The Jellyfin plugin loader recursively inspects DLL files in the plugin folder. Native
Windows DLLs therefore must never be shipped in the Linux/Synology beta package: on
Linux they can be mistaken for managed assemblies and disable or destabilize plugin
startup. This validator fails the build if unsafe files are present.
"""

from __future__ import annotations

import argparse
import struct
import sys
import zipfile
from pathlib import PurePosixPath


REQUIRED_FILES = {
    "Jellyfin.Plugin.SpectralTV.dll",
    "Microsoft.Data.Sqlite.dll",
    "Microsoft.EntityFrameworkCore.Sqlite.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
    "meta.json",
}


def is_managed_pe(data: bytes) -> bool:
    """Return True when a PE DLL contains a CLR/CLI header."""
    try:
        if len(data) < 0x40 or data[:2] != b"MZ":
            return False

        pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
        if pe_offset + 24 > len(data) or data[pe_offset : pe_offset + 4] != b"PE\0\0":
            return False

        optional_offset = pe_offset + 24
        magic = struct.unpack_from("<H", data, optional_offset)[0]
        if magic == 0x10B:  # PE32
            data_directory_offset = optional_offset + 96
        elif magic == 0x20B:  # PE32+
            data_directory_offset = optional_offset + 112
        else:
            return False

        # IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR / CLR runtime header is entry 14.
        cli_entry_offset = data_directory_offset + (14 * 8)
        if cli_entry_offset + 8 > len(data):
            return False

        cli_rva, cli_size = struct.unpack_from("<II", data, cli_entry_offset)
        return cli_rva != 0 and cli_size != 0
    except (IndexError, struct.error):
        return False


def fail(errors: list[str]) -> int:
    print("Spectral TV package validation FAILED:", file=sys.stderr)
    for error in errors:
        print(f"  - {error}", file=sys.stderr)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", help="Path to the plugin ZIP archive")
    args = parser.parse_args()

    errors: list[str] = []

    with zipfile.ZipFile(args.archive, "r") as zf:
        infos = zf.infolist()
        file_names = {info.filename for info in infos if not info.is_dir()}

        # Prevent malformed archives from writing outside the plugin directory.
        for info in infos:
            path = PurePosixPath(info.filename)
            if path.is_absolute() or ".." in path.parts:
                errors.append(f"unsafe archive path: {info.filename}")

        missing = sorted(REQUIRED_FILES - file_names)
        if missing:
            errors.append("missing required files: " + ", ".join(missing))

        windows_runtime_files = sorted(
            name
            for name in file_names
            if name.lower().startswith("runtimes/win-")
        )
        if windows_runtime_files:
            errors.append(
                "Windows runtime files are forbidden in the Linux/Synology beta package: "
                + ", ".join(windows_runtime_files[:8])
                + (" ..." if len(windows_runtime_files) > 8 else "")
            )

        # This is the exact class of packaging error that broke the first beta:
        # any .dll that Jellyfin sees must be a managed .NET assembly.
        unmanaged_dlls: list[str] = []
        for name in sorted(n for n in file_names if n.lower().endswith(".dll")):
            if not is_managed_pe(zf.read(name)):
                unmanaged_dlls.append(name)

        if unmanaged_dlls:
            errors.append(
                "native/unmanaged DLLs detected; Jellyfin may try to load these as assemblies: "
                + ", ".join(unmanaged_dlls)
            )

        linux_sqlite = sorted(
            name
            for name in file_names
            if name.startswith("runtimes/linux-")
            and name.endswith("/native/libe_sqlite3.so")
        )
        if not linux_sqlite:
            errors.append("no Linux native SQLite runtime was packaged")

    if errors:
        return fail(errors)

    print("Spectral TV package validation passed.")
    print(f"  Managed DLLs only: yes")
    print(f"  Windows runtime files: none")
    print(f"  Linux SQLite runtimes: {len(linux_sqlite)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
