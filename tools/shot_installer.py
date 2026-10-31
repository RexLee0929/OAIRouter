"""抓安装向导的界面截图（不装任何东西）。

用法：
    python shot_installer.py <setup.exe> <输出目录> <标签>

启动安装器 → 逐页点「下一步」→ 抓 6 页（欢迎/模式/许可/位置/任务/准备）→ 关窗口。
不点「安装」，所以不会真往磁盘写东西。
"""
import ctypes
import os
import struct
import sys
import time
import zlib
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 2
BM_CLICK = 0x00F5
WM_CLOSE = 0x0010

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    try:
        user32.SetProcessDPIAware()
    except Exception:
        pass


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", ctypes.c_long),
                ("biHeight", ctypes.c_long), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def grab(hwnd):
    """抓客户区，返回 (w, h, RGB bytes)。"""
    wr, cr = RECT(), RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(wr))
    user32.GetClientRect(hwnd, ctypes.byref(cr))

    pt = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))

    ww, wh = wr.right - wr.left, wr.bottom - wr.top
    hdc_win = user32.GetWindowDC(hwnd)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_win)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_win, ww, wh)
    gdi32.SelectObject(hdc_mem, hbmp)

    user32.PrintWindow(hwnd, hdc_mem, PW_RENDERFULLCONTENT)

    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = ww
    bmi.bmiHeader.biHeight = -wh          # 负数 = 自上而下
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0       # BI_RGB

    buf = ctypes.create_string_buffer(ww * wh * 4)
    gdi32.GetDIBits(hdc_mem, hbmp, 0, wh, buf, ctypes.byref(bmi), 0)

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(hwnd, hdc_win)

    raw = buf.raw
    ox, oy = pt.x - wr.left, pt.y - wr.top
    cw, ch = cr.right - cr.left, cr.bottom - cr.top
    cw = min(cw, ww - ox)
    ch = min(ch, wh - oy)

    out = bytearray(cw * ch * 3)
    for y in range(ch):
        start = ((oy + y) * ww + ox) * 4
        row = raw[start:start + cw * 4]
        line = bytearray(cw * 3)
        line[0::3] = row[2::4]      # R
        line[1::3] = row[1::4]      # G
        line[2::3] = row[0::4]      # B
        out[y * cw * 3:(y + 1) * cw * 3] = line
    return cw, ch, bytes(out)


def write_png(path, w, h, rgb):
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    stride = w * 3
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += rgb[y * stride:(y + 1) * stride]

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def find_child(hwnd, cls_kw, text=None):
    """按窗口类名关键字（和可选文本）找第一个子控件。"""
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(child, _):
        cls = ctypes.create_unicode_buffer(128)
        user32.GetClassNameW(child, cls, 128)
        if cls_kw in cls.value.upper():
            if text is None:
                found.append(child)
                return False
            buf = ctypes.create_unicode_buffer(128)
            user32.GetWindowTextW(child, buf, 128)
            if buf.value.strip() == text:
                found.append(child)
                return False
        return True

    user32.EnumChildWindows(hwnd, cb, 0)
    return found[0] if found else None


def dismiss_dialog():
    """安装器弹了确认框就点掉（默认按钮）。返回是否处理过。"""
    dlg = user32.FindWindowW("#32770", None)
    if not dlg:
        return False
    btn = find_child(dlg, "BUTTON")
    if btn:
        user32.PostMessageW(btn, BM_CLICK, 0, 0)
        time.sleep(0.45)
    return True


def artifacts_setup(root):
    """安装包在 artifacts/ 下且文件名带版本号（规范 §一）。版本从 version.props 读，别写死。"""
    import re
    with open(os.path.join(root, "src", "version.props"), encoding="utf-8") as f:
        v = re.search(r"<Version>([^<]+)</Version>", f.read()).group(1)
    return os.path.join(root, "artifacts", "ComicConverter-%s-setup.exe" % v)


def main():
    exe, outdir, tag = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(outdir, exist_ok=True)

    import subprocess
    import tempfile

    proc = subprocess.Popen([exe])
    hwnd = None
    for _ in range(80):
        hwnd = user32.FindWindowW(None, "OAIRouter 安装")
        if hwnd:
            break
        time.sleep(0.1)
    if not hwnd:
        proc.kill()
        raise SystemExit("找不到安装器窗口")

    time.sleep(0.9)

    for i in range(6):
        # 第 4 页先把安装位置改到临时目录，免得动真实安装目录、触发覆盖提示
        if i == 3:
            edit = find_child(hwnd, "EDIT")
            if edit:
                target = os.path.join(tempfile.gettempdir(), "cc-shot", "ComicConverter")
                user32.SetWindowTextW(edit, target)
                time.sleep(0.2)

        w, h, rgb = grab(hwnd)
        write_png(os.path.join(outdir, "%s-p%d.png" % (tag, i + 1)), w, h, rgb)
        print("%s 第 %d 页 %dx%d" % (tag, i + 1, w, h))
        if i == 5:
            break

        btn = find_child(hwnd, "BUTTON", "下一步")
        if not btn:
            print("第 %d 页找不到「下一步」" % (i + 1))
            break
        user32.PostMessageW(btn, BM_CLICK, 0, 0)
        time.sleep(0.5)

        for _ in range(3):
            if not dismiss_dialog():
                break

    user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
    time.sleep(0.4)
    if proc.poll() is None:
        proc.kill()


if __name__ == "__main__":
    main()
