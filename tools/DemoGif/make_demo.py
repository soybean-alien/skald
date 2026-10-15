"""Render the real Skald dashboard page with SAMPLE data (tools/DemoGif/demo_stub.js) into docs/demo.gif.

Usage: python tools/DemoGif/make_demo.py docs/demo.gif   (needs Google Chrome and Pillow)
Frames are captured with headless Chrome at different --virtual-time-budget values.
"""
import os, subprocess, sys
from PIL import Image

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Skald", "src", "WebServer.cs")
HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.join(HERE, "_work")
OUT = sys.argv[1]
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
W, H = 1270, 840

cs = open(SRC, encoding="utf-8").read()
i = cs.index('const string Page = @"') + len('const string Page = @"')
j = cs.index('";', i)
page = cs[i:j].replace('""', '"')                     # C# verbatim string -> HTML

stub = open(os.path.join(HERE, "demo_stub.js"), encoding="utf-8").read()
dark = ("<style>:root{--bg:#16140f;--fg:#ece6da;--mut:#9a927f;--line:#332e25;--acc:#e0a84a;"
        "--bad:#ff8a80;--badbg:#3a1714} body{zoom:1.0}</style>")
page = page.replace("<head>", "<head>" + "<script>" + stub + "</script>" + dark, 1) if "<head>" in page \
    else page.replace("<html lang=en>", "<html lang=en><head><script>" + stub + "</script>" + dark + "</head>", 1)
os.makedirs(WORK, exist_ok=True)
html_path = os.path.join(WORK, "demo_page.html")
open(html_path, "w", encoding="utf-8").write(page)
url = "file:///" + html_path.replace("\\", "/")

frames_dir = os.path.join(WORK, "frames")
os.makedirs(frames_dir, exist_ok=True)
times = [0.4, 1.0, 1.8, 2.6, 3.4, 4.2, 5.0, 5.8, 6.6, 7.4, 8.2, 9.0, 9.8, 10.6, 11.4, 12.2, 12.8, 13.5, 14.2, 15.2, 16.0, 16.8, 17.6, 18.4]
frames = []
for n, t in enumerate(times):
    png = os.path.join(frames_dir, f"f{n:02d}.png")
    subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--mute-audio",
                    "--no-first-run", "--no-default-browser-check", f"--window-size={W},{H}",
                    f"--virtual-time-budget={int(t * 1000)}", f"--screenshot={png}", url],
                   check=True, capture_output=True, timeout=60)
    frames.append(Image.open(png).convert("RGB").crop((0, 0, W, H)))
    print("frame", n, "t=", t)

# hold key moments longer (ms per frame)
dur = []
for t in times:
    if t in (0.4,): dur.append(1500)
    elif t in (6.6, 12.8, 14.2): dur.append(1600)
    elif t == times[-1]: dur.append(2200)
    else: dur.append(650)

pal = [f.quantize(colors=128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f in frames]
pal[0].save(OUT, save_all=True, append_images=pal[1:], duration=dur, loop=0, optimize=True, disposal=1)
print("saved", OUT, os.path.getsize(OUT) // 1024, "KB,", len(frames), "frames,", sum(dur) / 1000, "s")
