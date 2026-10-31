#!/usr/bin/env python3
"""把品牌目录里最新的 OAIRouter 图标同步到本项目。

图标唯一来源：D:\\Library\\WorkBuddy\\APP构建\\brand
  - svg/OAIRouter.svg         矢量源（改设计改这里）
  - export/OAIRouter/*.ico    多尺寸图标（本脚本拷贝的就是它）

以前的 tools/gen_icon.py 会就地画一套旧图标并覆盖 app.ico，
已经删掉，避免和品牌目录打架。
"""
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
BRAND = os.path.normpath(os.path.join(HERE, "..", "..", "..", "APP构建", "brand"))
SRC = os.path.join(BRAND, "export", "OAIRouter", "OAIRouter.ico")
DST = os.path.join(PROJECT, "app.ico")


def main():
    if not os.path.isfile(SRC):
        print("找不到品牌图标：", SRC)
        print("先在 brand 目录跑 python tools/gen.py && python tools/export.py")
        return 1
    shutil.copy2(SRC, DST)
    print(f"已同步 {os.path.getsize(DST)} 字节 -> {DST}")
    print("提示：改了图标要重新 dotnet build，exe 里的资源才会更新。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
