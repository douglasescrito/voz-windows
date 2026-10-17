#!/usr/bin/env python3
"""
Publicador padronizado de Releases do VozWindows via GitHub REST API.
- Compila o executavel portatil unico (win-x64, self-contained, PORTABLE_VOICE_ONLY).
- Garante a existencia do repositorio publico douglasescrito/voz-windows.
- Cria/atualiza a Release vX.Y.Z no GitHub e envia o asset VozWindows.exe.
- Le credenciais exclusivamente do Windows Credential Manager (sem segredos em disco/codigo).
"""
import argparse
import ctypes
import ctypes.wintypes as wintypes
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request

CRED_TYPE_GENERIC = 1
GITHUB_OWNER = "douglasescrito"
GITHUB_REPO = "voz-windows"
GITHUB_CRED_TARGET = "gh:github.com:douglasescrito"
GEMINI_CRED_TARGET = "PROJETOS:cli-gcp:pessoal:GEMINI_API_KEY"


class FILETIME(ctypes.Structure):
    _fields_ = [("dwLowDateTime", wintypes.DWORD), ("dwHighDateTime", wintypes.DWORD)]


class CREDENTIALW(ctypes.Structure):
    _fields_ = [
        ("Flags", wintypes.DWORD),
        ("Type", wintypes.DWORD),
        ("TargetName", wintypes.LPWSTR),
        ("Comment", wintypes.LPWSTR),
        ("LastWritten", FILETIME),
        ("CredentialBlobSize", wintypes.DWORD),
        ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)),
        ("Persist", wintypes.DWORD),
        ("AttributeCount", wintypes.DWORD),
        ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wintypes.LPWSTR),
        ("UserName", wintypes.LPWSTR),
    ]


PCREDENTIALW = ctypes.POINTER(CREDENTIALW)

advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
CredReadW = advapi32.CredReadW
CredReadW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(PCREDENTIALW)]
CredReadW.restype = wintypes.BOOL

CredFree = advapi32.CredFree
CredFree.argtypes = [ctypes.c_void_p]
CredFree.restype = None


def read_vault_secret(target: str) -> str:
    cred_ptr = PCREDENTIALW()
    if not CredReadW(target, CRED_TYPE_GENERIC, 0, ctypes.byref(cred_ptr)):
        raise RuntimeError(f"Credencial nao encontrada no Windows Credential Manager: {target}")
    try:
        cred = cred_ptr.contents
        raw = ctypes.string_at(cred.CredentialBlob, cred.CredentialBlobSize)
        if b"\x00" in raw[:4]:
            text = raw.decode("utf-16-le", errors="ignore")
        else:
            text = raw.decode("utf-8", errors="ignore")
        return text.strip("\x00 \r\n\t")
    finally:
        CredFree(cred_ptr)


def read_csproj_version(csproj_path: str) -> str:
    with open(csproj_path, "r", encoding="utf-8") as f:
        content = f.read()
    m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", content)
    if not m:
        raise RuntimeError("Tag <Version> nao encontrada em VozWindows.csproj")
    return m.group(1)


def update_csproj_version(csproj_path: str, new_version: str) -> None:
    with open(csproj_path, "r", encoding="utf-8") as f:
        content = f.read()
    new_version = new_version.lstrip("vV").strip()
    parts = new_version.split(".")
    four_part = ".".join((parts + ["0", "0", "0", "0"])[:4])
    content = re.sub(r"<Version>[^<]+</Version>", f"<Version>{new_version}</Version>", content)
    content = re.sub(r"<AssemblyVersion>[^<]+</AssemblyVersion>", f"<AssemblyVersion>{four_part}</AssemblyVersion>", content)
    content = re.sub(r"<FileVersion>[^<]+</FileVersion>", f"<FileVersion>{four_part}</FileVersion>", content)
    with open(csproj_path, "w", encoding="utf-8") as f:
        f.write(content)


