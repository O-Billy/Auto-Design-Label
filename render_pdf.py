#!/usr/bin/env python3
"""LDM -> PDF renderer + printability linter (demo reference implementation).

Doc file LDM JSON, binding du lieu mau, xuat:
  1. <out>-1to1.pdf   : moi trang = dung kich thuoc nhan, in 100% de do thuc te
  2. <out>-proof.pdf  : A4 proof sheet co thuoc ke va chu thich cho QC
  3. <out>-lint.json  : bao cao X-dimension / QR version / tran khung
"""
import json, re, sys, copy
from pathlib import Path
from reportlab.pdfgen import canvas
from reportlab.lib.units import mm
from reportlab.lib.pagesizes import A4
from reportlab.graphics.barcode import code128
from reportlab.graphics.barcode.qr import QrCodeWidget
from reportlab.graphics.shapes import Drawing
from reportlab.graphics import renderPDF
from reportlab.pdfbase import pdfmetrics

# --- nguong kiem tra chat luong quet -------------------------------------
MIN_X_DIM_MM = 0.19   # X-dimension toi thieu khuyen nghi cho Code128
MIN_QR_MODULE_MM = 0.25
DPI_TARGETS = [203, 300]

FONT = {"medium": "Helvetica-Bold", "light": "Helvetica"}


def bind(text, data):
    def repl(m):
        k = m.group(1)
        if k not in data:
            raise KeyError(f"Thieu du lieu cho truong {{{{{k}}}}}")
        return str(data[k])
    return re.sub(r"\{\{(\w+)\}\}", repl, text)


def expand(elements, data):
    """Bung element 'repeat' thanh cac element thuong."""
    out = []
    for el in elements:
        if el.get("type") != "repeat":
            out.append(el)
            continue
        n = min(int(data.get("UNIT_COUNT", el["max"])), el["max"])
        for i in range(1, n + 1):
            y = el["y0"] + (i - 1) * el["stepY"]
            row = dict(data)
            row["i"] = i
            row["RSN_i"] = data[f"RSN{i}"]
            row["MAC_i"] = data[f"MAC{i}"]
            for t in el["template"]:
                e = copy.deepcopy(t)
                e["y"] = y + e["y"]
                for key in ("text", "data"):
                    if key in e:
                        e[key] = bind(e[key], row)
                e["_bound"] = True
                out.append(e)
    return out


class Linter:
    def __init__(self):
        self.rows = []

    def code128(self, label, el, value, target_w_mm):
        bc = code128.Code128(value, barWidth=0.1 * mm, barHeight=1 * mm,
                             humanReadable=False, quiet=False)
        modules = round(bc.width / mm / 0.1)
        x_dim = target_w_mm / modules
        dpi_fit = {}
        for dpi in DPI_TARGETS:
            dots_per_mm = dpi / 25.4
            n = max(1, round(x_dim * dots_per_mm))
            dpi_fit[dpi] = {"barWidthDots": n,
                            "actualXDimMm": round(n / dots_per_mm, 4),
                            "actualWidthMm": round(modules * n / dots_per_mm, 2)}
        status = "OK" if x_dim >= MIN_X_DIM_MM else "CANH BAO"
        self.rows.append({"label": label, "type": "code128", "value": value,
                          "modules": modules, "targetWidthMm": target_w_mm,
                          "xDimMm": round(x_dim, 4), "status": status,
                          "dpi": dpi_fit})
        return status

    def qr(self, label, el, value, size_mm):
        w = QrCodeWidget(value, barLevel=el.get("ecc", "M"))
        n = w.qr.getModuleCount()
        module = size_mm / (n + 8)          # +8 = quiet zone 4 module moi ben
        dpi_fit = {}
        for dpi in DPI_TARGETS:
            dots_per_mm = dpi / 25.4
            mag = max(1, int(size_mm * dots_per_mm / (n + 8)))
            dpi_fit[dpi] = {"magnification": mag,
                            "printedMm": round((n + 8) * mag / dots_per_mm, 2)}
        status = "OK" if module >= MIN_QR_MODULE_MM else "CANH BAO"
        self.rows.append({"label": label, "type": "qr", "bytes": len(value),
                          "modules": n, "targetSizeMm": size_mm,
                          "moduleMm": round(module, 4), "status": status,
                          "dpi": dpi_fit})
        return status

    def text_fit(self, label, s, x, y, size, font, align, lw):
        w = pdfmetrics.stringWidth(s, FONT[font], size) / mm
        x0 = x - w if align == "right" else (x - w / 2 if align == "center" else x)
        if x0 < 0 or x0 + w > lw + 0.01:
            self.rows.append({"label": label, "type": "text-overflow",
                              "text": s[:48], "xMm": round(x0, 2),
                              "widthMm": round(w, 2), "mediaWidthMm": lw,
                              "status": "LOI"})

    def collide(self, label, boxes):
        """Kiem tra giao khung giua cac phan tu (bo qua text-vs-text lien ke)."""
        for i in range(len(boxes)):
            for j in range(i + 1, len(boxes)):
                (k1, n1, a) = boxes[i]
                (k2, n2, b) = boxes[j]
                if k1 == "text" and k2 == "text":
                    continue
                ox = min(a[2], b[2]) - max(a[0], b[0])
                oy = min(a[3], b[3]) - max(a[1], b[1])
                if ox > 0.3 and oy > 0.3:
                    self.rows.append({"label": label, "type": "collision",
                                      "a": f"{k1}:{n1}", "b": f"{k2}:{n2}",
                                      "overlapMm": [round(ox, 2), round(oy, 2)],
                                      "status": "LOI"})

    def overflow(self, label, el, x, y, w, h, lw, lh):
        if x < 0 or y < 0 or x + w > lw + 0.01 or y + h > lh + 0.01:
            self.rows.append({"label": label, "type": "overflow",
                              "element": el.get("type"),
                              "box": [x, y, w, h], "media": [lw, lh],
                              "status": "LOI"})


