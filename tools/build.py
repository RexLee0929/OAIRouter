#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
OAIRouter 一键构建：主程序(publish) → payload.zip → 安装器 → 产物进 artifacts/ → 同步源码到发布区。

发布产物统一放在编辑区根的 artifacts/（浅、好找）：安装器 exe 本身就是发布产物
（安装器设计规范 §一），发版直接从 artifacts/ 取 `OAIRouter-<版本>-setup.exe` 传 GitHub Releases。
bin/obj 只是编译中间目录，发布区仓库不放任何构建产物。

用法:
  python tools/build.py                 # 编译，产物复制进 artifacts/
  python tools/build.py --apply         # 编译后再把源码同步到发布区（不含任何构建产物）
  python tools/build.py --apply --clean # 先清 bin/obj 再编译
  python tools/build.py --no-installer  # 跳过安装器（只编主程序）

OAIRouter 与 ComicConverter 的差别：
  - 主程序是 net10.0-windows 自包含单文件，[1/3] 用 dotnet publish（产物在
    bin/Release/net10.0-windows/win-x64/publish/），payload 里只有一个 OAIRouter.exe；
  - 主程序数据全在安装目录（config.json / backups / logs / usage.jsonl），安装器卸载时默认保留。
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
APP_DIR = ROOT / "src" / "app"
SETUP_DIR = ROOT / "src" / "installer"
APP_OUT = APP_DIR / "bin" / "Release" / "net10.0-windows" / "win-x64" / "publish"
SETUP_OUT = SETUP_DIR / "bin" / "Release" / "net48"
PAYLOAD = SETUP_DIR / "payload.zip"
ARTIFACTS = ROOT / "artifacts"          # 发布产物统一出口（编辑区根，浅层好找）


def read_version() -> str:
    """版本唯一来源是 src/version.props，安装器名与发布包名都从这里取。"""
    text = (ROOT / "src" / "version.props").read_text(encoding="utf-8")
    m = re.search(r"<Version>([^<]+)</Version>", text)
    return m.group(1).strip() if m else "0.0.0"


VERSION = read_version()
# 安装器的文件名必须带版本号（安装器设计规范 §一），
# 且安装器 exe 本身就是发布产物，不再打 zip/7z 包
SETUP_EXE = f"OAIRouter-{VERSION}-setup.exe"
SKIP_EXT = {".pdb", ".xml", ".zip"}
# 运行时产生的文件绝不能进安装包：会把开发者本机的数据带给用户
# （OAIRouter 的 publish 产物只有 exe 与 pdb，这里是兜底）
SKIP_NAMES = {"config.json", "error.log", "usage.jsonl"}


def wanted(p: Path) -> bool:
    return p.is_file() and p.suffix.lower() not in SKIP_EXT and p.name.lower() not in SKIP_NAMES


def run(cmd: list[str], cwd: Path) -> bool:
    print(f"$ {' '.join(cmd)}   (cwd={cwd.relative_to(ROOT)})")
    try:
        p = subprocess.run(cmd, cwd=str(cwd), capture_output=True)
    except FileNotFoundError:
        print("× 找不到 dotnet，请确认已安装 .NET SDK")
        return False

    out = p.stdout.decode("utf-8", "replace").strip()
    err = p.stderr.decode("utf-8", "replace").strip()
    if p.returncode != 0:
        print(out[-3000:])
        print(err[-3000:])
        return False

    # 只回显警告与摘要，避免刷屏
    for line in out.splitlines():
        s = line.strip()
        if s.startswith("warning") or "警告" in s or "错误" in s or "-> " in s:
            print("  " + s)
    return True


def make_payload() -> int:
    if not APP_OUT.is_dir():
        print(f"× 找不到主程序产物目录: {APP_OUT}")
        return 0

    files = [p for p in APP_OUT.rglob("*") if wanted(p)]
    if not files:
        print("× 主程序产物为空")
        return 0

    if PAYLOAD.exists():
        PAYLOAD.unlink()
    with zipfile.ZipFile(PAYLOAD, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for p in sorted(files):
            z.write(p, p.relative_to(APP_OUT).as_posix())

    kb = PAYLOAD.stat().st_size / 1024
    print(f"payload.zip  {len(files)} 个文件  {kb:.0f} KB")
    return len(files)


def artifact_list() -> list[tuple[Path, Path]]:
    """(源绝对路径, 目标相对路径)"""
    pairs: list[tuple[Path, Path]] = []
    if APP_OUT.is_dir():
        for p in APP_OUT.rglob("*"):
            if not wanted(p):
                continue
            pairs.append((p, p.relative_to(APP_OUT)))
    setup = SETUP_OUT / SETUP_EXE
    if setup.is_file():
        pairs.append((setup, Path(SETUP_EXE)))
    return pairs


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="同步到发布区（默认只编译）")
    ap.add_argument("--clean", action="store_true", help="编译前删除 bin/obj")
    ap.add_argument("--no-installer", action="store_true")
    ap.add_argument("--dest", default=os.environ.get("OAI_DEST", r"D:/Code/OAIRouter"))
    args = ap.parse_args()

    dest = Path(args.dest).resolve()

    if args.clean:
        for d in [APP_DIR / "bin", APP_DIR / "obj", SETUP_DIR / "bin", SETUP_DIR / "obj"]:
            if d.is_dir():
                shutil.rmtree(d, ignore_errors=True)
                print(f"清理 {d.relative_to(ROOT)}")

    print("=" * 64)
    print("[1/3] 编译主程序（自包含单文件 publish）")
    if not run(["dotnet", "publish", "-c", "Release"], APP_DIR):
        return 1

    print("[2/3] 打包 payload.zip")
    n = make_payload()
    if n == 0:
        return 1

    if not args.no_installer:
        print("[3/3] 编译安装器")
        if not run(["dotnet", "build", "-c", "Release", "-v", "m"], SETUP_DIR):
            return 1
    else:
        print("[3/3] 跳过安装器")

    pairs = artifact_list()
    print("-" * 64)
    print(f"产物 {len(pairs)} 个：")
    for src, rel in pairs:
        print(f"  {rel.as_posix():<44} {src.stat().st_size / 1024:>8.0f} KB")

    # 产物统一复制进 artifacts/：整目录重建，旧版本的安装器不会残留
    if ARTIFACTS.is_dir():
        shutil.rmtree(ARTIFACTS, ignore_errors=True)
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    for src, rel in pairs:
        dst = ARTIFACTS / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
    print(f"已复制到 {ARTIFACTS}")

    if not args.apply:
        print("-" * 64)
        print("未指定 --apply，发布区未改动。")
        return 0

    if not dest.is_dir():
        print(f"× 发布区不存在: {dest}")
        return 2

    # 只同步源码：发布区仓库不放构建产物，安装器 exe 直接传 GitHub Releases（规范 §一）
    print("-" * 64)
    print("同步源码")
    # --prune：发布区是编辑区的镜像，编辑区删掉的文件发布区也要删，
    # 否则残留旧 .cs 会和新文件重复定义，发布区直接编译不过。
    rc = subprocess.run([sys.executable, str(HERE / "sync_to_code.py"),
                         "--apply", "--prune", "--dest", str(dest)])
    return rc.returncode


if __name__ == "__main__":
    sys.exit(main())