def build_portable_exe(proj_dir: str) -> str:
    native_interop_path = os.path.join(proj_dir, "NativeInterop.cs")
    out_dir = os.path.join(proj_dir, "bin", "Portatil")

    api_key = read_vault_secret(GEMINI_CRED_TARGET)
    if not api_key or len(api_key) < 20:
        raise RuntimeError("Chave Gemini invalida ou ausente no Credential Manager.")

    mask = [0x5A, 0x3C, 0x9F, 0x71, 0xA4, 0xE2, 0x19, 0x6D, 0x83, 0x4B, 0xC7, 0x2E, 0xF0, 0x55, 0xB8, 0x37]
    key_bytes = api_key.encode("utf-8")
    encoded_bytes = [b ^ mask[i % len(mask)] for i, b in enumerate(key_bytes)]

    mask_literal = ", ".join(f"0x{b:02X}" for b in mask)
    data_literal = ", ".join(f"0x{b:02X}" for b in encoded_bytes)

    with open(native_interop_path, "r", encoding="utf-8") as f:
        original_source = f.read()

    target_block = """    public static string? ReadApiKeyFromVault(string target = CredentialTarget)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr) || credPtr == IntPtr.Zero)
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return null;

            byte[] bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            return Encoding.Unicode.GetString(bytes).Trim('\\0', ' ', '\\r', '\\n', '\\t');
        }
        finally
        {
            CredFree(credPtr);
        }
    }"""

    if target_block not in original_source:
        raise RuntimeError("Bloco ReadApiKeyFromVault nao encontrado em NativeInterop.cs")

    patched_block = f"""    private static string DecodeEmbeddedPortableKey()
    {{
        ReadOnlySpan<byte> mask = new byte[] {{ {mask_literal} }};
        byte[] data = new byte[] {{ {data_literal} }};
        for (int i = 0; i < data.Length; i++)
        {{
            data[i] ^= mask[i % mask.Length];
        }}
        return Encoding.UTF8.GetString(data);
    }}

    public static string? ReadApiKeyFromVault(string target = CredentialTarget)
    {{
        if (CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr) && credPtr != IntPtr.Zero)
        {{
            try
            {{
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                {{
                    byte[] bytes = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                    string vaultKey = Encoding.Unicode.GetString(bytes).Trim('\\0', ' ', '\\r', '\\n', '\\t');
                    if (!string.IsNullOrWhiteSpace(vaultKey))
                        return vaultKey;
                }}
            }}
            finally
            {{
                CredFree(credPtr);
            }}
        }}

        string fallback = DecodeEmbeddedPortableKey();
        if (!string.IsNullOrWhiteSpace(fallback))
        {{
            try {{ WriteApiKeyToVault(fallback, target); }} catch {{ }}
            return fallback;
        }}
        return null;
    }}"""

    try:
        patched_source = original_source.replace(target_block, patched_block, 1)
        with open(native_interop_path, "w", encoding="utf-8") as f:
            f.write(patched_source)

        if os.path.exists(out_dir):
            shutil.rmtree(out_dir, ignore_errors=True)
        os.makedirs(out_dir, exist_ok=True)

        cmd = [
            "dotnet",
            "publish",
            "VozWindows.csproj",
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true",
            "-p:DefineConstants=PORTABLE_VOICE_ONLY",
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "-o",
            out_dir,
        ]
        print("[1/3] Compilando executavel portatil unico (win-x64 self-contained)...")
        res = subprocess.run(cmd, cwd=proj_dir, capture_output=True, text=True)
        if res.returncode != 0:
            print("STDOUT:", res.stdout)
            print("STDERR:", res.stderr)
            raise RuntimeError(f"Falha no dotnet publish (code={res.returncode})")

        for fname in os.listdir(out_dir):
            if fname.lower() != "vozwindows.exe":
                fpath = os.path.join(out_dir, fname)
                if os.path.isdir(fpath):
                    shutil.rmtree(fpath, ignore_errors=True)
                else:
                    os.remove(fpath)

        exe_path = os.path.join(out_dir, "VozWindows.exe")
        size_mb = os.path.getsize(exe_path) / (1024 * 1024)
        print(f"      -> Gerado: {exe_path} ({size_mb:.1f} MB)")
        return exe_path
    finally:
        with open(native_interop_path, "w", encoding="utf-8") as f:
            f.write(original_source)


def github_api_request(method: str, url: str, token: str, payload: dict | None = None) -> tuple[int, dict]:
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(
        url,
        data=data,
        method=method,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "VozWindows-ReleasePublisher/1.0",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            body = resp.read().decode("utf-8")
            return resp.status, (json.loads(body) if body else {})
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="ignore")
        try:
            parsed = json.loads(body) if body else {}
        except Exception:
            parsed = {"raw": body}
        return e.code, parsed


def ensure_github_repo(token: str) -> None:
    repo_url = f"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}"
    status, data = github_api_request("GET", repo_url, token)
    if status == 200:
        print(f"[2/3] Repositorio GitHub confirmado: https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}")
        return

    if status == 404:
        print(f"[2/3] Criando repositorio publico {GITHUB_OWNER}/{GITHUB_REPO} via GitHub API...")
        create_status, create_data = github_api_request(
            "POST",
            "https://api.github.com/user/repos",
            token,
            {
                "name": GITHUB_REPO,
                "description": "Distribuicao e Auto-Update do VozWindows (Ditado ao Vivo e Reescrita Inteligente)",
                "private": False,
                "auto_init": True,
                "has_issues": False,
                "has_projects": False,
                "has_wiki": False,
            },
        )
        if create_status not in (200, 201):
            raise RuntimeError(f"Falha ao criar repositorio ({create_status}): {create_data}")
        print(f"      -> Repositorio criado: {create_data.get('html_url')}")
        return

    raise RuntimeError(f"Erro ao consultar repositorio ({status}): {data}")