def draw_label(c, lab, data, ox, oy, lint, debug=False):
    """Ve mot nhan. (ox, oy) = goc duoi-trai cua nhan trong he toa do PDF."""
    W, H = lab["widthMm"], lab["heightMm"]

    def px(x):      # mm tu trai -> point
        return ox + x * mm

    def py(y):      # mm tu tren xuong -> point
        return oy + (H - y) * mm

    c.setLineWidth(0.25)
    c.setDash()
    # Vien nhan: bo goc die-cut neu LDM co cornerRadiusMm (xem PmdExtractor), nguoc lai goc vuong.
    r = lab.get("cornerRadiusMm", 0) or 0
    if r > 0.05:
        c.roundRect(ox, oy, W * mm, H * mm, r * mm)
    else:
        c.rect(ox, oy, W * mm, H * mm)

    boxes = []
    for el in expand(lab["elements"], data):
        t = el["type"]
        if t == "text":
            s = el["text"] if el.get("_bound") else bind(el["text"], data)
            c.setFont(FONT[el.get("font", "light")], el["size"])
            a = el.get("align", "left")
            if a == "right":
                c.drawRightString(px(el["x"]), py(el["y"]), s)
            elif a == "center":
                c.drawCentredString(px(el["x"]), py(el["y"]), s)
            else:
                c.drawString(px(el["x"]), py(el["y"]), s)
            lint.text_fit(lab["id"], s, el["x"], el["y"], el["size"],
                          el.get("font", "light"), a, W)
            tw = pdfmetrics.stringWidth(s, FONT[el.get("font", "light")], el["size"]) / mm
            x0 = el["x"] - tw if a == "right" else (el["x"] - tw / 2 if a == "center" else el["x"])
            asc, dsc = el["size"] * 0.72 / 2.835, el["size"] * 0.20 / 2.835
            boxes.append(("text", s[:28], (x0, el["y"] - asc, x0 + tw, el["y"] + dsc)))

        elif t == "line":
            c.setDash(*( [el["dash"], 0] if el.get("dash") else [] ))
            c.setLineWidth(0.3)
            c.line(px(el["x1"]), py(el["y1"]), px(el["x2"]), py(el["y2"]))
            c.setDash()

        elif t == "barcode128":
            val = el["data"] if el.get("_bound") else bind(el["data"], data)
            lint.code128(lab["id"], el, val, el["width"])
            lint.overflow(lab["id"], el, el["x"], el["y"], el["width"], el["height"], W, H)
            probe = code128.Code128(val, barWidth=0.1 * mm, barHeight=1 * mm,
                                    humanReadable=False, quiet=False)
            bw = (el["width"] * mm) / (probe.width / (0.1 * mm))
            bc = code128.Code128(val, barWidth=bw, barHeight=el["height"] * mm,
                                 humanReadable=False, quiet=False)
            bc.drawOn(c, px(el["x"]), py(el["y"] + el["height"]))
            boxes.append(("barcode", val[:16], (el["x"], el["y"],
                          el["x"] + el["width"], el["y"] + el["height"])))

        elif t == "qr":
            val = el["data"] if el.get("_bound") else bind(el["data"], data)
            lint.qr(lab["id"], el, val, el["size"])
            lint.overflow(lab["id"], el, el["x"], el["y"], el["size"], el["size"], W, H)
            w = QrCodeWidget(val, barLevel=el.get("ecc", "M"))
            b = w.getBounds()
            s = el["size"] * mm / (b[2] - b[0])
            d = Drawing(el["size"] * mm, el["size"] * mm, transform=[s, 0, 0, s, 0, 0])
            d.add(w)
            renderPDF.draw(d, c, px(el["x"]), py(el["y"] + el["size"]))
            boxes.append(("qr", "QR", (el["x"], el["y"],
                          el["x"] + el["size"], el["y"] + el["size"])))

        elif t == "image":
            lint.overflow(lab["id"], el, el["x"], el["y"], el["width"], el["height"], W, H)
            c.setDash(1, 1)
            c.setLineWidth(0.25)
            c.rect(px(el["x"]), py(el["y"] + el["height"]),
                   el["width"] * mm, el["height"] * mm)
            c.setDash()
            c.setFont("Helvetica", 5)
            c.drawCentredString(px(el["x"] + el["width"] / 2),
                                py(el["y"] + el["height"] / 2 + 0.7),
                                el.get("placeholder", "IMG"))
            boxes.append(("image", el.get("placeholder", "IMG"),
                          (el["x"], el["y"], el["x"] + el["width"], el["y"] + el["height"])))

    lint.collide(lab["id"], boxes)


