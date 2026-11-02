#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
规范 §七 三条硬禁止的**源码级断言**（§十 要求，挂在构建前）。

禁止项：
  * 系统消息框  MessageBox.Show / MessageBoxIcon / Interaction.MsgBox / MessageBoxW|A
  * 提示音      SystemSounds / MessageBeep / PlaySound / NotifyIcon.ShowBalloonTip
  * 网页原生框  alert( / confirm( / prompt(（裸调用或 window.xxx）
  * Qt 遗留     QMessageBox / QApplication.beep

「代码里看着没写」不等于「听不到」—— 框架的默认框会替你响，所以这条要能在构建前自动拦住。

用法:
  python tools/no_system_popup.py            # 扫黑，零命中退出 0，命中退出 1
  python tools/no_system_popup.py --list      # 只打印规则清单

两条正则细节（踩过坑）：
  1) 网页框那条用 (?<![A-Za-z0-9_.]) 打头 —— 程序的 UI.confirm( / _ask_confirm( 是自定义封装，
     不是浏览器原生框；裸 alert( 和 window.alert( 才算违规。
  2) 注释里写「这里不要用 MessageBox」会被自己扫出来 —— 注释要改写成不含 API 名的说法，
     或加进 ALLOW 并写理由，不许为了变绿就删规则。
"""

from __future__ import annotations

import argparse
import os
import re
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

#: 只扫源码目录。别扫仓库根 —— 备份/同步目录里的历史代码会把结果冲成噪声。
SCAN_DIRS = ["src/app", "src/installer"]

SKIP_DIRS = {"bin", "obj", ".vs", "node_modules", ".git", "artifacts", "dist"}

TEXT_EXT = {".cs", ".xaml", ".html", ".htm", ".js", ".css", ".cmd", ".bat", ".ps1", ".manifest"}

#: 「网页原生框」的声明形态：function confirm(...) { ... } 或对象方法简写 confirm(id){ ... }。
#: 这类是程序自己的封装（如 UI.confirm 由宿主调起内联确认条），不是浏览器原生框。
#: 判据很窄：原生调用不可能在参数表后面直接跟 `{`。
WEB_DECL = re.compile(r"^\s*(?:async\s+)?(?:alert|confirm|prompt)\s*\([^)]*\)\s*\{")


#: (标签, 正则, 是否套用网页声明豁免)
RULES = [
    ("系统消息框 MessageBox.Show", re.compile(r"\bMessageBox\s*\.\s*Show\b"), False),
    ("系统消息框 MessageBoxIcon", re.compile(r"\bMessageBoxIcon\b"), False),
    ("系统消息框 Interaction.MsgBox", re.compile(r"\bInteraction\s*\.\s*MsgBox\b"), False),
    ("系统消息框 Win32 MessageBoxW/A", re.compile(r"\bMessageBox[WA]\b"), False),
    ("提示音 SystemSounds", re.compile(r"\bSystemSounds\b"), False),
    ("提示音 MessageBeep", re.compile(r"\bMessageBeep\b"), False),
    ("提示音 PlaySound", re.compile(r"\bPlaySound\b"), False),
    ("托盘气泡 NotifyIcon.ShowBalloonTip", re.compile(r"\bNotifyIcon\s*\.\s*ShowBalloonTip\b"), False),
    ("网页原生框 alert|confirm|prompt",
     re.compile(r"(?<![A-Za-z0-9_.])(?:alert|confirm|prompt)\s*\("), True),
    ("网页原生框 window.alert|confirm|prompt",
     re.compile(r"\bwindow\s*\.\s*(?:alert|confirm|prompt)\s*\("), True),
    ("Qt 消息框 QMessageBox", re.compile(r"\bQMessageBox\b"), False),
    ("Qt 提示音 QApplication.beep", re.compile(r"\bQApplication\s*\.\s*beep\b"), False),
]

#: 逐行豁免：(相对路径正则, 该行正则, 理由)。只放「确实合规但长得像」的行。
ALLOW: list[tuple[str, str, str]] = []


def decode(data: bytes) -> str:
    for enc in ("utf-8-sig", "utf-8", "gbk"):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", "replace")


def iter_files():
    for rel in SCAN_DIRS:
        base = os.path.join(ROOT, rel.replace("/", os.sep))
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in filenames:
                if os.path.splitext(fn)[1].lower() in TEXT_EXT:
                    yield os.path.join(dirpath, fn)


def is_allowed(rel: str, line: str) -> bool:
    for path_pat, line_pat, _reason in ALLOW:
        if re.search(path_pat, rel) and re.search(line_pat, line):
            return True
    return False


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true", help="只打印规则清单")
    args = ap.parse_args()

    if args.list:
        print("扫黑规则（规范 §七）：")
        for label, _rx, _decl in RULES:
            print("  - " + label)
        print("扫描目录：" + "、".join(SCAN_DIRS))
        return 0

    scanned = 0
    hits = []          # (rel, lineno, label, text)

    for path in iter_files():
        scanned += 1
        rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
        try:
            with open(path, "rb") as f:
                text = decode(f.read())
        except OSError as e:
            print("× 读不了 %s: %s" % (rel, e))
            return 2

        for lineno, line in enumerate(text.splitlines(), 1):
            stripped = line.strip()
            if not stripped:
                continue
            for label, rx, web_decl_ok in RULES:
                if not rx.search(line):
                    continue
                if web_decl_ok and WEB_DECL.search(line):
                    continue
                if is_allowed(rel, line):
                    continue
                hits.append((rel, lineno, label, stripped[:120]))
                break

    if not hits:
        print("OK：%d 个源码文件，静音黑名单零命中" % scanned)
        return 0

    print("FAIL：%d 处命中 §七 的硬禁止项（系统框 / 提示音 / 网页原生框）" % len(hits))
    print("-" * 72)
    for rel, lineno, label, snippet in hits:
        print("  %s:%d" % (rel, lineno))
        print("    [%s]  %s" % (label, snippet))
    print("-" * 72)
    print("逐条看过调用对象再改：确认是真违规（删掉/换成自绘框），")
    print("还是自定义封装被误伤（加进 ALLOW 并写理由）。不要为了变绿删规则。")
    return 1


if __name__ == "__main__":
    sys.exit(main())