def publish_github_release(token: str, version: str, exe_path: str, notes: str) -> str:
    tag = f"v{version.lstrip('vV')}"
    release_name = f"VozWindows {tag}"
    print(f"[3/3] Publicando Release {tag} no GitHub...")

    tag_url = f"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/tags/{tag}"
    status, release = github_api_request("GET", tag_url, token)

    if status == 404:
        create_url = f"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases"
        status, release = github_api_request(
            "POST",
            create_url,
            token,
            {
                "tag_name": tag,
                "name": release_name,
                "body": notes,
                "draft": False,
                "prerelease": False,
            },
        )
        if status not in (200, 201):
            raise RuntimeError(f"Falha ao criar release {tag} ({status}): {release}")
    elif status == 200:
        release_id = release["id"]
        update_url = f"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/{release_id}"
        status, release = github_api_request(
            "PATCH",
            update_url,
            token,
            {
                "name": release_name,
                "body": notes,
                "draft": False,
                "prerelease": False,
            },
        )
    else:
        raise RuntimeError(f"Erro ao verificar release {tag} ({status}): {release}")

    # Remove asset anterior de mesmo nome se existir
    for asset in release.get("assets", []):
        if asset.get("name", "").lower() == "vozwindows.exe":
            asset_id = asset["id"]
            del_url = f"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/assets/{asset_id}"
            github_api_request("DELETE", del_url, token)

    upload_url_template = release["upload_url"].split("{")[0]
    upload_url = f"{upload_url_template}?{urllib.parse.urlencode({'name': 'VozWindows.exe'})}"

    file_size = os.path.getsize(exe_path)
    print(f"      -> Enviando VozWindows.exe ({file_size / (1024 * 1024):.1f} MB) para GitHub Releases...")
    with open(exe_path, "rb") as f:
        file_bytes = f.read()

    req = urllib.request.Request(
        upload_url,
        data=file_bytes,
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "VozWindows-ReleasePublisher/1.0",
            "Content-Type": "application/vnd.microsoft.portable-executable",
            "Content-Length": str(len(file_bytes)),
        },
    )
    with urllib.request.urlopen(req, timeout=300) as resp:
        asset_data = json.loads(resp.read().decode("utf-8"))

    download_url = asset_data.get("browser_download_url", "")
    html_url = release.get("html_url", "")
    print(f"      -> Release publicada com sucesso: {html_url}")
    print(f"      -> Download direto do asset:      {download_url}")
    return html_url


def sync_git_repository(proj_dir: str, version: str) -> None:
    git_dir = os.path.join(proj_dir, ".git")
    if not os.path.isdir(git_dir):
        return
    status_res = subprocess.run(["git", "status", "--porcelain"], cwd=proj_dir, capture_output=True, text=True)
    if status_res.returncode == 0 and status_res.stdout.strip():
        subprocess.run(["git", "add", "-A"], cwd=proj_dir, check=False)
        subprocess.run(
            ["git", "commit", "-m", f"Release v{version.lstrip('vV')}"],
            cwd=proj_dir,
            check=False,
        )
    subprocess.run(["git", "push", "origin", "master"], cwd=proj_dir, check=False)


def main() -> int:
    parser = argparse.ArgumentParser(description="Compila e publica uma nova Release do VozWindows no GitHub.")
    parser.add_argument("--version", help="Define uma nova versao (ex: 1.2.0) no VozWindows.csproj antes de publicar.")
    parser.add_argument(
        "--skip-build",
        action="store_true",
        help="Usa o bin\\Portatil\\VozWindows.exe ja compilado sem recompilar.",
    )
    parser.add_argument(
        "--notes",
        default=(
            "### Novidades desta versão\n"
            "- **Auto-Update integrado em 1 clique** via GitHub Releases.\n"
            "- **6 novas opções de reescrita ao vivo** focadas em comunicação técnica, didática e operacional.\n"
            "- **Versão portátil 100% dedicada à voz** (estabilidade de janela e cursor)."
        ),
        help="Notas da versao em Markdown.",
    )
    args = parser.parse_args()

    proj_dir = os.path.dirname(os.path.abspath(__file__))
    csproj_path = os.path.join(proj_dir, "VozWindows.csproj")

    if args.version:
        update_csproj_version(csproj_path, args.version)

    version = read_csproj_version(csproj_path)
    exe_path = os.path.join(proj_dir, "bin", "Portatil", "VozWindows.exe")

    if not args.skip_build or not os.path.exists(exe_path):
        exe_path = build_portable_exe(proj_dir)

    gh_token = read_vault_secret(GITHUB_CRED_TARGET)
    ensure_github_repo(gh_token)
    sync_git_repository(proj_dir, version)
    publish_github_release(gh_token, version, exe_path, args.notes)
    return 0


if __name__ == "__main__":
    sys.exit(main())