def ruler(c, ox, oy, W, H):
    c.setLineWidth(0.2)
    c.setFont("Helvetica", 4)
    for i in range(0, int(W) + 1, 5):
        c.line(ox + i * mm, oy - 1.5 * mm, ox + i * mm, oy)
        if i % 10 == 0:
            c.drawCentredString(ox + i * mm, oy - 4 * mm, str(i))
    for j in range(0, int(H) + 1, 5):
        c.line(ox - 1.5 * mm, oy + j * mm, ox, oy + j * mm)
        if j % 10 == 0:
            c.drawRightString(ox - 2.5 * mm, oy + j * mm - 1.2, str(j))


def main(ldm_path, data_path, out_prefix):
    ldm = json.loads(Path(ldm_path).read_text(encoding="utf-8"))
    data = json.loads(Path(data_path).read_text(encoding="utf-8"))
    lint = Linter()

    # --- 1) ban 1:1, moi trang mot nhan --------------------------------
    c = canvas.Canvas(f"{out_prefix}-1to1.pdf")
    for lab in ldm["labels"]:
        c.setPageSize((lab["widthMm"] * mm, lab["heightMm"] * mm))
        draw_label(c, lab, data, 0, 0, lint)
        c.showPage()
    c.save()

    # --- 2) proof sheet A4 ---------------------------------------------
    lint2 = Linter()
    c = canvas.Canvas(f"{out_prefix}-proof.pdf", pagesize=A4)
    for lab in ldm["labels"]:
        c.setFont("Helvetica-Bold", 11)
        c.drawString(20 * mm, 280 * mm,
                     f'{ldm["documentId"]} rev {ldm["revision"]}  |  {lab["name"]}')
        c.setFont("Helvetica", 8)
        c.drawString(20 * mm, 275 * mm,
                     f'P/N {lab["partNumber"]}  -  {lab["widthMm"]}x{lab["heightMm"]} mm  -  '
                     f'{lab["material"]}  -  SL {lab["quantity"]}/don vi')
        c.setFont("Helvetica-Oblique", 7)
        c.drawString(20 * mm, 271 * mm, f'Layout: {lab.get("layoutConfidence","")}')
        ox, oy = 25 * mm, (260 - lab["heightMm"]) * mm
        draw_label(c, lab, data, ox, oy, lint2)
        ruler(c, ox, oy, lab["widthMm"], lab["heightMm"])
        c.setFont("Helvetica", 7)
        c.drawString(20 * mm, 20 * mm,
                     "In o ty le 100% (Actual size / None). Do khung ngoai bang thuoc de xac nhan.")
        c.showPage()
    c.save()

    Path(f"{out_prefix}-lint.json").write_text(
        json.dumps(lint.rows, indent=2, ensure_ascii=False), encoding="utf-8")

    for r in lint.rows:
        r.setdefault("status", "OK")
    warn = [r for r in lint.rows if r["status"] != "OK"]
    print(f"Da xuat {out_prefix}-1to1.pdf / -proof.pdf / -lint.json")
    print(f"Tong kiem tra: {len(lint.rows)}, canh bao/loi: {len(warn)}")
    for r in warn:
        print("  !", r["label"], r["type"],
              (r.get("value") or r.get("text") or "")[:44],
              r.get("xDimMm") or r.get("moduleMm") or "", r["status"])


if __name__ == "__main__":
    main(*sys.argv[1:4])
