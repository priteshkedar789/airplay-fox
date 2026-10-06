"""Builds docs/qr-<coin>.png for the README tip section and proves each QR decodes to the exact address.
Run: python tools/make_donate_qr.py   (needs: pip install qrcode[pil] opencv-python-headless)
The QR holds the bare address (accepted by every wallet), not a coin-specific URI."""
from pathlib import Path

import cv2
import qrcode
from qrcode.constants import ERROR_CORRECT_Q

ROOT = Path(__file__).resolve().parent.parent
ADDRESSES = {
    "btc": "bc1q730l0j6xnvfvvhkrgzg9ytjul6eyv8pv6lg76w",
    "bnb": "0xd43C5a3662f7B2620161c4600A1dDf26545c4Db9",
    "sol": "3wL2Fj7Xmh28khWtHaF5jtSegD1pmkj3yorHWjGbLNEf",
    "trx": "TXoumqAU8FASfnoEgWmPedS9zg9K9PNuxd",
}

readme = (ROOT / "README.md").read_text(encoding="utf-8")
detector = cv2.QRCodeDetector()
aruco = cv2.QRCodeDetectorAruco()
for coin, addr in ADDRESSES.items():
    assert addr in readme, f"{coin} address missing from README"
    qr = qrcode.QRCode(error_correction=ERROR_CORRECT_Q, box_size=12, border=4)
    qr.add_data(addr)
    qr.make(fit=True)
    out = ROOT / "docs" / f"qr-{coin}.png"
    qr.make_image(fill_color="black", back_color="white").save(out)

    # Two independent decoders; the classic one is scale-sensitive and may return nothing for a valid code,
    # so: at least one must read the exact address, and none may read a different one.
    image = cv2.imread(str(out))
    results = {"classic": detector.detectAndDecode(image)[0], "aruco": aruco.detectAndDecode(image)[0]}
    wrong = {k: v for k, v in results.items() if v and v != addr}
    assert not wrong, f"{coin}: a decoder read a DIFFERENT address: {wrong}"
    assert addr in results.values(), f"{coin}: no decoder could read the QR ({results})"
    ok = [k for k, v in results.items() if v == addr]
    print(f"{coin}: {out.name} {out.stat().st_size // 1024} KB, exact address read by: {', '.join(ok)}")
